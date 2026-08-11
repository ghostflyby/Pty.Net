#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using static Windows.Win32.PInvoke;

namespace Ghostflyby.Pty;

/// <summary>
/// Windows half of <see cref="PtyStream"/>: two ConPTY pipe handles (input-write for the
/// child's stdin, output-read for the child's merged stdout+stderr).
///
/// ConPTY supports only synchronous I/O (no overlapped), so a blocked ReadFile cannot be
/// cancelled. The "no thread-pool starvation" promise is kept by dedicating ONE background
/// thread per stream as the sole reader and one as the sole writer:
///
///  * The <b>reader thread</b> blocks on <c>ReadFile(outputRead)</c> and pumps bytes into
///    pending async reads (FIFO waiter queue) or the internal buffer (for sync reads).
///    No thread-pool thread is ever parked.
///  * <b>Async reads</b> register a waiter the reader satisfies. Cancellation removes the
///    waiter from the queue immediately; the blocked ReadFile keeps filling the internal
///    buffer, so canceling never has to abort a syscall.
///  * <b>Sync reads</b> wait on a data-available event and copy from the internal buffer.
///  * <b>Writes</b> are synchronous WriteFile loops on the writer thread (async writes are
///    queued); a large blocked write does not occupy a thread-pool thread, and partial
///    progress-then-cancel mirrors the Unix half.
///
/// EOF: when the pseudo console closes, the read pipe reports ERROR_BROKEN_PIPE; the
/// reader reports EOF (0) to every pending waiter and marks the stream EOF.
/// </summary>
public sealed partial class PtyStream
{
    private const uint PipeBufferSize = 64 * 1024;

    private readonly SafeFileHandle inputWrite;
    private readonly SafeFileHandle outputRead;
    private readonly ClosePseudoConsoleSafeHandle pseudoConsole;

    private readonly object gate = new();
    private readonly byte[] buffer = new byte[PipeBufferSize]; // internal buffer for sync reads
    private int bufferStart;
    private int bufferCount;
    private bool readerEof;

    private readonly List<ReadOperation> readWaiters = new();
    private readonly AutoResetEvent dataAvailable = new(false);

    private readonly Channel<WriteOperation> writeChannel;
    private volatile bool disposed;

    /// <summary>Takes ownership of the ConPTY pipe ends and the pseudo console.</summary>
    internal PtyStream(SafeFileHandle inputWrite, SafeFileHandle outputRead, ClosePseudoConsoleSafeHandle pseudoConsole)
    {
        this.inputWrite = inputWrite;
        this.outputRead = outputRead;
        this.pseudoConsole = pseudoConsole;
        writeChannel = Channel.CreateUnbounded<WriteOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "Pty.Net-io-reader" };
        readerThread.Start();
        var writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "Pty.Net-io-writer" };
        writerThread.Start(writeChannel.Reader);
    }

    internal bool IsClosed => disposed || inputWrite.IsClosed || outputRead.IsClosed;

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        // No userspace buffering, so there is nothing to flush.
    }

    // --------------------------------------------------------------------- read

    public override int Read(Span<byte> target)
    {
        return target.IsEmpty ? 0 : Read(target, Timeout.Infinite, out _);
    }

    /// <summary>
    /// Reads into <paramref name="target"/>, waiting up to <paramref name="timeoutMs"/>
    /// for data (infinite if negative). Returns the number of bytes copied, 0 on timeout
    /// (<paramref name="eof"/> false) or EOF (<paramref name="eof"/> true).
    /// </summary>
    internal int Read(Span<byte> target, int timeoutMs, out bool eof)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (Volatile.Read(ref pendingAsyncReads) > 0)
            throw new InvalidOperationException(
                "A pending async read is in progress on this pty stream; sync and async reads on the same stream cannot be mixed.");
        eof = false;

        var deadline = timeoutMs < 0 ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (true)
        {
            lock (gate)
            {
                if (bufferCount > 0)
                {
                    var n = Math.Min(bufferCount, target.Length);
                    buffer.AsSpan(bufferStart, n).CopyTo(target);
                    bufferStart += n;
                    bufferCount -= n;
                    if (bufferStart == buffer.Length)
                    {
                        buffer.AsSpan(0, bufferCount).CopyTo(buffer);
                        bufferStart = 0;
                    }
                    return n;
                }
                if (readerEof)
                {
                    eof = true;
                    return 0;
                }
            }

            // Nothing buffered yet: wait for the reader to deliver bytes (or EOF).
            var remainingMs = timeoutMs < 0
                ? Timeout.Infinite
                : (int)Math.Max(0, Math.Min((deadline - DateTime.UtcNow).TotalMilliseconds, int.MaxValue));
            if (remainingMs == 0)
                return 0; // timed out with nothing available
            if (!dataAvailable.WaitOne(remainingMs))
                return 0;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (target.IsEmpty)
            return new ValueTask<int>(0);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken));

        Interlocked.Increment(ref pendingAsyncReads);
        try
        {
            var waiter = new ReadOperation(this, target, cancellationToken);
            lock (gate)
            {
                if (bufferCount > 0 && TrySatisfyFromBuffer(waiter))
                    return new ValueTask<int>(waiter.Offset);
                readWaiters.Add(waiter);
            }

            cancellationToken.Register(static state =>
            {
                var w = (ReadOperation)state!;
                lock (w.Owner.gate)
                {
                    if (!w.Completed)
                    {
                        var found = false;
                        for (var i = 0; i < w.Owner.readWaiters.Count; i++)
                        {
                            if (ReferenceEquals(w.Owner.readWaiters[i], w))
                            {
                                w.Owner.readWaiters.RemoveAt(i);
                                found = true;
                                break;
                            }
                        }
                        if (found)
                        {
                            w.Completed = true;
                            w.Tcs.TrySetCanceled(w.Token);
                        }
                    }
                }
            }, waiter);

            return new ValueTask<int>(waiter.Tcs.Task);
        }
        finally
        {
            Interlocked.Decrement(ref pendingAsyncReads);
        }
    }

    // -------------------------------------------------------------------- write

    public override void Write(ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (source.IsEmpty)
            return;
        WriteCore(inputWrite, source, CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously writes all of <paramref name="source"/> via the per-stream writer
    /// thread. The bytes are copied eagerly (the caller's memory may be reused once the
    /// call returns). Cancellation mid-write stops after whatever the pipe consumed and
    /// throws <see cref="OperationCanceledException"/> — mirroring the Unix half.
    /// </summary>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (source.IsEmpty)
            return ValueTask.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromException(new OperationCanceledException(cancellationToken));

        var op = new WriteOperation(this, source.ToArray(), cancellationToken);
        writeChannel.Writer.TryWrite(op);
        return new ValueTask(op.Tcs.Task);
    }

    // ------------------------------------------------------------------ dispose

    protected override void Dispose(bool disposing)
    {
        if (disposed)
            return;
        disposed = true;

        if (disposing)
        {
            // ClosePseudoConsole terminates the attached process tree and closes the
            // console's channel; closing our output pipe then unblocks the reader's
            // blocked ReadFile (ERROR_BROKEN_PIPE), which is what ends the reader thread.
            // The writer thread ends once the channel completes and drains. No
            // thread-pool involvement anywhere.
            pseudoConsole.Dispose();
            writeChannel.Writer.TryComplete();
            inputWrite.Dispose();
            outputRead.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- read plumbing

    private sealed class ReadOperation
    {
        public readonly PtyStream Owner;
        public readonly Memory<byte> Buffer;
        public readonly CancellationToken Token;
        public readonly TaskCompletionSource<int> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Offset; // bytes already copied into Buffer by the reader
        public bool Completed;

        public ReadOperation(PtyStream owner, Memory<byte> buffer, CancellationToken token)
        {
            Owner = owner;
            Buffer = buffer;
            Token = token;
        }
    }

    private void ReadLoop()
    {
        var temp = new byte[PipeBufferSize];
        while (!disposed)
        {
            uint read = 0;
            bool ok;
            unsafe
            {
                fixed (byte* p = temp)
                {
                    ok = ReadFile((HANDLE)outputRead.DangerousGetHandle(), p, PipeBufferSize, &read, null);
                }
            }
            if (!ok || read == 0)
            {
                // ERROR_BROKEN_PIPE / ERROR_NO_DATA / ERROR_INVALID_HANDLE (or a clean 0-byte
                // read) all mean the channel is gone: the child exited and the pseudo console
                // closed, or the stream was disposed. That is EOF — the same 0 the Unix half
                // reports — so pending reads complete with 0 and sync reads see eof=true.
                lock (gate)
                {
                    readerEof = true;
                    dataAvailable.Set();
                    CompleteAllWaitersAsEof();
                }
                return;
            }

            lock (gate)
            {
                var span = temp.AsSpan(0, (int)read);
                // Satisfy pending async reads first (FIFO), then buffer the remainder.
                while (span.Length > 0 && readWaiters.Count > 0)
                {
                    var waiter = readWaiters[0];
                    var space = waiter.Buffer.Length - waiter.Offset;
                    if (space == 0)
                    {
                        readWaiters.RemoveAt(0);
                        continue;
                    }
                    var n = Math.Min(span.Length, space);
                    span[..n].CopyTo(waiter.Buffer.Span.Slice(waiter.Offset, n));
                    waiter.Offset += n;
                    span = span[n..];
                    if (waiter.Offset == waiter.Buffer.Length)
                    {
                        readWaiters.RemoveAt(0);
                        waiter.Completed = true;
                        waiter.Tcs.TrySetResult(waiter.Offset);
                    }
                }

                if (span.Length > 0)
                    AppendToBuffer(span);
            }
        }
    }

    /// <summary>Appends reader data to the sync-read buffer, dropping oldest bytes when full.</summary>
    private void AppendToBuffer(ReadOnlySpan<byte> data)
    {
        // Compact so the buffer is always linear at [bufferStart=0..].
        if (bufferStart > 0)
        {
            buffer.AsSpan(bufferStart, bufferCount).CopyTo(buffer);
            bufferStart = 0;
        }
        if (data.Length > buffer.Length)
            data = data[^buffer.Length..]; // keep only the newest bytes
        var overflow = data.Length - (buffer.Length - bufferCount);
        if (overflow > 0)
        {
            // Drop the oldest bytes to make room.
            buffer.AsSpan(overflow, bufferCount - overflow).CopyTo(buffer);
            bufferCount -= overflow;
        }
        data.CopyTo(buffer.AsSpan(bufferCount));
        bufferCount += data.Length;
        dataAvailable.Set();
    }

    private bool TrySatisfyFromBuffer(ReadOperation waiter)
    {
        if (bufferCount == 0)
            return false;
        var n = Math.Min(bufferCount, waiter.Buffer.Length);
        buffer.AsSpan(bufferStart, n).CopyTo(waiter.Buffer.Span);
        bufferStart += n;
        bufferCount -= n;
        if (bufferStart == buffer.Length)
        {
            buffer.AsSpan(0, bufferCount).CopyTo(buffer);
            bufferStart = 0;
        }
        waiter.Completed = true;
        waiter.Tcs.TrySetResult(n);
        return true;
    }

    private void CompleteAllWaitersAsEof()
    {
        for (var i = readWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = readWaiters[i];
            readWaiters.RemoveAt(i);
            if (!waiter.Completed)
            {
                waiter.Completed = true;
                waiter.Tcs.TrySetResult(waiter.Offset);
            }
        }
    }

    // ---------------------------------------------------------------- write plumbing

    private sealed class WriteOperation
    {
        public readonly PtyStream Owner;
        public readonly byte[] Data;
        public readonly CancellationToken Token;
        public readonly TaskCompletionSource<int> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOperation(PtyStream owner, byte[] data, CancellationToken token)
        {
            Owner = owner;
            Data = data;
            Token = token;
        }
    }

    private static void WriteLoop(object? state)
    {
        var reader = (ChannelReader<WriteOperation>)state!;
        while (reader.TryRead(out var op) || reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out op))
                ExecuteWrite(op);
        }
    }

    private static void ExecuteWrite(WriteOperation op)
    {
        // The stream may be disposed while a queued write is pending; surface a clean
        // error instead of crashing on closed handles.
        if (op.Owner.disposed)
        {
            op.Tcs.TrySetException(new ObjectDisposedException(nameof(PtyStream)));
            return;
        }
        try
        {
            WriteCore(op.Owner.inputWrite, op.Data, op.Token);
            op.Tcs.TrySetResult(op.Data.Length);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && op.Token.IsCancellationRequested)
                op.Tcs.TrySetCanceled(op.Token);
            else
                op.Tcs.TrySetException(ex);
        }
    }

    /// <summary>Blocking WriteFile loop until the whole buffer is written or cancellation.</summary>
    private static unsafe void WriteCore(SafeFileHandle handle, ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (data.IsEmpty)
            return;
        fixed (byte* p = data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                token.ThrowIfCancellationRequested();
                uint written;
                var ok = WriteFile((HANDLE)handle.DangerousGetHandle(), p + offset, (uint)(data.Length - offset), &written, null);
                if (!ok)
                    throw new IOException($"pty write failed: {new Win32Exception().Message}");
                if (written == 0)
                    throw new IOException("pty write failed: the child closed the terminal");
                offset += (int)written;
            }
        }
    }
}
#endif

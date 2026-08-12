#if WINDOWS
using System.IO.Pipes;
using Windows.Win32;

namespace Ghostflyby.Pty;

/// <summary>
/// Windows half of <see cref="PtyStream"/>. A single overlapped BCL read pump owns the
/// ConPTY output pipe and publishes bytes into a bounded managed buffer. User reads consume
/// only that buffer, so final-frame draining never races or steals bytes from callers.
/// </summary>
public sealed partial class PtyStream
{
    private const int ReadChunkSize = 16 * 1024;
    private const int MaxBufferedBytes = 1024 * 1024;

    private readonly NamedPipeClientStream inputWrite;
    private readonly NamedPipeClientStream outputRead;
    private readonly ClosePseudoConsoleSafeHandle pseudoConsole;
    private readonly CancellationTokenSource pumpCancellation = new();
    private readonly System.Threading.Lock readGate = new();
    private readonly Queue<BufferChunk> chunks = [];
    private readonly List<TaskCompletionSource<bool>> readSignals = [];
    private readonly List<TaskCompletionSource<bool>> spaceSignals = [];
    private readonly Task pumpTask;
    private readonly TaskCompletionSource<bool> consoleCloseCompletion = NewSignal();
    private int bufferedBytes;
    private int exitWaiters;
    private bool teardownStarted;
    private bool outputEof;
    private Exception? pumpError;
    private int consoleCloseStarted;
    private int disposed;

    /// <summary>Takes ownership of both parent pipe ends and the pseudo console.</summary>
    internal PtyStream(
        NamedPipeClientStream inputWrite,
        NamedPipeClientStream outputRead,
        ClosePseudoConsoleSafeHandle pseudoConsole)
    {
        this.inputWrite = inputWrite;
        this.outputRead = outputRead;
        this.pseudoConsole = pseudoConsole;
        pumpTask = PumpOutputAsync();
    }

    internal bool IsClosed => Volatile.Read(ref disposed) != 0;

    public override void Flush()
    {
        ThrowIfDisposed();
        inputWrite.Flush();
    }

    public override int Read(Span<byte> target)
    {
        return target.IsEmpty ? 0 : Read(target, Timeout.Infinite, out _);
    }

    /// <summary>
    /// Reads from the managed output buffer with the bounded-wait semantics used by
    /// <see cref="PtyProcess"/>. Zero is a non-blocking probe; negative waits indefinitely.
    /// </summary>
    internal int Read(Span<byte> target, int timeoutMs, out bool eof)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref pendingAsyncReads) > 0)
            throw new InvalidOperationException(
                "A pending async read is in progress on this pty stream; sync and async reads on the same stream cannot be mixed.");

        eof = false;
        if (target.IsEmpty)
            return 0;

        var deadline = timeoutMs < 0
            ? DateTime.MaxValue
            : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);

        while (true)
        {
            Task signal;
            lock (readGate)
            {
                var count = CopyBuffered(target);
                if (count > 0)
                    return count;
                if (outputEof)
                {
                    eof = true;
                    return 0;
                }
                if (pumpError is not null)
                    throw new IOException("ConPTY output pump failed.", pumpError);
                if (timeoutMs == 0)
                    return 0;

                var waiter = NewSignal();
                readSignals.Add(waiter);
                signal = waiter.Task;
            }

            var remaining = timeoutMs < 0
                ? Timeout.Infinite
                : (int)Math.Max(0, Math.Min((deadline - DateTime.UtcNow).TotalMilliseconds, int.MaxValue));
            if (remaining == 0 || !signal.Wait(remaining))
            {
                RemoveSignal(readSignals, signal);
                return 0;
            }
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> target,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (target.IsEmpty)
            return 0;

        Interlocked.Increment(ref pendingAsyncReads);
        try
        {
            while (true)
            {
                Task signal;
                lock (readGate)
                {
                    var count = CopyBuffered(target.Span);
                    if (count > 0)
                        return count;
                    if (outputEof)
                        return 0;
                    if (pumpError is not null)
                        throw new IOException("ConPTY output pump failed.", pumpError);

                    var waiter = NewSignal();
                    readSignals.Add(waiter);
                    signal = waiter.Task;
                }

                try
                {
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    RemoveSignal(readSignals, signal);
                    throw;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref pendingAsyncReads);
        }
    }

    public override void Write(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        if (!source.IsEmpty)
            inputWrite.Write(source);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return source.IsEmpty
            ? ValueTask.CompletedTask
            : inputWrite.WriteAsync(source, cancellationToken);
    }

    /// <summary>
    /// Called before an exit wait starts. While at least one waiter is active, the normal buffer
    /// bound is lifted so process exit cannot deadlock behind an application waiting before read.
    /// </summary>
    internal void EnterExitWait()
    {
        lock (readGate)
        {
            exitWaiters++;
            CompleteSignals(spaceSignals);
        }
    }

    /// <summary>Balances <see cref="EnterExitWait"/> when a wait returns, times out, or is canceled.</summary>
    internal void ExitExitWait()
    {
        lock (readGate)
        {
            if (exitWaiters > 0)
                exitWaiters--;
        }
    }

    /// <summary>
    /// Called by the reaper after the root process exits. The close and final drain continue on
    /// the thread pool so the single global reaper thread remains non-blocking.
    /// </summary>
    internal void NotifyProcessExited()
    {
        BeginTeardown();
        if (Interlocked.Exchange(ref consoleCloseStarted, 1) == 0)
            ThreadPool.QueueUserWorkItem(static state => ((PtyStream)state!).CloseConsoleAndDrain(), this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        BeginTeardown();
        if (Interlocked.Exchange(ref consoleCloseStarted, 1) == 0)
            CloseConsoleAndDrain();
        else
            consoleCloseCompletion.Task.GetAwaiter().GetResult();

        inputWrite.Dispose();
        outputRead.Dispose();
        pumpCancellation.Cancel();
        ObservePump(pumpTask);

        lock (readGate)
        {
            outputEof = true;
            CompleteSignals(readSignals);
            CompleteSignals(spaceSignals);
        }
        pumpCancellation.Dispose();
        base.Dispose(disposing);
    }

    private async Task PumpOutputAsync()
    {
        var buffer = new byte[ReadChunkSize];
        try
        {
            while (true)
            {
                await WaitForBufferSpaceAsync(pumpCancellation.Token).ConfigureAwait(false);
                var count = await outputRead.ReadAsync(buffer, pumpCancellation.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    PublishEof();
                    return;
                }

                var copy = new byte[count];
                buffer.AsSpan(0, count).CopyTo(copy);
                lock (readGate)
                {
                    chunks.Enqueue(new BufferChunk(copy));
                    bufferedBytes += count;
                    CompleteSignals(readSignals);
                }
            }
        }
        catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested)
        {
            PublishEof();
        }
        catch (ObjectDisposedException) when (IsClosed)
        {
            PublishEof();
        }
        catch (IOException) when (pseudoConsole.IsClosed || IsClosed)
        {
            PublishEof();
        }
        catch (Exception ex)
        {
            lock (readGate)
            {
                pumpError = ex;
                CompleteSignals(readSignals);
                CompleteSignals(spaceSignals);
            }
        }
    }

    private async Task WaitForBufferSpaceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (readGate)
            {
                if (teardownStarted || exitWaiters > 0 || bufferedBytes < MaxBufferedBytes)
                    return;
                var waiter = NewSignal();
                spaceSignals.Add(waiter);
                signal = waiter.Task;
            }

            try
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RemoveSignal(spaceSignals, signal);
                throw;
            }
        }
    }

    private void CloseConsoleAndDrain()
    {
        try
        {
            pseudoConsole.Dispose();
        }
        catch
        {
            // Root exit has already been recorded; a teardown error is surfaced to readers
            // only when the pump itself cannot publish buffered data or EOF.
        }
        finally
        {
            // On Windows 11 24H2+ ClosePseudoConsole returns immediately and the output
            // pipe is not guaranteed to deliver EOF on its own; cancel the pump so the
            // close is deterministic instead of relying on pipe EOF timing. (On older
            // Windows the final frame is drained inside the ClosePseudoConsole call above,
            // so canceling afterwards cannot truncate it.)
            pumpCancellation.Cancel();
            ObservePump(pumpTask);
            consoleCloseCompletion.TrySetResult(true);
        }
    }

    private int CopyBuffered(Span<byte> target)
    {
        var copied = 0;
        while (copied < target.Length && chunks.Count > 0)
        {
            var chunk = chunks.Peek();
            var count = Math.Min(target.Length - copied, chunk.Remaining);
            chunk.Data.AsSpan(chunk.Offset, count).CopyTo(target[copied..]);
            chunk.Offset += count;
            copied += count;
            bufferedBytes -= count;
            if (chunk.Remaining == 0)
                chunks.Dequeue();
        }
        if (copied > 0)
            CompleteSignals(spaceSignals);
        return copied;
    }

    private void BeginTeardown()
    {
        lock (readGate)
        {
            teardownStarted = true;
            CompleteSignals(spaceSignals);
        }
    }

    private void PublishEof()
    {
        lock (readGate)
        {
            outputEof = true;
            CompleteSignals(readSignals);
            CompleteSignals(spaceSignals);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RemoveSignal(List<TaskCompletionSource<bool>> signals, Task signal)
    {
        lock (readGate)
        {
            for (var i = signals.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(signals[i].Task, signal))
                {
                    signals.RemoveAt(i);
                    break;
                }
            }
        }
    }

    private static void CompleteSignals(List<TaskCompletionSource<bool>> signals)
    {
        foreach (var signal in signals)
            signal.TrySetResult(true);
        signals.Clear();
    }

    private static void ObservePump(Task pump)
    {
        try
        {
            pump.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
    }

    private sealed class BufferChunk(byte[] data)
    {
        internal byte[] Data { get; } = data;
        internal int Offset { get; set; }
        internal int Remaining => Data.Length - Offset;
    }
}
#endif

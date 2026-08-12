#if WINDOWS
using System.Buffers;
using System.IO.Pipes;
using Windows.Win32;

namespace Ghostflyby.Pty;

/// <summary>
/// Windows half of <see cref="PtyStream"/>. ConPTY receives synchronous server handles,
/// while these parent-side client streams were opened with <see cref="PipeOptions.Asynchronous"/>.
/// The BCL therefore provides overlapped, cancellable I/O without native read/write calls or
/// per-session worker threads.
/// </summary>
public sealed partial class PtyStream
{
    private readonly NamedPipeClientStream inputWrite;
    private readonly NamedPipeClientStream outputRead;
    private readonly ClosePseudoConsoleSafeHandle pseudoConsole;
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
    /// Reads with the bounded-wait semantics used by <see cref="PtyProcess"/>. A zero timeout
    /// is a genuine non-blocking probe; a negative timeout waits indefinitely. A return value
    /// of zero means timeout when <paramref name="eof"/> is false, or end-of-stream when true.
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

        if (timeoutMs < 0)
        {
            var read = outputRead.Read(target);
            eof = read == 0;
            return read;
        }

        var rented = ArrayPool<byte>.Shared.Rent(target.Length);
        try
        {
            using var timeout = new CancellationTokenSource();
            if (timeoutMs > 0)
                timeout.CancelAfter(timeoutMs);

            var read = outputRead.ReadAsync(rented.AsMemory(0, target.Length), timeout.Token);
            if (timeoutMs == 0 && !read.IsCompleted)
                timeout.Cancel();

            int count;
            try
            {
                count = read.IsCompletedSuccessfully
                    ? read.Result
                    : read.AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return 0;
            }

            rented.AsSpan(0, count).CopyTo(target);
            eof = count == 0;
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
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
            return await outputRead.ReadAsync(target, cancellationToken).ConfigureAwait(false);
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

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        Task? drain = null;
        try
        {
            // ClosePseudoConsole can emit a final frame and wait for its output channel to
            // drain. Keep an overlapped BCL read active before closing it, then abort that
            // read by closing our pipe if EOF did not already complete it.
            drain = outputRead.CopyToAsync(Stream.Null);
            pseudoConsole.Dispose();
        }
        finally
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            ObserveTeardownDrain(drain);
            base.Dispose(disposing);
        }
    }

    private static void ObserveTeardownDrain(Task? drain)
    {
        if (drain is null)
            return;

        try
        {
            drain.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Disposing an async pipe cancels an outstanding overlapped read.
        }
        catch (ObjectDisposedException)
        {
            // The output client is deliberately closed after the pseudo console.
        }
        catch (IOException)
        {
            // A broken/aborted pipe is the expected terminal state during teardown.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
    }
}
#endif

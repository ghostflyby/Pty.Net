using System.IO.Pipes;
using Windows.Win32;

namespace Ghostflyby.Pty;

// Windows half of PtyStream: a single overlapped BCL read pump owns the ConPTY output
// pipe and publishes bytes into a bounded managed buffer. User reads consume only that
// buffer, so final-frame draining never races or steals bytes from callers. Windows-only:
// compiled only by the Windows target (see csproj).
public sealed partial class PtyStream
{
    private const int ReadChunkSize = 16 * 1024;
    private const int MaxBufferedBytes = 1024 * 1024;
    // Bounded window for the pump to drain the ConPTY final frame after ClosePseudoConsole
    // returns (see CloseConsoleAndDrain).
    private const int CloseDrainGraceMs = 1000;

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

    /// <summary>There is no user-space buffering, so this does nothing beyond checking the stream is open.</summary>
    public override void Flush()
    {
        ThrowIfDisposed();
        inputWrite.Flush();
    }

    /// <summary>
    /// Reads up to <paramref name="target"/>.Length bytes, blocking until at least one
    /// byte is available.
    /// <br/>
    /// Returns the number of bytes actually read (not necessarily the buffer length),
    /// or 0 at end of stream.
    /// </summary>
    /// <param name="target">The buffer to fill.</param>
    /// <returns>The number of bytes read, or 0 at end of stream.</returns>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
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
                // A Dispose may have completed since the entry check. BCL Process
                // semantics: reads pending at close throw ObjectDisposedException —
                // they do not deliver buffered bytes or an artificial EOF.
                ThrowIfDisposed();
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

    /// <summary>
    /// Asynchronously reads up to <paramref name="target"/>.Length bytes, completing as
    /// soon as data is available.
    /// <br/>
    /// Returns the number of bytes read, or 0 at end of stream.
    /// </summary>
    /// <param name="target">The buffer to fill.</param>
    /// <param name="cancellationToken">Canceled to abort the pending read.</param>
    /// <returns>The number of bytes read, or 0 at end of stream.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before any data was read.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed while the read is pending.</exception>
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
                    // BCL Process semantics: reads pending at close throw
                    // ObjectDisposedException instead of returning buffered bytes.
                    ThrowIfDisposed();
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

    /// <summary>
    /// Writes all of <paramref name="source"/>, blocking as needed while the child
    /// drains the terminal.
    /// </summary>
    /// <param name="source">The bytes to write.</param>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override void Write(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        if (!source.IsEmpty)
            inputWrite.Write(source);
    }

    /// <summary>
    /// Asynchronously writes all of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The bytes to write.</param>
    /// <param name="cancellationToken">Canceled to abort the write.</param>
    /// <returns>A task that completes when all bytes have been written.</returns>
    /// <exception cref="OperationCanceledException">The write was canceled before it completed.</exception>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
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

    /// <summary>Releases the stream's ConPTY channels, aborting any in-flight operations first.</summary>
    /// <param name="disposing">True when called from user code (not the finalizer).</param>
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
            // Wake parked readers: they re-check the disposed flag in their loops and
            // complete with ObjectDisposedException (BCL Process semantics) rather than
            // being handed buffered bytes or an artificial EOF.
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
            // pipe is not guaranteed to deliver EOF on its own, so the close must not
            // depend on pipe EOF timing. On older Windows the final frame is drained
            // inside the ClosePseudoConsole call above — but it still sits in the pipe
            // awaiting the pump's pending ReadAsync, and canceling immediately can abort
            // that read and truncate the tail. Give the pump a bounded window to observe
            // pipe EOF (or read the last bytes) first, then cancel for the 24H2 case.
            // A faulted pump is covered too: Wait rethrows, and the finally below still
            // observes it (ObservePump swallows the failure) and signals completion.
            try
            {
                if (!pumpTask.Wait(CloseDrainGraceMs))
                    pumpCancellation.Cancel();
            }
            finally
            {
                ObservePump(pumpTask);
                consoleCloseCompletion.TrySetResult(true);
            }
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

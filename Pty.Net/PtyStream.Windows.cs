using System.Buffers;
using System.ComponentModel;
using System.IO.Pipes;
using Windows.Win32;
using Windows.Win32.System.Console;

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

    private readonly NamedPipeClientStream _inputWrite;
    private readonly NamedPipeClientStream _outputRead;
    private readonly ClosePseudoConsoleSafeHandle _pseudoConsole;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly System.Threading.Lock _readGate = new();
    private readonly Queue<BufferChunk> _chunks = [];
    // One-shot wakeup gates (AsyncManualResetEvent-like) for waiters of the buffered
    // data and the buffer space respectively. Per-cycle TCS instead of per-waiter:
    // a burst of N blocked readers shares a single task, and timed-out/canceled
    // waiters need no bookkeeping.
    private readonly SignalGate _dataGate = new();
    private readonly SignalGate _spaceGate = new();
    private readonly Task _pumpTask;
    private readonly TaskCompletionSource<bool> _consoleCloseCompletion = NewSignal();
    private int _bufferedBytes;
    private int _exitWaiters;
    private bool _teardownStarted;
    private bool _outputEof;
    private Exception? _pumpError;
    private int _consoleCloseStarted;
    private int _disposed;

    /// <summary>Takes ownership of both parent pipe ends and the pseudo console.</summary>
    internal PtyStream(
        NamedPipeClientStream _inputWrite,
        NamedPipeClientStream _outputRead,
        ClosePseudoConsoleSafeHandle _pseudoConsole)
    {
        this._inputWrite = _inputWrite;
        this._outputRead = _outputRead;
        this._pseudoConsole = _pseudoConsole;
        _pumpTask = PumpOutputAsync();
    }

    internal bool IsClosed => Volatile.Read(ref _disposed) != 0;

    /// <summary>There is no user-space buffering, so this does nothing beyond checking the stream is open.</summary>
    public override void Flush()
    {
        ThrowIfDisposed();
        _inputWrite.Flush();
    }

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes, blocking until at least one
    /// byte is available.
    /// <br/>
    /// Returns the number of bytes actually read (not necessarily the buffer length),
    /// or 0 at end of stream.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>The number of bytes read, or 0 at end of stream.</returns>
    /// <exception cref="InvalidOperationException">A pending async read is in progress on this stream; sync and async reads cannot be mixed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override int Read(Span<byte> buffer)
    {
        return buffer.IsEmpty ? 0 : Read(buffer, Timeout.Infinite);
    }

    /// <summary>
    /// Reads from the managed output buffer with the bounded-wait semantics used by
    /// <see cref="PtyProcess"/>. Zero is a non-blocking probe; negative waits indefinitely.
    /// Returns 0 at end of stream (output EOF) and when the timeout elapses with no data.
    /// </summary>
    private int Read(Span<byte> target, int timeoutMs)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _pendingAsyncReads) > 0)
            throw new InvalidOperationException(
                "A pending async read is in progress on this pty stream; sync and async reads on the same stream cannot be mixed.");

        if (target.IsEmpty)
            return 0;

        var deadline = timeoutMs < 0
            ? DateTime.MaxValue
            : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);

        while (true)
        {
            Task signal;
            lock (_readGate)
            {
                // A Dispose may have completed since the entry check. BCL Process
                // semantics: reads pending at close throw ObjectDisposedException —
                // they do not deliver buffered bytes or an artificial EOF.
                ThrowIfDisposed();
                var count = CopyBuffered(target);
                if (count > 0)
                    return count;
                if (_outputEof)
                    return 0;
                if (_pumpError is not null)
                    throw new IOException("ConPTY output pump failed.", _pumpError);
                if (timeoutMs == 0)
                    return 0;

                signal = _dataGate.GetWaitTask();
            }

            var remaining = timeoutMs < 0
                ? Timeout.Infinite
                : (int)Math.Max(0, Math.Min((deadline - DateTime.UtcNow).TotalMilliseconds, int.MaxValue));
            // A timeout just abandons this task: the gate stays armed for the next
            // waiter, and the first completion resets it. No unregistration needed.
            if (remaining == 0 || !signal.Wait(remaining))
                return 0;
        }
    }

    /// <summary>
    /// Asynchronously reads up to <paramref name="buffer"/>.Length bytes, completing as
    /// soon as data is available.
    /// <br/>
    /// Returns the number of bytes read, or 0 at end of stream.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="cancellationToken">Canceled to abort the pending read.</param>
    /// <returns>The number of bytes read, or 0 at end of stream.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled before any data was read.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed while the read is pending.</exception>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
            return 0;

        Interlocked.Increment(ref _pendingAsyncReads);
        try
        {
            while (true)
            {
                Task signal;
                lock (_readGate)
                {
                    // BCL Process semantics: reads pending at close throw
                    // ObjectDisposedException instead of returning buffered bytes.
                    ThrowIfDisposed();
                    var count = CopyBuffered(buffer.Span);
                    if (count > 0)
                        return count;
                    if (_outputEof)
                        return 0;
                    if (_pumpError is not null)
                        throw new IOException("ConPTY output pump failed.", _pumpError);

                    signal = _dataGate.GetWaitTask();
                }

                // Cancellation just abandons this task: the gate stays armed for the
                // next waiter, and the first completion resets it. No unregistration
                // needed, so the read cannot race the cancel/complete bookkeeping.
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingAsyncReads);
        }
    }

    /// <summary>
    /// Writes all of <paramref name="buffer"/>, blocking as needed while the child
    /// drains the terminal.
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (!buffer.IsEmpty)
            _inputWrite.Write(buffer);
    }

    /// <summary>
    /// Asynchronously writes all of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="cancellationToken">Canceled to abort the write.</param>
    /// <returns>A task that completes when all bytes have been written.</returns>
    /// <exception cref="OperationCanceledException">The write was canceled before it completed.</exception>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return buffer.IsEmpty
            ? ValueTask.CompletedTask
            : _inputWrite.WriteAsync(buffer, cancellationToken);
    }

    // ------------------------------------------------------------ window size

    /// <summary>
    /// Sets the pseudo console's window size in character cells. ConPTY propagates the
    /// new size to the attached client, which re-layouts its screen buffer.
    /// </summary>
    /// <param name="columns">Number of character columns.</param>
    /// <param name="rows">Number of character rows.</param>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    /// <exception cref="Win32Exception">ResizePseudoConsole failed.</exception>
    internal void SetWindowSize(int columns, int rows)
    {
        ThrowIfDisposed();
        // The CsWin32 friendly overload keeps the SafeHandle add-ref'd for the call;
        // COORD fields are short, so values are validated as ushort-range by the caller.
        var hr = PInvoke.ResizePseudoConsole(
            _pseudoConsole, new COORD { X = (short)columns, Y = (short)rows });
        if (hr.Failed)
            throw new Win32Exception(hr.Value, $"ResizePseudoConsole failed: {hr}");
    }

    /// <summary>
    /// Called before an exit wait starts. While at least one waiter is active, the normal buffer
    /// bound is lifted so process exit cannot deadlock behind an application waiting before read.
    /// </summary>
    internal void EnterExitWait()
    {
        lock (_readGate)
        {
            _exitWaiters++;
            _spaceGate.Set();
        }
    }

    /// <summary>Balances <see cref="EnterExitWait"/> when a wait returns, times out, or is canceled.</summary>
    internal void ExitExitWait()
    {
        lock (_readGate)
        {
            if (_exitWaiters > 0)
                _exitWaiters--;
        }
    }

    /// <summary>
    /// Initiates the pseudo-console close on a thread-pool thread without waiting: it
    /// sends CTRL_CLOSE_EVENT to the attached clients (the Windows analog of SIGHUP), so
    /// a still-alive child gets a chance to exit cleanly, and then drains the final
    /// frame. Used by the dispose grace window; the reaper also calls it once the child
    /// exits. Idempotent: <see cref="consoleCloseStarted"/> ensures the close runs
    /// exactly once, whichever path gets there first.
    /// </summary>
    internal void BeginAsyncClose()
    {
        BeginTeardown();
        if (Interlocked.Exchange(ref _consoleCloseStarted, 1) == 0)
            ThreadPool.QueueUserWorkItem(static state => ((PtyStream)state!).CloseConsoleAndDrain(), this);
    }

    /// <summary>
    /// Called by the reaper after the root process exits. The close and final drain continue on
    /// the thread pool so the single global reaper thread remains non-blocking.
    /// </summary>
    internal void NotifyProcessExited()
    {
        BeginAsyncClose();
    }

    /// <summary>Releases the stream's ConPTY channels, aborting any in-flight operations first.</summary>
    /// <param name="disposing">True when called from user code (not the finalizer).</param>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        BeginTeardown();
        if (Interlocked.Exchange(ref _consoleCloseStarted, 1) == 0)
            CloseConsoleAndDrain();
        else
            _consoleCloseCompletion.Task.GetAwaiter().GetResult();

        _inputWrite.Dispose();
        _outputRead.Dispose();
        _pumpCancellation.Cancel();
        ObservePump(_pumpTask);

        lock (_readGate)
        {
            // The pump has stopped by now (CloseConsoleAndDrain waited for or canceled
            // it), so the queue holds every un-consumed chunk: return them to the pool.
            while (_chunks.Count > 0)
                ArrayPool<byte>.Shared.Return(_chunks.Dequeue().Data);

            // Wake parked readers: they re-check the disposed flag in their loops and
            // complete with ObjectDisposedException (BCL Process semantics) rather than
            // being handed buffered bytes or an artificial EOF.
            _dataGate.Set();
            _spaceGate.Set();
        }
        _pumpCancellation.Dispose();
        base.Dispose(disposing);
    }

    private async Task PumpOutputAsync()
    {
        try
        {
            while (true)
            {
                await WaitForBufferSpaceAsync(_pumpCancellation.Token).ConfigureAwait(false);

                // Rented from the shared pool for the chunk's lifetime and returned once
                // it is fully consumed (CopyBuffered) or the stream is disposed: no fresh
                // exact-size array per read chunk and no second copy.
                var chunk = ArrayPool<byte>.Shared.Rent(ReadChunkSize);
                int count;
                try
                {
                    count = await _outputRead.ReadAsync(chunk.AsMemory(0, ReadChunkSize), _pumpCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                    throw;
                }

                if (count == 0)
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                    PublishEof();
                    return;
                }

                lock (_readGate)
                {
                    _chunks.Enqueue(new BufferChunk(chunk, count));
                    _bufferedBytes += count;
                    _dataGate.Set();
                }
            }
        }
        catch (OperationCanceledException) when (_pumpCancellation.IsCancellationRequested)
        {
            PublishEof();
        }
        catch (ObjectDisposedException) when (IsClosed)
        {
            PublishEof();
        }
        catch (IOException) when (_pseudoConsole.IsClosed || IsClosed)
        {
            PublishEof();
        }
        catch (Exception ex)
        {
            lock (_readGate)
            {
                _pumpError = ex;
                _dataGate.Set();
                _spaceGate.Set();
            }
        }
    }

    private async Task WaitForBufferSpaceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_readGate)
            {
                if (_teardownStarted || _exitWaiters > 0 || _bufferedBytes < MaxBufferedBytes)
                    return;
                signal = _spaceGate.GetWaitTask();
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void CloseConsoleAndDrain()
    {
        try
        {
            _pseudoConsole.Dispose();
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
                if (!_pumpTask.Wait(CloseDrainGraceMs))
                    _pumpCancellation.Cancel();
            }
            finally
            {
                ObservePump(_pumpTask);
                _consoleCloseCompletion.TrySetResult(true);
            }
        }
    }

    private int CopyBuffered(Span<byte> target)
    {
        var copied = 0;
        while (copied < target.Length && _chunks.Count > 0)
        {
            var chunk = _chunks.Peek();
            var count = Math.Min(target.Length - copied, chunk.Remaining);
            chunk.Data.AsSpan(chunk.Offset, count).CopyTo(target[copied..]);
            chunk.Offset += count;
            copied += count;
            _bufferedBytes -= count;
            if (chunk.Remaining == 0)
            {
                _chunks.Dequeue();
                // The chunk's rented buffer is fully consumed: give it back to the pool.
                ArrayPool<byte>.Shared.Return(chunk.Data);
            }
        }
        if (copied > 0)
            _spaceGate.Set();
        return copied;
    }

    private void BeginTeardown()
    {
        lock (_readGate)
        {
            _teardownStarted = true;
            _spaceGate.Set();
        }
    }

    private void PublishEof()
    {
        lock (_readGate)
        {
            _outputEof = true;
            _dataGate.Set();
            _spaceGate.Set();
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// One-shot wakeup gate for a class of waiters (buffered data / buffer space),
    /// like a reset-on-read AsyncManualResetEvent. All access happens under
    /// <see cref="readGate"/>, so the predicate check, the task capture and the reset
    /// are atomic with the publisher's state change (which precedes
    /// <see cref="Set"/>): a waiter that captured the task before the state change is
    /// woken by <see cref="Set"/>, one that captures after it sees the state directly —
    /// a signal can never be lost.
    /// </summary>
    private sealed class SignalGate
    {
        private TaskCompletionSource<bool>? _tcs;

        /// <summary>
        /// The task to await for the next signal. All waiters active during one signal
        /// cycle share a single completion source, so a burst of N blocked readers costs
        /// one TCS, not one per waiter; a timed-out or canceled waiter simply abandons
        /// the task — the gate stays armed for the next waiter.
        /// </summary>
        public Task GetWaitTask()
        {
            _tcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _tcs.Task;
        }

        /// <summary>Completes all current waiters and disarms the gate for the next cycle.</summary>
        public void Set()
        {
            _tcs?.TrySetResult(true);
            _tcs = null;
        }
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

    /// <summary>
    /// One buffered read chunk from the pump. <see cref="Data"/> is a buffer rented from
    /// <see cref="ArrayPool{T}.Shared"/> and is returned to the pool once the chunk is
    /// fully consumed (or the stream is disposed); <see cref="Length"/> is the number of
    /// live bytes, which for a pooled buffer may be less than <see cref="Data"/>.Length.
    /// </summary>
    private sealed class BufferChunk(byte[] data, int length)
    {
        internal byte[] Data { get; } = data;
        internal int Length { get; } = length;
        internal int Offset { get; set; }
        internal int Remaining => Length - Offset;
    }
}

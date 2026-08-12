using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// Process-wide helper that services <see cref="PtyStream"/> async reads/writes on a
/// single background thread instead of the thread pool. On Unix, .NET's thread pool
/// executes async file I/O as blocking syscalls on pool threads and cancellation cannot
/// free a thread stuck in a blocking read(2)/write(2) on a character device — enough
/// blocked PTYs starve the pool. This engine drives the master fds with poll(2), so a
/// pending operation holds no thread at all and cancels in O(1).
///
/// Model: one lazily-started background thread runs a poll(2) loop over every active
/// fd plus a self-pipe. A registration/unregistration is just a control message pushed
/// through a channel followed by one byte on the pipe (the pipe interrupts poll; the
/// channel carries the message). Only the engine thread touches its per-fd bookkeeping,
/// so cancellation (which must race a completed read) is serialized: if the engine
/// already dispatched the I/O, the read result wins; otherwise the registration is
/// removed and the task completes canceled. Either way the outcome is deterministic.
///
/// The master fds are non-blocking (opened via <c>posix_openpt(O_NONBLOCK)</c>), so a
/// syscall never blocks: a read returns EAGAIN when no data is available, and we simply
/// stay registered; a write returns EAGAIN when the pty buffer is full, and we resume on
/// the next POLLOUT. The engine poll uses a finite timeout as a safety net so a lost
/// wakeup can never deadlock the loop.
///
/// Operations are tracked per fd <em>and direction</em>: a pending read and a pending
/// write on the same stream are polled in parallel (POLLIN vs POLLOUT), while overlapping
/// calls of the same direction serialize on a wait queue rather than corrupting each other.
///
/// The engine keeps each registered <see cref="SafeFileHandle"/> alive with
/// DangerousAddRef for the whole time its fd is polled, and holds the caller's buffer
/// pinned. Both are released on completion or cancellation, before the task completes.
/// SafeHandle.Dispose() also defers the actual close until these refs are released, so
/// PtyStream.Dispose can never close an fd that is still in the poll set (which would
/// let a reused fd number be polled for the wrong file).
///
/// Unix-only: compiled only by the non-Windows target (see csproj); Windows uses BCL
/// pipe I/O through IOCP instead.
/// </summary>
internal static class PtyIoEngine
{
    // Safety net for the poll loop: with a finite timeout, a lost wakeup costs at most
    // this much latency instead of a permanent hang.
    private const int PollSafetyTimeoutMs = 500;

    private const int Eintr = NativeMethods.Eintr;
    private const int Eagain = NativeMethods.Eagain;
    private const int Eio = NativeMethods.Eio;

    // Lazily started on first use and never stopped: a process-lifetime singleton, so
    // repeated PtyStream/Dispose cycles do not churn threads. Lazy<T> provides the
    // thread-safe, exactly-once initialization without a manual lock.
    private static readonly Lazy<IoThread> Thread = new(IoThread.Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Queues an async read; the returned task completes when the I/O or its cancellation does.</summary>
    public static Task<int> ReadAsync(SafeFileHandle handle, Memory<byte> buffer, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<int>(ct);
        return buffer.IsEmpty ? Task.FromResult(0) : Enqueue(handle, OperationKind.Read, buffer, ct);
    }

    /// <summary>Queues an async write; the returned task completes when the I/O or its cancellation does.</summary>
    public static Task<int> WriteAsync(SafeFileHandle handle, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<int>(ct);
        return buffer.IsEmpty ? Task.FromResult(0) : Enqueue(handle, OperationKind.Write, buffer, ct);
    }

    /// <summary>Removes every pending operation for the handle; used by PtyStream.Dispose.</summary>
    public static void AbortHandle(SafeFileHandle handle)
    {
        // If the engine never started (sync-only usage), there is nothing in flight and
        // nothing to abort — forcing the singleton thread to spawn just for this would
        // leak a process-lifetime background thread.
        if (!Thread.IsValueCreated)
            return;
        Thread.Value.Post(new Control(ControlKind.CancelHandle, handle: handle));
    }

    private static void EnsureStarted() => _ = Thread.Value;

    private static Task<int> Enqueue(SafeFileHandle handle, OperationKind kind, ReadOnlyMemory<byte> buffer,
        CancellationToken ct)
    {
        EnsureStarted();
        var op = new Operation
        {
            Owner = Thread.Value,
            Handle = handle,
            Kind = kind,
            Tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
            Buffer = buffer,
            Count = buffer.Length,
            Token = ct,
        };
        Thread.Value.Post(new Control(ControlKind.Register, op));
        return op.Tcs.Task;
    }

    private enum OperationKind : byte
    {
        Read,
        Write,
    }

    private enum ControlKind : byte
    {
        Register,
        Cancel,
        CancelHandle,
    }

    private enum OpStatus : byte
    {
        Succeeded,
        Failed,
        Canceled,
    }

    /// <summary>
    /// Transport message for the engine's inbox. A value type: the message is written
    /// once, read once, and never aliased or identity-compared, so storing it by value
    /// in the channel (no boxing) avoids a heap allocation per async operation.
    /// </summary>
    private readonly struct Control(ControlKind kind, Operation? op = null, SafeFileHandle? handle = null)
    {
        public readonly ControlKind Kind = kind;
        public readonly Operation? Op = op;
        public readonly SafeFileHandle? Handle = handle;
    }

    /// <summary>
    /// Per-(fd, direction) registration state: the polled operation plus any overlapping
    /// ones that are waiting their turn. Created by the engine thread; the wait queue
    /// serializes concurrent async calls of the same direction on the same stream, while
    /// reads and writes on the same fd stay independent (two slots per fd).
    /// </summary>
    private sealed class Slot
    {
        public Operation? Current;
        public readonly Queue<Operation> Waiters = new();
    }

    /// <summary>
    /// Engine-side state for one registered operation. Created by the caller thread in
    /// <see cref="Enqueue"/>; all remaining fields are owned by the engine thread.
    /// </summary>
    private sealed class Operation
    {
        public required IoThread Owner;
        public required SafeFileHandle Handle;
        public required OperationKind Kind;
        public required TaskCompletionSource<int> Tcs;
        public required ReadOnlyMemory<byte> Buffer;
        public required int Count;
        public required CancellationToken Token;

        // Engine thread only.
        public Slot? Slot;
        public int Fd;
        public MemoryHandle Pin;
        public IntPtr Pointer;
        public int Offset;
        public bool AddRefAdded;
        public CancellationTokenRegistration CtReg;
        public bool Done;

        /// <summary>Called from the cancellation callback (any thread): wakes the engine to cancel this op.</summary>
        public void PostCancelRequest() => Owner.PostCancel(this);
    }

    private sealed class IoThread
    {
        // Waiters hold no thread while pending, so the queue only carries transient
        // control messages and is effectively unbounded in practice.
        private readonly Channel<Control> inbox = Channel.CreateUnbounded<Control>(
            new UnboundedChannelOptions
                { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

        // Self-pipe: [0] read end (engine only), [1] write end (any thread). Both ends
        // stay blocking; the drain uses poll-before-read and Post uses poll-before-write,
        // so neither can ever block. The engine poll's finite timeout is the backstop.
        private readonly int wakeRead;
        private readonly int wakeWrite;

        // Engine thread only.
        private readonly Dictionary<(int Fd, OperationKind Kind), Slot> slots = [];

        // Handles that PtyStream.Dispose has torn down (via AbortHandle). AbortHandle
        // queues a CancelHandle message, but a concurrent Enqueue may deliver its
        // Register message *after* CancelHandle has already scanned the slots — without
        // this marker that op would then be registered with no one left to cancel it.
        // A ConditionalWeakTable keeps the marker without keeping the handle alive.
        private readonly ConditionalWeakTable<SafeFileHandle, object> canceledHandles = new();
        private readonly byte[] wakeBuffer = new byte[64];
        private NativeMethods.PollFd[] pollFds = new NativeMethods.PollFd[64];
        private int pollCount;
        private readonly IntPtr wakeBufPtr;

        private IoThread(int wakeRead, int wakeWrite)
        {
            this.wakeRead = wakeRead;
            this.wakeWrite = wakeWrite;
            var wakePin1 = wakeBuffer.AsMemory().Pin();
            unsafe
            {
                wakeBufPtr = (IntPtr)wakePin1.Pointer;
            }
        }

        public static IoThread Start()
        {
            var fds = new int[2];
            if (NativeMethods.pipe(fds) != 0)
                throw new IOException($"pipe failed: errno={Marshal.GetLastPInvokeError()}");

            var thread = new IoThread(fds[0], fds[1]);
            var t = new Thread(thread.Loop) { IsBackground = true, Name = "Pty.Net-io" };
            t.Start();
            return thread;
        }

        /// <summary>Queues a control message and interrupts the poll loop with a self-pipe byte.</summary>
        public void Post(Control c)
        {
            inbox.Writer.TryWrite(c);
            Wake();
        }

        internal void PostCancel(Operation op) => Post(new Control(ControlKind.Cancel, op));

        /// <summary>
        /// Writes one byte to the wake pipe. The write is gated by poll(POLLOUT) and the
        /// engine is the only reader of this pipe, so there is always room by the time
        /// poll returns — the write cannot block.
        /// </summary>
        private void Wake()
        {
            // A collection expression is fine here: only the poll return value matters,
            // the copied Revents is never read back.
            var pollFd = new NativeMethods.PollFd { Fd = wakeWrite, Events = NativeMethods.PollEvents.Pollout };
            int r;
            do
            {
                r = NativeMethods.poll([pollFd], -1);
            } while (r < 0 && Marshal.GetLastPInvokeError() == Eintr);

            if (r > 0)
                _ = NativeMethods.write(wakeWrite, wakeBufPtr, 1);
        }

        // ---------------------------------------------------------------- loop

        private void Loop()
        {
            // An unhandled exception here fails the process fast with the poll error
            // rather than silently leaving every async operation hung forever.
            LoopCore();
        }

        private void LoopCore()
        {
            while (true)
            {
                DrainInbox();
                BuildPollSet();

                int r;
                do
                {
                    // The poll set is a reused field: BuildPollSet fills only
                    // [0, pollCount), so poll exactly that range — passing the whole
                    // array would poll stale entries left over from a larger previous
                    // iteration (closed or reused fds), corrupting results and latency.
                    r = NativeMethods.poll(pollFds.AsSpan(0, pollCount), PollSafetyTimeoutMs);
                } while (r < 0 && Marshal.GetLastPInvokeError() == Eintr);

                if (r < 0)
                    throw new IOException($"Pty.Net poll failed: errno={Marshal.GetLastPInvokeError()}");

                if (pollCount > 0 && (pollFds[0].Revents & NativeMethods.PollEvents.Pollin) != 0)
                    DrainWakePipe();

                for (var i = 1; i < pollCount; i++)
                {
                    var revents = pollFds[i].Revents;
                    if (revents == 0)
                        continue;
                    // Each poll entry was built for one (fd, direction) slot; the Events
                    // field (untouched by poll) identifies which one. The slot may have
                    // been completed (and replaced) earlier in this same iteration; a
                    // stale entry then simply doesn't match anymore.
                    var kind = (pollFds[i].Events & NativeMethods.PollEvents.Pollin) != 0
                        ? OperationKind.Read
                        : OperationKind.Write;
                    if (!slots.TryGetValue((pollFds[i].Fd, kind), out var slot) ||
                        slot.Current is not { Done: false } op)
                        continue;
                    Dispatch(op, revents);
                }
            }
        }

        private void DrainInbox()
        {
            while (inbox.Reader.TryRead(out var c))
            {
                switch (c.Kind)
                {
                    case ControlKind.Register: Register(c.Op!); break;
                    case ControlKind.Cancel: Cancel(c.Op!); break;
                    case ControlKind.CancelHandle: CancelHandle(c.Handle!); break;
                }
            }
        }

        // ----------------------------------------------------------- registration

        private void Register(Operation op)
        {
            if (op.Done)
                return; // canceled before the engine picked up the registration

            try
            {
                op.Handle.DangerousAddRef(ref op.AddRefAdded);
                if (!op.AddRefAdded)
                {
                    // Handle already closed: the stream was disposed before the engine
                    // got here. Surface it as the stream being gone.
                    op.Done = true;
                    op.Tcs.TrySetException(
                        new ObjectDisposedException("The pty stream was disposed before the operation started."));
                    return;
                }

                // AbortHandle may have been processed before this Register: the handle
                // is already torn down and no Abort will ever arrive for this op.
                // (AddRef above still succeeded because the actual close is deferred
                // while other ops hold refs, so IsClosed alone cannot detect this.)
                if (canceledHandles.TryGetValue(op.Handle, out _))
                {
                    Complete(op, OpStatus.Failed, 0,
                        new ObjectDisposedException("PtyStream", "The pty stream was disposed."));
                    return;
                }

                // Fd is captured only now, under the AddRef: a raw value read earlier
                // could have been reused by the OS if the handle closed in between.
                op.Fd = (int)op.Handle.DangerousGetHandle();
                op.Pin = op.Buffer.Pin();
                unsafe
                {
                    op.Pointer = (IntPtr)op.Pin.Pointer;
                }

                op.Offset = 0;

                // One slot per (fd, direction): a pending read never blocks a write on
                // the same stream (and vice versa).
                var key = (op.Fd, op.Kind);
                if (!slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot();
                    slots[key] = slot;
                }

                op.Slot = slot;
                if (slot.Current is null)
                    slot.Current = op;
                else
                    slot.Waiters.Enqueue(op);

                if (op.Token.CanBeCanceled)
                    op.CtReg = op.Token.Register(static s => ((Operation)s!).PostCancelRequest(), op);
            }
            catch (Exception ex)
            {
                Complete(op, OpStatus.Failed, 0, ex);
            }
        }

        private void Cancel(Operation op)
        {
            if (op.Done)
                return;
            if (op.Fd == 0)
            {
                // Never made it into a slot (its Register message is still in the
                // channel): nothing was acquired, just complete canceled.
                op.Done = true;
                op.Tcs.TrySetCanceled();
                return;
            }

            if (ReferenceEquals(op.Slot?.Current, op))
            {
                Complete(op, OpStatus.Canceled, 0, null);
                return;
            }

            // Queued waiter: drop it from the queue.
            RemoveWaiter(op);
            Complete(op, OpStatus.Canceled, 0, null);
        }

        /// <summary>
        /// Tears down an operation because its stream is being disposed: completes with
        /// <see cref="ObjectDisposedException"/> — the same shape BCL
        /// <see cref="System.Diagnostics.Process"/> gives when its streams close with a
        /// read pending. Token cancellation stays <see cref="OperationCanceledException"/>
        /// (see <see cref="Cancel"/>); only handle teardown aborts the operation this way.
        /// </summary>
        private void Abort(Operation op)
        {
            if (op.Done)
                return;

            // Queued waiter: drop it from the queue so it is never promoted later.
            RemoveWaiter(op);

            Complete(op, OpStatus.Failed, 0, new ObjectDisposedException("PtyStream", "The pty stream was disposed."));
        }

        /// <summary>Drops <paramref name="op"/> from its slot's waiter queue when it is queued there.</summary>
        private static void RemoveWaiter(Operation op)
        {
            if (op.Slot is null || ReferenceEquals(op.Slot.Current, op))
                return;
            var removed = false;
            var waiters = op.Slot.Waiters;
            for (var i = 0; i < waiters.Count; i++)
            {
                var w = waiters.Dequeue();
                if (ReferenceEquals(w, op) && !removed)
                    removed = true;
                else
                    waiters.Enqueue(w);
            }
        }

        private void CancelHandle(SafeFileHandle handle)
        {
            // Snapshot first: Complete may remove or replace slot.Current while we run.
            var toAbort = new List<Operation>();
            foreach (var slot in slots.Values)
            {
                if (slot.Current is { Done: false } c && ReferenceEquals(c.Handle, handle))
                    toAbort.Add(c);
                foreach (var w in slot.Waiters)
                    if (ReferenceEquals(w.Handle, handle))
                        toAbort.Add(w);
            }

            // Dispose is tearing the stream down: pending reads/writes abort with
            // ObjectDisposedException (BCL Process semantics), not a cancellation.
            foreach (var op in toAbort)
                Abort(op);

            // Remember the handle so any Register that arrives *after* this message is
            // rejected instead of being registered with no cancel to ever come.
            canceledHandles.AddOrUpdate(handle, Sentinel.Value);
        }

        private static class Sentinel
        {
            internal static readonly object Value = new();
        }

        // --------------------------------------------------------------- dispatch

        private void Dispatch(Operation op, NativeMethods.PollEvents revents)
        {
            try
            {
                if (op.Kind == OperationKind.Read)
                    DispatchRead(op, revents);
                else
                    DispatchWrite(op, revents);
            }
            catch (Exception ex)
            {
                Complete(op, OpStatus.Failed, 0, ex);
            }
        }

        private void DispatchRead(Operation op, NativeMethods.PollEvents revents)
        {
            // Poll asserted POLLIN, so a read normally returns data. A single read:
            // partial reads are legal and the caller can issue another op for the
            // rest. If the slave is gone, poll reports HUP — read to confirm EOF.
            while (true)
            {
                var r = NativeMethods.read(op.Fd, op.Pointer, (nuint)op.Count);
                switch (r)
                {
                    case > 0:
                        Complete(op, OpStatus.Succeeded, (int)r, null);
                        return;
                    case 0:
                        Complete(op, OpStatus.Succeeded, 0, null); // EOF
                        return;
                }

                var err = Marshal.GetLastPInvokeError();
                switch (err)
                {
                    case Eintr:
                        continue;
                    case Eagain:
                    {
                        // Spurious wakeup: the data was consumed before this read ran (e.g.
                        // by a concurrent read on the same stream), or the readiness state
                        // changed. Stay registered for the next POLLIN — not an error. If the
                        // fd also reports hangup/error, the slave is gone: that is EOF.
                        if ((revents & (NativeMethods.PollEvents.Pollhup | NativeMethods.PollEvents.Pollerr)) != 0)
                            Complete(op, OpStatus.Succeeded, 0, null);
                        return;
                    }
                    case Eio:
                        Complete(op, OpStatus.Succeeded, 0, null); // slave closed: EOF on both platforms
                        return;
                    default:
                        Complete(op, OpStatus.Failed, 0, new IOException($"pty read failed: errno={err}"));
                        return;
                }
            }
        }

        private void DispatchWrite(Operation op, NativeMethods.PollEvents revents)
        {
            var total = op.Count;
            while (op.Offset < total)
            {
                // Non-blocking fd: the write accepts what it can; when the pty buffer is
                // full it returns EAGAIN and we stay registered for the next POLLOUT.
                var r = NativeMethods.write(op.Fd, op.Pointer + op.Offset, (nuint)(total - op.Offset));
                switch (r)
                {
                    case > 0:
                        op.Offset += (int)r;
                        continue;
                    case 0:
                        return; // degenerate zero-length write; wait for the next POLLOUT
                }

                var err = Marshal.GetLastPInvokeError();
                switch (err)
                {
                    case Eintr:
                        continue;
                    case Eagain:
                    {
                        // The pty buffer is full for now; resume on the next POLLOUT. If the
                        // slave is gone (HUP/ERR), the remaining bytes can never be written.
                        if ((revents & (NativeMethods.PollEvents.Pollhup | NativeMethods.PollEvents.Pollerr |
                                        NativeMethods.PollEvents.Pollnval)) != 0)
                            Complete(op, OpStatus.Failed, 0,
                                new IOException("pty write failed: the child closed the terminal"));
                        return;
                    }
                    case Eio:
                        Complete(op, OpStatus.Failed, 0,
                            new IOException("pty write failed: the child closed the terminal (EIO)"));
                        return;
                    default:
                        Complete(op, OpStatus.Failed, 0, new IOException($"pty write failed: errno={err}"));
                        return;
                }
            }

            Complete(op, OpStatus.Succeeded, total, null);
        }

        // -------------------------------------------------------------- completion

        /// <summary>Completes the operation, promotes the next waiter, and releases its resources.</summary>
        private void Complete(Operation op, OpStatus status, int result, Exception? error)
        {
            if (op.Done)
                return;
            op.Done = true;

            // Detach from its slot and promote the next waiter if this was the current op.
            var slot = op.Slot;
            if (slot is not null)
            {
                if (ReferenceEquals(slot.Current, op))
                {
                    slot.Current = slot.Waiters.Count > 0 ? slot.Waiters.Dequeue() : null;
                    if (slot.Current is null)
                        slots.Remove((op.Fd, op.Kind));
                }
                // (A queued waiter that was canceled is simply no longer in the queue.)
            }

            // Release the pin, the token registration and the handle ref before the task
            // signals, so the caller's Dispose can proceed as soon as it observes completion.
            op.CtReg.Dispose();
            op.Pin.Dispose();
            if (op.AddRefAdded)
                op.Handle.DangerousRelease();

            switch (status)
            {
                case OpStatus.Succeeded:
                    op.Tcs.TrySetResult(result);
                    break;
                case OpStatus.Failed:
                    op.Tcs.TrySetException(error ?? new IOException("pty I/O failed"));
                    break;
                case OpStatus.Canceled:
                    op.Tcs.TrySetCanceled();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, null);
            }
        }

        // ----------------------------------------------------------------- helpers

        private void BuildPollSet()
        {
            pollFds[0] = new NativeMethods.PollFd { Fd = wakeRead, Events = NativeMethods.PollEvents.Pollin };
            pollCount = 1;
            foreach (var slot in slots.Values)
            {
                var op = slot.Current;
                if (op is null || op.Done)
                    continue;
                if (pollCount == pollFds.Length)
                    Array.Resize(ref pollFds, pollFds.Length * 2);
                pollFds[pollCount++] = new NativeMethods.PollFd
                {
                    Fd = op.Fd,
                    Events = op.Kind == OperationKind.Read
                        ? NativeMethods.PollEvents.Pollin
                        : NativeMethods.PollEvents.Pollout,
                };
            }
        }

        /// <summary>Reads the wake pipe without ever blocking (poll(0) then read).</summary>
        private void DrainWakePipe()
        {
            // A collection expression is fine here: only the poll return value matters,
            // the copied Revents is never read back.
            var pollFd = new NativeMethods.PollFd { Fd = wakeRead, Events = NativeMethods.PollEvents.Pollin };
            while (true)
            {
                int r;
                do
                {
                    r = NativeMethods.poll([pollFd], 0);
                } while (r < 0 && Marshal.GetLastPInvokeError() == Eintr);

                if (r <= 0)
                    return; // no (more) wake bytes

                nint n;
                do
                {
                    n = NativeMethods.read(wakeRead, wakeBufPtr, (nuint)wakeBuffer.Length);
                } while (n < 0 && Marshal.GetLastPInvokeError() == Eintr);

                if (n <= 0)
                    return; // drained or the rare EOF
            }
        }
    }
}

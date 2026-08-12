using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// Unix half of <see cref="PtyStream"/>: a single non-blocking pty master fd
/// (opened via <c>posix_openpt(O_NONBLOCK)</c> — never fcntl, which is broken as a
/// variadic call on Apple arm64). Read/write are driven by poll(2):
///
///  * <b>No thread-pool starvation.</b> Sync reads/writes run on the calling thread
///    (poll + non-blocking read/write; zero pool involvement). Async reads/writes are
///    serviced by <see cref="PtyIoEngine"/>, a single process-wide poll loop; a pending
///    operation holds no thread at all, and canceling it never has to abort a blocked
///    syscall (there is none).
///
///  * <b>Partial reads.</b> <see cref="Read(byte[], int, int)"/> returns whatever is
///    currently available once the device is readable (at least 1 byte), like a socket.
///
///  * <b>Immediate cancellation.</b> Canceling a pending <see cref="ReadAsync"/> returns
///    without waiting for a timeout. Bytes already copied into the caller's buffer win
///    over cancellation; cancellation wins only if nothing was read yet. Writes can
///    partially advance before an <see cref="OperationCanceledException"/> is thrown.
///
///  * <b>Prompt EOF.</b> When the child's slave side goes away, poll reports HUP and a
///    subsequent read returns 0 (EOF) instead of blocking forever.
///
/// Unix-only: compiled only by the non-Windows target (see csproj).
/// </summary>
public sealed partial class PtyStream
{
    private readonly SafeFileHandle handle;

    /// <summary>Takes ownership of <paramref name="handle"/> (the non-blocking pty master).</summary>
    internal PtyStream(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    internal bool IsClosed => handle.IsClosed;

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        // No userspace buffering, so there is nothing to flush.
    }

    // --------------------------------------------------------------------- read

    public override int Read(Span<byte> buffer)
    {
        return buffer.IsEmpty ? 0 : Read(buffer, Timeout.Infinite, out _);
    }

    /// <summary>
    /// Polls for readability up to <paramref name="timeoutMs"/> (infinite if negative),
    /// then reads whatever is available. Returns the number of bytes read, or 0 when
    /// nothing arrived within the timeout (<paramref name="eof"/> is false) or when the
    /// child's slave side has gone (<paramref name="eof"/> is true). Used by
    /// <see cref="PtyProcess"/> to keep its existing bounded-wait read semantics without
    /// a second syscall layer.
    /// </summary>
    internal int Read(Span<byte> buffer, int timeoutMs, out bool eof)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (Volatile.Read(ref pendingAsyncReads) > 0)
            throw new InvalidOperationException(
                "A pending async read is in progress on this pty stream; sync and async reads on the same stream cannot be mixed.");
        eof = false;
        if (buffer.IsEmpty)
            return 0;
        var fd = (int)handle.DangerousGetHandle();

        if (!WaitForPoll(fd, NativeMethods.PollEvents.Pollin, timeoutMs, out var revents))
            return 0; // timed out with nothing available

        var hungUp = (revents & (NativeMethods.PollEvents.Pollhup | NativeMethods.PollEvents.Pollerr)) != 0;
        unsafe
        {
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    var r = NativeMethods.read(fd, (IntPtr)p, (nuint)buffer.Length);
                    switch (r)
                    {
                        case > 0:
                            return (int)r;
                        case 0:
                            eof = true;
                            return 0;
                    }

                    var err = Marshal.GetLastPInvokeError();
                    switch (err)
                    {
                        case NativeMethods.Eintr:
                            continue;
                        case NativeMethods.Eagain:
                        {
                            // Nothing actually available (spurious poll wakeup). If the fd
                            // also reports hangup, the slave is gone — that is EOF.
                            if (hungUp)
                                eof = true;
                            return 0;
                        }
                        case NativeMethods.Eio:
                            eof = true; // slave side closed: EOF (macOS and Linux)
                            return 0;
                        default:
                            throw new IOException($"pty read failed: errno={err}");
                    }
                }
            }
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (buffer.IsEmpty)
            return 0;

        // After the engine copies bytes into the buffer it completes with the count,
        // so data already read wins over a concurrent cancellation by construction.
        Interlocked.Increment(ref pendingAsyncReads);
        try
        {
            return await PtyIoEngine.ReadAsync(handle, buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref pendingAsyncReads);
        }
    }

    // -------------------------------------------------------------------- write

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (buffer.IsEmpty)
            return;
        var fd = (int)handle.DangerousGetHandle();

        unsafe
        {
            fixed (byte* p = buffer)
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var r = NativeMethods.write(fd, (IntPtr)(p + offset), (nuint)(buffer.Length - offset));
                    if (r > 0)
                    {
                        offset += (int)r;
                        continue;
                    }

                    var err = Marshal.GetLastPInvokeError();
                    switch (err)
                    {
                        case NativeMethods.Eintr:
                            continue;
                        case NativeMethods.Eagain:
                            // pty buffer full: wait until the child drains it (or the slave is gone).
                            WaitForPoll(fd, NativeMethods.PollEvents.Pollout, Timeout.Infinite, out _);
                            continue;
                        case NativeMethods.Eio:
                            throw new IOException("pty write failed: the child closed the terminal (EIO)");
                        default:
                            throw new IOException($"pty write failed: errno={err}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Asynchronously writes all of <paramref name="buffer"/>. If the child is not
    /// draining the pty and cancellation is requested mid-write, the operation stops
    /// after whatever the device consumed and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (buffer.IsEmpty)
            return ValueTask.CompletedTask;
        return new ValueTask(PtyIoEngine.WriteAsync(handle, buffer, cancellationToken));
    }

    // ------------------------------------------------------------------ dispose

    protected override void Dispose(bool disposing)
    {
        // Unregister everything engine-side first, so no poll is still watching this
        // fd when it is closed below (a closed-then-reused fd must never be polled).
        PtyIoEngine.AbortHandle(handle);
        if (disposing)
            handle.Dispose();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Polls <paramref name="fd"/> for <paramref name="events"/> for up to
    /// <paramref name="timeoutMs"/> ms (0 = check once, non-blocking; negative = infinite).
    /// Returns true when the events fired; false on timeout. HUP/ERR are reported through
    /// <paramref name="revents"/> so callers can distinguish "slave gone" from plain EAGAIN.
    /// </summary>
    private static bool WaitForPoll(int fd, NativeMethods.PollEvents events, int timeoutMs, out NativeMethods.PollEvents revents)
    {
        var pollFd = new NativeMethods.PollFd { Fd = fd, Events = events };
        var deadline = timeoutMs < 0 ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);

        while (true)
        {
            var remainingMs = timeoutMs < 0
                ? -1
                : (int)Math.Max(0, Math.Min((deadline - DateTime.UtcNow).TotalMilliseconds, int.MaxValue));

            int r;
            do
            {
                r = NativeMethods.poll([pollFd], 1, remainingMs);
            } while (r < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Eintr);

            switch (r)
            {
                case < 0:
                    throw new IOException($"poll failed: errno={Marshal.GetLastPInvokeError()}");
                case > 0:
                    revents = pollFd.Revents;
                    return true;
            }

            if (remainingMs != 0) continue;
            revents = 0;
            return false; // poll(..., 0) timed out: nothing available right now
        }
    }
}

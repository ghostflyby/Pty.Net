using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

// Unix half of PtyStream: a single non-blocking pty master fd (opened via
// posix_openpt(O_NONBLOCK) — never fcntl, which is broken as a variadic call on
// Apple arm64). Read/write are driven by poll(2):
//   * No thread-pool starvation: sync reads/writes run on the calling thread; async
//     reads/writes are serviced by PtyIoEngine, a single process-wide poll loop.
//   * Partial reads: a read returns whatever is available once readable (>= 1 byte).
//   * Immediate cancellation: canceling a pending async op returns without waiting for
//     a timeout; bytes already copied win over cancellation.
//   * Prompt EOF: when the child's slave side goes away, poll reports HUP and the next
//     read returns 0 instead of blocking forever.
// Unix-only: compiled only by the non-Windows target (see csproj).
public sealed partial class PtyStream
{
    private readonly SafeFileHandle handle;

    /// <summary>Takes ownership of <paramref name="handle"/> (the non-blocking pty master).</summary>
    internal PtyStream(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    internal bool IsClosed => handle.IsClosed;

    /// <summary>There is no user-space buffering, so this does nothing beyond checking the stream is open.</summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        // No userspace buffering, so there is nothing to flush.
    }

    // --------------------------------------------------------------------- read

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes, blocking until at least one
    /// byte is available.
    /// <br/>
    /// Returns the number of bytes actually read (not necessarily the buffer length),
    /// or 0 at end of stream.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>The number of bytes read, or 0 at end of stream.</returns>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
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

    /// <summary>
    /// Writes all of <paramref name="buffer"/>, blocking as needed while the child
    /// drains the terminal.
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
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
    /// Asynchronously writes all of <paramref name="buffer"/>.
    /// <para>If the child stops reading and <paramref name="cancellationToken"/> is
    /// canceled mid-write, the operation stops after whatever the device consumed and
    /// throws <see cref="OperationCanceledException"/>.</para>
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="cancellationToken">Canceled to abort the write.</param>
    /// <returns>A task that completes when all bytes have been written.</returns>
    /// <exception cref="OperationCanceledException">The write was canceled before it completed.</exception>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (buffer.IsEmpty)
            return ValueTask.CompletedTask;
        return new ValueTask(PtyIoEngine.WriteAsync(handle, buffer, cancellationToken));
    }

    // ------------------------------------------------------------ window size

    /// <summary>
    /// Sets the terminal window size in character cells. The kernel propagates the new
    /// size to the child's foreground process group by sending SIGWINCH, so interactive
    /// programs (vim, htop, readline) re-layout automatically.
    /// </summary>
    /// <param name="columns">Number of character columns.</param>
    /// <param name="rows">Number of character rows.</param>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    /// <exception cref="IOException">The ioctl failed.</exception>
    internal void SetWindowSize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var fd = (int)handle.DangerousGetHandle();

        // Stack-allocated winsize, pinned for the ioctl. IoCtl selects the arm64
        // pad-register calling form that the variadic ABI requires (see the declaration).
        Span<NativeMethods.Winsize> winsize = stackalloc NativeMethods.Winsize[1];
        winsize[0] = new NativeMethods.Winsize { Row = (ushort)rows, Col = (ushort)columns };
        int rc;
        unsafe
        {
            fixed (NativeMethods.Winsize* p = winsize)
            {
                rc = NativeMethods.IoCtl(fd, NativeMethods.Tiocswinsz, (IntPtr)p);
            }
        }
        if (rc != 0)
            throw new IOException($"pty resize failed: errno={Marshal.GetLastPInvokeError()}");
    }

    // ------------------------------------------------------------------ dispose

    /// <summary>Releases the stream's pty handle, aborting any in-flight operations first.</summary>
    /// <param name="disposing">True when called from user code (not the finalizer).</param>
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
        var deadline = timeoutMs < 0 ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);

        // Stack-allocated one-element poll set: a fresh PollFd[] per call would
        // allocate on every sync read/write. poll rewrites Revents in place, and the
        // EINTR-retry loop below must observe that value. Typed stackalloc keeps the
        // element naturally aligned (PollFd is int + 2 x short), so no manual
        // alignment is needed.
        Span<NativeMethods.PollFd> pollFds = stackalloc NativeMethods.PollFd[1];
        pollFds[0] = new NativeMethods.PollFd { Fd = fd, Events = events };

        while (true)
        {
            var remainingMs = timeoutMs < 0
                ? -1
                : (int)Math.Max(0, Math.Min((deadline - DateTime.UtcNow).TotalMilliseconds, int.MaxValue));

            int r;
            unsafe
            {
                fixed (NativeMethods.PollFd* p = pollFds)
                {
                    do
                    {
                        r = NativeMethods.poll((IntPtr)p, 1, remainingMs);
                    } while (r < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Eintr);
                }
            }

            switch (r)
            {
                case < 0:
                    throw new IOException($"poll failed: errno={Marshal.GetLastPInvokeError()}");
                case > 0:
                    revents = pollFds[0].Revents;
                    return true;
            }

            if (remainingMs != 0) continue;
            revents = 0;
            return false; // poll(..., 0) timed out: nothing available right now
        }
    }
}

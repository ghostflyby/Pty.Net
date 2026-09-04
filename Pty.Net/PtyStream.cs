namespace Ghostflyby.Pty;

/// <summary>
/// A read/write <see cref="Stream"/> over the master end of a pseudo-terminal.
/// <para>
/// The child's stdout and stderr are merged into this single stream; there is no
/// separate stderr channel. Reads return 0 (end of stream) once the child's terminal
/// side closes, and every operation throws <see cref="ObjectDisposedException"/> after
/// the stream — or the <see cref="PtyProcess"/> that owns it — is disposed.
/// </para>
/// <para>
/// Use one of <see cref="PtyProcess.Input"/> / <see cref="PtyProcess.Output"/>
/// for text, or this raw stream for bytes; never mix both on the same direction.
/// </para>
/// </summary>
public sealed partial class PtyStream : Stream
{
    // Number of async reads currently in flight on this stream. The sync read path checks
    // it: while an async read is pending, a sync read would race the reader for the same
    // bytes — which one wins is undefined and a lost race can even look like EOF. Mixing
    // is refused with a clear error instead of a wrong result.
    private int pendingAsyncReads;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <summary>A pty stream has no seekable position.</summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Length => throw new NotSupportedException("The pty stream is not seekable.");

    /// <inheritdoc cref="Length"/>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Position
    {
        get => throw new NotSupportedException("The pty stream is not seekable.");
        set => throw new NotSupportedException("The pty stream is not seekable.");
    }

    /// <inheritdoc cref="Length"/>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("The pty stream is not seekable.");

    /// <inheritdoc cref="Length"/>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void SetLength(long value) => throw new NotSupportedException("The pty stream is not seekable.");

    // The Stream base default for FlushAsync is Task.Run(Flush) — a thread-pool hop we
    // must not inherit. Async writes are already unbuffered passthroughs. The per-platform
    // half implements Flush() with its own closed-state check.
    /// <summary>There is no user-space buffering at the stream level, so this returns immediately.</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Blocks until at least one byte is available, then reads up to <paramref name="count"/>
    /// bytes into <paramref name="buffer"/> at <paramref name="offset"/>.
    /// <br/>
    /// Returns the number of bytes actually read (not necessarily <paramref name="count"/>),
    /// or 0 at end of stream.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offset">Zero-based offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <returns>The number of bytes read, or 0 at end of stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="count"/> is out of bounds for <paramref name="buffer"/>.</exception>
    /// <exception cref="InvalidOperationException">A pending async read is in progress on this stream; sync and async reads cannot be mixed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateArgs(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count), Timeout.Infinite);
    }

    /// <summary>
    /// Writes all of <paramref name="buffer"/>, blocking as needed while the child drains
    /// the terminal.
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="offset">Zero-based offset in <paramref name="buffer"/> at which to start.</param>
    /// <param name="count">Number of bytes to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="count"/> is out of bounds for <paramref name="buffer"/>.</exception>
    /// <exception cref="IOException">The child's terminal is closed.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateArgs(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    private static void ValidateArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count are out of bounds for the buffer.");
    }
}
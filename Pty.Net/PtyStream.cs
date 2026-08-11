namespace Ghostflyby.Pty;

/// <summary>
/// A <see cref="Stream"/> over the master end of a pseudo-terminal.
/// Underlying semantics are file semantics (read/write, no seek).
///
/// The platform halves live in separate partial files:
///  * <c>PtyStream.Unix.cs</c> — a single non-blocking pty master fd, every read/write
///    driven by poll(2) (<b>no thread-pool starvation</b>: a pending operation holds no
///    thread, cancellation never has to abort a blocked syscall, partial reads return
///    whatever is available once readable, and EOF is reported promptly when the slave
///    side goes away).
///  * <c>PtyStream.Windows.cs</c> — two ConPTY pipe handles (input-write / output-read),
///    synchronous I/O (ConPTY does not support overlapped I/O). A per-stream reader
///    thread pumps data into pending async reads / the internal buffer; writes run on
///    a per-stream writer thread. No thread-pool thread is ever parked.
/// </summary>
public sealed partial class PtyStream : Stream
{
    // Number of async reads currently in flight on this stream. The sync read path checks
    // it: while an async read is pending, a sync read would race the reader for the same
    // bytes — which one wins is undefined and a lost race can even look like EOF. Mixing
    // is refused with a clear error instead of a wrong result.
    private int pendingAsyncReads;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    /// <summary>A pty has no seekable position.</summary>
    public override long Length => throw new NotSupportedException("The pty stream is not seekable.");

    public override long Position
    {
        get => throw new NotSupportedException("The pty stream is not seekable.");
        set => throw new NotSupportedException("The pty stream is not seekable.");
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The pty stream is not seekable.");

    public override void SetLength(long value) => throw new NotSupportedException("The pty stream is not seekable.");

    // The Stream base default for FlushAsync is Task.Run(Flush) — a thread-pool hop we
    // must not inherit. Async writes are already unbuffered passthroughs. The per-platform
    // half implements Flush() with its own closed-state check.
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Blocks until the device is readable, then returns whatever is available
    /// (1..count bytes) — not necessarily a full buffer. Returns 0 once the child's
    /// terminal side has closed (EOF).
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateArgs(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count), Timeout.Infinite, out _);
    }

    /// <summary>
    /// Writes all of <paramref name="buffer"/>, blocking as needed. If the child has
    /// closed its terminal, the write fails with an <see cref="IOException"/>.
    /// </summary>
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

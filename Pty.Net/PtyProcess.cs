using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// A child process attached to a pseudo-terminal (PTY).
/// <para>
/// Use it to drive an interactive shell: write commands to <see cref="StandardInput"/>,
/// read back the terminal output from <see cref="StandardOutput"/>. The child's stdout
/// and stderr are merged into the one terminal stream; there is no separate stderr.
/// </para>
/// <para>
/// The process-control surface is async-capable: <see cref="WaitForExitAsync(CancellationToken)"/>
/// waits without occupying a thread, <see cref="DisposeAsync"/> mirrors
/// <see cref="Dispose()"/>, and <see cref="Exited"/> fires once the child is reaped by
/// a process-wide background reaper. All async I/O and waits are thread-pool-free.
/// </para>
/// </summary>
public sealed partial class PtyProcess : IDisposable, IAsyncDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly SemaphoreSlim gate = new(1, 1);

    // Reused by DrainOutput, which WaitForExit calls every ~10ms; a fresh allocation per
    // call would churn ~4KB each iteration. Serialized by the gate.
    private readonly byte[] drainBuf = new byte[ReadBufferSize];

    private bool disposed;

    /// <summary>OS process id of the child.</summary>
    public int Pid { get; }

    /// <summary>
    /// Child process handle. Non-null only on Windows (ConPTY path), where the reaper
    /// waits on it and <see cref="Kill"/> terminates through it — waitpid(2) does not
    /// exist there. Owned by this process and disposed with it.
    /// </summary>
    private SafeProcessHandle? ProcessHandle { get; }
    // Reaped exit code.
    // int.MinValue means "not reaped yet"; afterward it is 0..255,
    // 128 + signal, or -1 when the status could not be determined.
    // Written by the
    // process-wide reaper thread, read from any thread: Volatile keeps it visible.
    private int exitCode = int.MinValue;

    /// <summary>Exit code of the child, once it has been reaped by the process-wide reaper; null while it is running.</summary>
    public int? ExitCode
    {
        get
        {
            var value = Volatile.Read(ref exitCode);
            return value == int.MinValue ? null : value;
        }
    }

    /// <summary>True once the child has been reaped (<see cref="ExitCode"/> is not null).</summary>
    public bool HasExited => ExitCode is not null;

    /// <summary>
    /// Raised once the child has been reaped by the process-wide reaper.
    /// <para>By then <see cref="ExitCode"/> is set and <see cref="HasExited"/> is true.
    /// The handler runs on the shared reaper thread and must not block; exceptions it
    /// throws are swallowed.</para>
    /// <para>May fire after <see cref="Dispose"/> returned, when the child was still
    /// alive at dispose time and exited only after the bounded reap wait elapsed.</para>
    /// </summary>
    public event EventHandler? Exited;

    /// <summary>
    /// The raw byte stream over the pty master — the single source of bytes for both
    /// text facades.
    /// <para>Reading here consumes bytes that <see cref="StandardOutput"/> would
    /// otherwise decode; use only one of them at a time.</para>
    /// </summary>
    public PtyStream BaseStream { get; }

    /// <summary>Text writer to the child's input, like <see cref="System.Diagnostics.Process.StandardInput"/>. Auto-flushed so writes are visible to the child immediately.</summary>
    public StreamWriter StandardInput { get; }

    /// <summary>
    /// Text reader over the child's output, like <see cref="System.Diagnostics.Process.StandardOutput"/>.
    /// <para>A pty merges the child's stdout and stderr into this one reader. Prefer
    /// disposing the whole <see cref="PtyProcess"/> over disposing this reader alone.</para>
    /// </summary>
    public StreamReader StandardOutput { get; }

    private PtyProcess(PtyStream stream, int pid, Encoding? inputEncoding, Encoding? outputEncoding, SafeProcessHandle? processHandle)
    {
        BaseStream = stream;
        // Null encoding means UTF-8 — the terminal default on macOS and Linux and the
        // mandatory ConPTY byte transport on Windows.
        // The reader's read-ahead buffer is fine: this text facade owns the stream until
        // the caller switches to BaseStream (never mix — like Process's StandardOutput).
        var effectiveInputEncoding = inputEncoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var effectiveOutputEncoding = outputEncoding ?? Encoding.UTF8;
        // Platform hook: Windows wraps the raw stream in transcoding facades (ConPTY
        // transports UTF-8 bytes); Unix uses the stream directly.
        CreateFacades(effectiveInputEncoding, effectiveOutputEncoding, out var inputFacadeStream, out var outputFacadeStream);
        StandardInput = new StreamWriter(inputFacadeStream, effectiveInputEncoding)
        {
            AutoFlush = true,
        };
        StandardOutput = new StreamReader(outputFacadeStream, effectiveOutputEncoding);
        Pid = pid;
        ProcessHandle = processHandle;
        // The process-wide reaper owns the exit wait for this child: it sets ExitCode and
        // raises Exited, and every WaitForExit/Dispose path just observes the result.
        PtyReaper.Watch(this);
    }

    /// <summary>
    /// Launches <paramref name="info"/> in a new PTY session.
    /// </summary>
    /// <param name="info">The launch description.</param>
    /// <returns>The running <see cref="PtyProcess"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
    /// <exception cref="IOException">The pseudo-terminal could not be created or the child could not be launched.</exception>
    /// <exception cref="PlatformNotSupportedException">ConPTY is not available (Windows 10 1809 or earlier). Windows only.</exception>
    public static PtyProcess Start(PtyStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return StartCore(info.FileName, info.ResolveArguments(), info.WorkingDirectory, info.Environment,
            info.StandardInputEncoding, info.StandardOutputEncoding, info.InitialCols, info.InitialRows);
    }

    /// <summary>
    /// Launches <paramref name="file"/> with <paramref name="arguments"/> in a new PTY session, using UTF-8 I/O.
    /// </summary>
    /// <param name="file">The executable to run.</param>
    /// <param name="arguments">Command-line arguments for <paramref name="file"/>.</param>
    /// <param name="workingDirectory">Initial working directory of the child; the parent's current directory when null.</param>
    /// <returns>The running <see cref="PtyProcess"/>.</returns>
    /// <exception cref="IOException">The pseudo-terminal could not be created or the child could not be launched.</exception>
    /// <exception cref="PlatformNotSupportedException">ConPTY is not available (Windows 10 1809 or earlier). Windows only.</exception>
    public static PtyProcess Start(string file, string[] arguments, string? workingDirectory = null)
    {
        return StartCore(file, arguments, workingDirectory, environment: null,
            inputEncoding: null, outputEncoding: null, initialCols: 120, initialRows: 30);
    }

    private static PtyProcess StartCore(string file, string[] arguments, string? workingDirectory,
        IDictionary<string, string?>? environment, Encoding? inputEncoding, Encoding? outputEncoding,
        int initialCols, int initialRows)
    {
        // Platform hook: posix_spawn on Unix, ConPTY (CreatePseudoConsole +
        // CreateProcessW) on Windows.
        return StartPlatform(file, arguments, workingDirectory, environment ?? ParentEnvironment(),
            inputEncoding, outputEncoding, initialCols, initialRows);
    }

    /// <summary>
    /// Blocks until the child exits or <paramref name="timeout"/> elapses.
    /// <para>While waiting, output is drained continuously so the child never blocks
    /// writing to a full pty buffer; the drained bytes remain readable afterward.
    /// Concurrent consumption of <see cref="StandardOutput"/> / <see cref="BaseStream"/>
    /// during the wait is not portable, so consume output before or after it.</para>
    /// <para>Reaping happens on the process-wide reaper thread; this method only observes
    /// <see cref="ExitCode"/>. Pass <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// indefinitely. For a thread-free equivalent see
    /// <see cref="WaitForExitAsync(CancellationToken)"/>.</para>
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>True if the child exited within <paramref name="timeout"/>; false on timeout.</returns>
    /// <exception cref="ObjectDisposedException">The process is disposed.</exception>
    public bool WaitForExit(TimeSpan timeout)
    {
        BeginExitWait();
        try
        {
            var infinite = timeout == Timeout.InfiniteTimeSpan;
            var deadline = infinite ? DateTime.MaxValue : DateTime.UtcNow + timeout;
            while (true)
            {
                gate.Wait();
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    DrainOutput(); // keep the pty buffer flowing while we wait
                    if (HasExited)
                        return true;
                }
                finally
                {
                    gate.Release();
                }

                if (!infinite && DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(WaitStepMs(deadline, infinite));
            }
        }
        finally
        {
            EndExitWait();
        }
    }

    /// <summary>Blocks until the child exits and has been reaped. Equivalent to <see cref="WaitForExit(TimeSpan)"/> with an infinite timeout.</summary>
    public void WaitForExit() => WaitForExit(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Waits until the child exits and has been reaped, without blocking a thread.
    /// <para>Output is drained continuously while waiting (same caveat as
    /// <see cref="WaitForExit(TimeSpan)"/>).</para>
    /// </summary>
    /// <param name="ct">Canceled to abandon the wait.</param>
    /// <returns>A task that completes once the child has been reaped.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is canceled before the child exited.</exception>
    /// <exception cref="ObjectDisposedException">The process is disposed while waiting.</exception>
    public Task WaitForExitAsync(CancellationToken ct = default) => WaitForExitCoreAsync(Timeout.InfiniteTimeSpan, ct);

    /// <summary>
    /// Waits until the child exits and has been reaped, or until <paramref name="timeout"/>
    /// elapses.
    /// <para>Non-blocking and cancellable.</para>
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="ct">Canceled to abandon the wait.</param>
    /// <returns>True if the child exited within <paramref name="timeout"/>; false on timeout.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is canceled before the wait completed.</exception>
    /// <exception cref="ObjectDisposedException">The process is disposed while waiting.</exception>
    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct = default) => WaitForExitCoreAsync(timeout, ct);

    private async Task<bool> WaitForExitCoreAsync(TimeSpan timeout, CancellationToken ct)
    {
        BeginExitWait();
        try
        {
            var infinite = timeout == Timeout.InfiniteTimeSpan;
            var deadline = infinite ? DateTime.MaxValue : DateTime.UtcNow + timeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (HasExited)
                    return true;

                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    DrainOutput(); // keep the pty buffer flowing while we wait
                    if (HasExited)
                        return true;
                }
                finally
                {
                    gate.Release();
                }

                if (!infinite && DateTime.UtcNow >= deadline)
                    return false;
                await Task.Delay(WaitStepMs(deadline, infinite), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            EndExitWait();
        }
    }

    /// <summary>
    /// Terminates the child immediately (SIGKILL on Unix, TerminateProcess on Windows),
    /// without giving it a chance to clean up — matching
    /// <see cref="System.Diagnostics.Process.Kill()"/>.
    /// <para>The child is not reaped here; call <see cref="WaitForExit()"/> or
    /// <see cref="Dispose"/> afterwards. Prefer <see cref="Dispose"/> for normal
    /// termination, which lets the shell exit cleanly.</para>
    /// </summary>
    public void Kill()
    {
        // Platform hook: SIGKILL on Unix, TerminateProcess on Windows.
        KillPlatform();
    }

    /// <summary>
    /// Resizes the terminal to <paramref name="columns"/> × <paramref name="rows"/>
    /// character cells.
    /// <para>On Unix the kernel delivers SIGWINCH to the child's foreground process
    /// group, so full-screen programs (vim, htop, readline) re-layout immediately; on
    /// Windows ConPTY propagates the new size to the attached client.</para>
    /// </summary>
    /// <param name="columns">Number of character columns.</param>
    /// <param name="rows">Number of character rows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> or <paramref name="rows"/> is less than 1 or exceeds the platform's 16-bit range.</exception>
    /// <exception cref="ObjectDisposedException">The process is disposed.</exception>
    /// <exception cref="IOException">The terminal could not be resized.</exception>
    public void Resize(int columns, int rows)
    {
        // Both platforms carry the size in 16-bit fields (ushort winsize / short COORD).
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns, nameof(columns));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows, nameof(rows));
        if (columns > ushort.MaxValue || rows > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions are limited to 16 bits per axis.");
        BaseStream.SetWindowSize(columns, rows);
    }

    /// <summary>
    /// Terminates the child and releases the pty resources.
    /// <para>A still-running child is terminated first; output produced up to that point
    /// remains readable through <see cref="BaseStream"/> / <see cref="StandardOutput"/>.
    /// After this returns, all operations on the streams throw
    /// <see cref="ObjectDisposedException"/>.</para>
    /// <para>If the child has not been reaped within a bounded wait, the process-wide
    /// reaper keeps watching it in the background, so it cannot linger as a zombie.</para>
    /// </summary>
    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed)
                return;
            disposed = true;

            // Platform hook: SIGHUP the still-alive child on Unix; on Windows terminate
            // it so ClosePseudoConsole does not wait indefinitely (exited children are
            // left alone so their final output is preserved).
            TerminateChildIfAlive();

            // Disposing the stream aborts in-flight I/O and releases the terminal channels.
            // On Windows it keeps an async output drain active while ClosePseudoConsole
            // emits its final frame; on Unix the engine defers fd close until its last
            // operation reference is released.
            BaseStream.Dispose();

            // Bounded wait for the reaper to collect the child. If it has not exited by
            // the deadline, the process-wide reaper keeps watching in the background, so
            // the child cannot linger as a zombie — only the reap completion is deferred
            // past dispose (see <see cref="Exited"/>).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!HasExited && DateTime.UtcNow < deadline)
                Thread.Sleep(10);

            // The reaper may still be watching an un-exited child; only release the process
            // handle once the wait is over, or the reaper would lose its wait target.
            if (HasExited)
                ProcessHandle?.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="Dispose()"/>, with the same semantics and
    /// without blocking a thread while waiting. Safe to await from async code paths.
    /// </summary>
    /// <returns>A task that completes when the process is terminated and its resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;

            TerminateChildIfAlive();

            BaseStream.Dispose();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);

            if (HasExited)
                ProcessHandle?.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    // --- internals ---------------------------------------------------------

    /// <summary>
    /// Called by the process-wide reaper once the child has been collected: records
    /// the exit code and raises <see cref="Exited"/>. Runs on the shared reaper thread;
    /// handler exceptions are swallowed so one misbehaving handler cannot stall reaping
    /// for every other session.
    /// </summary>
    internal void OnReaped(int code)
    {
        // Platform hook: Windows queues the pseudo-console close + final-frame drain
        // away from this shared reaper thread; Unix has no teardown work.
        OnReapedPlatform();
        Volatile.Write(ref exitCode, code);
        try
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A handler that throws must not kill the shared reaper thread.
        }
    }

    /// <summary>Single non-blocking reap attempt for this child; true when collected, with the exit code.</summary>
    internal bool TryReap(out int exitCode) => TryReapPlatform(out exitCode);

    /// <summary>A snapshot of the parent's environment, for launches with no explicit <see cref="PtyStartInfo.Environment"/>.</summary>
    private static Dictionary<string, string?> ParentEnvironment() => PtyStartInfo.SnapshotParentEnvironment();

    /// <summary>Decodes a waitpid(2) status into an exit code: 0..255, or 128 + signal when killed.</summary>
    private static int ExtractExitCode(int status)
    {
        var signal = status & 0x7F;
        return signal == 0 ? (status >> 8) & 0xFF : 128 + signal;
    }

    /// <summary>
    /// Poll interval for the exit-wait loops: a fixed 10 ms, but bounded by the remaining
    /// timeout so a wait never overshoots its deadline by more than one poll tick.
    /// </summary>
    private static int WaitStepMs(DateTime deadline, bool infinite)
    {
        if (infinite)
            return 10;
        return (int)Math.Clamp((deadline - DateTime.UtcNow).TotalMilliseconds, 0, 10);
    }

    // --- platform partial hooks ---------------------------------------------
    // Each has exactly one implementing part: PtyProcess.Start.Windows.cs or
    // PtyProcess.Start.Unix.cs (only the matching file is compiled per platform).

    /// <summary>Creates the facade streams the text facades wrap; Windows transcodes to/from UTF-8.</summary>
    private partial void CreateFacades(
        Encoding inputEncoding, Encoding outputEncoding,
        out Stream inputFacadeStream, out Stream outputFacadeStream);

    /// <summary>Launches the child attached to a pty and returns the new <see cref="PtyProcess"/>.</summary>
    private static partial PtyProcess StartPlatform(
        string file, string[] arguments, string? workingDirectory,
        IDictionary<string, string?> environment, Encoding? inputEncoding, Encoding? outputEncoding,
        int initialCols, int initialRows);

    /// <summary>Terminates the child: SIGKILL on Unix, TerminateProcess on Windows.</summary>
    private partial void KillPlatform();

    /// <summary>Non-blocking reap attempt for the child; true when collected, with the exit code.</summary>
    private partial bool TryReapPlatform(out int exitCode);

    /// <summary>Platform teardown that must run off the shared reaper thread (Windows only).</summary>
    private partial void OnReapedPlatform();

    /// <summary>Drains available output without consuming it for the caller (Unix discards it).</summary>
    private partial void DrainOutput();

    /// <summary>
    /// Called around an exit wait. Windows lifts the output pump's buffer bound for the
    /// duration of the wait, so a child blocked writing more output than the bound cannot
    /// deadlock the wait; Unix has no equivalent bound.
    /// </summary>
    private partial void BeginExitWait();

    /// <summary>Balances <see cref="BeginExitWait"/> when the wait returns, times out, or is canceled.</summary>
    private partial void EndExitWait();
}

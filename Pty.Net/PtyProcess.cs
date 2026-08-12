using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// A child process attached to a pseudo-terminal (PTY): on macOS/Linux created via
/// <c>posix_openpt(3)</c> + <c>posix_spawn(2)</c>, on Windows via ConPTY
/// (<c>CreatePseudoConsole</c> + <c>CreateProcessW</c>).
/// Use it to drive an interactive shell: write commands to <see cref="StandardInput"/>,
/// read back the terminal output from <see cref="StandardOutput"/>.
/// </summary>
/// <remarks>
/// I/O is exposed the way <see cref="System.Diagnostics.Process"/> does it:
/// <see cref="StandardInput"/> / <see cref="StandardOutput"/> are the text-facing
/// <see cref="System.IO.StreamWriter"/> / <see cref="System.IO.StreamReader"/>, and
/// <see cref="BaseStream"/> is the raw byte stream (the same one both text facades
/// wrap). A pty is a single bidirectional device — the child's stdout and stderr are
/// merged into the one master stream, and there is no separate stderr channel.
///
/// On Unix the master fd is non-blocking and all I/O is driven by poll(2) through
/// <see cref="PtyIoEngine"/>, so no operation ever blocks a thread-pool thread and
/// cancellation is immediate. On Windows, ConPTY receives synchronous named-pipe server
/// handles while the parent uses asynchronous BCL pipe clients, so its I/O is driven by
/// overlapped operations without per-session worker threads.
///
/// The process-control surface is async-capable too: <see cref="WaitForExitAsync(CancellationToken)"/>
/// waits without occupying a thread, <see cref="DisposeAsync"/> mirrors
/// <see cref="Dispose()"/>, and <see cref="Exited"/> fires once the child is reaped.
/// Reaping is owned by a process-wide background reaper (waitpid on Unix, the process
/// handle on Windows), so exit results are deterministic across concurrent waiters.
/// </remarks>
/// <remarks>
/// Platform code lives in the partial files <c>PtyProcess.Start.Windows.cs</c> /
/// <c>PtyProcess.Start.Unix.cs</c> (see the csproj file globs); this file is
/// platform-free and the only per-platform hooks are partial methods.
/// </remarks>
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

    /// <summary>Exit code, set once the child has been reaped by the process-wide reaper.</summary>
    public int? ExitCode
    {
        get
        {
            var value = Volatile.Read(ref exitCode);
            return value == int.MinValue ? null : value;
        }
    }

    public bool HasExited => ExitCode is not null;

    /// <summary>
    /// Raised on the process-wide reaper thread once the child has been reaped.
    /// By then <see cref="ExitCode"/> is set and <see cref="HasExited"/> is true. The
    /// handler runs on the shared reaper thread and must not block (a blocking handler
    /// would stall reaping for every other session); exceptions it throws are swallowed.
    /// May fire after <see cref="Dispose"/> returned, when the child was still alive at
    /// dispose time and exited only after the bounded reap wait elapsed.
    /// </summary>
    public event EventHandler? Exited;

    /// <summary>
    /// The raw byte stream over the pty master — the single source of bytes for both
    /// text facades. Reading here consumes bytes that <see cref="StandardOutput"/> would
    /// otherwise decode, so use only one of them at a time.
    /// </summary>
    public PtyStream BaseStream { get; }

    /// <summary>Text writer to the child's input, like <see cref="System.Diagnostics.Process.StandardInput"/>. Auto-flushed so writes are visible to the child immediately.</summary>
    public StreamWriter StandardInput { get; }

    /// <summary>
    /// Text reader over the child's output, like <see cref="System.Diagnostics.Process.StandardOutput"/>
    /// (a pty merges the child's stdout and stderr). Like <see cref="StreamReader"/>, disposing
    /// this reader closes the underlying master fd — dispose <see cref="BaseStream"/> (or the
    /// whole <see cref="PtyProcess"/>) instead, or never dispose the facades at all.
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
    /// Launches <paramref name="info"/> in a new PTY session (child process attached to a
    /// pseudo-terminal). On Unix this uses <c>posix_openpt(3)</c> + <c>posix_spawn(2)</c>:
    /// posix_spawn avoids <c>fork(2)</c>, so launching stays safe even when other threads
    /// are concurrently allocating memory (fork in a multithreaded process can deadlock
    /// the child on inherited malloc locks). On Windows it uses ConPTY
    /// (<c>CreatePseudoConsole</c>). A <see cref="System.Diagnostics.ProcessStartInfo"/>
    /// can be passed via <see cref="PtyStartInfo(ProcessStartInfo)"/>.
    /// </summary>
    public static PtyProcess Start(PtyStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return StartCore(info.FileName, info.ResolveArguments(), info.WorkingDirectory, info.Environment,
            info.StandardInputEncoding, info.StandardOutputEncoding);
    }

    /// <summary>Convenience overload of <see cref="Start(PtyStartInfo)"/> with the launch parameters inline (UTF-8 I/O).</summary>
    public static PtyProcess Start(string file, string[] arguments, string? workingDirectory = null)
    {
        return StartCore(file, arguments, workingDirectory, environment: null,
            inputEncoding: null, outputEncoding: null);
    }

    private static PtyProcess StartCore(string file, string[] arguments, string? workingDirectory,
        IDictionary<string, string?>? environment, Encoding? inputEncoding, Encoding? outputEncoding)
    {
        // Platform hook: posix_spawn on Unix, ConPTY (CreatePseudoConsole +
        // CreateProcessW) on Windows.
        return StartPlatform(file, arguments, workingDirectory, environment ?? ParentEnvironment(),
            inputEncoding, outputEncoding);
    }

    /// <summary>
    /// Blocks until the child exits or the timeout elapses. Returns false on timeout.
    /// While waiting, native output is drained continuously so the child never blocks writing
    /// to a full pty buffer. On Unix those bytes are discarded; on Windows the output pump
    /// preserves them in its managed queue for subsequent reads. Like
    /// <see cref="System.Diagnostics.Process.WaitForExit()"/> with redirected output,
    /// concurrent consumption of <see cref="StandardOutput"/> / <see cref="BaseStream"/>
    /// is not portable, so consume output before or after the wait rather than during it.
    /// Pass <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.
    ///
    /// Reaping happens on the process-wide reaper thread; this method only observes
    /// <see cref="ExitCode"/> (set by the reaper), so it never races waitpid(2).
    /// For an asynchronous, thread-free equivalent see <see cref="WaitForExitAsync(CancellationToken)"/>.
    /// </summary>
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

    /// <summary>
    /// Blocks until the child exits and has been reaped. Aligns with
    /// <see cref="System.Diagnostics.Process.WaitForExit()"/> (no timeout); the infinite
    /// wait only returns once the child has been reaped.
    /// </summary>
    public void WaitForExit() => WaitForExit(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Waits until the child exits and has been reaped, like
    /// <see cref="System.Diagnostics.Process.WaitForExitAsync"/>, without blocking a
    /// thread: the wait is a <see cref="Task.Delay(int)"/> loop, so a pending wait holds no
    /// thread-pool thread. While waiting, output is drained continuously (same caveat
    /// as <see cref="WaitForExit(TimeSpan)"/>: concurrent consumption of the output
    /// facades is not safe during the wait).
    /// </summary>
    public Task WaitForExitAsync(CancellationToken ct = default) => WaitForExitCoreAsync(Timeout.InfiniteTimeSpan, ct);

    /// <summary>
    /// Waits until the child exits and has been reaped, or until <paramref name="timeout"/>
    /// elapses; returns false on timeout. Non-blocking and cancellable (see
    /// <see cref="WaitForExitAsync(CancellationToken)"/>): the wait throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is canceled.
    /// </summary>
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
    /// Terminates the child with SIGKILL immediately, without giving it a chance to
    /// clean up — matching <see cref="System.Diagnostics.Process.Kill()"/>. For PTY
    /// workflows prefer <see cref="Dispose"/> (SIGHUP + closing the master), which lets
    /// the shell exit normally. The child is not reaped here; call
    /// <see cref="WaitForExit()"/> or <see cref="Dispose"/> afterwards.
    /// </summary>
    public void Kill()
    {
        // Platform hook: SIGKILL on Unix, TerminateProcess on Windows.
        KillPlatform();
    }

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

            // The reaper may still be watching an unexited child; only release the process
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
    /// Asynchronous counterpart of <see cref="Dispose()"/>: the exact same flow
    /// (SIGHUP / terminate the live child → close the terminal → bounded reap wait)
    /// without blocking a thread while waiting. Safe to await from async code paths.
    /// </summary>
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
    private static IDictionary<string, string?> ParentEnvironment() => PtyStartInfo.SnapshotParentEnvironment();

    /// <summary>Decodes a waitpid(2) status into an exit code: 0..255, or 128 + signal when killed.</summary>
    internal static int ExtractExitCode(int status)
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
        IDictionary<string, string?> environment, Encoding? inputEncoding, Encoding? outputEncoding);

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
    /// deadlock the wait; Unix has no equivalent bound. See <see cref="PtyStream.EnterExitWait"/>.
    /// </summary>
    private partial void BeginExitWait();

    /// <summary>Balances <see cref="BeginExitWait"/> when the wait returns, times out, or is canceled.</summary>
    private partial void EndExitWait();
}

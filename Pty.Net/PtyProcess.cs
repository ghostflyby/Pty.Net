using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// A child process attached to a pseudo-terminal (PTY), created via
/// <c>posix_openpt(3)</c> + <c>posix_spawn(2)</c>.
/// Use it to drive an interactive shell (e.g. bash): write commands to
/// <see cref="StandardInput"/>, read back the terminal output from
/// <see cref="StandardOutput"/>.
/// </summary>
/// <remarks>
/// I/O is exposed the way <see cref="System.Diagnostics.Process"/> does it:
/// <see cref="StandardInput"/> / <see cref="StandardOutput"/> are the text-facing
/// <see cref="System.IO.StreamWriter"/> / <see cref="System.IO.StreamReader"/>, and
/// <see cref="BaseStream"/> is the raw byte stream (the same one both text facades
/// wrap). A pty is a single bidirectional device — the child's stdout and stderr are
/// merged into the one master stream, and there is no separate stderr channel.
///
/// The master fd is non-blocking (opened via <c>posix_openpt(O_NONBLOCK)</c>) and all
/// I/O is driven by poll(2) through <see cref="PtyIoEngine"/>, so no operation ever
/// blocks a thread-pool thread and cancellation is immediate.
///
/// The process-control surface is async-capable too: <see cref="WaitForExitAsync(CancellationToken)"/>
/// waits without occupying a thread, <see cref="DisposeAsync"/> mirrors
/// <see cref="Dispose()"/>, and <see cref="Exited"/> fires once the child is reaped.
/// Reaping (waitpid) is owned by a process-wide background reaper, so exit results
/// are deterministic across concurrent waiters.
/// </remarks>
public sealed class PtyProcess : IDisposable, IAsyncDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly SemaphoreSlim gate = new(1, 1);

    // Reused by DrainOutput, which WaitForExit calls every ~10ms; a fresh allocation per
    // call would churn ~4KB each iteration. Serialized by the gate.
    private readonly byte[] drainBuf = new byte[ReadBufferSize];

    private bool disposed;

    /// <summary>OS process id of the child.</summary>
    public int Pid { get; }

    // Reaped exit code. int.MinValue means "not reaped yet"; afterwards it is 0..255,
    // 128 + signal, or -1 when the status could not be determined. Written by the
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

    private PtyProcess(PtyStream stream, int pid, Encoding? inputEncoding, Encoding? outputEncoding)
    {
        BaseStream = stream;
        // Null encoding means UTF-8 — the terminal default on both macOS and Linux.
        // The reader's read-ahead buffer is fine: this text facade owns the stream until
        // the caller switches to BaseStream (never mix — like Process's StandardOutput).
        StandardInput = new StreamWriter(stream, inputEncoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        StandardOutput = new StreamReader(stream, outputEncoding ?? Encoding.UTF8);
        Pid = pid;
        // The process-wide reaper owns waitpid for this child: it sets ExitCode and
        // raises Exited, and every WaitForExit/Dispose path just observes the result.
        PtyReaper.Watch(this);
    }

    /// <summary>
    /// Launches <paramref name="info"/> in a new PTY session (child process attached to a
    /// pseudo-terminal). Uses <c>posix_openpt(3)</c> + <c>posix_spawn(2)</c>: posix_spawn
    /// avoids <c>fork(2)</c>, so launching stays safe even when other threads are
    /// concurrently allocating memory (fork in a multithreaded process can deadlock the
    /// child on inherited malloc locks). A <see cref="System.Diagnostics.ProcessStartInfo"/>
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
        // Everything is prepared in the parent; posix_spawn performs the exec natively.
        var env = environment ?? ParentEnvironment();
        var envp = ToNative(BuildEnvironment(env));
        var argv = ToNative([Path.GetFileName(file), .. arguments]);
        var path = Marshal.StringToHGlobalAnsi(file);

        // Create the pty via posix_openpt(O_NONBLOCK) + grantpt/unlockpt/ptsname/open:
        // all non-variadic, so they work on Apple arm64 (where the variadic fcntl call
        // mis-delivers its third argument and could never set O_NONBLOCK). The master
        // fd is non-blocking from birth — the foundation of PtyStream's poll-driven I/O.
        PtyStream? stream = null;
        var masterFd = NativeMethods.posix_openpt(NativeMethods.ORdwr | NativeMethods.ONonblock);
        if (masterFd < 0 ||
            NativeMethods.grantpt(masterFd) != 0 ||
            NativeMethods.unlockpt(masterFd) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            if (masterFd >= 0)
                NativeMethods.close(masterFd);
            throw new IOException($"posix_openpt/grantpt/unlockpt failed: errno={err}");
        }

        var slavePath = Marshal.PtrToStringUTF8(NativeMethods.ptsname(masterFd)) ?? string.Empty;
        var slaveFd = NativeMethods.open(slavePath, NativeMethods.ORdwr | NativeMethods.ONoctty);
        if (slaveFd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            NativeMethods.close(masterFd);
            throw new IOException($"open slave '{slavePath}' failed: errno={err}");
        }

        var fileActions = Marshal.AllocHGlobal(NativeMethods.PosixSpawnFileActionsSize);
        var attr = Marshal.AllocHGlobal(NativeMethods.PosixSpawnAttrSize);
        var spawned = false;
        try
        {
            // PtyStream takes over the non-blocking master fd. Reads/writes are
            // poll-gated, so no thread-pool thread is ever blocked on the pty.
            stream = new PtyStream(new SafeFileHandle(new IntPtr(masterFd), ownsHandle: true));

            if (NativeMethods.posix_spawn_file_actions_init(fileActions) != 0 ||
                NativeMethods.posix_spawnattr_init(attr) != 0)
                throw new IOException($"posix_spawn init failed: errno={Marshal.GetLastPInvokeError()}");

#if OSX
            // macOS: SETSID + close-on-exec-everything so runtime fds do not leak into the shell.
            var flagsRc = NativeMethods.posix_spawnattr_setflags(
                attr,
                NativeMethods.PosixSpawnFlags.Setsid | NativeMethods.PosixSpawnFlags.CloexecDefault);
            if (flagsRc != 0)
                throw new IOException($"posix_spawnattr_setflags failed: errno={flagsRc}");
#elif LINUX
            var flagsRc = NativeMethods.posix_spawnattr_setflags(
                attr,
                NativeMethods.PosixSpawnFlags.Setsid | NativeMethods.PosixSpawnFlags.Setsigdef);
            if (flagsRc != 0)
                throw new IOException($"posix_spawnattr_setflags failed: errno={flagsRc}");

            // POSIX spawn inherits SIG_IGN dispositions from the parent (macOS resets
            // them automatically, glibc does not). The .NET runtime ignores SIGPIPE and
            // friends, so reset the common ones to their defaults for a clean shell.
            var sigdefRc = NativeMethods.posix_spawnattr_setsigdefault(
                attr,
                NativeMethods.SignalSet(
                    NativeMethods.Signals.Hup,
                    NativeMethods.Signals.Int,
                    NativeMethods.Signals.Quit,
                    NativeMethods.Signals.Pipe,
                    NativeMethods.Signals.Term));
            if (sigdefRc != 0)
                throw new IOException($"posix_spawnattr_setsigdefault failed: errno={sigdefRc}");
#else
#error "Pty.Net supports macOS (define OSX) and Linux (define LINUX) only."
#endif

            // Wire the pty slave to the child's stdio and drop our copy of it. These take
            // the exact fd numbers (referenced by the file actions inside the child), so
            // raw handle values are passed rather than SafeHandles.
            // Failures are theoretically impossible (the slave fd was just opened and
            // 0/1/2 are always valid), but checked for consistency with the other steps:
            // a silently broken stdio wiring would surface as a child with no output.
            foreach (var target in new[] { 0, 1, 2 })
            {
                if (NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slaveFd, target) != 0)
                    throw new IOException($"posix_spawn adddup2({target}) failed: errno={Marshal.GetLastPInvokeError()}");
            }
            if (NativeMethods.posix_spawn_file_actions_addclose(fileActions, slaveFd) != 0)
                throw new IOException($"posix_spawn addclose failed: errno={Marshal.GetLastPInvokeError()}");

#if LINUX
            // Linux equivalent of macOS POSIX_SPAWN_CLOEXEC_DEFAULT: close every inherited
            // fd >= 3 in the child (the .NET runtime's sockets/pipes/files) so the shell
            // starts with a clean fd table.
            if (NativeMethods.posix_spawn_file_actions_addclosefrom_np(fileActions, 3) != 0)
                throw new IOException($"posix_spawn addclosefrom failed: errno={Marshal.GetLastPInvokeError()}");
#endif

            if (workingDirectory is not null)
            {
                var chdirRc = NativeMethods.posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory);
                if (chdirRc != 0)
                    throw new IOException($"posix_spawn addchdir failed: errno={chdirRc}");
            }

            var spawnRc = NativeMethods.posix_spawn(out var pid, path, fileActions, attr, argv, envp);
            if (spawnRc != 0)
                throw new IOException($"posix_spawn failed: errno={spawnRc}");

            spawned = true;
            return new PtyProcess(stream, pid, inputEncoding, outputEncoding);
        }
        finally
        {
            NativeMethods.posix_spawn_file_actions_destroy(fileActions);
            NativeMethods.posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attr);
            NativeMethods.close(slaveFd); // parent's copy of the slave is no longer needed
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            if (!spawned)
            {
                // The PtyStream owns the master fd (SafeFileHandle, ownsHandle: true):
                // disposing it closes the fd. Only fall back to a raw close if the
                // stream was never constructed — never close an already-closed fd, as
                // the OS may have reused the number for a concurrent open.
                if (stream is not null)
                    stream.Dispose();
                else
                    NativeMethods.close(masterFd);
            }
        }
    }

    /// <summary>
    /// Blocks until the child exits or the timeout elapses. Returns false on timeout.
    /// While waiting, output is drained continuously so the child never blocks writing
    /// to a full pty buffer. Note: like <see cref="System.Diagnostics.Process.WaitForExit()"/>
    /// with redirected output, concurrent consumption of <see cref="StandardOutput"/> /
    /// <see cref="BaseStream"/> is not safe while waiting — the drained bytes are
    /// discarded, so capture output you need before or concurrently with this call.
    /// Pass <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.
    ///
    /// Reaping happens on the process-wide reaper thread; this method only observes
    /// <see cref="ExitCode"/> (set by the reaper), so it never races waitpid(2).
    /// For an asynchronous, thread-free equivalent see <see cref="WaitForExitAsync(CancellationToken)"/>.
    /// </summary>
    public bool WaitForExit(TimeSpan timeout)
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

    /// <summary>
    /// Blocks until the child exits and has been reaped. Aligns with
    /// <see cref="System.Diagnostics.Process.WaitForExit()"/> (no timeout); the infinite
    /// wait only returns once the child has been reaped.
    /// </summary>
    public void WaitForExit() => WaitForExit(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Waits until the child exits and has been reaped, like
    /// <see cref="System.Diagnostics.Process.WaitForExitAsync()"/>, without blocking a
    /// thread: the wait is a <see cref="Task.Delay"/> loop, so a pending wait holds no
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

    /// <summary>
    /// Terminates the child with SIGKILL immediately, without giving it a chance to
    /// clean up — matching <see cref="System.Diagnostics.Process.Kill()"/>. For PTY
    /// workflows prefer <see cref="Dispose"/> (SIGHUP + closing the master), which lets
    /// the shell exit normally. The child is not reaped here; call
    /// <see cref="WaitForExit()"/> or <see cref="Dispose"/> afterwards.
    /// </summary>
    public void Kill() => NativeMethods.kill(Pid, NativeMethods.Signals.Kill);

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed)
                return;
            disposed = true;

            if (!HasExited)
            {
                // The child was spawned with posix_spawn + SETSID, so it has no controlling
                // terminal: closing the pty master alone does not deliver a hangup. Signal
                // it explicitly, then close the master so its output writes fail cleanly.
                NativeMethods.kill(Pid, NativeMethods.Signals.Hup);
            }

            // Disposing the stream aborts any in-flight async operations and closes the
            // master fd (the engine defers the close until its last ref is released, so
            // no fd is ever closed while still being polled).
            BaseStream.Dispose();

            // Bounded wait for the reaper to collect the child. If it has not exited by
            // the deadline, the process-wide reaper keeps watching in the background, so
            // the child cannot linger as a zombie — only the reap completion is deferred
            // past dispose (see <see cref="Exited"/>).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!HasExited && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="Dispose()"/>: the exact same flow
    /// (SIGHUP → close the master → bounded reap wait) without blocking a thread while
    /// waiting. Safe to await from async code paths.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;

            if (!HasExited)
                NativeMethods.kill(Pid, NativeMethods.Signals.Hup);

            BaseStream.Dispose();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // --- internals ---------------------------------------------------------

    /// <summary>
    /// Called by the process-wide reaper once waitpid(2) collected this child: records
    /// the exit code and raises <see cref="Exited"/>. Runs on the shared reaper thread;
    /// handler exceptions are swallowed so one misbehaving handler cannot stall reaping
    /// for every other session.
    /// </summary>
    internal void OnReaped(int code)
    {
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

    /// <summary>A snapshot of the parent's environment, for launches with no explicit <see cref="PtyStartInfo.Environment"/>.</summary>
    private static IDictionary<string, string?> ParentEnvironment() => PtyStartInfo.SnapshotParentEnvironment();

    /// <summary>Flattens the environment dictionary into the <c>KEY=VALUE</c> array posix_spawn expects, dropping null values.</summary>
    private static string[] BuildEnvironment(IDictionary<string, string?> env)
    {
        var result = env
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();
        // Without a TERM the shell may fall back to dumb/unknown; set a sane default.
        if (!result.Any(e => e.StartsWith("TERM=", StringComparison.Ordinal)))
            result.Add("TERM=xterm-256color");
        return [.. result];
    }

    private static IntPtr[] ToNative(string[] strs)
    {
        var result = new IntPtr[strs.Length + 1];
        for (var i = 0; i < strs.Length; i++)
            result[i] = Marshal.StringToHGlobalAnsi(strs[i]);
        result[strs.Length] = IntPtr.Zero;
        return result;
    }

    private static void FreeNative(IntPtr[] arr)
    {
        foreach (var p in arr)
            if (p != IntPtr.Zero)
                Marshal.FreeHGlobal(p);
    }

    /// <summary>
    /// Non-blocking drain: reads whatever output is currently available and discards it.
    /// Used by <see cref="WaitForExit"/> so the child never blocks on a full pty buffer
    /// while nobody is reading.
    /// </summary>
    private void DrainOutput()
    {
        while (true)
        {
            // 0ms timeout: only drain what is already there, never wait.
            var n = BaseStream.Read(drainBuf, 0, out _);
            if (n <= 0)
                return; // nothing available right now, or EOF
        }
    }

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
}

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// Unix half of <see cref="PtyProcess"/>: fork/exec-based launch (replacing the
/// posix_spawn path), SIGHUP teardown and waitpid reaping. Compiled only on the
/// non-Windows target (see csproj), so the shared <c>PtyProcess.cs</c> carries no
/// platform conditionals.
/// </summary>
public sealed partial class PtyProcess
{
    // The stdio targets the pty slave is dup2'd onto in the child. A static array so
    // each spawn does not allocate a fresh int[] for the loop below.
    private static readonly int[] StdioTargets = [0, 1, 2];

    // Shared drain buffer, used by DrainOutput (the exit-wait loops call it every ~2 ms).
    private const int ReadBufferSize = 4096;
    private static readonly byte[] DrainBuffer = new byte[ReadBufferSize];
    private static readonly Lock DrainLock = new();

    private static partial PtyProcess StartPlatform(
        string file, IReadOnlyList<string> arguments, string? workingDirectory,
        IDictionary<string, string?> environment, Encoding? inputEncoding, Encoding? outputEncoding,
        int initialCols, int initialRows)
    {
        // Prepare everything the child needs in the parent, before the no-GC region.
        var envp = ToNative(BuildEnvironment(environment));
        var argv = ToNative([Path.GetFileName(file), .. arguments]);
        var path = Marshal.StringToHGlobalAnsi(file);

        // Create the pty. The master fd is non-blocking from birth.
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

        var slavePath = ResolveSlavePath(masterFd);
        // O_NOCTTY: the child re-acquires the terminal itself via TIOCSCTTY after
        // setsid(); opening it with O_NOCTTY here keeps the parent from ever attaching.
        // O_CLOEXEC: the slave fd must not leak into the exec'd child — and it must not
        // be explicitly closed in the fork child either (a close(2) on a live fd in the
        // fork child hangs on macOS, where runtime fd bookkeeping locks are gone after
        // fork). CLOEXEC lets the kernel close it at exec instead.
        var slaveFd = NativeMethods.open(slavePath, NativeMethods.ORdwr | NativeMethods.ONoctty | NativeMethods.OCloexec);
        if (slaveFd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            NativeMethods.close(masterFd);
            throw new IOException($"open slave '{slavePath}' failed: errno={err}");
        }

        // Apply the requested initial size before the child starts.
        Span<NativeMethods.Winsize> winsize = stackalloc NativeMethods.Winsize[1];
        winsize[0] = new NativeMethods.Winsize { Row = (ushort)initialRows, Col = (ushort)initialCols };
        int resizeRc;
        unsafe
        {
            fixed (NativeMethods.Winsize* p = winsize)
            {
                resizeRc = NativeMethods.IoCtl(masterFd, NativeMethods.Tiocswinsz, (IntPtr)p);
            }
        }
        if (resizeRc != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            NativeMethods.close(masterFd);
            NativeMethods.close(slaveFd);
            throw new IOException($"pty resize failed: errno={err}");
        }

        // Error-report pipe (parent read end, child write end, CLOEXEC on the write end):
        // the child writes its errno here when exec fails; a successful exec closes the
        // write end (CLOEXEC), so the parent reads EOF.
        var errPipe = new int[2];
        if (NativeMethods.pipe(errPipe) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            NativeMethods.close(masterFd);
            NativeMethods.close(slaveFd);
            throw new IOException($"pipe failed: errno={err}");
        }
        // Fcntl helper: on Apple arm64 fcntl's variadic third argument must go through
        // the pad-register form, or the CLOEXEC bit never lands and the error pipe's
        // write end survives exec in the child — which makes the parent's exec-result
        // read block forever (the child's exec does not close the pipe).
        NativeMethods.Fcntl(errPipe[1], NativeMethods.FSetfd, NativeMethods.FdCloexec);

        var spawned = false;
        try
        {
            stream = new PtyStream(new SafeFileHandle(new IntPtr(masterFd), ownsHandle: true));

            // Fork critical section. A no-GC region pauses the GC for the duration of
            // the fork so the child's inherited heap/allocator state is consistent (the
            // experiment suite measured ~0.5% child hangs under concurrent allocation
            // pressure without it, 0/4000 with it). But the no-GC region is
            // process-global: concurrent spawns would fight over it, so the whole
            // fork+region is serialized with a lock. Other threads still allocate
            // during the region (their objects just accumulate until it ends, which the
            // budget absorbs), so this does not stall the process — it only guarantees
            // one fork at a time.
            int pid;
            lock (ForkLock)
            {
                if (!GC.TryStartNoGCRegion(ForkNoGcBudget, true))
                    throw new IOException("fork launch failed: the GC could not be paused (concurrent GC in progress).");

                var inNoGcRegion = true;
                try
                {
                    pid = NativeMethods.fork();
                    if (pid < 0)
                    {
                        var err = Marshal.GetLastPInvokeError();
                        GC.EndNoGCRegion();
                        inNoGcRegion = false;
                        FreeNative(envp);
                        FreeNative(argv);
                        Marshal.FreeHGlobal(path);
                        NativeMethods.close(errPipe[0]);
                        NativeMethods.close(errPipe[1]);
                        NativeMethods.close(slaveFd);
                        stream.Dispose();
                        throw new IOException($"fork failed: errno={err}");
                    }
                    if (pid == 0)
                    {
                        // Child: never returns, never allocates.
                        NativeMethods.close(errPipe[0]);
                        NativeMethods.close(masterFd);
                        unsafe
                        {
                            fixed (IntPtr* argvP = argv)
                            fixed (IntPtr* envpP = envp)
                            {
                                ChildMain(path, (IntPtr)argvP, (IntPtr)envpP, workingDirectory, errPipe[1], slaveFd);
                            }
                        }
                        NativeMethods._exit(127); // unreachable
                    }
                    GC.EndNoGCRegion();
                    inNoGcRegion = false;
                }
                catch
                {
                    if (inNoGcRegion)
                    {
                        GC.EndNoGCRegion();
                        inNoGcRegion = false;
                    }
                    throw;
                }

                // Parent cleanup + wait for the exec result. Still inside the lock:
                // the blocking read is bounded by the child's exec (or failure write),
                // and holding the lock keeps concurrent spawns from piling up a second
                // no-GC region while this one is still draining.
                NativeMethods.close(errPipe[1]);
                var execErrno = ReadChildExecError(errPipe[0]);
                if (execErrno >= 0)
                    throw TranslateSpawnError(file, execErrno);

                NativeMethods.close(slaveFd);
            }

            spawned = true;
            return new PtyProcess(stream, pid, inputEncoding, outputEncoding, processHandle: null);
        }
        finally
        {
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            NativeMethods.close(errPipe[0]);
            if (!spawned)
            {
                if (stream is not null)
                    stream.Dispose();
                else
                    NativeMethods.close(masterFd);
            }
        }
    }

    /// <summary>
    /// Serializes the fork critical section (see <see cref="StartPlatform"/>): the
    /// no-GC region is process-global, so concurrent spawns must not enter it
    /// simultaneously. Only the fork itself and the exec-result read are held — a
    /// microsecond-scale critical section in the common case.
    /// </summary>
    private static readonly Lock ForkLock = new();

    /// <summary>No-GC budget for the fork critical section (a safety margin; the region
    /// is held only for the duration of fork(2)).</summary>
    private const long ForkNoGcBudget = 32 * 1024 * 1024;

    /// <summary>
    /// The child's post-fork entry point. Runs in the forked copy with the no-GC region
    /// active: must not allocate managed objects, touch runtime locks, or return. Does
    /// the libc-only setup that posix_spawn's file actions used to do:
    ///   * dup2 the inherited slave fd onto 0/1/2;
    ///   * setsid() + ioctl(0, TIOCSCTTY, 0) to acquire the controlling terminal;
    ///   * chdir to the working directory when requested;
    ///   * a per-fd close sweep (fd 3..RLIMIT_NOFILE);
    ///   * execve. On failure writes errno to the pipe and _exit(127).
    /// </summary>
    private static unsafe void ChildMain(
        IntPtr path, IntPtr argv, IntPtr envp, string? workingDirectory,
        int errWrite, int slaveFd)
    {
        foreach (var target in StdioTargets)
        {
            if (NativeMethods.dup2(slaveFd, target) != target)
            {
                ReportChildError(errWrite, NativeMethods.Eacces);
                NativeMethods._exit(127);
            }
        }

        if (NativeMethods.setsid() < 0 || NativeMethods.IoCtl(0, NativeMethods.Tiocsctty, IntPtr.Zero) != 0)
        {
            ReportChildError(errWrite, NativeMethods.Eacces);
            NativeMethods._exit(127);
        }

        if (workingDirectory is not null && NativeMethods.chdir(workingDirectory) != 0)
        {
            ReportChildError(errWrite, NativeMethods.ENoent);
            NativeMethods._exit(127);
        }

        // The inherited slave fd (dup2'd onto 0/1/2) and the error pipe are CLOEXEC,
        // so the kernel closes them at exec. .NET's own fds are all CLOEXEC too, so no
        // explicit close is needed here — and none is allowed: a close(2) on any live
        // fd in the fork child hangs on macOS, where runtime fd bookkeeping locks are
        // gone after fork.

        NativeMethods.execve(path, argv, envp);
        ReportChildError(errWrite, Marshal.GetLastPInvokeError());
        NativeMethods._exit(127);
    }

    /// <summary>Writes a single errno int into the exec-failure pipe (child side, no allocation).</summary>
    private static unsafe void ReportChildError(int errWrite, int errno)
    {
        var slot = stackalloc int[1];
        slot[0] = errno;
        _ = NativeMethods.write(errWrite, (IntPtr)slot, (nuint)sizeof(int));
    }

    /// <summary>
    /// Reads the exec result from the error pipe. Returns -1 for success (EOF), or the
    /// child's errno on failure.
    /// </summary>
    private static unsafe int ReadChildExecError(int fd)
    {
        Span<byte> buf = stackalloc byte[4];
        var total = 0;
        fixed (byte* p = buf)
        {
            while (total < 4)
            {
                var r = NativeMethods.read(fd, (IntPtr)(p + total), (nuint)(4 - total));
                switch (r)
                {
                    case > 0:
                        total += (int)r;
                        continue;
                    case 0:
                        return -1; // EOF: exec succeeded (write end closed by CLOEXEC)
                }

                var err = Marshal.GetLastPInvokeError();
                if (err == NativeMethods.Eintr)
                    continue;
                return -1; // read error: treat as launched (defensive)
            }
        }
        return MemoryMarshal.Read<int>(buf);
    }

    // Serializes the ptsname(3) static-buffer read on macOS (which has no ptsname_r).
#if OSX
    private static readonly Lock PtsnameLock = new();
#endif

    /// <summary>
    /// Resolves the slave device path for <paramref name="masterFd"/> in a thread-safe
    /// way. Linux uses ptsname_r with a caller-owned stack buffer; macOS has no
    /// ptsname_r, so the static-buffer read is serialized with <c>PtsnameLock</c>.
    /// </summary>
    private static string ResolveSlavePath(int masterFd)
    {
#if LINUX
        Span<byte> buf = stackalloc byte[NativeMethods.PtsPathMax];
        int rc;
        unsafe
        {
            fixed (byte* p = buf)
            {
                rc = NativeMethods.ptsname_r(masterFd, (IntPtr)p, (nuint)buf.Length);
            }
        }
        if (rc != 0)
            throw new IOException($"ptsname failed: errno={rc}");
        var len = buf.IndexOf((byte)0);
        if (len < 0)
            len = buf.Length;
        return Encoding.UTF8.GetString(buf[..len]);
#elif OSX
        lock (PtsnameLock)
        {
            var path = Marshal.PtrToStringUTF8(NativeMethods.ptsname(masterFd));
            return path ?? throw new IOException($"ptsname failed: errno={Marshal.GetLastPInvokeError()}");
        }
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif
    }

    /// <summary>
    /// Signals the child before the terminal is closed so it exits instead of hanging
    /// without a controlling terminal (SIGHUP on Unix; on Windows a live child is
    /// terminated so ClosePseudoConsole does not wait on it indefinitely).
    /// </summary>
    private void SignalChildIfAlive()
    {
        if (!HasExited)
        {
            RequestClosePlatform();
        }
    }

    /// <summary>Unix: SIGHUP the child — the terminal-hangup signal (see <see cref="PtyProcess.RequestClose"/>).</summary>
    private partial void RequestClosePlatform() => NativeMethods.kill(Pid, NativeMethods.Signals.Hup);

    /// <summary>Unix: the pty stream is the facade stream; no transcoding is involved.</summary>
    private partial void CreateFacades(
        Encoding inputEncoding, Encoding outputEncoding,
        out Stream inputFacadeStream, out Stream outputFacadeStream)
    {
        inputFacadeStream = BaseStream;
        outputFacadeStream = BaseStream;
    }

    /// <summary>Unix: SIGKILL the child.</summary>
    private partial void KillPlatform() => NativeMethods.kill(Pid, NativeMethods.Signals.Kill);

    /// <summary>
    /// Non-blocking drain: reads whatever output is currently available and discards it.
    /// Used by <see cref="WaitForExit(TimeSpan)"/> so the child never blocks on a full pty buffer
    /// while nobody is reading.
    /// </summary>
    private partial void DrainOutput()
    {
        lock (DrainLock)
        {
            while (true)
            {
                var n = BaseStream.Read(DrainBuffer, 0, out _);
                if (n <= 0)
                    return;
            }
        }
    }

    /// <summary>Unix has no teardown work that must run off the shared reaper thread.</summary>
    private partial void OnReapedPlatform()
    {
    }

    /// <summary>Unix: the wait drains through the stream directly; no buffer bound to lift.</summary>
    private partial void BeginExitWait()
    {
    }

    /// <summary>Balances <see cref="BeginExitWait"/>.</summary>
    private partial void EndExitWait()
    {
    }

    /// <summary>
    /// Single non-blocking reap attempt for the child: waitpid(WNOHANG). Returns true
    /// when the child was collected (or is unreachable), with the exit code.
    /// </summary>
    private partial bool TryReapPlatform(out int exitCode)
    {
        while (true)
        {
            var r = NativeMethods.waitpid(Pid, out var status, NativeMethods.WaitOptions.Wnohang);
            switch (r)
            {
                case > 0:
                    exitCode = ExtractExitCode(status);
                    return true;
                case 0:
                    exitCode = -1;
                    return false; // still running
            }

            var err = Marshal.GetLastPInvokeError();
            if (err == NativeMethods.Eintr)
                continue;

            exitCode = -1;
            return true;
        }
    }

    /// <summary>
    /// Translates a fork/exec errno into the BCL exception type that names the cause.
    /// </summary>
    private static Exception TranslateSpawnError(string file, int errno)
    {
        return errno switch
        {
            NativeMethods.ENoent => new FileNotFoundException($"The executable '{file}' was not found."),
            NativeMethods.Enotdir => new DirectoryNotFoundException($"A component of the executable path '{file}' is not a directory."),
            NativeMethods.Eacces => new UnauthorizedAccessException($"The executable '{file}' could not be executed: permission denied."),
            _ => new IOException($"fork/exec failed for '{file}': errno={errno}"),
        };
    }

    /// <summary>Flattens the environment dictionary into the <c>KEY=VALUE</c> array, dropping null values.</summary>
    private static List<string> BuildEnvironment(IDictionary<string, string?> env)
    {
        var result = env
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();
        if (!result.Any(e => e.StartsWith("TERM=", StringComparison.Ordinal)))
            result.Add("TERM=xterm-256color");
        return result;
    }

    private static IntPtr[] ToNative(IReadOnlyList<string> strs)
    {
        var result = new IntPtr[strs.Count + 1];
        for (var i = 0; i < strs.Count; i++)
            result[i] = Marshal.StringToHGlobalAnsi(strs[i]);
        result[strs.Count] = IntPtr.Zero;
        return result;
    }

    private static void FreeNative(IntPtr[] arr)
    {
        foreach (var p in arr)
            if (p != IntPtr.Zero)
                Marshal.FreeHGlobal(p);
    }
}

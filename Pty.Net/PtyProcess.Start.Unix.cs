using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// Unix half of <see cref="PtyProcess"/>: fork-based launch with in-place exec
/// (macOS: posix_spawn's Apple SETEXEC attribute applies the session/ctty/fd
/// isolation kernel-side; Linux: libc-only setsid + open + dup2 + execve),
/// SIGHUP teardown and waitpid reaping. Compiled only on the non-Windows target
/// (see csproj), so the shared <c>PtyProcess.cs</c> carries no platform
/// conditionals.
/// </summary>
public sealed partial class PtyProcess
{
    // Shared drain buffer, used by DrainOutput (the exit-wait loops call it every ~2 ms).
    private const int ReadBufferSize = 4096;
    private static readonly byte[] DrainBuffer = new byte[ReadBufferSize];
    private static readonly Lock DrainLock = new();
    private static readonly ChildNativeApi ChildApi = ResolveChildNativeApi();
    private static readonly bool ForkChildPathPrepared = PrepareForkChildPath();

    private static partial PtyProcess StartPlatform(
        string file, IReadOnlyList<string> arguments, string? workingDirectory,
        IDictionary<string, string?> environment, Encoding? inputEncoding, Encoding? outputEncoding,
        int initialCols, int initialRows)
    {
        // Prepare everything the child needs in the parent, before the no-GC region.
        var executablePath = ResolveExecutablePath(file, environment);
        var envp = ToNative(BuildEnvironment(environment));
        var argv = ToNative([Path.GetFileName(file), .. arguments]);
        var path = Marshal.StringToHGlobalAnsi(executablePath);
        var workingDirectoryPath = workingDirectory is null
            ? IntPtr.Zero
            : Marshal.StringToHGlobalAnsi(workingDirectory);

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
            Marshal.FreeHGlobal(workingDirectoryPath);
            if (masterFd >= 0)
                NativeMethods.close(masterFd);
            throw new IOException($"posix_openpt/grantpt/unlockpt failed: errno={err}");
        }

        var slavePath = ResolveSlavePath(masterFd);
        var slavePathPtr = Marshal.StringToHGlobalAnsi(slavePath);

        // Open the slave here and keep it open across the fork. On macOS it is the
        // dup2 source for the child's posix_spawn file actions, so it must NOT carry
        // O_CLOEXEC: with CLOEXEC_DEFAULT the kernel can drop it before the file
        // actions run (observed EBADF). CLOEXEC_DEFAULT still removes it from the
        // spawned image, and the parent closes its copy after the exec result. On
        // Linux the child re-opens the slave itself after setsid; this fd only keeps
        // the tty side initialized so the winsize set below survives. O_NOCTTY: the
        // parent must never acquire the terminal.
#if OSX
        var slaveFd = NativeMethods.open(slavePath, NativeMethods.ORdwr | NativeMethods.ONoctty);
#elif LINUX
        var slaveFd = NativeMethods.open(slavePath, NativeMethods.ORdwr | NativeMethods.ONoctty | NativeMethods.OCloexec);
#endif
        if (slaveFd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            Marshal.FreeHGlobal(workingDirectoryPath);
            Marshal.FreeHGlobal(slavePathPtr);
            NativeMethods.close(masterFd);
            throw new IOException($"open slave '{slavePath}' failed: errno={err}");
        }

        // Apply the requested initial size before the child starts, on the master like
        // the master read/write path itself (the master accepts TIOCSWINSZ now that the
        // slave side is open).
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
            NativeMethods.close(slaveFd);
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            Marshal.FreeHGlobal(workingDirectoryPath);
            Marshal.FreeHGlobal(slavePathPtr);
            NativeMethods.close(masterFd);
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
            Marshal.FreeHGlobal(workingDirectoryPath);
            Marshal.FreeHGlobal(slavePathPtr);
            NativeMethods.close(masterFd);
            throw new IOException($"pipe failed: errno={err}");
        }
        // All child-only copies close in the kernel at exec. The child never calls
        // close(2) between fork and exec.
        if (NativeMethods.Fcntl(masterFd, NativeMethods.FSetfd, NativeMethods.FdCloexec) != 0 ||
            NativeMethods.Fcntl(errPipe[0], NativeMethods.FSetfd, NativeMethods.FdCloexec) != 0 ||
            NativeMethods.Fcntl(errPipe[1], NativeMethods.FSetfd, NativeMethods.FdCloexec) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            Marshal.FreeHGlobal(workingDirectoryPath);
            Marshal.FreeHGlobal(slavePathPtr);
            NativeMethods.close(masterFd);
            NativeMethods.close(errPipe[0]);
            NativeMethods.close(errPipe[1]);
            throw new IOException($"fcntl(FD_CLOEXEC) failed: errno={err}");
        }

        var spawned = false;
        // macOS: the child's posix_spawn(SETEXEC) call consumes these parent-built
        // spawn attribute / file-actions buffers, so they live across the fork and are
        // destroyed in the finally below. Linux does its setup in the child directly.
        var fileActions = IntPtr.Zero;
        var spawnAttr = IntPtr.Zero;
        try
        {
#if OSX
            // Build the spawn description once, in the parent: the pty slave dup2'd onto
            // stdio, an optional working directory, and the attribute set that makes the
            // child's posix_spawn call
            //   * SETEXEC — replace the calling (forked) process instead of spawning,
            //   * SETSID — create the session + controlling terminal kernel-side, the
            //     shape that does not hit the userspace-setsid exit block on macOS,
            //   * CLOEXEC_DEFAULT — every inherited fd above stdio closes automatically.
            fileActions = Marshal.AllocHGlobal(NativeMethods.PosixSpawnFileActionsSize);
            spawnAttr = Marshal.AllocHGlobal(NativeMethods.PosixSpawnAttrSize);
            if (NativeMethods.posix_spawn_file_actions_init(fileActions) != 0 ||
                NativeMethods.posix_spawnattr_init(spawnAttr) != 0)
            {
                throw new IOException($"posix_spawn init failed: errno={Marshal.GetLastPInvokeError()}");
            }

            foreach (var target in new[] { 0, 1, 2 })
            {
                if (NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slaveFd, target) != 0)
                    throw new IOException($"posix_spawn adddup2({target}) failed: errno={Marshal.GetLastPInvokeError()}");
            }
            if (workingDirectory is not null &&
                NativeMethods.posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory) != 0)
            {
                throw new IOException($"posix_spawn addchdir failed: errno={Marshal.GetLastPInvokeError()}");
            }
            var flagsRc = NativeMethods.posix_spawnattr_setflags(
                spawnAttr,
                NativeMethods.PosixSpawnFlags.Setexec |
                NativeMethods.PosixSpawnFlags.Setsid |
                NativeMethods.PosixSpawnFlags.CloexecDefault);
            if (flagsRc != 0)
                throw new IOException($"posix_spawnattr_setflags failed: errno={flagsRc}");
#endif

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
                _ = ForkChildPathPrepared;
                if (!GC.TryStartNoGCRegion(ForkNoGcBudget, true))
                    throw new IOException("fork launch failed: the GC could not be paused (concurrent GC in progress).");

                var inNoGcRegion = true;
                try
                {
                    pid = NativeMethods.fork();
                    if (pid < 0)
                    {
                        var err = Marshal.GetLastPInvokeError();
                        throw new IOException($"fork failed: errno={err}");
                    }
                    if (pid == 0)
                    {
                        // Child: never returns, never allocates, and never closes an fd.
                        // All libc calls below go through function pointers resolved in
                        // the parent: the generated P/Invoke IL stub is compiled lazily
                        // on first call, and under a concurrent fork it can block on an
                        // inherited CoreCLR code-heap lock inside the forked child.
                        unsafe
                        {
                            fixed (IntPtr* argvP = argv)
                            fixed (IntPtr* envpP = envp)
                            {
                                ChildMain(
                                    path,
                                    (IntPtr)argvP,
                                    (IntPtr)envpP,
                                    workingDirectoryPath,
                                    slavePathPtr,
                                    fileActions,
                                    spawnAttr,
                                    errPipe[1],
                                    ChildApi);
                                ChildApi.Exit(127); // unreachable
                            }
                        }
                    }
                    PtyDiagnostics.Log($"fork parent child-pid={pid} slave-path={slavePath}");
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
                errPipe[1] = -1;
                var execErrno = ReadChildExecError(errPipe[0]);
                if (execErrno >= 0)
                {
                    ReapFailedExec(pid);
                    throw TranslateSpawnError(file, execErrno);
                }

                NativeMethods.close(slaveFd);
                slaveFd = -1;
            }

            spawned = true;
            PtyDiagnostics.Log($"start published pid={pid}");
            return new PtyProcess(stream, pid, inputEncoding, outputEncoding, processHandle: null);
        }
        finally
        {
#if OSX
            if (fileActions != IntPtr.Zero)
            {
                NativeMethods.posix_spawn_file_actions_destroy(fileActions);
                Marshal.FreeHGlobal(fileActions);
            }
            if (spawnAttr != IntPtr.Zero)
            {
                NativeMethods.posix_spawnattr_destroy(spawnAttr);
                Marshal.FreeHGlobal(spawnAttr);
            }
#endif
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            Marshal.FreeHGlobal(workingDirectoryPath);
            Marshal.FreeHGlobal(slavePathPtr);
            if (errPipe[0] >= 0)
                NativeMethods.close(errPipe[0]);
            if (errPipe[1] >= 0)
                NativeMethods.close(errPipe[1]);
            if (slaveFd >= 0)
                NativeMethods.close(slaveFd);
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

    private static unsafe bool PrepareForkChildPath()
    {
        Prepare((ChildMainDelegate)ChildMain);
        Prepare((ReportChildErrorDelegate)ReportChildError);
        return true;

        static void Prepare(Delegate method)
            => RuntimeHelpers.PrepareMethod(method.Method.MethodHandle);
    }

    private static unsafe ChildNativeApi ResolveChildNativeApi()
    {
        var process = NativeLibrary.GetMainProgramHandle();
        var api = new ChildNativeApi
        {
            Write = (delegate* unmanaged[Cdecl]<int, IntPtr, nuint, nint>)NativeLibrary.GetExport(process, "write"),
            Exit = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(process, "_exit"),
#if OSX
            // The kernel-side spawn machinery does the whole setup (see ChildMain);
            // only posix_spawn, the failure write and _exit run in the forked child.
            PosixSpawn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(process, "posix_spawn"),
#elif LINUX
            Dup2 = (delegate* unmanaged[Cdecl]<int, int, int>)NativeLibrary.GetExport(process, "dup2"),
            Setsid = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(process, "setsid"),
            Open = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(process, "open"),
            Chdir = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(process, "chdir"),
            Execve = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(process, "execve"),
            Close = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(process, "close"),
            Error = (delegate* unmanaged[Cdecl]<int*>)NativeLibrary.GetExport(process, "__errno_location"),
#endif
        };
        WarmUpChildSignatures(api);
        return api;
    }

    /// <summary>
    /// Runs every calli signature the child uses once in the parent with harmless
    /// arguments. The managed→native calli stub is generated lazily on the first call;
    /// if that first call happened inside the forked child it would JIT under the
    /// inherited CoreCLR code-heap lock and hang the child before exec (observed as
    /// GenericPInvokeCalliStubWorker waiting in __psynch_mutexwait). Each warm-up call
    /// fails harmlessly at the libc level (EBADF/EFAULT/EINVAL/ENOENT).
    /// </summary>
    private static unsafe void WarmUpChildSignatures(ChildNativeApi api)
    {
#if OSX
        // posix_spawn validates its arguments before doing anything: all-null inputs
        // return EINVAL without touching processes. The SETEXEC attribute is not needed
        // for the warm-up — only the call signature (VASigCookie) must be compiled.
        // _exit itself must never be warmed by calling it; close(int)->int shares its
        // signature (Exit is declared int-returning, which is ABI-invisible for a
        // call that never returns).
        _ = api.PosixSpawn(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ = api.Write(-1, IntPtr.Zero, 0);
        var close = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "close");
        _ = close(-1);
#elif LINUX
        _ = api.Dup2(-1, -1);
        _ = api.Open((IntPtr)1, 0);
        _ = api.Chdir(IntPtr.Zero);
        _ = api.Execve(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ = api.Write(-1, IntPtr.Zero, 0);
        _ = api.Close(-1);
        _ = api.Error();
#endif
    }

#if OSX
    private unsafe struct ChildNativeApi
    {
        internal delegate* unmanaged[Cdecl]<int, IntPtr, nuint, nint> Write;
        internal delegate* unmanaged[Cdecl]<int, int> Exit;
        // posix_spawn(pid*, path, file_actions, attr, argv, envp) — with the SETEXEC
        // attr flag this replaces the calling (forked) process in place.
        internal delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int> PosixSpawn;
    }
#elif LINUX
    private unsafe struct ChildNativeApi
    {
        internal delegate* unmanaged[Cdecl]<int, int, int> Dup2;
        internal delegate* unmanaged[Cdecl]<int> Setsid;
        internal delegate* unmanaged[Cdecl]<IntPtr, int, int> Open;
        internal delegate* unmanaged[Cdecl]<IntPtr, int> Chdir;
        internal delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> Execve;
        internal delegate* unmanaged[Cdecl]<int, IntPtr, nuint, nint> Write;
        internal delegate* unmanaged[Cdecl]<int, int> Close;
        internal delegate* unmanaged[Cdecl]<int, int> Exit;
        internal delegate* unmanaged[Cdecl]<int*> Error;
    }
#endif

    private unsafe delegate void ChildMainDelegate(
        IntPtr path, IntPtr argv, IntPtr envp, IntPtr workingDirectory,
        IntPtr ptySlavePath, IntPtr fileActions, IntPtr spawnAttr, int errWrite, ChildNativeApi api);

    private unsafe delegate void ReportChildErrorDelegate(ChildNativeApi api, int errWrite, int errno);

    /// <summary>
    /// The child's post-fork entry point. Runs in the forked copy with the no-GC region
    /// active: must not allocate managed objects, touch runtime locks, or return.
    /// Signal dispositions are intentionally not touched: exec(2) resets every *caught*
    /// signal to SIG_DFL and preserves *ignored* ones — the same state posix_spawn
    /// produces. Every libc call goes through <paramref name="api"/>'s function
    /// pointers, whose calli stubs were warmed up in the parent before the fork: a
    /// lazily-generated stub would JIT inside the forked child under the inherited
    /// CoreCLR code-heap lock and hang the spawn.
    /// <para>macOS: a single posix_spawn call with the Apple SETEXEC attribute —
    /// the kernel-side spawn machinery applies the parent-built file actions (pty
    /// slave onto 0/1/2, optional chdir), creates the session and controlling terminal
    /// (SETSID — the shape that does not hit the userspace-setsid exit block), closes
    /// every other inherited fd (CLOEXEC_DEFAULT) and replaces this image, all in
    /// place. On failure it returns the errno, which goes to the parent's pipe.</para>
    /// <para>Linux: glibc/musl have no SETEXEC/CLOEXEC_DEFAULT, so the libc-only setup
    /// is done directly: setsid(), then open the pty slave (no O_NOCTTY) so the session
    /// leader acquires it as the controlling terminal, dup2 it onto 0/1/2, a per-fd
    /// close sweep over the inherited fds above stdio, chdir when requested, execve.
    /// On failure writes errno to the pipe and _exit(127).</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void ChildMain(
        IntPtr path, IntPtr argv, IntPtr envp, IntPtr workingDirectory,
        IntPtr ptySlavePath, IntPtr fileActions, IntPtr spawnAttr, int errWrite, ChildNativeApi api)
    {
#if OSX
        int spawnedPid = 0;
        var rc = api.PosixSpawn(
            (IntPtr)(&spawnedPid), path, fileActions, spawnAttr, argv, envp);
        // SETEXEC success never returns here; a failure returns the errno.
        ReportChildError(api, errWrite, rc);
        api.Exit(127);
#elif LINUX
        if (api.Setsid() < 0)
        {
            ReportChildError(api, errWrite, NativeMethods.Eacces);
            api.Exit(127);
        }

        var cttyFd = api.Open(ptySlavePath, NativeMethods.ORdwr | NativeMethods.OCloexec);
        if (cttyFd < 0)
        {
            ReportChildError(api, errWrite, NativeMethods.ENoent);
            api.Exit(127);
        }

        if (api.Dup2(cttyFd, 0) != 0 ||
            api.Dup2(cttyFd, 1) != 1 ||
            api.Dup2(cttyFd, 2) != 2)
        {
            ReportChildError(api, errWrite, NativeMethods.Eacces);
            api.Exit(127);
        }

        // Close every inherited fd above stdio except the error pipe: the fork child
        // inherits the parent's whole fd table, and non-CLOEXEC fds (sockets from
        // native libraries, the reaper's kqueue) would leak into the exec'd program —
        // the same hole POSIX_SPAWN_CLOEXEC_DEFAULT closes on macOS.
        for (var fd = 3; fd < NativeMethods.FdIsolationCap; fd++)
        {
            if (fd != errWrite)
                _ = api.Close(fd);
        }

        if (workingDirectory != IntPtr.Zero && api.Chdir(workingDirectory) != 0)
        {
            ReportChildError(api, errWrite, NativeMethods.ENoent);
            api.Exit(127);
        }

        // The ctty fd above was dup2'd onto 0/1/2; its original fd and the error pipe
        // are CLOEXEC, so the kernel closes them at exec.
        api.Execve(path, argv, envp);
        ReportChildError(api, errWrite, *api.Error());
        api.Exit(127);
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif
    }

    /// <summary>Writes a single errno int into the exec-failure pipe (child side, no allocation).</summary>
    private static unsafe void ReportChildError(ChildNativeApi api, int errWrite, int errno)
    {
        var slot = stackalloc int[1];
        slot[0] = errno;
        _ = api.Write(errWrite, (IntPtr)slot, (nuint)sizeof(int));
    }

    private static void ReapFailedExec(int pid)
    {
        while (NativeMethods.waitpid(pid, out _, NativeMethods.WaitOptions.None) < 0)
        {
            if (Marshal.GetLastPInvokeError() != NativeMethods.Eintr)
                return;
        }
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
        if (HasExited)
            return;

        RequestClosePlatform();
        // Dispose is closing the terminal session, not merely sending a signal. Closing
        // the master is what makes the slave hang up and lets an interactive shell finish
        // its SIGHUP exit path. RequestClose() remains signal-only for callers that want
        // to keep reading from the terminal while waiting explicitly.
        BaseStream.Dispose();
    }

    /// <summary>Unix: SIGHUP the child — the terminal-hangup signal (see <see cref="PtyProcess.RequestClose"/>).</summary>
    private partial void RequestClosePlatform()
    {
        var result = NativeMethods.kill(Pid, NativeMethods.Signals.Hup);
        var errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        PtyDiagnostics.Log($"sighup pid={Pid} result={result} errno={errno} state={DescribeUnixState()}");
    }

    /// <summary>Unix: the pty stream is the facade stream; no transcoding is involved.</summary>
    private partial void CreateFacades(
        Encoding inputEncoding, Encoding outputEncoding,
        out Stream inputFacadeStream, out Stream outputFacadeStream)
    {
        inputFacadeStream = BaseStream;
        outputFacadeStream = BaseStream;
    }

    /// <summary>Unix: SIGKILL the child.</summary>
    private partial void KillPlatform()
    {
        var result = NativeMethods.kill(Pid, NativeMethods.Signals.Kill);
        var errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        PtyDiagnostics.Log($"sigkill pid={Pid} result={result} errno={errno} state={DescribeUnixState()}");
    }

    /// <summary>
    /// True while the child is stuck mid-exit: still visible to kill(0) but already
    /// disassociated from its process group and not yet reapable. On macOS a fork-spawned
    /// session leader holding the controlling terminal parks here indefinitely — the
    /// final slave close inside the kernel's exit path waits (non-interruptibly) for the
    /// tty output to drain to the pty master, and if nobody reads the master nothing
    /// drains it. posix_spawn's in-kernel session setup does not hit this; closing the
    /// master ends the wait.
    /// </summary>
    internal bool IsStuckExiting()
    {
        if (HasExited || BaseStream.IsClosed)
            return false;
        var probe = NativeMethods.kill(Pid, 0);
        if (probe != 0)
            return false;
        var pgid = NativeMethods.getpgid(Pid);
        return pgid < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Esrch;
    }

    /// <summary>
    /// Closes the pty master to end a stuck mid-exit (see <see cref="IsStuckExiting"/>).
    /// The child has already finished its exit path; only the tty teardown is blocked.
    /// Unread terminal output is abandoned — the same trade-off Dispose makes on Unix.
    /// </summary>
    internal void CloseTerminalForStuckExit()
    {
        try
        {
            BaseStream.Dispose();
        }
        catch
        {
            // Already closed by Dispose racing us: nothing left to do.
        }
    }

    private static string DescribeUnixState(int pid)
    {
        var probe = NativeMethods.kill(pid, 0);
        var probeErrno = probe == 0 ? 0 : Marshal.GetLastPInvokeError();
        var pgid = NativeMethods.getpgid(pid);
        var pgidErrno = pgid < 0 ? Marshal.GetLastPInvokeError() : 0;
        var sid = NativeMethods.getsid(pid);
        var sidErrno = sid < 0 ? Marshal.GetLastPInvokeError() : 0;
        return $"alive-probe={probe} errno={probeErrno} pgid={pgid} pgid-errno={pgidErrno} sid={sid} sid-errno={sidErrno}";
    }

    private string DescribeUnixState() => DescribeUnixState(Pid);

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
                    PtyDiagnostics.Log($"waitpid completed pid={Pid} result={r} status={status}");
                    exitCode = ExtractExitCode(status);
                    return true;
                case 0:
                    exitCode = -1;
                    return false; // still running
            }

            var err = Marshal.GetLastPInvokeError();
            if (err == NativeMethods.Eintr)
                continue;

            PtyDiagnostics.Log($"waitpid pid={Pid} result={r} errno={err}");
            exitCode = -1;
            return false;
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

    private static string ResolveExecutablePath(string file, IDictionary<string, string?> environment)
    {
        if (file.Contains(Path.DirectorySeparatorChar))
            return file;

        if (!environment.TryGetValue("PATH", out var path) || string.IsNullOrEmpty(path))
            path = "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Length == 0 ? "." : directory, file);
            if (File.Exists(candidate))
                return candidate;
        }

        return file;
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

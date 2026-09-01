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

        // Open the slave here and keep it open across the fork: the master only
        // accepts TIOCSWINSZ once the slave side has been opened, and closing the last
        // slave fd resets the tty on macOS, losing the size. The child's stdio is
        // wired by an addopen spawn file action (below), so this fd is never used by
        // the child — CLOEXEC_DEFAULT removes it from the spawned image, and the
        // parent closes its copy after the exec result arrives. O_NOCTTY: the parent
        // must never acquire the terminal.
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
        // macOS: the child's posix_spawn(SETEXEC) call consumes this parent-built spawn
        // attribute buffer, so it lives across the fork and is destroyed in the finally
        // below. No file actions are used: the fork child wires its own stdio (see
        // ChildMain), so file_actions is passed as NULL. Linux does its setup in the
        // child directly and execve()s without posix_spawn.
        var spawnAttr = IntPtr.Zero;
        try
        {
#if OSX
            // POSIX_SPAWN_SETEXEC makes posix_spawn replace the *calling* (forked)
            // process instead of spawning a grandchild — the fork child stays the
            // direct child of this process, so the reaper's waitpid keeps ownership.
            // The ctty itself is NOT acquired here: the child does setsid() + open the
            // slave (no O_NOCTTY) before the spawn call (see ChildMain) — the classic
            // login idiom. No SETSID flag (the child is already a session leader by
            // then) and no CLOEXEC_DEFAULT (the child wired stdio itself; the fd sweep
            // is done manually in the child).
            spawnAttr = Marshal.AllocHGlobal(NativeMethods.PosixSpawnAttrSize);
            if (NativeMethods.posix_spawnattr_init(spawnAttr) != 0)
            {
                throw new IOException($"posix_spawnattr_init failed: errno={Marshal.GetLastPInvokeError()}");
            }
            var flagsRc = NativeMethods.posix_spawnattr_setflags(
                spawnAttr,
                NativeMethods.PosixSpawnFlags.Setexec |
                NativeMethods.PosixSpawnFlags.CloexecDefault);
            if (flagsRc != 0)
                throw new IOException($"posix_spawnattr_setflags failed: errno={flagsRc}");
#endif

            stream = new PtyStream(new SafeFileHandle(new IntPtr(masterFd), ownsHandle: true));

            // Fork critical section. A no-GC region pauses the GC for the duration of
            // the fork so the child's inherited heap/allocator state is consistent (the
            // experiment suite measured ~0.5% child hangs under concurrent allocation
            // pressure without it, 0/4000 with it). The no-GC region is process-global:
            // concurrent spawns would fight over it, so the whole fork+region is
            // serialized with a lock.
            //
            // Two no-GC-region realities this code must survive (both observed):
            //   * Start can FAIL while a background GC drains — retried below with
            //     short backoff instead of failing the spawn;
            //   * the region's budget is consumed by OTHER threads' allocations, and
            //     when it runs out the runtime force-terminates the region (mode back
            //     to NotActive) — so EndNoGCRegion may throw even though Start returned
            //     true, and it is wrapped. A terminated region means a GC ran near the
            //     fork; the child may then wedge before exec, which the exec-result
            //     timeout below surfaces as a diagnosable failure.
            int pid;
            lock (ForkLock)
            {
                _ = ForkChildPathPrepared;
                var started = false;
                for (var attempt = 0; attempt < 5 && !started; attempt++)
                {
                    started = GC.TryStartNoGCRegion(ForkNoGcBudget, true);
                    if (!started)
                        Thread.Sleep(10 << attempt); // background GC draining; back off
                }
                if (!started)
                    throw new IOException("fork launch failed: the GC could not be paused (concurrent GC in progress).");

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
                                    spawnAttr,
                                    errPipe[1],
                                    ChildApi);
                                ChildApi.Exit(127); // unreachable
                            }
                        }
                    }
                    // The region may have been force-terminated meanwhile (other
                    // threads' allocations drained the budget); EndNoGCRegion then
                    // throws even though Start succeeded — swallow that specific
                    // outcome, the child is already forked and unaffected.
                    try { GC.EndNoGCRegion(); }
                    catch (InvalidOperationException ex) { PtyDiagnostics.Log($"no-gc region ended early: {ex.Message}"); }
                    // Logging after the region ends: the interpolated string allocates
                    // even when diagnostics are disabled, and allocations inside an
                    // active no-GC region eat its budget.
                    PtyDiagnostics.Log($"fork parent child-pid={pid} slave-path={slavePath}");
                }
                catch
                {
                    try { GC.EndNoGCRegion(); }
                    catch (InvalidOperationException) { /* region already terminated */ }
                    throw;
                }

                // Parent cleanup + wait for the exec result. Still inside the lock:
                // holding it keeps concurrent spawns from piling up a second no-GC
                // region while this one is still draining. The read is bounded: a
                // child that neither execs nor exits (any post-fork bug) must not
                // wedge the ForkLock — and every future spawn — forever.
                NativeMethods.close(errPipe[1]);
                errPipe[1] = -1;
                var execErrno = ReadChildExecError(errPipe[0], pid);
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
            PosixSpawn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(process, "posix_spawn"),
            FileActionsInit = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(process, "posix_spawn_file_actions_init"),
            FileActionsAddDup2 = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)NativeLibrary.GetExport(process, "posix_spawn_file_actions_adddup2"),
            Dup2 = (delegate* unmanaged[Cdecl]<int, int, int>)NativeLibrary.GetExport(process, "dup2"),
            Setsid = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(process, "setsid"),
            Open = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(process, "open"),
            Chdir = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(process, "chdir"),
            Signal = (delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr>)NativeLibrary.GetExport(process, "signal"),
            Error = (delegate* unmanaged[Cdecl]<int*>)NativeLibrary.GetExport(process, "__error"),
            Close = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(process, "close"),
#elif LINUX
            Dup2 = (delegate* unmanaged[Cdecl]<int, int, int>)NativeLibrary.GetExport(process, "dup2"),
            Setsid = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(process, "setsid"),
            Open = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(process, "open"),
            Chdir = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(process, "chdir"),
            Execve = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(process, "execve"),
            Close = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(process, "close"),
            Error = (delegate* unmanaged[Cdecl]<int*>)NativeLibrary.GetExport(process, "__errno_location"),
            Signal = (delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr>)NativeLibrary.GetExport(process, "signal"),
            // syscall(2) is variadic; a fixed signature is safe here — Linux reads the
            // leading arguments from registers on both x86_64 and arm64 (the same
            // pattern as NativeMethods' two-argument syscall for pidfd_open).
            Syscall = (delegate* unmanaged[Cdecl]<long, uint, uint, IntPtr, long>)NativeLibrary.GetExport(process, "syscall"),
#endif
        };
        WarmUpChildSignatures(process, api);
        return api;
    }

    /// <summary>
    /// Runs every calli signature the child uses once in the parent with harmless
    /// arguments. The managed→native calli stub is generated lazily on the first call;
    /// if that first call happened inside the forked child it would JIT under the
    /// inherited CoreCLR code-heap lock and hang the child before exec (observed as
    /// GenericPInvokeCalliStubWorker waiting in __psynch_mutexwait). Each warm-up call
    /// fails harmlessly at the libc level (EBADF/EFAULT/EINVAL/ENOENT). The signature
    /// (argument types, return type, calling convention) defines the stub — a same-
    /// shape stand-in warms it just as well as the real callee.
    /// </summary>
    private static unsafe void WarmUpChildSignatures(IntPtr process, ChildNativeApi api)
    {
        var getpid = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(process, "getpid");

        _ = api.Dup2(-1, -1);
        _ = getpid(); // shares Setsid's int(void) signature
        _ = api.Open((IntPtr)1, 0);
        _ = api.Chdir(IntPtr.Zero);
        _ = api.Write(-1, IntPtr.Zero, 0);
        _ = api.Error();
        _ = api.Signal(23 /* SIGURG, default action: ignore */, IntPtr.Zero);
#if OSX
        // posix_spawn validates its arguments before doing anything: all-null inputs
        // return EINVAL without touching processes. The SETEXEC attribute is not needed
        // for the warm-up — only the call signature (VASigCookie) must be compiled.
        // The file-actions object is a stack buffer; adddup2 with an invalid fd fails
        // registration harmlessly. _exit itself must never be warmed by calling it;
        // close(int)->int shares its signature (Exit is declared int-returning, which
        // is ABI-invisible for a call that never returns).
        _ = api.PosixSpawn(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var fileActions = stackalloc byte[NativeMethods.PosixSpawnFileActionsSize];
        _ = api.FileActionsInit((IntPtr)fileActions);
        _ = api.FileActionsAddDup2((IntPtr)fileActions, -1, -1);
        var close = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(process, "close");
        _ = close(-1);
#elif LINUX
        _ = api.Execve(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ = api.Close(-1);
        _ = api.Syscall(-1, 0, 0, IntPtr.Zero);
#endif
    }

#if OSX
    private unsafe struct ChildNativeApi
    {
        internal delegate* unmanaged[Cdecl]<int, IntPtr, nuint, nint> Write;
        internal delegate* unmanaged[Cdecl]<int, int> Exit;
        // posix_spawn(pid*, path, file_actions, attr, argv, envp) — with the SETEXEC
        // attr flag this replaces the calling (forked) process in place; with
        // CLOEXEC_DEFAULT the kernel closes every fd the file actions did not create.
        internal delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int> PosixSpawn;
        internal delegate* unmanaged[Cdecl]<IntPtr, int> FileActionsInit;
        internal delegate* unmanaged[Cdecl]<IntPtr, int, int, int> FileActionsAddDup2;
        internal delegate* unmanaged[Cdecl]<int, int, int> Dup2;
        internal delegate* unmanaged[Cdecl]<int> Setsid;
        internal delegate* unmanaged[Cdecl]<IntPtr, int, int> Open;
        internal delegate* unmanaged[Cdecl]<IntPtr, int> Chdir;
        internal delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> Signal;
        internal delegate* unmanaged[Cdecl]<int*> Error;
        // The macOS fd sweep is always the manual loop (no close_range on Darwin).
        internal delegate* unmanaged[Cdecl]<int, int> Close;
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
        // signal(sig, handler): used to reset inherited SIG_IGN dispositions (exec
        // preserves ignored signals; glibc/musl have no SETSIGDEF equivalent here).
        internal delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr> Signal;
        // syscall(number, first, last, flags) — the close_range(2) fast path.
        internal delegate* unmanaged[Cdecl]<long, uint, uint, IntPtr, long> Syscall;
    }
#endif

    private unsafe delegate void ChildMainDelegate(
        IntPtr path, IntPtr argv, IntPtr envp, IntPtr workingDirectory,
        IntPtr ptySlavePath, IntPtr spawnAttr, int errWrite, ChildNativeApi api);

    private unsafe delegate void ReportChildErrorDelegate(ChildNativeApi api, int errWrite, int errno);

    /// <summary>
    /// The child's post-fork entry point. Runs in the forked copy with the no-GC region
    /// active: must not allocate managed objects, touch runtime locks, or return.
    /// Every libc call goes through <paramref name="api"/>'s function pointers, whose
    /// calli stubs were warmed up in the parent before the fork: a lazily-generated
    /// stub would JIT inside the forked child under the inherited CoreCLR code-heap
    /// lock and hang the spawn.
    /// <para>Both platforms, the same libc-only sequence: setsid(), then open the pty
    /// slave (no O_NOCTTY) so the session leader acquires it as the controlling
    /// terminal (a dup2 of a parent-opened fd never does — the python ctty probe
    /// caught exactly that), dup2 it onto 0/1/2, reset the inherited SIG_IGN
    /// dispositions (exec only resets *caught* signals — the same gap main's
    /// POSIX_SPAWN_SETSIGDEF covered), close the inherited fds above stdio, chdir when
    /// requested, exec.</para>
    /// <para>macOS: the exec is posix_spawn with Apple's SETEXEC attribute plus
    /// CLOEXEC_DEFAULT — the child builds its own file actions (adddup2 of the ctty fd
    /// onto 0/1/2, on a stack buffer) because under CLOEXEC_DEFAULT only
    /// file-action-created fds survive, and the parent cannot know the fd number the
    /// child's open returned. The kernel closes every other fd; the
    /// controlling-terminal link lives in the session and survives the exec. On
    /// failure posix_spawn returns the errno, which goes to the parent's pipe.</para>
    /// <para>Linux: glibc/musl have no SETEXEC/CLOEXEC_DEFAULT — the exec is execve and
    /// the sweep is close_range(2) (kernel 5.9+) with a bounded loop fallback. On any
    /// failure writes errno to the pipe and _exit(127).</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void ChildMain(
        IntPtr path, IntPtr argv, IntPtr envp, IntPtr workingDirectory,
        IntPtr ptySlavePath, IntPtr spawnAttr, int errWrite, ChildNativeApi api)
    {
        if (api.Setsid() < 0)
        {
            ReportChildError(api, errWrite, *api.Error());
            api.Exit(127);
        }

        // Reset the dispositions exec(2) preserves: caught signals are already reset
        // to SIG_DFL by exec, but SIG_IGN survives (.NET ignores SIGPIPE) — the same
        // gap main's POSIX_SPAWN_SETSIGDEF covered for the pre-fork implementation.
        api.Signal((int)NativeMethods.Signals.Hup, IntPtr.Zero);
        api.Signal((int)NativeMethods.Signals.Int, IntPtr.Zero);
        api.Signal((int)NativeMethods.Signals.Quit, IntPtr.Zero);
        api.Signal((int)NativeMethods.Signals.Pipe, IntPtr.Zero);
        api.Signal((int)NativeMethods.Signals.Term, IntPtr.Zero);

        // Close every inherited fd above stdio except the error pipe (it carries the
        // exec-failure errno). Linux-only: on macOS CLOEXEC_DEFAULT does this
        // kernel-side at exec (the whole reason posix_spawn is used there). The sweep
        // runs BEFORE the ctty open below, so no exclusion for it is needed.
#if LINUX
        // Prefer close_range(2) (syscall 436, kernel 5.9+) — one syscall instead of a
        // loop bounded by FdIsolationCap, so fds above the cap are closed too. Any
        // failure (ENOSYS on old kernels, EINVAL) falls back to the bounded loop,
        // which harmlessly re-closes already-closed fds.
        var sweepManually = true;
        if (errWrite > 3)
            sweepManually = api.Syscall(NativeMethods.CloseRangeSyscallNumber, 3, (uint)(errWrite - 1), IntPtr.Zero) != 0;
        if (!sweepManually && errWrite < int.MaxValue - 1)
            sweepManually = api.Syscall(NativeMethods.CloseRangeSyscallNumber, (uint)Math.Max(errWrite + 1, 3), uint.MaxValue, IntPtr.Zero) != 0;
        if (sweepManually)
        {
            for (var fd = 3; fd < NativeMethods.FdIsolationCap; fd++)
            {
                if (fd != errWrite)
                    _ = api.Close(fd);
            }
        }
#endif

        // Session leader opens the pty slave without O_NOCTTY — that is what assigns
        // the tty as the session's controlling terminal (a dup2 of a parent-opened fd
        // never does; the python ctty probe caught exactly that).
        var cttyFd = api.Open(ptySlavePath, NativeMethods.ORdwr
#if LINUX
            | NativeMethods.OCloexec
#endif
        );
        if (cttyFd < 0)
        {
            ReportChildError(api, errWrite, *api.Error());
            api.Exit(127);
        }

#if OSX
        // stdio: under CLOEXEC_DEFAULT only file-action-created fds survive the spawn,
        // so the manually-opened ctty fd is dup2'd onto 0/1/2 by file actions — built
        // here, on the stack, because the parent cannot know the fd number the open
        // above returned. The kernel then closes every other fd (the ctty fd itself
        // included); the controlling-terminal link lives in the session and survives
        // the exec.
        var fileActions = stackalloc byte[NativeMethods.PosixSpawnFileActionsSize];
        if (api.FileActionsInit((IntPtr)fileActions) != 0)
        {
            ReportChildError(api, errWrite, *api.Error());
            api.Exit(127);
        }
        if (api.FileActionsAddDup2((IntPtr)fileActions, cttyFd, 0) != 0 ||
            api.FileActionsAddDup2((IntPtr)fileActions, cttyFd, 1) != 0 ||
            api.FileActionsAddDup2((IntPtr)fileActions, cttyFd, 2) != 0)
        {
            ReportChildError(api, errWrite, *api.Error());
            api.Exit(127);
        }

        if (workingDirectory != IntPtr.Zero && api.Chdir(workingDirectory) != 0)
        {
            ReportChildError(api, errWrite, *api.Error());
            api.Exit(127);
        }

        // SETEXEC: exec this fork child in place (a grandchild would break the
        // reaper's waitpid ownership). Success never returns here; a failure returns
        // the errno (nothing was exec'd).
        int spawnedPid = 0;
        var rc = api.PosixSpawn(
            (IntPtr)(&spawnedPid), path, (IntPtr)fileActions, spawnAttr, argv, envp);
        ReportChildError(api, errWrite, rc);
        api.Exit(127);
#elif LINUX
        if (api.Dup2(cttyFd, 0) != 0 ||
            api.Dup2(cttyFd, 1) != 1 ||
            api.Dup2(cttyFd, 2) != 2)
        {
            ReportChildError(api, errWrite, *api.Error());
            api.Exit(127);
        }

        if (workingDirectory != IntPtr.Zero && api.Chdir(workingDirectory) != 0)
        {
            ReportChildError(api, errWrite, *api.Error());
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
    /// Reads the exec result from the error pipe, bounded by <see cref="ExecResultTimeoutMs"/>.
    /// Returns -1 for success (EOF), or the child's errno on failure. Throws
    /// <see cref="IOException"/> when the child neither execs nor exits within the
    /// timeout: the fork child is SIGKILLed and reaped so the ForkLock and every
    /// future spawn stay usable (any post-fork bug must fail the spawn, not wedge it).
    /// </summary>
    private static unsafe int ReadChildExecError(int fd, int pid)
    {
        Span<byte> buf = stackalloc byte[4];
        var total = 0;
        fixed (byte* p = buf)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ExecResultTimeoutMs);
            while (total < 4)
            {
                var remaining = (int)Math.Clamp(
                    (deadline - DateTime.UtcNow).TotalMilliseconds, 0, int.MaxValue);
                var pr = NativeMethods.poll(
                    [new NativeMethods.PollFd { Fd = fd, Events = NativeMethods.PollEvents.Pollin }],
                    remaining);
                if (pr < 0)
                {
                    if (Marshal.GetLastPInvokeError() == NativeMethods.Eintr)
                        continue;
                    KillAndReapStuckChild(pid);
                    throw new IOException($"fork/exec launch failed: poll error waiting for the child to exec (pid={pid}).");
                }
                if (pr == 0)
                {
                    // Timeout: the child is not making progress. Capture the kernel-side
                    // evidence FIRST (the stuck child's /proc state survives until we
                    // kill it), then SIGKILL + reap so the ForkLock stays usable.
                    var evidence = CaptureStuckChildEvidence(pid);
                    KillAndReapStuckChild(pid);
                    throw new ForkHangException(
                        $"fork/exec launch failed: the child did not exec within {ExecResultTimeoutMs} ms (pid={pid}).{evidence}");
                }

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
                if (err is NativeMethods.Eintr or NativeMethods.Eagain)
                    continue; // transient — go back to poll
                return -1; // read error: treat as launched (defensive)
            }
        }
        return MemoryMarshal.Read<int>(buf);
    }

    /// <summary>How long the parent waits for the fork child to exec before SIGKILLing it.</summary>
    private const int ExecResultTimeoutMs = 10_000;

    /// <summary>
    /// The fork child wedged before exec (see <see cref="CaptureStuckChildEvidence"/>):
    /// a ~0.02% multithreaded-fork race where the child's first GC-mode transition
    /// waits on a suspension that its dead runtime will never grant. Thrown once;
    /// <see cref="StartCore"/> retries the whole launch once — a fresh fork almost
    /// never loses the same race.
    /// </summary>
    private sealed class ForkHangException : IOException
    {
        public ForkHangException(string message)
            : base(message)
        {
        }
    }

    private static void KillAndReapStuckChild(int pid)
    {
        _ = NativeMethods.kill(pid, NativeMethods.Signals.Kill);
        ReapFailedExec(pid);
    }

    /// <summary>
    /// A fork child failing to exec within <see cref="ExecResultTimeoutMs"/> is an
    /// abnormality, not a slow machine: the post-fork path is a dozen microsecond-scale
    /// libc calls, so the only way it takes seconds is the child blocking on an
    /// inherited runtime lock (the ~0.5% multithreaded-fork hang the warm-up work
    /// reduced but evidently did not fully eliminate). Before killing the evidence,
    /// snapshot what the kernel knows about it:
    ///   * /proc/{pid}/syscall — the syscall the child is parked in (raw number; look
    ///     it up against the runner architecture's table when diagnosing);
    ///   * /proc/{pid}/cmdline — empty means exec never happened, a path means the
    ///     kernel replaced the image and the stall is elsewhere;
    ///   * /proc/{pid}/status — scheduler state plus the inherited signal masks
    ///     (SigBlk/SigIgn/SigCgt directly expose disposition/mask inheritance bugs).
    /// On macOS the equivalent is /usr/bin/sample: the library runs it against the
    /// stuck child (still alive until we kill it) and reports the stack file path —
    /// the child is a copy of this process, so its stack shows exactly which
    /// post-fork step or inherited lock it is parked in.
    /// </summary>
    private static string CaptureStuckChildEvidence(int pid)
    {
#if LINUX
        try
        {
            var syscall = File.ReadAllText($"/proc/{pid}/syscall").Trim();
            var cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
            var status = File.ReadAllText($"/proc/{pid}/status");
            var line = (string l) => status.Split('\n').FirstOrDefault(x => x.StartsWith(l, StringComparison.Ordinal))?.Trim();
            return $" stuck: syscall='{syscall}' cmdline='{cmdline}' {line("State")} {line("SigBlk")} {line("SigIgn")} {line("SigCgt")}";
        }
        catch
        {
            // The child may have exited (or even exec'd) between the poll timeout and
            // these reads — then there is nothing to diagnose and nothing to add.
            return " (child state vanished before capture; it may have exited)";
        }
#elif OSX
        try
        {
            // /usr/bin/sample <pid> <duration-s> <fraction-s> -file <out>: samples the
            // process for 2 seconds and writes the call graph. The child is a forked
            // copy of THIS process before exec, so unresolved frames belong to the
            // hosting dotnet/runtime modules of the parent.
            var file = $"/tmp/pty-stuck-child-{pid}.txt";
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/sample", $"{pid} 2 1 -file {file}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var sampler = System.Diagnostics.Process.Start(psi);
            sampler?.WaitForExit(8000);
            return $" child stack sampled to {file}";
        }
        catch
        {
            // The child may have exited between the poll timeout and the sample —
            // then there is nothing to diagnose and nothing to add.
            return " (child state vanished before capture; it may have exited)";
        }
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif
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

    /// <summary>
    /// True while the pid still belongs to <em>this</em> child and can be signaled:
    /// every child this library spawns becomes a session leader, so its session id
    /// equals its pid — a property a recycled pid of an unrelated process will almost
    /// certainly not have. Guards every kill/signal call against the pid-reuse race
    /// (child reaped, pid recycled, signal lands in a stranger).
    /// </summary>
    private bool IsOurSessionLeader()
    {
        return NativeMethods.getsid(Pid) == Pid;
    }

    /// <summary>Unix: SIGHUP the child — the terminal-hangup signal (see <see cref="PtyProcess.RequestClose"/>).</summary>
    private partial void RequestClosePlatform()
    {
        if (!IsOurSessionLeader())
        {
            PtyDiagnostics.Log($"sighup skipped pid={Pid} not-our-session-leader");
            return;
        }
        var result = NativeMethods.kill(Pid, NativeMethods.Signals.Hup);
        var errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        PtyDiagnostics.Log($"sighup pid={Pid} result={result} errno={errno} state={DescribeUnixState()}");
    }

    /// <summary>
    /// Unix: the pty stream doubles as both facade streams (no transcoding). The
    /// StreamWriter/StreamReader wrapping it are constructed with leaveOpen, so
    /// disposing a facade never closes the pty — PtyProcess owns BaseStream.
    /// </summary>
    private partial void CreateFacades(
        Encoding inputEncoding, Encoding outputEncoding,
        out Stream inputFacadeStream, out Stream outputFacadeStream)
    {
        inputFacadeStream = BaseStream;
        outputFacadeStream = BaseStream;
    }

    /// <summary>
    /// Unix: SIGKILL the child. Returns false when the signal could not be delivered
    /// to a process that is still ours (session-leader check passed but kill failed,
    /// e.g. EPERM from a sandbox) — the caller must not treat the child as killed.
    /// </summary>
    private partial bool KillPlatform()
    {
        if (!IsOurSessionLeader())
        {
            PtyDiagnostics.Log($"sigkill skipped pid={Pid} not-our-session-leader");
            return true; // pid no longer ours: nothing to kill, do not retry
        }
        var result = NativeMethods.kill(Pid, NativeMethods.Signals.Kill);
        var errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        PtyDiagnostics.Log($"sigkill pid={Pid} result={result} errno={errno} state={DescribeUnixState()}");
        return result == 0;
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
    /// Non-blocking drain step: moves whatever output is currently available into the
    /// stream's replay buffer, so the child never blocks on a full pty buffer during a
    /// wait and the output remains readable after the wait — the same preserve contract
    /// as the Windows pump (memory grows with the output produced during the wait).
    /// </summary>
    private partial void DrainOutput()
    {
        while (BaseStream.DrainAvailableIntoReplay())
        {
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

    /// <summary>
    /// Flattens the environment dictionary into the <c>KEY=VALUE</c> array, dropping
    /// null values. No variable is injected implicitly: the caller controls the child's
    /// environment exactly. In the default inherit mode a shell typically inherits
    /// TERM from the parent anyway; in allowlist mode (<c>InheritParentEnvironment = false</c>)
    /// users who run terminal-aware programs should add TERM themselves, e.g.
    /// <c>Environment = new Dictionary&lt;string, string?&gt; { ["TERM"] = "xterm-256color" }</c>.
    /// </summary>
    private static List<string> BuildEnvironment(IDictionary<string, string?> env)
    {
        return env
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();
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

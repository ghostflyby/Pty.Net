using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// Unix half of <see cref="PtyProcess"/>: posix_spawn-based launch, SIGHUP teardown and
/// waitpid reaping. Compiled only on the non-Windows target (see csproj), so the shared
/// <c>PtyProcess.cs</c> carries no platform conditionals.
/// </summary>
public sealed partial class PtyProcess
{
    private static partial PtyProcess StartPlatform(
        string file, string[] arguments, string? workingDirectory,
        IDictionary<string, string?> environment, Encoding? inputEncoding, Encoding? outputEncoding)
    {
        // Everything is prepared in the parent; posix_spawn performs the exec natively.
        var envp = ToNative(BuildEnvironment(environment));
        var argv = ToNative([Path.GetFileName(file), .. arguments]);
        var path = Marshal.StringToHGlobalAnsi(file);

        // Create the pty via posix_openpt(O_NONBLOCK) + grantpt/unlockpt/ptsname/open:
        // all non-variadic, so they work on Apple arm64 (where the variadic fcntl call
        // misdelivers its third argument and could never set O_NONBLOCK). The master
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
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
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
            // starts with a clean fd table. glibc's addclosefrom_np would do this in one
            // call, but musl does not export it — a loop of the standard addclose file
            // action works on both. Bound the loop by the current soft fd limit (glibc
            // rejects addclose for fds >= it with EBADF), capped so huge limits do not
            // turn every spawn into a million-iteration loop.
            var maxFd = NativeMethods.FdIsolationCap;
            if (NativeMethods.getrlimit(NativeMethods.RlimitNofile, out var rlim) == 0 &&
                rlim.Cur < (nuint)maxFd)
                maxFd = (int)rlim.Cur;
            for (var fd = 3; fd < maxFd; fd++)
            {
                if (NativeMethods.posix_spawn_file_actions_addclose(fileActions, fd) != 0)
                    throw new IOException($"posix_spawn addclose({fd}) failed: errno={Marshal.GetLastPInvokeError()}");
            }
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
            return new PtyProcess(stream, pid, inputEncoding, outputEncoding, processHandle: null);
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
    /// Signals the child before the terminal is closed so it exits instead of hanging
    /// without a controlling terminal (SIGHUP on Unix; on Windows a live child is
    /// terminated so ClosePseudoConsole does not wait on it indefinitely).
    /// </summary>
    private void TerminateChildIfAlive()
    {
        if (!HasExited)
        {
            // The child was spawned with posix_spawn + SETSID, so it has no controlling
            // terminal: closing the pty master alone does not deliver a hangup. Signal
            // it explicitly, then close the master so its output writes fail cleanly.
            NativeMethods.kill(Pid, NativeMethods.Signals.Hup);
        }
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
    private partial void KillPlatform() => NativeMethods.kill(Pid, NativeMethods.Signals.Kill);

    /// <summary>
    /// Non-blocking drain: reads whatever output is currently available and discards it.
    /// Used by <see cref="WaitForExit"/> so the child never blocks on a full pty buffer
    /// while nobody is reading.
    /// </summary>
    private partial void DrainOutput()
    {
        while (true)
        {
            // 0ms timeout: only drain what is already there, never wait.
            var n = BaseStream.Read(drainBuf, 0, out _);
            if (n <= 0)
                return; // nothing available right now, or EOF
        }
    }

    /// <summary>
    /// Called by the process-wide reaper once waitpid(2) collected this child. Unix has
    /// no teardown work that must run off the shared reaper thread.
    /// </summary>
    private partial void OnReapedPlatform()
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

            // ECHILD (reaped elsewhere / not our child) or an unexpected error:
            // record the exit code as unknown instead of throwing here, which would
            // kill the shared reaper thread and leave every other session unreaped.
            exitCode = -1;
            return true;
        }
    }

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
}

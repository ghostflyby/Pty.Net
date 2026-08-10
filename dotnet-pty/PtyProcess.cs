using System.Runtime.InteropServices;
using System.Text;

namespace dotnet_pty;

/// <summary>
/// A child process attached to a pseudo-terminal (PTY), created via <c>openpty(3)</c> +
/// <c>posix_spawn(2)</c>.
/// Use it to drive an interactive shell (e.g. bash): write commands, read back the terminal output.
/// </summary>
/// <remarks>
/// The pty master fd comes from openpty as a <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>
/// and is wrapped in an unbuffered <see cref="FileStream"/>, so read/write go through managed APIs
/// and disposing the stream closes the fd automatically. Reading still uses a short-poll +
/// blocking-read model: <c>poll(2)</c> tells us when data is readable (we only rely on its return
/// value, not <c>revents</c>), then a single blocking read is safe because poll guaranteed data is
/// available.
/// </remarks>
public sealed class PtyProcess : IDisposable
{
    private const int ReadBufferSize = 4096;

    // Raw master fd for poll(2) only — the FileStream owns and closes the actual handle.
    private readonly int masterFd;
    private readonly FileStream stream;
    private readonly int pid;
    private readonly Lock gate = new();
    private readonly List<byte> pending = [];

    private int? exitCode;
    private bool eof;
    private bool disposed;

    /// <summary>OS process id of the child.</summary>
    public int Pid => pid;

    /// <summary>Exit code, set once the child has been reaped via <see cref="WaitForExit"/> or dispose.</summary>
    public int? ExitCode => exitCode;

    public bool HasExited => exitCode is not null;

    private PtyProcess(int masterFd, FileStream stream, int pid)
    {
        this.masterFd = masterFd;
        this.stream = stream;
        this.pid = pid;
    }

    /// <summary>
    /// Spawns an interactive bash. Runs with <c>--noprofile --norc --noediting -i</c> so the session
    /// is deterministic and does not pick up the user's rc files. <c>--noediting</c> disables readline,
    /// so <c>stty -echo</c> can suppress input echo (readline does its own echoing regardless).
    /// Note: the long options must come before <c>-i</c>, otherwise macOS bash 3.2 rejects the invocation.
    /// </summary>
    public static PtyProcess StartBash(string? workingDirectory = null, params string[]? arguments)
    {
        var args = arguments is { Length: > 0 }
            ? arguments
            : ["--noprofile", "--norc", "--noediting", "-i"];
        return Start("/bin/bash", args, workingDirectory);
    }

    /// <summary>
    /// Runs <paramref name="file"/> in a new PTY session (child process attached to a pseudo-terminal).
    /// Uses <c>openpty(3)</c> + <c>posix_spawn(2)</c>: posix_spawn avoids <c>fork(2)</c>, so spawning
    /// stays safe even when other threads are concurrently allocating memory (fork in a multi-threaded
    /// process can deadlock the child on inherited malloc locks).
    /// </summary>
    public static PtyProcess Start(string file, string[] arguments, string? workingDirectory = null)
    {
        // Everything is prepared in the parent; posix_spawn performs the exec natively.
        var envp = ToNative(BuildEnvironment());
        var argv = ToNative([Path.GetFileName(file), .. arguments]);
        var path = Marshal.StringToHGlobalAnsi(file);

        // openpty hands us the master/slave fds as SafeFileHandles (ownership transferred).
        // The FileStream takes over the master; the slave is closed once spawn has run.
        FileStream? stream = null;
        if (NativeMethods.openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            throw new IOException($"openpty failed: errno={err}");
        }

        var masterFd = (int)master.DangerousGetHandle();
        var slaveFd = (int)slave.DangerousGetHandle();

        var fileActions = Marshal.AllocHGlobal(NativeMethods.PosixSpawnFileActionsSize);
        var attr = Marshal.AllocHGlobal(NativeMethods.PosixSpawnAttrSize);
        var spawned = false;
        try
        {
            // Unbuffered (bufferSize 1) so reads/writes map 1:1 to syscalls: a pty is not
            // seekable, and our poll-based reader must never leave data stranded in a
            // stream buffer that poll(2) cannot see.
            stream = new FileStream(master, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);

            if (NativeMethods.posix_spawn_file_actions_init(fileActions) != 0 ||
                NativeMethods.posix_spawnattr_init(attr) != 0)
                throw new IOException($"posix_spawn init failed: errno={Marshal.GetLastPInvokeError()}");

#if OSX
            // macOS: SETSID + close-on-exec-everything so runtime fds do not leak into the shell.
            var flagsRc = NativeMethods.posix_spawnattr_setflags(
                attr,
                NativeMethods.PosixSpawnFlags.Setsid | NativeMethods.PosixSpawnFlags.CloexecDefault);
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
#error "dotnet-pty supports macOS (define OSX) and Linux (define LINUX) only."
#endif

            // Wire the pty slave to the child's stdio and drop our copy of it. These take
            // the exact fd numbers (referenced by the file actions inside the child), so
            // raw handle values are passed rather than SafeHandles.
            NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slaveFd, 0);
            NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slaveFd, 1);
            NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slaveFd, 2);
            NativeMethods.posix_spawn_file_actions_addclose(fileActions, slaveFd);

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
            return new PtyProcess(masterFd, stream, pid);
        }
        finally
        {
            NativeMethods.posix_spawn_file_actions_destroy(fileActions);
            NativeMethods.posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attr);
            slave.Dispose(); // parent's copy of the slave is no longer needed
            FreeNative(envp);
            FreeNative(argv);
            Marshal.FreeHGlobal(path);
            if (!spawned)
            {
                // The stream owns the master handle; dispose both (SafeHandle disposal is
                // idempotent) so a failed spawn cannot leak the fd.
                stream?.Dispose();
                master.Dispose();
            }
        }
    }

    /// <summary>Sends <paramref name="data"/> to the shell's stdin (via the PTY master).</summary>
    public void Write(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bytes = Encoding.UTF8.GetBytes(data);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Unbuffered FileStream: writes go straight to the pty master. The fd is
            // blocking, so a full pty buffer just blocks here until the child drains it.
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }

    /// <summary>
    /// Reads whatever output is currently available, waiting up to <paramref name="timeout"/>
    /// for the first byte. Returns an empty string if nothing arrives within the timeout.
    /// </summary>
    public string ReadAvailable(TimeSpan timeout)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ReadAvailableCore((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
        }
    }

    /// <summary>
    /// Reads until <paramref name="marker"/> appears in the output (inclusive) and returns
    /// everything read so far. Throws <see cref="TimeoutException"/> if the marker does not
    /// appear within <paramref name="timeout"/>. Returns whatever was read if the child
    /// exits before the marker shows up.
    /// </summary>
    public string ReadUntil(string marker, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Length == 0)
            throw new ArgumentException("Marker must not be empty.", nameof(marker));

        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                var diag = $"pid={pid} eof={eof} pending={pending.Count} exited={HasExited}";
                throw new TimeoutException($"Timed out after {timeout} waiting for '{marker}'. Got: {sb} [{diag}]");
            }

            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                sb.Append(ReadAvailableCore((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue)));

                if (sb.ToString().Contains(marker, StringComparison.Ordinal))
                    return sb.ToString();
                if (eof)
                    return sb.ToString(); // child exited before the marker appeared
            }
        }
    }

    /// <summary>
    /// Blocks until the child exits or the timeout elapses. Returns false on timeout.
    /// While waiting, output is drained continuously so the child never blocks writing
    /// to a full pty buffer; the drained bytes stay queued for later <see cref="ReadAvailable"/>.
    /// </summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                DrainOutput(); // keep the pty buffer flowing while we wait
                if (TryReap())
                    return true;
            }

            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;

            if (!HasExited)
            {
                // The child was spawned with posix_spawn + SETSID, so it has no controlling
                // terminal: closing the pty master alone does not deliver a hangup. Signal
                // it explicitly, then close the master so its output writes fail cleanly.
                NativeMethods.kill(pid, NativeMethods.Signals.Hup);
            }

            // Disposing the stream closes the master fd (owned via the SafeFileHandle).
            stream.Dispose();

            // Reap the child so it does not linger as a zombie.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!TryReap() && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
        }
    }

    // --- internals ---------------------------------------------------------

    private static string[] BuildEnvironment()
    {
        var env = Environment.GetEnvironmentVariables()
            .Keys.Cast<string>()
            .Select(key => $"{key}={Environment.GetEnvironmentVariable(key)}")
            .ToList();
        if (!env.Any(e => e.StartsWith("TERM=", StringComparison.Ordinal)))
            env.Add("TERM=xterm-256color");
        return env.ToArray();
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

    private string ReadAvailableCore(int timeoutMs)
    {
        if (eof)
            return Drain();

        // Wait up to timeoutMs for the first byte (or EOF).
        if (!WaitReadable(timeoutMs))
            return Drain(); // timed out with nothing new

        // Drain: keep reading while data keeps arriving; treat a quiet gap as "drained".
        while (true)
        {
            if (!WaitReadable(50))
            {
                // Nothing this instant; give the child a brief chance to produce more
                // before declaring the output drained (guards against spurious polls).
                Thread.Sleep(10);
                if (!WaitReadable(50))
                    break;
            }

            var buf = new byte[ReadBufferSize];
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                // The child's slave side closed: the master read returns EIO (macOS and
                // Linux), which FileStream surfaces as IOException. Treat as EOF.
                eof = true;
                break;
            }

            if (n > 0)
            {
                pending.AddRange(buf.AsSpan(0, n));
                continue;
            }

            eof = true; // EOF (the master read returns 0 once the slave is gone)
            break;
        }

        return Drain();
    }

    /// <summary>
    /// Polls the master fd for readability (or hangup). Returns true when poll reports an event;
    /// only the poll return value is trusted, not <c>revents</c>. Uses a short poll timeout plus
    /// a sleep so it keeps working even on platforms where the poll timeout misbehaves.
    /// </summary>
    private bool WaitReadable(int timeoutMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (true)
        {
            var fds = new[] { new NativeMethods.PollFd { Fd = masterFd, Events = NativeMethods.PollEvents.Pollin } };
            int r;
            do
            {
                r = NativeMethods.poll(fds, 1, 50);
            } while (r < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Eintr);

            if (r < 0)
                throw new IOException($"poll failed: errno={Marshal.GetLastPInvokeError()}");
            if (r > 0)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Non-blocking drain: reads whatever output is currently available into <c>_pending</c>
    /// without consuming it. Used by <see cref="WaitForExit"/> so the child never blocks on a
    /// full pty buffer while we are not reading.
    /// </summary>
    private void DrainOutput()
    {
        if (eof)
            return;

        while (true)
        {
            var fds = new[] { new NativeMethods.PollFd { Fd = masterFd, Events = NativeMethods.PollEvents.Pollin } };
            int r;
            do
            {
                r = NativeMethods.poll(fds, 1, 0); // immediate, non-blocking
            } while (r < 0 && Marshal.GetLastPInvokeError() == NativeMethods.Eintr);

            if (r < 0)
                throw new IOException($"poll failed: errno={Marshal.GetLastPInvokeError()}");
            if (r == 0)
                return; // nothing readable right now

            var buf = new byte[ReadBufferSize];
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                // Slave side gone (EIO) — same EOF semantics as ReadAvailableCore.
                eof = true;
                return;
            }

            if (n > 0)
            {
                pending.AddRange(buf.AsSpan(0, n));
                continue; // more may be buffered
            }

            eof = true;
            return;
        }
    }

    /// <summary>
    /// Decodes accumulated bytes as UTF-8, keeping any trailing bytes that are the head of
    /// a multi-byte sequence so they can be combined with the next chunk.
    /// </summary>
    private string Drain()
    {
        if (pending.Count == 0)
            return string.Empty;

        var bytes = pending.ToArray();
        pending.Clear();

        var keep = IncompleteTailLength(bytes);
        var text = Encoding.UTF8.GetString(bytes, 0, bytes.Length - keep);
        if (keep > 0)
            pending.AddRange(bytes.AsSpan(bytes.Length - keep, keep));
        return text;
    }

    private static int IncompleteTailLength(byte[] bytes)
    {
        var i = bytes.Length - 1;
        if (i < 0 || bytes[i] < 0x80)
            return 0;

        // Walk back over continuation bytes to find the lead byte of the last sequence.
        var start = i;
        while (start > 0 && bytes[start] is >= 0x80 and < 0xC0)
            start--;

        if (bytes[start] < 0xC0)
            return 0; // stray continuation byte(s) with no lead byte

        var expected = bytes[start] switch
        {
            < 0xE0 => 1,
            < 0xF0 => 2,
            _ => 3,
        };
        var available = i - start;
        return available < expected ? i - start + 1 : 0;
    }

    private bool TryReap()
    {
        if (exitCode is not null)
            return true;

        var r = NativeMethods.waitpid(pid, out var status, NativeMethods.WaitOptions.Wnohang);
        if (r == 0)
            return false; // still running

        if (r < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == NativeMethods.Eintr)
                return false;
            if (err == NativeMethods.Echild)
            {
                exitCode = -1; // already reaped elsewhere
                return true;
            }
            throw new IOException($"waitpid failed: errno={err}");
        }

        exitCode = ExtractExitCode(status);
        return true;
    }

    private static int ExtractExitCode(int status)
    {
        var signal = status & 0x7F;
        return signal == 0 ? (status >> 8) & 0xFF : 128 + signal;
    }
}

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace dotnet_pty;

/// <summary>
/// A child process attached to a pseudo-terminal (PTY), created via <c>openpty(3)</c> +
/// <c>posix_spawn(2)</c>.
/// Use it to drive an interactive shell (e.g. bash): write commands, read back the terminal output.
/// </summary>
/// <remarks>
/// The pty master fd comes from openpty as a <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>
/// and is wrapped in a non-blocking <see cref="PtyStream"/>: all I/O is driven by poll(2),
/// so no operation ever blocks a thread-pool thread and cancellation is immediate (see
/// <see cref="PtyIoEngine"/>). <see cref="BaseStream"/> exposes the raw byte stream; the
/// string methods below layer a UTF-8 text buffer on top of it.
/// </remarks>
public sealed class PtyProcess : IDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<byte> pending = [];

    private bool eof;
    private bool disposed;

    /// <summary>OS process id of the child.</summary>
    public int Pid { get; }

    /// <summary>Exit code, set once the child has been reaped via <see cref="WaitForExit"/> or dispose.</summary>
    public int? ExitCode { get; private set; }

    public bool HasExited => ExitCode is not null;

    /// <summary>
    /// The raw byte stream over the pty master. Read/write directly only when you need
    /// byte-level (not string) I/O; note that reading from <see cref="BaseStream"/> bypasses
    /// the pending-text buffer that the string methods (<see cref="ReadUntil"/>,
    /// <see cref="ReadAvailable"/>) accumulate, so interleaving both kinds of reads can
    /// reorder or consume output in surprising ways.
    /// </summary>
    public PtyStream BaseStream { get; }

    private PtyProcess(PtyStream stream, int pid)
    {
        BaseStream = stream;
        Pid = pid;
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
    private static PtyProcess Start(string file, string[] arguments, string? workingDirectory = null)
    {
        // Everything is prepared in the parent; posix_spawn performs the exec natively.
        var envp = ToNative(BuildEnvironment());
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
            return new PtyProcess(stream, pid);
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
                // The stream owns the master fd; dispose both (SafeHandle disposal is
                // idempotent) so a failed spawn cannot leak the fd.
                stream?.Dispose();
                NativeMethods.close(masterFd);
            }
        }
    }

    /// <summary>Sends <paramref name="data"/> to the shell's stdin (via the PTY master).</summary>
    public void Write(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bytes = Encoding.UTF8.GetBytes(data);
        gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // The fd is non-blocking; PtyStream's poll loop blocks here until the child
            // drains enough for all bytes to be accepted.
            BaseStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends <paramref name="data"/> to the shell's stdin asynchronously. Serialized with the
    /// sync <see cref="Write"/> by the same gate. Cancellation stops the write after whatever
    /// the device has consumed (a partial write) and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bytes = Encoding.UTF8.GetBytes(data);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads whatever output is currently available, waiting up to <paramref name="timeout"/>
    /// for the first byte. Returns an empty string if nothing arrives within the timeout.
    /// </summary>
    public string ReadAvailable(TimeSpan timeout)
    {
        gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ReadAvailableCore((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
        }
        finally
        {
            gate.Release();
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
                var diag = $"pid={Pid} eof={eof} pending={pending.Count} exited={HasExited}";
                throw new TimeoutException($"Timed out after {timeout} waiting for '{marker}'. Got: {sb} [{diag}]");
            }

            gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                sb.Append(ReadAvailableCore((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue)));

                if (sb.ToString().Contains(marker, StringComparison.Ordinal))
                    return sb.ToString();
                if (eof)
                    return sb.ToString(); // child exited before the marker appeared
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Reads until <paramref name="marker"/> appears in the output (inclusive) and returns
    /// everything read so far, asynchronously. Throws <see cref="TimeoutException"/> if the
    /// marker does not appear within <paramref name="timeout"/>. Returns whatever was read
    /// if the child exits before the marker shows up. Cancellation is immediate: no thread
    /// is parked while waiting.
    /// </summary>
    public async Task<string> ReadUntilAsync(string marker, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Length == 0)
            throw new ArgumentException("Marker must not be empty.", nameof(marker));

        var sb = new StringBuilder();
        var chunk = new byte[ReadBufferSize];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (true)
            {
                int n;
                try
                {
                    n = await BaseStream.ReadAsync(chunk, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The timeout fired (the caller's token did not).
                    var diag = $"pid={Pid} eof={eof} pending={pending.Count} exited={HasExited}";
                    throw new TimeoutException($"Timed out after {timeout} waiting for '{marker}'. Got: {sb} [{diag}]");
                }

                if (n == 0)
                {
                    eof = true; // child's slave side closed
                    return sb.ToString();
                }

                pending.AddRange(chunk.AsSpan(0, n));
                sb.Append(Drain());

                if (sb.ToString().Contains(marker, StringComparison.Ordinal))
                    return sb.ToString();
                if (eof)
                    return sb.ToString();
            }
        }
        finally
        {
            gate.Release();
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
            gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                DrainOutput(); // keep the pty buffer flowing while we wait
                if (TryReap())
                    return true;
            }
            finally
            {
                gate.Release();
            }

            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(10);
        }
    }

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

            // Reap the child so it does not linger as a zombie.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!TryReap() && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
        }
        finally
        {
            gate.Release();
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
        return [.. env];
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

        var buf = new byte[ReadBufferSize];
        if (!ReadOnce(buf, timeoutMs))
            return Drain(); // timed out with nothing new

        // Drain: keep reading while data keeps arriving; treat a quiet gap as "drained".
        while (true)
        {
            if (!ReadOnce(buf, 50))
            {
                // Nothing this instant; give the child a brief chance to produce more
                // before declaring the output drained (guards against spurious polls).
                Thread.Sleep(10);
                if (!ReadOnce(buf, 50))
                    break;
            }
            if (eof)
                break;
        }

        return Drain();
    }

    /// <summary>
    /// Reads one chunk into <paramref name="buf"/>, waiting up to <paramref name="timeoutMs"/>
    /// for the first byte. Returns true when data was read (appended to <c>pending</c>) or the
    /// slave side closed (<c>eof</c> set); false when nothing arrived within the timeout.
    /// </summary>
    private bool ReadOnce(byte[] buf, int timeoutMs)
    {
        if (eof)
            return true; // already at EOF: nothing more to read

        try
        {
            var n = BaseStream.Read(buf, timeoutMs, out var reachedEof);
            if (reachedEof)
                eof = true;
            if (n <= 0) return eof; // n == 0: timeout (false) or EOF (true)
            pending.AddRange(buf.AsSpan(0, n));
            return true;
        }
        catch (IOException)
        {
            // The child's slave side closed (EIO). Treat as EOF.
            eof = true;
            return true;
        }
    }

    /// <summary>
    /// Non-blocking drain: reads whatever output is currently available into <c>pending</c>
    /// without consuming it. Used by <see cref="WaitForExit"/> so the child never blocks on a
    /// full pty buffer while we are not reading.
    /// </summary>
    private void DrainOutput()
    {
        if (eof)
            return;

        var buf = new byte[ReadBufferSize];
        while (true)
        {
            var n = BaseStream.Read(buf, 0, out var reachedEof); // immediate, non-blocking
            if (reachedEof)
                eof = true;
            if (n > 0)
            {
                pending.AddRange(buf.AsSpan(0, n));
                continue; // more may be buffered
            }
            return; // timeout (nothing available right now) or EOF
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
        if (ExitCode is not null)
            return true;

        var r = NativeMethods.waitpid(Pid, out var status, NativeMethods.WaitOptions.Wnohang);
        switch (r)
        {
            case 0:
                return false; // still running
            case < 0:
            {
                var err = Marshal.GetLastPInvokeError();
                switch (err)
                {
                    case NativeMethods.Eintr:
                        return false;
                    case NativeMethods.Echild:
                        ExitCode = -1; // already reaped elsewhere
                        return true;
                    default:
                        throw new IOException($"waitpid failed: errno={err}");
                }
            }
            default:
                ExitCode = ExtractExitCode(status);
                return true;
        }
    }

    private static int ExtractExitCode(int status)
    {
        var signal = status & 0x7F;
        return signal == 0 ? (status >> 8) & 0xFF : 128 + signal;
    }
}

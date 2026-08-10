using System.Runtime.InteropServices;

namespace dotnet_pty;

/// <summary>
/// P/Invoke declarations for the libc functions used to set up a pseudo-terminal
/// and run an interactive shell in it. Uses <c>openpty(3)</c> + <c>posix_spawn(2)</c>
/// (not fork+exec) so spawning stays safe in a multi-threaded process.
/// Constants are split per platform: macOS (OSX) and Linux glibc differ in the
/// POSIX_SPAWN_SETSID value, errno numbers and the fd-closing mechanism.
/// </summary>
internal static partial class NativeMethods
{
    // poll(2) event bits
    internal const short Pollin = 0x0001;

    // waitpid(2) options
    internal const int Wnohang = 0x0001;

    // errno values that are identical on macOS and Linux.
    internal const int Eintr = 4;
    internal const int Eio = 5;
    internal const int Echild = 10;

#if OSX
    // macOS errno values.
    internal const int Eagain = 35;
    internal const int Ewouldblock = 35; // == EAGAIN on macOS
#elif LINUX
    // Linux (glibc) errno values.
    internal const int Eagain = 11;
    internal const int Ewouldblock = 11; // == EAGAIN on Linux
#else
#error "dotnet-pty supports macOS (define OSX) and Linux (define LINUX) only."
#endif

    // SIGHUP: delivered explicitly on dispose because posix_spawn+SETSID gives the
    // child no controlling terminal, so closing the pty master alone won't hang it up.
    internal const int Sighup = 1;

    // posix_spawnattr flag: make the child a new session leader.
    // The value differs per libc — macOS defines POSIX_SPAWN_SETSID as 0x0400
    // (Darwin-specific flag), glibc as 0x0800. Using the wrong value silently fails
    // to create the session, which breaks ctty acquisition and job control.
    // (On macOS 0x0800 is _POSIX_SPAWN_RESLIDE; on glibc there is no 0x0400 flag.)
#if OSX
    internal const short PosixSpawnSetsid = 0x0400;
#elif LINUX
    // glibc posix/spawn.h: POSIX_SPAWN_SETSID is 0x80 (low byte). macOS's 0x0800
    // value is NOT valid here — glibc stores it as a flag that no code path reads,
    // so setsid() silently never runs and the child gets no session/ctty.
    internal const short PosixSpawnSetsid = 0x0080;

    // glibc: POSIX_SPAWN_SETSIGDEF = 0x04 — signals listed in the "sigdefault" set
    // are reset to SIG_DFL in the child. POSIX spawn inherits SIG_IGN dispositions
    // from the parent (macOS resets them automatically, glibc does not), and the
    // .NET runtime ignores SIGPIPE/SIGINT etc., so we reset the common ones.
    internal const short PosixSpawnSetsigdef = 0x0004;

    // sizeof(sigset_t) on Linux (glibc): 16 unsigned longs = 128 bytes.
    internal const int SigsetSize = 128;
#endif

#if OSX
    // macOS: inherited fds default to close-on-exec in the child unless a file action
    // explicitly keeps them (our dup2 of the pty slave to 0/1/2). Without this flag the
    // child would inherit every fd the parent runtime has open (sockets, files, pipes),
    // leaking runtime global state into the shell.
    internal const short PosixSpawnCloexecDefault = 0x4000;
#endif

    // Buffer sizes for the opaque spawn types: both are well under this on macOS.
    internal const int PosixSpawnFileActionsSize = 512;
    internal const int PosixSpawnAttrSize = 512;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        internal int Fd;
        internal short Events;
        internal short Revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int openpty(ref int master, ref int slave, IntPtr name, IntPtr termp, IntPtr winp);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn(
        out int pid, IntPtr path, IntPtr fileActions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_init(IntPtr fileActions);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_destroy(IntPtr fileActions);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_adddup2(IntPtr fileActions, int from, int to);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_addclose(IntPtr fileActions, int fd);

    [DllImport("libc", SetLastError = true)]
    internal static extern int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, string path);

#if LINUX
    // glibc extension (>= 2.34): close every fd >= lowfd in the child except the ones
    // the file actions keep open (our dup2 of the pty slave to 0/1/2). Linux equivalent
    // of macOS POSIX_SPAWN_CLOEXEC_DEFAULT: keeps the runtime's fds (sockets, files,
    // pipes) out of the shell. glibc's vfork-based posix_spawn does this anyway, but
    // being explicit makes the guarantee independent of that implementation detail.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_addclosefrom_np(IntPtr fileActions, int lowfd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_setsigdefault(IntPtr attr, byte[] sigset);

    /// <summary>
    /// Builds a glibc sigset_t: an all-zero buffer is sigemptyset, and each signal N
    /// is bit (N-1) of the set (glibc __sigset_t, LSB-first within the word).
    /// </summary>
    internal static byte[] SignalSet(params int[] signals)
    {
        var set = new byte[SigsetSize];
        foreach (var sig in signals)
            if (sig >= 1 && sig <= SigsetSize * 8)
                set[(sig - 1) / 8] |= (byte)(1 << ((sig - 1) % 8));
        return set;
    }
#endif

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_init(IntPtr attr);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_destroy(IntPtr attr);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_setflags(IntPtr attr, short flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int read(int fd, byte[] buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int write(int fd, byte[] buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, int options);
}

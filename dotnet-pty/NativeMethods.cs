using System.Runtime.InteropServices;

namespace dotnet_pty;

/// <summary>
/// P/Invoke declarations for the libc functions used to set up a pseudo-terminal
/// and run an interactive shell in it. Uses <c>openpty(3)</c> + <c>posix_spawn(2)</c>
/// (not fork+exec) so spawning stays safe in a multi-threaded process.
/// macOS-specific constants included.
/// </summary>
internal static partial class NativeMethods
{
    // poll(2) event bits (macOS)
    internal const short Pollin = 0x0001;

    // waitpid(2) options (macOS)
    internal const int Wnohang = 0x0001;

    // errno values (macOS)
    internal const int Eintr = 4;
    internal const int Eio = 5;
    internal const int Echild = 10;
    internal const int Eagain = 35;
    internal const int Ewouldblock = 35; // == EAGAIN on macOS

    // SIGHUP: delivered explicitly on dispose because posix_spawn+SETSID gives the
    // child no controlling terminal, so closing the pty master alone won't hang it up.
    internal const int Sighup = 1;

    // posix_spawnattr flags (macOS sys/spawn.h): make the child a new session leader.
    // NOTE: macOS defines POSIX_SPAWN_SETSID as 0x0400 (Darwin-specific flag), NOT the
    // glibc value 0x0800. 0x0800 is _POSIX_SPAWN_RESLIDE here — using it silently fails
    // to create the session, which breaks ctty acquisition and job control.
    internal const short PosixSpawnSetsid = 0x0400;

    // macOS: inherited fds default to close-on-exec in the child unless a file action
    // explicitly keeps them (our dup2 of the pty slave to 0/1/2). Without this flag the
    // child would inherit every fd the parent runtime has open (sockets, files, pipes),
    // leaking runtime global state into the shell.
    internal const short PosixSpawnCloexecDefault = 0x4000;

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

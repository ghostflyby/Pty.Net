using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace dotnet_pty;

/// <summary>
/// P/Invoke declarations for the libc functions used to set up a pseudo-terminal
/// and run an interactive shell in it. Uses <c>openpty(3)</c> + <c>posix_spawn(2)</c>
/// (not fork+exec) so spawning stays safe in a multi-threaded process.
///
/// fd-typed declarations use <see cref="SafeFileHandle"/> and rely on the runtime's
/// automatic handle marshaling, so native fds are wrapped, owned and closed for us.
/// Byte transfer goes through a <see cref="FileStream"/> over the master handle
/// (see <see cref="PtyProcess"/>) instead of raw read/write P/Invokes.
///
/// Constants are split per platform: macOS (OSX) and Linux glibc differ in the
/// POSIX_SPAWN_SETSID value and the fd-closing mechanism.
/// </summary>
internal static partial class NativeMethods
{
    // poll(2) event bits.
    [Flags]
    internal enum PollEvents : short
    {
        None = 0,
        Pollin = 0x0001,
    }

    // waitpid(2) options.
    [Flags]
    internal enum WaitOptions : int
    {
        None = 0,
        Wnohang = 0x0001,
    }

    // Signal numbers (identical on macOS and Linux).
    internal enum Signals : int
    {
        Hup = 1,
        Int = 2,
        Quit = 3,
        Pipe = 13,
        Term = 15,
    }

    // posix_spawnattr flags. The SETSID value differs per libc — macOS defines
    // POSIX_SPAWN_SETSID as 0x0400 (Darwin-specific), glibc as 0x0080. Using the wrong
    // value silently fails to create the session, which breaks ctty acquisition and
    // job control. (On macOS 0x0800 is _POSIX_SPAWN_RESLIDE; on glibc it maps to a flag
    // no code path reads.)
    [Flags]
    internal enum PosixSpawnFlags : short
    {
        None = 0,
#if OSX
        // Darwin-specific: inherited fds default to close-on-exec in the child unless
        // a file action explicitly keeps them (our dup2 of the pty slave to 0/1/2).
        CloexecDefault = 0x4000,
        Setsid = 0x0400,
#elif LINUX
        // glibc: POSIX_SPAWN_SETSIGDEF — the signals in the "sigdefault" set are reset
        // to SIG_DFL in the child (POSIX spawn inherits SIG_IGN; macOS resets them
        // automatically, glibc does not).
        Setsigdef = 0x0004,
        Setsid = 0x0080,
#else
#error "dotnet-pty supports macOS (define OSX) and Linux (define LINUX) only."
#endif
    }

    // errno values used in the poll/waitpid retry logic (identical on macOS and Linux).
    internal const int Eintr = 4;
    internal const int Echild = 10;

    // Buffer sizes for the opaque spawn types: both are well under this on macOS.
    internal const int PosixSpawnFileActionsSize = 512;
    internal const int PosixSpawnAttrSize = 512;

#if LINUX
    // sizeof(sigset_t) on Linux (glibc): 16 unsigned longs = 128 bytes.
    internal const int SigsetSize = 128;
#endif

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        internal int Fd;
        internal PollEvents Events;
        internal PollEvents Revents;
    }

    // openpty writes the master/slave fds into the caller's slots; the runtime wraps
    // them in SafeFileHandles and transfers ownership (disposing the handle closes the
    // fd, and the SafeHandle finalizer is the backstop if we forget).
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int openpty(out SafeFileHandle master, out SafeFileHandle slave, IntPtr name, IntPtr termp, IntPtr winp);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn(
        out int pid, IntPtr path, IntPtr fileActions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_init(IntPtr fileActions);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_destroy(IntPtr fileActions);

    // These fd numbers must be the exact values (they are referenced by the file
    // actions inside the child), so callers pass raw handle values — SafeHandle
    // auto-marshaling could pass a duplicated fd number instead.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_adddup2(IntPtr fileActions, int from, int to);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn_file_actions_addclose(IntPtr fileActions, int fd);

    // Path is marshaled as UTF-8 (StringMarshalling) so LibraryImport stays AOT/trim
    // compatible, and the POSIX chdir file action takes const char* paths.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, string path);

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
    internal static byte[] SignalSet(params Signals[] signals)
    {
        var set = new byte[SigsetSize];
        foreach (var sig in signals)
        {
            var n = (int)sig;
            if (n >= 1 && n <= SigsetSize * 8)
                set[(n - 1) / 8] |= (byte)(1 << ((n - 1) % 8));
        }
        return set;
    }
#endif

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_init(IntPtr attr);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_destroy(IntPtr attr);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_setflags(IntPtr attr, PosixSpawnFlags flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, Signals sig);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, WaitOptions options);
}

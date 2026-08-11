using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// P/Invoke declarations for the libc functions used to set up a pseudo-terminal
/// and run an interactive shell in it. Uses <c>posix_openpt(3)</c> + <c>posix_spawn(2)</c>
/// (not fork+exec) so spawning stays safe in a multi-threaded process.
///
/// pty setup returns raw fds (posix_openpt/grantpt/unlockpt/ptsname are non-variadic,
/// so they work on Apple arm64 where the variadic fcntl mis-delivers its third argument);
/// the caller wraps the master in a <see cref="SafeFileHandle"/> for <see cref="PtyStream"/>.
/// Byte transfer goes through raw read(2)/write(2) on the non-blocking master fd,
/// driven by <see cref="PtyStream"/> / <see cref="PtyIoEngine"/> (see <see cref="PtyProcess"/>).
///
/// Constants are split per platform: macOS (OSX) and Linux glibc differ in the
/// POSIX_SPAWN_SETSID value and the fd-closing mechanism.
/// </summary>
// ReSharper disable IdentifierTypo
// ReSharper disable CommentTypo
internal static partial class NativeMethods
{
    // poll(2) event bits (identical values on macOS and Linux).
    [Flags]
    internal enum PollEvents : short
    {
        None = 0,
        Pollin = 0x0001,
        Pollout = 0x0004,
        Pollerr = 0x0008,
        Pollhup = 0x0010,
        Pollnval = 0x0020,
    }

    // waitpid(2) options.
    [Flags]
    internal enum WaitOptions
    {
        None = 0,
        Wnohang = 0x0001,
    }

    // Signal numbers (identical on macOS and Linux).
    internal enum Signals
    {
        Hup = 1,
        Int = 2,
        Quit = 3,
        Kill = 9,
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
#error "Pty.Net supports macOS (define OSX) and Linux (define LINUX) only."
#endif
    }

    // errno values used in the poll/read/write retry logic.
    // EINTR and EIO are identical on macOS and Linux; EAGAIN differs.
    internal const int Eintr = 4;
    internal const int Eio = 5;
    internal const int Echild = 10;
#if OSX
    internal const int Eagain = 35;
#elif LINUX
    internal const int Eagain = 11;
#else
#error "Pty.Net supports macOS (define OSX) and Linux (define LINUX) only."
#endif

    // open(2) / posix_openpt(2) flag bits. O_RDWR is identical; O_NONBLOCK and
    // O_NOCTTY differ per platform. O_NONBLOCK is applied to the pty master at
    // posix_openpt time (via fcntl it would hit the broken variadic fcntl on Apple
    // arm64 — posix_openpt is non-variadic and portable).
#if OSX
    internal const int ONonblock = 0x0004;
    internal const int ONoctty = 0x20000;
#elif LINUX
    internal const int ONonblock = 0x0800;
    internal const int ONoctty = 0x00100;
#else
#error "Pty.Net supports macOS (define OSX) and Linux (define LINUX) only."
#endif
    internal const int ORdwr = 0x0002;

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

    // posix_openpt(3) + grantpt/unlockpt/ptsname/open(2) replace openpty(3): all are
    // non-variadic, so they work on Apple arm64 (where variadic fcntl mis-delivers
    // its third argument). The O_NONBLOCK flag on the master fd is the foundation of
    // PtyStream's poll-driven I/O. posix_openpt/grantpt/unlockpt return raw fds;
    // the runtime wraps the master in a SafeFileHandle for PtyStream, the slave is
    // closed after spawn.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int unlockpt(int fd);

    // ptsname returns a pointer to a libc-owned static buffer. The runtime's string
    // return marshaling would try to free that pointer (interop treats returned char*
    // as allocated), corrupting every later call — so it is declared as IntPtr and
    // converted with Marshal.PtrToStringUTF8 (which does not free) by the caller.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial IntPtr ptsname(int fd);

    // Two-argument open(2) — the pty slave never needs O_CREAT, whose mode argument
    // would exercise the same broken variadic path as fcntl.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawn(
        out int pid, IntPtr path, IntPtr fileActions, IntPtr attr,
        [In] IntPtr[] argv, [In] IntPtr[] envp);

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
    // Upper bound for the per-fd isolation loop in PtyProcess.StartCore. The default
    // RLIMIT_NOFILE soft limit is 1024, and the runtime's own sockets/pipes/files live
    // in the low fds — anything above this would require a raisefd limit to exist at
    // all. A single closefrom call would be nicer (glibc's addclosefrom_np), but musl
    // does not export it, so a loop of the standard posix_spawn_file_actions_addclose
    // is used instead: both libcs execute FDOP_CLOSE in the child and ignore EBADF
    // when closing an already-closed fd, so the loop is portable and cheap.
    internal const int MaxInheritedFd = 1024;

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_setsigdefault(IntPtr attr, [In] byte[] sigset);

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

    // Byte transfer for PtyStream: raw read(2)/write(2) on the non-blocking pty master
    // fd. Callers pin the buffer (MemoryHandle / fixed) and pass the raw pointer, so a
    // single IntPtr overload serves both the sync and the engine-driven async paths.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint read(int fd, IntPtr buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint write(int fd, IntPtr buf, nuint count);

    // pipe(2) creates the PtyIoEngine wakeup channel: any thread writes a byte to
    // interrupt the engine's poll(2) so it applies pending register/unregister messages.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int pipe([Out] int[] fds);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, WaitOptions options);
}

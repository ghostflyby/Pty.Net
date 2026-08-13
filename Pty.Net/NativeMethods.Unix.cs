using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// P/Invoke declarations for the libc functions used to set up a pseudo-terminal
/// and run an interactive shell in it. Uses <c>posix_openpt(3)</c> + <c>posix_spawn(2)</c>
/// (not fork+exec) so spawning stays safe in a multi-threaded process.
///
/// Unix-only: this file is compiled only by the non-Windows target (see csproj), so
/// Windows-specific constants and branches are absent. pty setup returns raw fds
/// (posix_openpt/grantpt/unlockpt/ptsname are non-variadic, so they work on Apple arm64
/// where the variadic fcntl mis-delivers its third argument); the caller wraps the master
/// in a <see cref="SafeFileHandle"/> for <see cref="PtyStream"/>. Byte transfer goes
/// through raw read(2)/write(2) on the non-blocking master fd, driven by
/// <see cref="PtyStream"/> / <see cref="PtyIoEngine"/> (see <see cref="PtyProcess"/>).
///
/// Constants are split per platform: macOS (OSX) and Linux glibc differ in the
/// POSIX_SPAWN_SETSID value and the fd-closing mechanism.
/// </summary>
// ReSharper disable IdentifierTypo
// ReSharper disable CommentTypo
// ReSharper disable InconsistentNaming
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
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
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
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
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
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif
    internal const int ORdwr = 0x0002;

    // TIOCSWINSZ: the ioctl request that sets the terminal window size in characters.
    // macOS 0x80087467 (_IOW('t', 104, winsize), C-verified), Linux 0x5414
    // (asm-generic/ioctls.h).
#if OSX
    internal const nuint Tiocswinsz = 0x80087467;
#elif LINUX
    internal const nuint Tiocswinsz = 0x5414;
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif

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

    // struct winsize (sys/ioctl.h): identical layout on macOS and Linux — row and
    // column counts in characters, then pixel dimensions (unused by TIOCSWINSZ, kept 0).
    [StructLayout(LayoutKind.Sequential)]
    internal struct Winsize
    {
        internal ushort Row;
        internal ushort Col;
        internal ushort Xpixel;
        internal ushort Ypixel;
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

    // ioctl(2) is variadic (int ioctl(int, unsigned long, ...)) and .NET has no
    // varargs interop (__arglist throws InvalidProgramException on non-Windows), so a
    // fixed signature must substitute. Whether that works depends on the libc's
    // va_list mechanics:
    //   * Linux (glibc/musl) reads the leading variadic arguments from the argument
    //     registers, so a plain fixed signature (fd, request, arg in x0/x1/x2 on
    //     arm64, rdi/rsi/rdx on x64) delivers the pointer correctly on both.
    //   * Apple arm64 stores variadic arguments on the stack, so the fixed signature
    //     must push the real argument there: the proven workaround (IronPython's
    //     fcntl module, dotnet/runtime#48796) pads the six remaining general-purpose
    //     registers (x2-x7) with dummies, landing the pointer in the stack argument
    //     area where Apple's va_arg looks for it.
    // Both forms were verified empirically on Apple arm64 (pad works, plain crashes)
    // and the plain form on Linux x64; the Linux arm64 plain form is exercised by the
    // CI arm64 runner.
    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    internal static partial int ioctl(int fd, nuint request, IntPtr arg);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    internal static partial int ioctlArm64(int fd, nuint request,
        nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, IntPtr arg);

    /// <summary>Calls ioctl(fd, request, arg), selecting the pad-register form where the platform's variadic ABI requires it.</summary>
    internal static int IoCtl(int fd, nuint request, IntPtr arg)
    {
        return OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctlArm64(fd, request, 0, 0, 0, 0, 0, 0, arg)
            : ioctl(fd, request, arg);
    }

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
    // Upper bound for the per-fd isolation loop in PtyProcess.StartCore. The loop runs
    // to min(RLIMIT_NOFILE soft limit, this cap): the soft limit because glibc's addclose
    // rejects fds >= sysconf(_SC_OPEN_MAX) with EBADF (musl accepts any non-negative fd
    // and ignores EBADF at close time); the cap so a huge limit (servers/containers often
    // run hundreds of thousands) cannot turn every spawn into a million-file-action loop.
    // Fds above the cap would leak into the child, but .NET's own fds are all CLOEXEC
    // (closed by the kernel at exec), so only non-CLOEXEC fds from P/Invoke could leak —
    // those live in the low range in practice. A single closefrom call would be nicer
    // (glibc's addclosefrom_np), but musl does not export it, so the portable addclose
    // loop is used: both libcs execute FDOP_CLOSE in the child and ignore EBADF when
    // closing an already-closed fd.
    internal const int FdIsolationCap = 4096;

    // Linux: RLIMIT_NOFILE from <sys/resource.h>. Per-arch in musl/glibc (MIPS uses 5);
    // 7 is the generic value on x86_64/aarch64. Sits in the LINUX section because macOS
    // uses a different number.
    internal const int RlimitNofile = 7;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RLimit
    {
        internal nuint Cur; // rlim_cur (soft limit)
        internal nuint Max; // rlim_max (hard limit)
    }

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int getrlimit(int resource, out RLimit rlim);

    // Pointer form so the caller can pass a stackalloc'd sigset (pinned with fixed):
    // a byte[] overload would allocate a 128-byte array on every Linux spawn.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr sigset);

    /// <summary>
    /// Fills <paramref name="set"/> as a glibc sigset_t: an all-zero buffer is
    /// sigemptyset, and each signal N is bit (N-1) of the set (glibc __sigset_t,
    /// LSB-first within the word). The caller supplies a stack-allocated buffer so a
    /// Linux spawn performs no per-call allocation.
    /// </summary>
    internal static void BuildSignalSet(scoped Span<byte> set, scoped ReadOnlySpan<Signals> signals)
    {
        set.Clear();
        foreach (var sig in signals)
        {
            var n = (int)sig;
            if (n >= 1 && n <= SigsetSize * 8)
                set[(n - 1) / 8] |= (byte)(1 << ((n - 1) % 8));
        }
    }

    // ---- epoll / pidfd / eventfd: the event-driven reaper (PtyReaper.Unix.cs) ----
    // epoll(2) event and control constants (identical across Linux architectures).
    internal const int EpollIn = 0x001;
    internal const int EpollCloexec = 0x80000; // EPOLL_CLOEXEC == O_CLOEXEC on Linux
    internal const int EpollCtlAdd = 1;
    internal const int EpollCtlDel = 2;
    // eventfd(2) flags: EFD_CLOEXEC (== O_CLOEXEC) and EFD_NONBLOCK (== O_NONBLOCK).
    internal const int EfdCloexec = 0x80000;
    internal const int EfdNonblock = 0x800;

    // struct epoll_event, two layout variants. The kernel defines the struct as
    // __attribute__((packed)) on x86_64 (events + data = 12 bytes, no padding) but
    // with the natural layout everywhere else (16 bytes on arm64). A mismatched
    // layout makes epoll_wait write back events at the wrong offsets, so the reaper
    // would read a corrupted data field and silently miss every pidfd exit event.
    // Compile-time symbols cannot express this (AnyCPU builds define no architecture
    // symbol), so the caller selects the variant at runtime — the same pattern as
    // IoCtl's pad-register selection.
    internal static bool EpollIsPacked => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EpollEvent
    {
        internal uint Events;
        internal ulong Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct EpollEventPacked
    {
        internal uint Events;
        internal ulong Data;
    }

    // epoll_ctl / epoll_wait for each layout variant (same libc entry points).
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int epoll_ctl(int epfd, int op, int fd, ref EpollEvent ev);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int epoll_wait(int epfd, [Out] EpollEvent[] events, int maxevents, int timeout);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "epoll_ctl")]
    internal static partial int epoll_ctl_packed(int epfd, int op, int fd, ref EpollEventPacked ev);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "epoll_wait")]
    internal static partial int epoll_wait_packed(int epfd, [Out] EpollEventPacked[] events, int maxevents, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int epoll_create1(int flags);

    // pidfd_open(2) (kernel 5.3+): an fd that reports readable once the process exits.
    // The reaper listens for that with epoll — exit detection without touching SIGCHLD.
    // musl (Alpine) does not export a pidfd_open wrapper, so it is invoked through the
    // generic syscall(2) with the architecture's __NR_pidfd_open, which glibc and musl
    // both export. The fixed (long, int, int) signature is safe: syscall(2) forwards its
    // first three arguments in the same registers on x64 (rdi/rsi/rdx) and arm64 (x0/x1/x2).
    [LibraryImport("libc", SetLastError = true)]
    internal static partial long syscall(long number, int arg1, int arg2);

    // __NR_pidfd_open is 434 on x86_64, i386, arm and aarch64 (verified on both
    // libcs); only riscv64 differs (424). The library's Linux targets are x64 and
    // arm64 (matching the CI matrix), so a single constant covers them; riscv64 is
    // not a supported target.
    internal const int PidfdOpenSyscallNumber = 434;

    // eventfd(2): the reaper's self-wake channel — a counter fd that stays writable, so
    // waking never blocks (unlike a pipe, which can fill).
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int eventfd(uint initval, int flags);
#endif

#if OSX
    // ---- kqueue / kevent: the event-driven reaper (PtyReaper.Unix.cs) ----
    // kevent filters and flags from <sys/event.h> (Darwin). EVFILT_PROC reports NOTE_EXIT
    // when the watched process exits; EVFILT_USER is a self-wake channel (macOS 10.9+)
    // triggered from any thread with NOTE_TRIGGER.
    internal const short EvfilProc = -7;
    internal const short EvfilUser = -10;
    internal const ushort EvAdd = 0x0001;
    internal const ushort EvDelete = 0x0002;
    internal const ushort EvClear = 0x0020;
    internal const uint NoteExit = 0x80000000;
    internal const uint NoteTrigger = 0x01000000;

    // struct kevent (Darwin, 64-bit): ident, filter, flags, fflags, data, udata. Fixed
    // signature, so it is safe on Apple arm64 (unlike variadic fcntl).
    [StructLayout(LayoutKind.Sequential)]
    internal struct Kevent
    {
        internal nuint Ident;
        internal short Filter;
        internal ushort Flags;
        internal uint Fflags;
        internal nint Data;
        internal IntPtr Udata;
    }

    // struct timespec (Darwin): the kevent(2) timeout, passed as a pointer (null = wait
    // indefinitely). Only used to bound the reaper's wait while a registration retry is
    // pending.
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeSpec
    {
        internal long TvSec;
        internal long TvNsec;
    }

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kqueue();

    // kevent(kq, changelist, nchanges, eventlist, nevents, timeout): timeout is a
    // struct timespec* — IntPtr.Zero means block indefinitely. Either list may be null
    // with count 0; the eventlist is written back with returned events.
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kevent(int kq, [In] Kevent[]? changelist, int nchanges, [Out] Kevent[]? eventlist, int nevents, IntPtr timeout);
#endif

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_init(IntPtr attr);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_destroy(IntPtr attr);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_spawnattr_setflags(IntPtr attr, PosixSpawnFlags flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, Signals sig);

    internal static int poll(Span<PollFd> fds, int timeout)
    {
        return poll(fds, (nuint)fds.Length, timeout);
    }

    // poll(2) takes a pointer so callers can pass a stackalloc'd PollFd span (pinned
    // with fixed): a PollFd[] overload would allocate a fresh one-element heap array
    // on every sync read/write and every engine wake. The span is mutable because poll
    // rewrites each entry's Revents in place.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(Span<PollFd> fds, nuint nfds, int timeout);

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

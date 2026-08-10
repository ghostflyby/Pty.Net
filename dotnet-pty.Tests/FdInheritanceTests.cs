using System.Runtime.InteropServices;
using dotnet_pty;
using Microsoft.Win32.SafeHandles;

namespace dotnet_pty.Tests;

// Demonstrates that posix_spawn, unless told otherwise, inherits ALL parent fds
// into the child (same hole fork has, minus the lock deadlock). macOS
// POSIX_SPAWN_CLOEXEC_DEFAULT / Linux addclosefrom_np close the leak. This test
// FAILS without that isolation and PASSES with it.
public partial class FdInheritanceTests
{
    private const string ProbeFile = "/tmp/pty-fd-leak-probe";
    private const string Done = "__DONE__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ChildDoesNotInheritParentFileDescriptors()
    {
        // open(2) returns a raw fd (the LibraryImport generator supports SafeFileHandle
        // as parameters/out only, not as return values), so we wrap the fd ourselves;
        // the SafeFileHandle then owns and closes it via `using`.
        using var probe = new SafeFileHandle(
            (IntPtr)Native.open(
                ProbeFile,
                Native.OpenFlags.OCreat | Native.OpenFlags.ORdwr,
                Native.UnixPermissions.UserRead | Native.UnixPermissions.UserWrite
                    | Native.UnixPermissions.GroupRead | Native.UnixPermissions.OtherRead),
            ownsHandle: true);
        var fd = (int)probe.DangerousGetHandle();
        Assert.True(fd >= 3, $"expected a parent-side fd >= 3, got {fd}");
        try
        {
            using var bash = PtyProcess.StartBash();
            bash.ReadUntil("$", Timeout);

            // If fd leaked into the child, `/dev/fd/{fd}` is accessible from bash and
            // prints LEAKED; otherwise the child reports CLEAN.
            bash.Write($"if [ -e /dev/fd/{fd} ]; then echo LEAKED; else echo CLEAN; fi; echo {Done}\n");
            var output = bash.ReadUntil(Done, Timeout);

            Assert.Contains("CLEAN", output);
        }
        finally
        {
            File.Delete(ProbeFile);
        }
    }

    internal static partial class Native
    {
        // O_CREAT differs per libc: 0x0200 on macOS, 0x0040 on Linux (glibc) — on Linux
        // 0x0200 is O_TRUNC, so using the macOS value silently fails to create the file.
        [Flags]
        internal enum OpenFlags : int
        {
            ORdwr = 0x0002,
#if OSX
            OCreat = 0x0200,
#elif LINUX
            OCreat = 0x0040,
#endif
        }

        // Permission bits for the probe file (0644).
        [Flags]
        internal enum UnixPermissions : int
        {
            UserRead = 0x100,  // 0400
            UserWrite = 0x080, // 0200
            GroupRead = 0x020, // 0040
            OtherRead = 0x004, // 0004
        }

        // The LibraryImport generator supports SafeFileHandle as parameters/out only,
        // not as return values, so open(2) returns the raw fd and the caller wraps it
        // in a SafeFileHandle (which then owns and closes it).
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int open(string path, OpenFlags flags, UnixPermissions mode);
    }
}

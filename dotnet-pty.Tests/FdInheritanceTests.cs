using System.Runtime.InteropServices;
using dotnet_pty;

namespace dotnet_pty.Tests;

// Demonstrates that posix_spawn, unless told otherwise, inherits ALL parent fds
// into the child (same hole fork has, minus the lock deadlock). POSIX_SPAWN_CLOEXEC_DEFAULT
// closes the leak. This test FAILS without that flag and PASSES with it.
public partial class FdInheritanceTests
{
    private const string ProbeFile = "/tmp/pty-fd-leak-probe";
    private const string Done = "__DONE__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ChildDoesNotInheritParentFileDescriptors()
    {
        // Open a file in the parent, leaving the fd open across the spawn.
        var fd = Native.open(ProbeFile, OCreat | ORdwr, 0x1A4); // 0644
        Assert.True(fd >= 3, $"expected a parent-side fd >= 3, got {fd}");
        try
        {
            using var bash = PtyProcess.StartBash();
            bash.ReadUntil("$", Timeout);

            // If fd leaked into the child, `ls -l /dev/fd/{fd}` succeeds; otherwise
            // the child reports "No such file or directory".
            // If fd leaked into the child, `/dev/fd/{fd}` is accessible from bash and
            // prints LEAKED; otherwise the child reports CLEAN.
            bash.Write($"if [ -e /dev/fd/{fd} ]; then echo LEAKED; else echo CLEAN; fi; echo {Done}\n");
            var output = bash.ReadUntil(Done, Timeout);

            Assert.Contains("CLEAN", output);
        }
        finally
        {
            Native.close(fd);
            File.Delete(ProbeFile);
        }
    }

    private const int OCreat = 0x0200;
    private const int ORdwr = 0x0002;

    private static partial class Native
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int open(string path, int flags, int mode);

        [LibraryImport("libc", SetLastError = true)]
        internal static partial int close(int fd);
    }
}

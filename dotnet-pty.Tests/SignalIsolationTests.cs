using System.Runtime.InteropServices;
using dotnet_pty;

namespace dotnet_pty.Tests;

// The .NET runtime (or host app) may install custom signal handlers (e.g. ignore
// SIGPIPE). If posix_spawn inherited those dispositions into the shell, pipe behavior
// would silently change. This test asserts the child resets caught/ignored signals
// to their defaults — the sane "fresh process" contract.
public partial class SignalIsolationTests
{
    private const string Done = "__DONE__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ChildResetsSignalsToDefaults()
    {
        // Parent ignores SIGINT and SIGPIPE.
        Native.signal(2, new IntPtr(1)); // SIG_IGN
        Native.signal(13, new IntPtr(1));
        try
        {
            using var bash = PtyProcess.StartBash();
            bash.ReadUntil("$", Timeout);

            // SIGINT (2): if the child inherited SIG_IGN, trap -p prints
            // "trap '' INT". Default disposition prints nothing for INT.
            bash.Write($"trap -p INT TERM; echo {Done}\n");
            var output = bash.ReadUntil(Done, Timeout);

            Assert.DoesNotContain("trap '' INT", output);
        }
        finally
        {
            Native.signal(2, IntPtr.Zero); // SIG_DFL
            Native.signal(13, IntPtr.Zero);
        }
    }

    private static partial class Native
    {
        [LibraryImport("libc", SetLastError = true)]
        internal static partial IntPtr signal(int signum, IntPtr handler);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Ghostflyby.Pty.Tests;
#if !WINDOWS

// The .NET runtime (or host app) may install custom signal handlers (e.g. ignore
// SIGPIPE). If posix_spawn inherited those dispositions into the shell, pipe behavior
// would silently change. This test asserts the child resets caught/ignored signals
// to their defaults — the sane "fresh process" contract. (On Linux this is enforced
// explicitly via POSIX_SPAWN_SETSIGDEF; macOS resets caught signals automatically.)
public partial class SignalIsolationTests
{
    private const string Done = "__DONE__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ChildResetsSignalsToDefaults()
    {
        // Parent ignores SIGINT and SIGPIPE.
        Native.signal(Native.Signals.Int, new IntPtr(1)); // SIG_IGN
        Native.signal(Native.Signals.Pipe, new IntPtr(1));
        try
        {
            using var bash = TestBash.Start();
            TestBash.ReadUntil(bash.Output, "$", Timeout);

            // SIGINT (2): if the child inherited SIG_IGN, trap -p prints
            // "trap '' INT". Default disposition prints nothing for INT.
            bash.Input.WriteLine($"trap -p INT TERM; echo {Done}");
            var output = TestBash.ReadUntil(bash.Output, Done, Timeout);

            Assert.DoesNotContain("trap '' INT", output);
        }
        finally
        {
            Native.signal(Native.Signals.Int, IntPtr.Zero); // SIG_DFL
            Native.signal(Native.Signals.Pipe, IntPtr.Zero);
        }
    }

    internal static partial class Native
    {
        internal enum Signals
        {
            Int = 2,
            Pipe = 13,
        }

        [LibraryImport("libc", SetLastError = true)]
        [SuppressMessage("ReSharper","InconsistentNaming")]
        internal static partial IntPtr signal(Signals signum, IntPtr handler);
    }
}

#endif

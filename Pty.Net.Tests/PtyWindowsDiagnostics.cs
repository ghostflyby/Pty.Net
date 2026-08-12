#if WINDOWS
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Regression coverage for repeated ConPTY creation and teardown in one process.</summary>
public class PtyWindowsDiagnostics
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void SequentialSessions_AllProduceOutputAndExit()
    {
        for (var session = 1; session <= 4; session++)
        {
            var marker = $"WINDOWS_PTY_SESSION_{session}";
            using var process = PtyProcess.Start("cmd.exe", ["/d", "/c", $"echo {marker}"]);

            var output = TestBash.ReadUntil(process.StandardOutput, marker, Timeout);

            Assert.Contains(marker, output);
            Assert.True(process.WaitForExit(Timeout), $"Session {session} did not exit in time.");
            Assert.Equal(0, process.ExitCode);
        }
    }
}
#endif

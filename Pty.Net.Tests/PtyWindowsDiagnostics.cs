#if WINDOWS
using Ghostflyby.Pty;
using Xunit.Abstractions;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: dumps granular launch/IO state to the CI log.</summary>
public class PtyWindowsDiagnostics
{
    private readonly ITestOutputHelper output;
    public PtyWindowsDiagnostics(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void DumpLaunchAndIoState()
    {
        output.WriteLine("=== diagnostic start ===");
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
        output.WriteLine($"pid={p.Pid} hasExited={p.HasExited}");

        // Wait a moment, then try synchronous reads with progress reporting.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        var total = 0;
        while (DateTime.UtcNow < deadline)
        {
            var buf = new byte[256];
            try
            {
                var n = p.BaseStream.Read(buf, 0, buf.Length);
                output.WriteLine($"read: n={n} bytes=[{System.Text.Encoding.UTF8.GetString(buf, 0, Math.Max(0, n))}]");
                if (n == 0)
                {
                    output.WriteLine("EOF reached");
                    break;
                }
                total += n;
            }
            catch (Exception ex)
            {
                output.WriteLine($"read threw: {ex}");
                break;
            }
        }
        output.WriteLine($"total bytes read: {total}");
        output.WriteLine($"WaitForExit(2s)={p.WaitForExit(TimeSpan.FromSeconds(2))} exitCode={p.ExitCode}");
        output.WriteLine("=== diagnostic end ===");
    }
}
#endif

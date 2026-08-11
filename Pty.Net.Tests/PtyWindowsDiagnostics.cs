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
    public async Task DumpLaunchAndIoState()
    {
        output.WriteLine("=== diagnostic start ===");
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
        output.WriteLine($"pid={p.Pid} hasExited={p.HasExited}");

        // Timed async reads so we never block forever.
        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buf = new byte[512];
            try
            {
                var n = await p.BaseStream.ReadAsync(buf, cts.Token);
                output.WriteLine($"read#{i}: n={n} bytes=[{System.Text.Encoding.UTF8.GetString(buf, 0, n)}]");
                if (n == 0)
                {
                    output.WriteLine("EOF reached");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                output.WriteLine($"read#{i}: TIMED OUT (no data, no EOF)");
            }
            catch (Exception ex)
            {
                output.WriteLine($"read#{i}: threw {ex}");
            }
        }

        output.WriteLine($"WaitForExit(3s)={p.WaitForExit(TimeSpan.FromSeconds(3))} exitCode={p.ExitCode}");
        output.WriteLine("=== diagnostic end ===");
    }
}
#endif

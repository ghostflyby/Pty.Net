#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: fails with granular launch/IO state in the message.</summary>
public class PtyWindowsDiagnostics
{
    [Fact]
    public async Task DumpLaunchAndIoState()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== diagnostic start ===");
        try
        {
            using var p = PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
            sb.AppendLine($"Start OK: pid={p.Pid} hasExited={p.HasExited}");

            for (var i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var buf = new byte[512];
                try
                {
                    var n = await p.BaseStream.ReadAsync(buf, cts.Token);
                    sb.AppendLine($"read#{i}: n={n} bytes=[{Encoding.UTF8.GetString(buf, 0, n)}]");
                    if (n == 0)
                    {
                        sb.AppendLine("EOF reached");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    sb.AppendLine($"read#{i}: TIMED OUT (no data, no EOF)");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"read#{i}: threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            sb.AppendLine($"WaitForExit(3s)={p.WaitForExit(TimeSpan.FromSeconds(3))} exitCode={p.ExitCode}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Start threw {ex.GetType().Name}: {ex.Message}");
        }

        // WindowsPty is internal; pull the accumulated launch trace via reflection.
        try
        {
            var asm = typeof(PtyProcess).Assembly;
            var type = asm.GetType("Ghostflyby.Pty.WindowsPty")!;
            var log = (string)type.GetMethod("GetDebugLog", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
            sb.AppendLine("--- launch trace ---");
            sb.AppendLine(log);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"GetDebugLog failed: {ex.Message}");
        }

        sb.AppendLine("=== diagnostic end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

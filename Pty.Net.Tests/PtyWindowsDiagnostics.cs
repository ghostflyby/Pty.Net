#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: A (echo) then B (mkdir side-effect, no quoting hazards).</summary>
public class AaaPtyWindowsDiagnostics
{
    [Fact]
    public async Task ABWithSideEffect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AB-with-side-effect start ===");
        var dirB = Path.Combine(Path.GetTempPath(), "pty-b-dir-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]);
            var a = await ReadAllAsync(pA, 3).WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"A: got={a.Contains("AAA-OK")} len={a.Length} exited={pA.HasExited}");
        }
        catch (Exception ex) { sb.AppendLine($"A threw {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            using var pB = PtyProcess.Start("cmd.exe", ["/c", $"mkdir {dirB}"]);
            sb.AppendLine($"B started pid={pB.Pid}");
            var b = await ReadAllAsync(pB, 3).WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"B stdout: got={b.Contains("mkdir")} len={b.Length} bytes=[{b.Replace("\u001b", "<ESC>")}]");
            pB.WaitForExit(TimeSpan.FromSeconds(3));
            sb.AppendLine($"B exited={pB.HasExited} exitCode={pB.ExitCode}");
        }
        catch (Exception ex) { sb.AppendLine($"B threw {ex.GetType().Name}: {ex.Message}"); }

        await Task.Delay(500);
        sb.AppendLine($"B dir exists={Directory.Exists(dirB)}");

        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }

    private static async Task<string> ReadAllAsync(PtyProcess p, int seconds)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buf = new byte[512];
            try
            {
                var n = await p.BaseStream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (Exception) { break; }
        }
        return sb.ToString();
    }
}
#endif

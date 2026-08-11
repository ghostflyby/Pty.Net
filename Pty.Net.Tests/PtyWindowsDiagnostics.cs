#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: A then B with full reader/dispose trace.</summary>
public class AaaPtyWindowsDiagnostics
{
    [Fact]
    public async Task ABWithTrace()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AB-with-trace start ===");

        try
        {
            using (var pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]))
            {
                var a = await ReadAllAsync(pA, 3).WaitAsync(TimeSpan.FromSeconds(6));
                sb.AppendLine($"A: got={a.Contains("AAA-OK")} len={a.Length}");
            }
            sb.AppendLine("A disposed");
        }
        catch (Exception ex) { sb.AppendLine($"A threw {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            using var pB = PtyProcess.Start("cmd.exe", ["/c", "echo BBB-OK"]);
            var b = await ReadAllAsync(pB, 3).WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"B: got={b.Contains("BBB-OK")} len={b.Length}");
            pB.WaitForExit(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { sb.AppendLine($"B threw {ex.GetType().Name}: {ex.Message}"); }

        sb.AppendLine("--- trace ---");
        sb.AppendLine(WindowsPty.GetDebugLog());
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

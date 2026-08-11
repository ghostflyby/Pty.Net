#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: full lifecycle trace of A vs B sessions.</summary>
public class AaaPtyWindowsDiagnostics
{
    [Fact]
    public async Task TraceAB()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== trace-AB start ===");

        PtyProcess? pA = null;
        try
        {
            pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]);
            var a = await ReadAllText(pA).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"A: got={a.Contains("AAA-OK")} len={a.Length} exited={pA.HasExited}");
        }
        catch (Exception ex) { sb.AppendLine($"A threw {ex.GetType().Name}: {ex.Message}"); }

        PtyProcess? pB = null;
        try
        {
            pB = PtyProcess.Start("cmd.exe", ["/c", "echo BBB-OK"]);
            var b = await ReadAllText(pB).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"B: got={b.Contains("BBB-OK")} len={b.Length}");
            sb.AppendLine($"B WaitForExit(2s)={pB.WaitForExit(TimeSpan.FromSeconds(2))} exitCode={pB.ExitCode}");
        }
        catch (Exception ex) { sb.AppendLine($"B threw {ex.GetType().Name}: {ex.Message}"); }

        pA?.Dispose();
        pB?.Dispose();

        sb.AppendLine("--- trace ---");
        sb.AppendLine(WindowsPty.GetDebugLog());
        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }

    private static async Task<string> ReadAllText(PtyProcess p)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buf = new byte[512];
            try
            {
                var n = await p.BaseStream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (Exception) { break; }
        }
        return sb.ToString();
    }
}
#endif

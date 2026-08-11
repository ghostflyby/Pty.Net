#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: does the FIRST ConPTY session break later ones? Disposal as the trigger?</summary>
public class AaaPtyWindowsDiagnostics
{
    private static async Task<string> ReadAll(PtyProcess p, string tag, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
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

    [Fact]
    public async Task NoDisposeBetweenSessions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== no-dispose-between start ===");

        // Session A: start and read, but DO NOT dispose yet.
        var pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]);
        var a = await ReadAll(pA, "A", TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(8));
        sb.AppendLine($"A: got={a.Contains("AAA-OK")} len={a.Length}");

        // Session B: start while A still alive (not disposed).
        var pB = PtyProcess.Start("cmd.exe", ["/c", "echo BBB-OK"]);
        var b = await ReadAll(pB, "B", TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(8));
        sb.AppendLine($"B (A not disposed): got={b.Contains("BBB-OK")} len={b.Length}");

        // Dispose A, then session C.
        pA.Dispose();
        var pC = PtyProcess.Start("cmd.exe", ["/c", "echo CCC-OK"]);
        var c = await ReadAll(pC, "C", TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(8));
        sb.AppendLine($"C (after A disposed): got={c.Contains("CCC-OK")} len={c.Length}");

        pB.Dispose();
        pC.Dispose();

        sb.AppendLine("--- trace ---");
        sb.AppendLine(WindowsPty.GetDebugLog());
        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

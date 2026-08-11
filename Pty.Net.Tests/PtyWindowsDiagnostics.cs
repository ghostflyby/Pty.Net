#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: sequential sessions with full handle/reader trace.</summary>
public class AaaPtyWindowsDiagnostics
{
    private static async Task<string> ReadAll(PtyProcess p, TimeSpan timeout)
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
    public async Task SequentialWithTrace()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== sequential-with-trace start ===");

        for (var i = 0; i < 3; i++)
        {
            try
            {
                using var p = PtyProcess.Start("cmd.exe", ["/c", $"echo TRC-{i}-OK"]);
                var out1 = await ReadAll(p, TimeSpan.FromSeconds(4)).WaitAsync(TimeSpan.FromSeconds(7));
                sb.AppendLine($"seq#{i}: got={out1.Contains($"TRC-{i}-OK")} len={out1.Length}");
                p.WaitForExit(TimeSpan.FromSeconds(3));
                if (i == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); sb.AppendLine("forced GC after seq#0"); }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"seq#{i} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine("--- trace ---");
        sb.AppendLine(WindowsPty.GetDebugLog());
        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

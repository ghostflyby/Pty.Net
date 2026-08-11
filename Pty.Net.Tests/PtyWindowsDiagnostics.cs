#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: sequential vs parallel ConPTY sessions to isolate multi-session behavior.</summary>
public class AaaPtyWindowsDiagnostics
{
    /// <summary>Reads everything available up to a timeout; distinguishes frames from command output.</summary>
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
                if (n == 0) break; // EOF
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (Exception) { break; } // timeout/cancel: stop collecting
        }
        return sb.ToString();
    }

    [Fact]
    public async Task SequentialVsParallel()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== sequential-vs-parallel start ===");

        // Sequential: 5 sessions one after another, each fully drained.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var p = PtyProcess.Start("cmd.exe", ["/c", $"echo SEQ-{i}-OK"]);
                var out1 = await ReadAll(p, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(8));
                sb.AppendLine($"seq#{i}: hasSeq={out1.Contains($"SEQ-{i}-OK")} len={out1.Length} sample=[{out1.Replace("\u001b", "<ESC>")}]");
                p.WaitForExit(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"seq#{i} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: single vs concurrent ConPTY sessions, each reading its own output.</summary>
public class AaaPtyWindowsDiagnostics
{
    [Fact]
    public async Task ConcurrentVsSingle()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== concurrent-vs-single start ===");

        // Single session, StreamReader path (mirrors the suite).
        try
        {
            using var p = PtyProcess.Start("cmd.exe", ["/c", "echo SINGLE-OK"]);
            var sr = new StreamReader(p.BaseStream, Encoding.UTF8);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buf = new char[512];
            var n = await sr.ReadAsync(buf, cts.Token).WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"single StreamReader: n={n} text=[{new string(buf, 0, n)}]");
            p.WaitForExit(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"single threw {ex.GetType().Name}: {ex.Message}");
        }

        // 8 concurrent sessions, BaseStream reads.
        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var p = PtyProcess.Start("cmd.exe", ["/c", $"echo CONC-{idx}-OK"]);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var buf = new byte[256];
                    var n = await p.BaseStream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(6));
                    sb.AppendLine($"conc#{idx}: n={n} bytes=[{Encoding.UTF8.GetString(buf, 0, n)}]");
                    p.WaitForExit(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"conc#{idx} threw {ex.GetType().Name}: {ex.Message}");
                }
            }));
        }
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            sb.AppendLine("concurrent batch TIMED OUT after 30s");
        }

        sb.AppendLine("=== concurrent-vs-single end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

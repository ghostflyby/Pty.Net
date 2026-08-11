#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: does session B's child actually execute? File side-effect + hard timeouts.</summary>
public class AaaPtyWindowsDiagnostics
{
    [Fact]
    public async Task DoesBChildExecute()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== does-B-execute start ===");
        var markerB = Path.Combine(Path.GetTempPath(), "pty-b-marker-" + Guid.NewGuid().ToString("N") + ".txt");

        // A: confirm the baseline works.
        try
        {
            using var pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]);
            var a = await ReadAllAsync(pA, 3).WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"A: got={a.Contains("AAA-OK")} len={a.Length}");
        }
        catch (Exception ex) { sb.AppendLine($"A threw {ex.GetType().Name}: {ex.Message}"); }

        // B: child writes a file as a side effect (works even if stdout is disconnected).
        try
        {
            using var pB = PtyProcess.Start("cmd.exe", ["/c", $"echo BBB-OK > \"{markerB}\""]);
            sb.AppendLine($"B started pid={pB.Pid}");
            var b = await ReadAllAsync(pB, 3).WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"B stdout: got={b.Contains("BBB-OK")} len={b.Length}");
            pB.WaitForExit(TimeSpan.FromSeconds(2));
            sb.AppendLine($"B exited={pB.HasExited} exitCode={pB.ExitCode}");
        }
        catch (Exception ex) { sb.AppendLine($"B threw {ex.GetType().Name}: {ex.Message}"); }

        await Task.Delay(500);
        sb.AppendLine($"B marker file exists={File.Exists(markerB)} path={markerB}");
        if (File.Exists(markerB))
            sb.AppendLine($"B marker content=[{File.ReadAllText(markerB)}]");

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

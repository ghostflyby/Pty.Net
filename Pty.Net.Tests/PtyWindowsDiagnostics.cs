#if WINDOWS
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: is disposal of session A what breaks session B?</summary>
public class AaaPtyWindowsDiagnostics
{
    [Fact]
    public async Task DoesDisposalOfAbreakB()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== does-disposal-of-A-break-B start ===");

        // A: start + read (leave ALIVE, do not dispose).
        PtyProcess? pA = null;
        try
        {
            pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]);
            var a = await ReadAllText(pA).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"A: got={a.Contains("AAA-OK")} len={a.Length}");
        }
        catch (Exception ex) { sb.AppendLine($"A threw {ex.GetType().Name}: {ex.Message}"); }

        // B: start while A is alive (not disposed).
        PtyProcess? pB = null;
        try
        {
            pB = PtyProcess.Start("cmd.exe", ["/c", "echo BBB-OK"]);
            var b = await ReadAllText(pB).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"B (A alive): got={b.Contains("BBB-OK")} len={b.Length}");
        }
        catch (Exception ex) { sb.AppendLine($"B threw {ex.GetType().Name}: {ex.Message}"); }

        // Dispose A, then C.
        PtyProcess? pC = null;
        try
        {
            pA?.Dispose();
            pA = null;
            pC = PtyProcess.Start("cmd.exe", ["/c", "echo CCC-OK"]);
            var c = await ReadAllText(pC).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"C (after A disposed): got={c.Contains("CCC-OK")} len={c.Length}");
        }
        catch (Exception ex) { sb.AppendLine($"C threw {ex.GetType().Name}: {ex.Message}"); }

        pB?.Dispose();
        pC?.Dispose();

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

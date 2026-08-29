using System.Diagnostics;

namespace Ghostflyby.Pty;

/// <summary>
/// Opt-in diagnostics for process lifetime and reaper timing. Set
/// <c>PTY_REAPER_DIAG=1</c> before starting the process to enable it.
/// </summary>
internal static class PtyDiagnostics
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("PTY_REAPER_DIAG"), "1", StringComparison.Ordinal);

    internal static void Log(string message)
    {
        if (!Enabled)
            return;

        try
        {
            Console.Error.WriteLine($"[Pty.Net diag {DateTime.UtcNow:O} tid={Environment.CurrentManagedThreadId} " +
                                    $"tick={Stopwatch.GetTimestamp()}] {message}");
        }
        catch
        {
            // Diagnostics must never change process lifetime behavior.
        }
    }
}

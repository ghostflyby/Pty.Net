using System.Diagnostics.CodeAnalysis;

namespace Ghostflyby.Pty;

/// <summary>
/// Process-wide reaper: the single owner of the exit wait for every <see cref="PtyProcess"/>.
/// One background thread scans the registered processes every 10 ms — waitpid(WNOHANG) on
/// Unix, WaitForSingleObject(0) on Windows — delegating each non-blocking attempt to the
/// process itself (<see cref="PtyProcess.TryReap"/>); once a child is collected it sets
/// <see cref="PtyProcess.ExitCode"/>, raises <see cref="PtyProcess.Exited"/> and unregisters.
///
/// Single-owner matters: if WaitForExit/Dispose each waited directly, two callers would
/// race for the same child — on Unix the first reap wins and the loser would see ECHILD
/// and overwrite the ExitCode with -1 (on Windows a single owned process handle makes
/// that impossible, but funneling the wait through one thread keeps the result
/// deterministic all the same). Other paths only observe ExitCode.
///
/// Signal safety (Unix): no SIGCHLD handler is installed (.NET's runtime installs its own
/// SIGCHLD on Unix; overlaying it is risky), so reaping is polled; a pidfd-based
/// event-driven reap is a future optimization on Linux. The reaper thread only polls
/// while at least one process is registered — with none, it idles on a monitor wait
/// and is woken by the first registration. The same thread also makes the dispose-time
/// "wait up to 2 s" window non-fatal: even if it elapses while the child is still
/// alive, this reaper keeps watching, so a child can never be left as a zombie.
///
/// The per-process attempt is a partial method implemented in PtyProcess.Start.Windows.cs
/// / PtyProcess.Start.Unix.cs, so this file carries no platform conditionals.
/// </summary>
internal static class PtyReaper
{
    private const int PollIntervalMs = 10;

    private static readonly Lazy<ReaperThread> Reaper =
        new(() => new ReaperThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Registers a <see cref="PtyProcess"/> for reaping; removed once it is reaped.</summary>
    public static void Watch(PtyProcess process) => Reaper.Value.WatchProcess(process);

    private sealed class ReaperThread
    {
        private readonly object sync = new();
        private readonly List<PtyProcess> watched = [];
        // Reusable snapshot array: copying the watch set into a fresh List every 10 ms
        // tick would churn one allocation for the whole lifetime of any watched process.
        private PtyProcess[] snapshot = [];

        public ReaperThread()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Pty.Net-reaper" };
            thread.Start();
        }

        public void WatchProcess(PtyProcess process)
        {
            lock (sync)
            {
                watched.Add(process);
                Monitor.Pulse(sync); // wake the loop if it was idle-waiting for work
            }
        }

        [DoesNotReturn]
        private void Loop()
        {
            while (true)
            {
                int snapshotCount;
                lock (sync)
                {
                    if (watched.Count == 0)
                    {
                        // Nothing to reap: sleep until the first Watch, so the process-wide
                        // reaper thread is fully idle (no polling) between sessions.
                        Monitor.Wait(sync);
                        continue;
                    }

                    // Grow the reusable snapshot on demand (never shrink), like the
                    // engine's poll set. CopyTo writes watched.Count elements at index 0.
                    if (snapshot.Length < watched.Count)
                        Array.Resize(ref snapshot, watched.Count);
                    watched.CopyTo(snapshot);
                    snapshotCount = watched.Count;
                }

                for (var i = 0; i < snapshotCount; i++)
                {
                    var p = snapshot[i];
                    if (!p.TryReap(out var code)) continue;
                    p.OnReaped(code);
                    lock (sync)
                    {
                        watched.Remove(p);
                    }
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
    }
}

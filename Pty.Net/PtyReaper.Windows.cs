using System.Diagnostics.CodeAnalysis;

namespace Ghostflyby.Pty;

/// <summary>
/// Windows half of <see cref="PtyReaper"/>: the process handle is waited on with a
/// bounded poll (WaitForSingleObject(handle, 0)) on a dedicated thread, exactly like the
/// pre-split implementation — the BCL offers no single synchronous "wait on a handle
/// without a thread" that the library could call into here, and the Windows test
/// surface is covered by that bounded poll. Windows-only: compiled only by the Windows
/// target (see csproj).
/// </summary>
internal static partial class PtyReaper
{
    private const int PollIntervalMs = 10;

    private static readonly Lazy<ReaperThread> Reaper =
        new(() => new ReaperThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static partial void WatchPlatform(PtyProcess process) => Reaper.Value.WatchProcess(process);

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
                    if (!p.TryReap(out var status)) continue;
                    p.OnReaped(status);
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

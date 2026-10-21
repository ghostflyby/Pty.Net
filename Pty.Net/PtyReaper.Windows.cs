using System.Diagnostics.CodeAnalysis;

namespace Ghostflyby.Pty;

/// <summary>
/// Windows half of <see cref="PtyReaper"/>: the process handle is waited on with a
/// bounded poll (WaitForSingleObject(handle, 0)) on a dedicated thread, exactly like the
/// pre-split implementation — the BCL offers no single synchronous "wait on a handle
/// without a thread" that the library could call into here, and the Windows test
/// surface is covered by that bounded poll. Compiled on every OS (single source set,
/// see csproj); PtyReaper dispatches to it at runtime on Windows.
/// </summary>
internal static partial class PtyReaper
{
    private const int PollIntervalMs = 10;

    private static readonly Lazy<WindowsReaperThread> WindowsReaper =
        new(() => new WindowsReaperThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static void WatchPlatformWindows(PtyProcess process) => WindowsReaper.Value.WatchProcess(process);

    private sealed class WindowsReaperThread
    {
        private readonly object _sync = new();
        private readonly List<PtyProcess> _watched = [];
        // Reusable snapshot array: copying the watch set into a fresh List every 10 ms
        // tick would churn one allocation for the whole lifetime of any watched process.
        private PtyProcess[] _snapshot = [];

        public WindowsReaperThread()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Pty.Net-reaper" };
            thread.Start();
        }

        public void WatchProcess(PtyProcess process)
        {
            lock (_sync)
            {
                _watched.Add(process);
                Monitor.Pulse(_sync); // wake the loop if it was idle-waiting for work
            }
        }

        [DoesNotReturn]
        private void Loop()
        {
            while (true)
            {
                int snapshotCount;
                lock (_sync)
                {
                    if (_watched.Count == 0)
                    {
                        // Nothing to reap: sleep until the first Watch, so the process-wide
                        // reaper thread is fully idle (no polling) between sessions.
                        Monitor.Wait(_sync);
                        continue;
                    }

                    // Grow the reusable snapshot on demand (never shrink), like the
                    // engine's poll set. CopyTo writes watched.Count elements at index 0.
                    if (_snapshot.Length < _watched.Count)
                        Array.Resize(ref _snapshot, _watched.Count);
                    _watched.CopyTo(_snapshot);
                    snapshotCount = _watched.Count;
                }

                for (var i = 0; i < snapshotCount; i++)
                {
                    var p = _snapshot[i];
                    if (!p.TryReap(out var status)) continue;
                    p.OnReaped(status);
                    lock (_sync)
                    {
                        _watched.Remove(p);
                    }
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
    }
}

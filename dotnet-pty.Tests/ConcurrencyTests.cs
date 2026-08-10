using System.Collections.Concurrent;
using dotnet_pty;

namespace dotnet_pty.Tests;

public class ConcurrencyTests
{
    [Fact]
    public void ConcurrentSessions_AllStartAndEcho()
    {
        // Spawn and use many sessions in parallel. This validates the posix_spawn
        // approach: fork() in a multi-threaded process can deadlock the child on
        // inherited malloc locks, posix_spawn does not.
        const int total = 24;
        const int concurrency = 8;

        var work = Enumerable.Range(0, total).ToList();
        var failures = new ConcurrentQueue<string>();

        var tasks = Enumerable.Range(0, concurrency).Select(t => Task.Run(() =>
        {
            while (true)
            {
                int i;
                lock (work)
                {
                    if (work.Count == 0)
                        break;
                    i = work[0];
                    work.RemoveAt(0);
                }

                try
                {
                    using var bash = PtyProcess.StartBash();
                    bash.ReadUntil("$", TimeSpan.FromSeconds(8));
                    bash.Write($"echo marker-{i}; echo __DONE__\n");
                    var output = bash.ReadUntil("__DONE__", TimeSpan.FromSeconds(8));
                    if (!output.Contains($"marker-{i}"))
                        failures.Enqueue($"session {i}: marker missing in output");
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"session {i}: {ex.Message}");
                }
            }
        })).ToArray();

        Task.WaitAll(tasks);

        Assert.True(failures.IsEmpty, string.Join(" | ", failures));
    }
}

using System.Collections.Concurrent;

namespace Ghostflyby.Pty.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSessions_AllStartAndEcho()
    {
        // Spawn and use many sessions in parallel. This validates the posix_spawn
        // approach: fork() in a multi-threaded process can deadlock the child on
        // inherited malloc locks, posix_spawn does not.
        const int total = 24;
        const int concurrency = 8;

        var work = Enumerable.Range(0, total).ToList();
        var failures = new ConcurrentQueue<string>();

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
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
                    using var bash = TestBash.Start();
                    TestBash.ReadUntil(bash.Output, "$", TimeSpan.FromSeconds(8));
                    bash.Input.WriteLine($"echo marker-{i}; echo __DONE__\n");
                    var output = TestBash.ReadUntil(bash.Output, "__DONE__", TimeSpan.FromSeconds(8));
                    if (!output.Contains($"marker-{i}"))
                        failures.Enqueue($"session {i}: marker missing in output");
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"session {i}: {ex.Message}");
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(failures.IsEmpty, string.Join(" | ", failures));
    }
}

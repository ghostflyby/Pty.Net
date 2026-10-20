namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Serialized xunit collection for <see cref="ThreadPoolAccountingTests"/>, whose tests
/// assert on thread-pool worker availability. They measure a process-global resource
/// while the rest of the parallel suite freely borrows the same pool (spawning
/// processes, doing I/O), which perturbs the available-worker count and made the
/// assertions timing-dependent. Running them in a collection with
/// <c>DisableParallelization</c> gives them exclusive access to the pool, so the
/// measured counts are perturbed only by each test's own spawns — which the
/// re-baselined sampling inside each test then settles. The pair also runs sequentially
/// relative to each other.
/// </summary>
[CollectionDefinition("thread-pool accounting", DisableParallelization = true)]
public sealed class ThreadPoolAccountingCollection
{
}

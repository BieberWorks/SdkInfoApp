namespace SdkInfoApp.Scanner.Model;

/// <summary>
/// Pure static query functions over an <see cref="SdkSnapshot"/>.
/// All methods are side-effect-free and safe to call from any host (Web, MCP, tests).
/// </summary>
public static class SdkQuery
{
    /// <summary>
    /// Returns the set of module IDs that directly provide at least one of the requested capabilities.
    /// </summary>
    public static HashSet<string> DirectModulesForCapabilities(
        SdkSnapshot snapshot,
        IEnumerable<string> capabilityIds)
    {
        var ids = capabilityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Capabilities
            .Where(c => ids.Contains(c.Id))
            .SelectMany(c => c.ProvidedBy)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BFS upstream closure following ONLY <c>impl</c> edges
    /// (edge.From == current &amp;&amp; edge.Kind == "impl" → edge.To).
    /// Contracts edges are intentionally excluded.
    /// Returns all module IDs the consumer must install, including the seeds themselves.
    /// </summary>
    public static HashSet<string> TransitiveUpstreamClosure(
        SdkSnapshot snapshot,
        IEnumerable<string> seeds)
    {
        var visited = seeds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(visited);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in snapshot.Edges.Where(e =>
                string.Equals(e.From, current, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Kind, "impl", StringComparison.OrdinalIgnoreCase)))
            {
                if (visited.Add(edge.To))
                    queue.Enqueue(edge.To);
            }
        }
        return visited;
    }

    /// <summary>
    /// BFS reverse closure over ALL edge kinds.
    /// For a given <paramref name="moduleId"/>, returns all module IDs that depend on it
    /// (directly or transitively) — i.e. what would break on a breaking change.
    /// </summary>
    public static HashSet<string> ReverseImpact(SdkSnapshot snapshot, string moduleId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { moduleId };
        var queue = new Queue<string>();
        queue.Enqueue(moduleId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in snapshot.Edges.Where(e =>
                string.Equals(e.To, current, StringComparison.OrdinalIgnoreCase)))
            {
                if (visited.Add(edge.From))
                    queue.Enqueue(edge.From);
            }
        }
        return visited;
    }
}

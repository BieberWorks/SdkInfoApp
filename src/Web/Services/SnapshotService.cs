using SdkInfoApp.Scanner.Model;
using System.Net.Http.Json;
using System.Text.Json;

namespace SdkInfoApp.Web.Services;

public sealed class SnapshotService(HttpClient http)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private SdkSnapshot? _snapshot;
    private bool _loaded;
    private string? _error;
    private Task? _loadTask;

    public SdkSnapshot? Snapshot => _snapshot;
    public bool IsLoaded => _loaded;
    public string? Error => _error;
    public bool IsLocalMode => _snapshot?.SnapshotMode == "local";

    public event Action? OnChange;

    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        try
        {
            _snapshot = await http.GetFromJsonAsync<SdkSnapshot>("sdk-snapshot.json", Options);
            _loaded = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _loaded = true;
        }

        OnChange?.Invoke();
    }

    public IReadOnlyList<ModuleInfo> GetModulesSortedByTier()
        => _snapshot?.Modules
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id)
            .ToList() ?? [];

    public ModuleInfo? GetModule(string id)
        => _snapshot?.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<EdgeInfo> GetEdgesFrom(string moduleId)
        => _snapshot?.Edges.Where(e => e.From == moduleId).ToList() ?? [];

    public IReadOnlyList<EdgeInfo> GetEdgesTo(string moduleId)
        => _snapshot?.Edges.Where(e => e.To == moduleId).ToList() ?? [];

    public IReadOnlyList<PackageNode> GetPackageNodesSortedByTier()
        => _snapshot?.PackageNodes
            .OrderBy(p => p.Tier)
            .ThenBy(p => p.Id)
            .ToList() ?? [];

    public PackageNode? GetPackageNode(string id)
        => _snapshot?.PackageNodes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PackageEdge> GetPackageEdgesFrom(string packageId)
        => _snapshot?.PackageEdges.Where(e => e.From == packageId).ToList() ?? [];

    public IReadOnlyList<PackageEdge> GetPackageEdgesTo(string packageId)
        => _snapshot?.PackageEdges.Where(e => e.To == packageId).ToList() ?? [];

    /// <summary>
    /// Returns the direct provider modules for the given capability IDs.
    /// </summary>
    public HashSet<string> DirectModulesForCapabilities(IEnumerable<string> capabilityIds)
    {
        if (_snapshot is null) return [];
        var ids = capabilityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _snapshot.Capabilities
            .Where(c => ids.Contains(c.Id))
            .SelectMany(c => c.ProvidedBy)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BFS over snapshot edges (From depends-on To) to compute the full upstream transitive closure.
    /// Returns all module IDs the consumer must install, including seeds.
    /// </summary>
    public HashSet<string> TransitiveUpstreamClosure(IEnumerable<string> seeds)
    {
        if (_snapshot is null) return [];
        var visited = seeds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(visited);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _snapshot.Edges.Where(e =>
                string.Equals(e.From, current, StringComparison.OrdinalIgnoreCase)))
            {
                if (visited.Add(edge.To))
                    queue.Enqueue(edge.To);
            }
        }
        return visited;
    }
}

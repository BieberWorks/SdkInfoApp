using Microsoft.JSInterop;
using SdkInfoApp.Scanner.Model;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdkInfoApp.Web.Services;

/// <summary>Describes one entry in <c>snapshots/index.json</c>.</summary>
public sealed record BranchInfo
{
    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "";

    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; init; }
}

/// <summary>Schema for <c>snapshots/index.json</c>.</summary>
file sealed record SnapshotIndex
{
    [JsonPropertyName("branches")]
    public IReadOnlyList<BranchInfo> Branches { get; init; } = [];
}

public sealed class SnapshotService(HttpClient http, IJSRuntime js)
{
    private const string LocalStorageKey = "sdkinfo.branch";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private SdkSnapshot? _snapshot;
    private bool _loaded;
    private string? _error;
    private Task? _loadTask;
    private IReadOnlyList<BranchInfo> _branches = [];
    private string _selectedBranch = "local";

    public SdkSnapshot? Snapshot => _snapshot;
    public bool IsLoaded => _loaded;
    public string? Error => _error;
    public bool IsLocalMode => _snapshot?.Branch == "local";
    public IReadOnlyList<BranchInfo> Branches => _branches;
    public string SelectedBranch => _selectedBranch;

    public event Action? OnChange;

    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        try
        {
            // Try loading index first
            SnapshotIndex? index = null;
            try
            {
                index = await http.GetFromJsonAsync<SnapshotIndex>("snapshots/index.json", Options);
            }
            catch
            {
                // No index — fall back to classic single-file mode
            }

            if (index is not null && index.Branches.Count > 0)
            {
                _branches = index.Branches;

                // Determine selected branch from localStorage; default to first entry (main)
                var stored = await js.InvokeAsync<string?>("localStorage.getItem", LocalStorageKey);
                var desired = stored is not null && _branches.Any(b => b.Branch == stored)
                    ? stored
                    : _branches[0].Branch;

                _selectedBranch = desired;
                var entry = _branches.First(b => b.Branch == desired);
                _snapshot = await http.GetFromJsonAsync<SdkSnapshot>(entry.File, Options);
            }
            else
            {
                // Classic fallback: sdk-snapshot.json in root
                _snapshot = await http.GetFromJsonAsync<SdkSnapshot>("sdk-snapshot.json", Options);
                var branchName = _snapshot?.Branch ?? "local";
                _branches = [new BranchInfo { Branch = branchName, File = "sdk-snapshot.json" }];
                _selectedBranch = branchName;
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _loaded = true;
        }

        OnChange?.Invoke();
    }

    /// <summary>
    /// Persists <paramref name="branch"/> to localStorage and reloads the page so all
    /// components reinitialize with the new snapshot.
    /// </summary>
    public async Task SelectBranchAsync(string branch)
    {
        await js.InvokeVoidAsync("localStorage.setItem", LocalStorageKey, branch);
        await js.InvokeVoidAsync("location.reload");
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
    /// Delegates to <see cref="SdkQuery.DirectModulesForCapabilities"/>.
    /// </summary>
    public HashSet<string> DirectModulesForCapabilities(IEnumerable<string> capabilityIds)
        => _snapshot is null ? [] : SdkQuery.DirectModulesForCapabilities(_snapshot, capabilityIds);

    /// <summary>
    /// BFS upstream closure over impl-only edges.
    /// Delegates to <see cref="SdkQuery.TransitiveUpstreamClosure"/>.
    /// </summary>
    public HashSet<string> TransitiveUpstreamClosure(IEnumerable<string> seeds)
        => _snapshot is null ? [] : SdkQuery.TransitiveUpstreamClosure(_snapshot, seeds);
}

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
}

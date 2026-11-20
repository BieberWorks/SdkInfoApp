namespace SdkInfoApp.Scanner.Model;

/// <summary>Backward-compatibility helpers for older snapshot schema versions.</summary>
public static class SnapshotCompat
{
    /// <summary>
    /// v1 snapshots carry no <c>kind</c>; derive it from tags and repo prefix (same chain as the scanner).
    /// Snapshots of schema 2 or later are returned unchanged.
    /// </summary>
    public static SdkSnapshot? ApplyV1KindFallback(SdkSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.SchemaVersion != "1") return snapshot;
        var modules = snapshot.Modules
            .Select(m => m with { Kind = ModuleKindResolver.ResolveFallback(m.Tags, m.Id) })
            .ToList();
        return snapshot with { Modules = modules };
    }
}

using System.Text.Json.Serialization;

namespace SdkInfoApp.Scanner.Model;

/// <summary>Root document — sdk-snapshot.json.</summary>
public sealed record SdkSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1";

    [JsonPropertyName("snapshotMode")]
    public string SnapshotMode { get; init; } = "local";

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("modules")]
    public IReadOnlyList<ModuleInfo> Modules { get; init; } = [];

    [JsonPropertyName("edges")]
    public IReadOnlyList<EdgeInfo> Edges { get; init; } = [];

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<CapabilityInfo> Capabilities { get; init; } = [];

    [JsonPropertyName("capabilityGroups")]
    public IReadOnlyList<CapabilityGroup> CapabilityGroups { get; init; } = [];

    [JsonPropertyName("packageNodes")]
    public IReadOnlyList<PackageNode> PackageNodes { get; init; } = [];

    [JsonPropertyName("packageEdges")]
    public IReadOnlyList<PackageEdge> PackageEdges { get; init; } = [];
}

public sealed record ModuleInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("repoName")]
    public string RepoName { get; init; } = "";

    [JsonPropertyName("tier")]
    public int Tier { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyList<string> Packages { get; init; } = [];

    [JsonPropertyName("latestGhVersion")]
    public string? LatestGhVersion { get; init; }

    [JsonPropertyName("localDevVersion")]
    public string? LocalDevVersion { get; init; }

    [JsonPropertyName("localDevPresent")]
    public bool LocalDevPresent { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("ciStatus")]
    public string? CiStatus { get; init; }

    [JsonPropertyName("lastCiRun")]
    public DateTimeOffset? LastCiRun { get; init; }

    [JsonPropertyName("openPrCount")]
    public int OpenPrCount { get; init; }

    [JsonPropertyName("ghReleasesUrl")]
    public string? GhReleasesUrl { get; init; }

    [JsonPropertyName("ghDocsUrl")]
    public string? GhDocsUrl { get; init; }

    [JsonPropertyName("manifestSource")]
    public string ManifestSource { get; init; } = "fallback";
}

public sealed record EdgeInfo
{
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    [JsonPropertyName("to")]
    public string To { get; init; } = "";

    [JsonPropertyName("packageRef")]
    public string PackageRef { get; init; } = "";

    [JsonPropertyName("versionRange")]
    public string? VersionRange { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "impl";
}

public sealed class CapabilityInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Mutable for aggregation in SnapshotBuilder; serialised as JSON array.</summary>
    [JsonPropertyName("providedBy")]
    public List<string> ProvidedBy { get; init; } = [];
}

public sealed record PackageNode
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("module")]
    public string Module { get; init; } = "";

    [JsonPropertyName("tier")]
    public int Tier { get; init; }
}

public sealed record PackageEdge
{
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    [JsonPropertyName("to")]
    public string To { get; init; } = "";

    [JsonPropertyName("versionRange")]
    public string? VersionRange { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "impl";
}

public sealed record CapabilityGroup
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("members")]
    public IReadOnlyList<string> Members { get; init; } = [];
}

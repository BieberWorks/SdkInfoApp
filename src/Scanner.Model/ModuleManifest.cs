using System.Text.Json.Serialization;

namespace SdkInfoApp.Scanner.Model;

/// <summary>Parsed content of module.manifest.json found in each SDK repo root.</summary>
public sealed record ModuleManifest
{
    [JsonPropertyName("moduleId")]
    public string ModuleId { get; init; } = "";

    [JsonPropertyName("tier")]
    public int? Tier { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<ManifestCapability> Capabilities { get; init; } = [];

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("docsUrl")]
    public string? DocsUrl { get; init; }
}

public sealed record ManifestCapability
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

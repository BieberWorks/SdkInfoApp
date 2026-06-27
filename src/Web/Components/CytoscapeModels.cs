using System.Text.Json.Serialization;

namespace SdkInfoApp.Web.Components;

public sealed record CyNode
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("tier")] public int Tier { get; init; }
    [JsonPropertyName("ciStatus")] public string? CiStatus { get; init; }
    [JsonPropertyName("hasLocalDev")] public bool HasLocalDev { get; init; }
}

public sealed record CyEdge
{
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("target")] public string Target { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "impl";
}

using System.Text.Json;

namespace SdkInfoApp.Mcp;

internal static class SnapshotJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

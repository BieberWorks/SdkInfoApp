using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SdkInfoApp.Mcp;
using SdkInfoApp.Scanner.Model;
using System.Text.Json;

// Resolve the snapshot path from --snapshot <path> arg or SDKINFO_SNAPSHOT env,
// defaulting to src/Web/wwwroot/sdk-snapshot.json relative to the executable.
string snapshotPath = ResolveSnapshotPath(args);

if (!File.Exists(snapshotPath))
{
    await Console.Error.WriteLineAsync(
        $"[SdkInfoApp.Mcp] Snapshot file not found: {snapshotPath}. " +
        "Pass --snapshot <path> or set SDKINFO_SNAPSHOT.");
    return 1;
}

SdkSnapshot snapshot;
try
{
    await using var fs = File.OpenRead(snapshotPath);
    snapshot = await JsonSerializer.DeserializeAsync<SdkSnapshot>(fs, SnapshotJson.Options)
               ?? throw new InvalidDataException("Deserialized snapshot was null.");
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"[SdkInfoApp.Mcp] Failed to load snapshot: {ex.Message}");
    return 1;
}

// Use CreateEmptyApplicationBuilder so the host writes NOTHING extra to stdout
// (stdout is the JSON-RPC stdio channel).
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
});

// Route ALL log output to stderr so stdout stays clean for JSON-RPC framing.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(snapshot);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(SdkTools).Assembly);

await builder.Build().RunAsync();
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

static string ResolveSnapshotPath(string[] args)
{
    // --snapshot <path>
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--snapshot", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(args[i + 1]);
    }

    // env
    var env = Environment.GetEnvironmentVariable("SDKINFO_SNAPSHOT");
    if (!string.IsNullOrWhiteSpace(env))
        return Path.GetFullPath(env);

    // Default: resolve relative to exe location up to src/Web/wwwroot/sdk-snapshot.json
    var exeDir = AppContext.BaseDirectory;
    // Walk up until we find the src/ folder or hit root
    var dir = new DirectoryInfo(exeDir);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "src", "Web", "wwwroot", "sdk-snapshot.json");
        if (File.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }

    // Fallback relative
    return Path.GetFullPath(
        Path.Combine(exeDir, "..", "..", "..", "..", "Web", "wwwroot", "sdk-snapshot.json"));
}

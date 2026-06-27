using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Text.Json;

namespace SdkInfoApp.Scanner;

internal sealed partial class ScannerApp(ILoggerFactory loggerFactory)
{
    private readonly ILogger<ScannerApp> _logger = loggerFactory.CreateLogger<ScannerApp>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task RunAsync(
        string mode,
        string workspace,
        string org,
        string output,
        CancellationToken ct)
    {
        LogRunning(mode);

        SdkSnapshot snapshot = mode switch
        {
            "local" => await RunLocalAsync(workspace, ct),
            "release" => await RunReleaseAsync(org, ct),
            _ => throw new ArgumentException($"Unknown mode: {mode}. Use 'local' or 'release'.")
        };

        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = File.Create(output);
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct);

        LogSnapshotWritten(output, snapshot.Modules.Count, snapshot.Edges.Count);
    }

    private async Task<SdkSnapshot> RunLocalAsync(string workspace, CancellationToken ct)
    {
        var localFeed = Path.Combine(workspace, "local-nuget-feed");
        var scanner = new LocalWorkspaceScanner(loggerFactory.CreateLogger<LocalWorkspaceScanner>());
        var ghFetcher = new GitHubDataFetcher(loggerFactory.CreateLogger<GitHubDataFetcher>());
        var builder = new SnapshotBuilder(loggerFactory.CreateLogger<SnapshotBuilder>());

        var repoInfos = scanner.ScanWorkspace(workspace);
        LogFoundRepos(repoInfos.Count);

        var localDevVersions = LocalFeedScanner.ScanFeed(localFeed);
        LogFoundDevVersions(localDevVersions.Count);

        var ghDataMap = await ghFetcher.FetchAllAsync("BieberWorks", repoInfos.Keys, ct);

        return builder.Build(repoInfos, localDevVersions, ghDataMap, "local");
    }

    private async Task<SdkSnapshot> RunReleaseAsync(string org, CancellationToken ct)
    {
        var scanner = new ReleaseScanner(loggerFactory.CreateLogger<ReleaseScanner>());
        var ghFetcher = new GitHubDataFetcher(loggerFactory.CreateLogger<GitHubDataFetcher>());
        var builder = new SnapshotBuilder(loggerFactory.CreateLogger<SnapshotBuilder>());

        var repoInfos = await scanner.ScanOrgAsync(org, ct);
        LogFoundRepos(repoInfos.Count);

        var ghDataMap = await ghFetcher.FetchAllAsync(org, repoInfos.Keys, ct);

        // Release mode: no local feed; pass empty dict
        var snapshot = builder.Build(repoInfos, [], ghDataMap, "release");
        return SnapshotBuilder.SanitizeForRelease(snapshot);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanner running in mode={Mode}")]
    private partial void LogRunning(string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} SDK repos")]
    private partial void LogFoundRepos(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} local -dev package versions")]
    private partial void LogFoundDevVersions(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot written to {Output}: {ModuleCount} modules, {EdgeCount} edges")]
    private partial void LogSnapshotWritten(string output, int moduleCount, int edgeCount);

}

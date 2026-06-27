using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Text.Json;

namespace SdkInfoApp.Scanner;

internal sealed partial class ScannerApp(ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ScannerApp> _logger = loggerFactory.CreateLogger<ScannerApp>();

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
            "release" => await RunReleaseStubAsync(org, ct),
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

    private Task<SdkSnapshot> RunReleaseStubAsync(string org, CancellationToken ct)
    {
        // TODO Phase 3: Implement ReleaseScanner using gh API
        // - Enumerate repos via gh api /orgs/{org}/repos
        // - Fetch csproj content via gh api /repos/{org}/{repo}/git/trees/main?recursive=1
        // - Fetch module.manifest.json via gh api /repos/{org}/{repo}/contents/module.manifest.json
        // - Build snapshot with SanitizeForRelease() (no localDevVersion, no local paths)
        LogReleaseModeStub();
        var snapshot = new SdkSnapshot
        {
            SnapshotMode = "release",
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(snapshot);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanner running in mode={Mode}")]
    private partial void LogRunning(string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} SDK repos")]
    private partial void LogFoundRepos(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} local -dev package versions")]
    private partial void LogFoundDevVersions(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot written to {Output}: {ModuleCount} modules, {EdgeCount} edges")]
    private partial void LogSnapshotWritten(string output, int moduleCount, int edgeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Release mode is a stub (Phase 3). Writing empty snapshot.")]
    private partial void LogReleaseModeStub();
}

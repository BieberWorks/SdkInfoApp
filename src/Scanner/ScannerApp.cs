using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Text;
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
        string? branch,
        CancellationToken ct)
    {
        LogRunning(mode);

        SdkSnapshot snapshot = mode switch
        {
            "local" => await RunLocalAsync(workspace, ct),
            "release" => await RunReleaseAsync(org, branch, ct),
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
        var sdkRoot = Path.Combine(workspace, "Sdk");
        var localFeed = Path.Combine(sdkRoot, "local-nuget-feed");
        var scanner = new LocalWorkspaceScanner(loggerFactory.CreateLogger<LocalWorkspaceScanner>());
        var ghFetcher = new GitHubDataFetcher(loggerFactory.CreateLogger<GitHubDataFetcher>());
        var builder = new SnapshotBuilder(loggerFactory.CreateLogger<SnapshotBuilder>());

        var repoInfos = scanner.ScanWorkspace(sdkRoot);
        LogFoundRepos(repoInfos.Count);

        var localDevVersions = LocalFeedScanner.ScanFeed(localFeed);
        LogFoundDevVersions(localDevVersions.Count);

        var ghDataMap = await ghFetcher.FetchAllAsync("BieberWorks", repoInfos.Keys, ct);

        var releaseOrderMap = LoadLocalReleaseOrderMap(workspace);
        return builder.Build(repoInfos, localDevVersions, ghDataMap, "local", releaseOrderMap, "local");
    }

    private async Task<SdkSnapshot> RunReleaseAsync(string org, string? branch, CancellationToken ct)
    {
        var scanner = new ReleaseScanner(loggerFactory.CreateLogger<ReleaseScanner>());
        var ghFetcher = new GitHubDataFetcher(loggerFactory.CreateLogger<GitHubDataFetcher>());
        var builder = new SnapshotBuilder(loggerFactory.CreateLogger<SnapshotBuilder>());

        var repoInfos = await scanner.ScanOrgAsync(org, ct, branch);
        LogFoundRepos(repoInfos.Count);

        var ghDataMap = await ghFetcher.FetchAllAsync(org, repoInfos.Keys, ct);
        var releaseOrderMap = await LoadGhReleaseOrderMapAsync(org, ct);

        // Release mode: no local feed; pass empty dict
        var snapshotBranch = branch ?? "default";
        var snapshot = builder.Build(repoInfos, [], ghDataMap, "release", releaseOrderMap, snapshotBranch);
        return SnapshotBuilder.SanitizeForRelease(snapshot);
    }

    private Dictionary<string, int>? LoadLocalReleaseOrderMap(string workspace)
    {
        var path = Path.Combine(workspace, "tooling", "release-order.json");
        if (!File.Exists(path))
        {
            LogReleaseOrderNotFound(path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            return map is not null
                ? new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch (Exception ex)
        {
            LogReleaseOrderLoadFailed(path, ex.Message);
            return null;
        }
    }

    private async Task<Dictionary<string, int>?> LoadGhReleaseOrderMapAsync(string org, CancellationToken ct)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("api");
            process.StartInfo.ArgumentList.Add($"repos/{org}/tooling/contents/release-order.json");
            process.StartInfo.ArgumentList.Add("--jq");
            process.StartInfo.ArgumentList.Add(".content");

            process.Start();
            var b64 = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(b64))
            {
                LogReleaseOrderGhFailed(org);
                return null;
            }

            var clean = b64.Replace("\n", "").Replace("\r", "").Trim().Trim('"');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(clean));
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            return map is not null
                ? new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch (Exception ex)
        {
            LogReleaseOrderGhError(ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanner running in mode={Mode}")]
    private partial void LogRunning(string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} SDK repos")]
    private partial void LogFoundRepos(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} local -dev package versions")]
    private partial void LogFoundDevVersions(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot written to {Output}: {ModuleCount} modules, {EdgeCount} edges")]
    private partial void LogSnapshotWritten(string output, int moduleCount, int edgeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "release-order.json not found at {Path}")]
    private partial void LogReleaseOrderNotFound(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load release-order.json from {Path}: {Error}")]
    private partial void LogReleaseOrderLoadFailed(string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch release-order.json from gh for org '{Org}'")]
    private partial void LogReleaseOrderGhFailed(string org);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error fetching release-order.json via gh: {Error}")]
    private partial void LogReleaseOrderGhError(string error);

}

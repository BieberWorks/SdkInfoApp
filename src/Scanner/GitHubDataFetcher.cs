using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace SdkInfoApp.Scanner;

/// <summary>
/// Fetches GitHub data (release version, CI status, PR count) for SDK repos
/// using the gh CLI. Falls back gracefully when gh is not available.
/// </summary>
internal sealed partial class GitHubDataFetcher(ILogger<GitHubDataFetcher> logger)
{
    private const int MaxParallelism = 4;

    public async Task<Dictionary<string, GhRepoData>> FetchAllAsync(
        string org,
        IEnumerable<string> repoIds,
        CancellationToken ct)
    {
        var results = new Dictionary<string, GhRepoData>(StringComparer.OrdinalIgnoreCase);
        var semaphore = new SemaphoreSlim(MaxParallelism);

        var tasks = repoIds.Select(async repoId =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var data = await FetchRepoDataAsync(org, repoId, ct);
                lock (results) results[repoId] = data;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<GhRepoData> FetchRepoDataAsync(string org, string repoId, CancellationToken ct)
    {
        var fullRepo = $"{org}/{repoId}";

        var latestRelease = await RunGhAsync(
            ["release", "view", "--repo", fullRepo, "--json", "tagName,publishedAt"],
            ct);

        var prList = await RunGhAsync(
            ["pr", "list", "--repo", fullRepo, "--state", "open", "--json", "number"],
            ct);

        var ciRun = await RunGhAsync(
            ["run", "list", "--repo", fullRepo, "--limit", "1", "--json", "status,conclusion,createdAt"],
            ct);

        return ParseGhData(repoId, latestRelease, prList, ciRun);
    }

    private GhRepoData ParseGhData(string repoId, string? releaseJson, string? prJson, string? ciJson)
    {
        string? latestVersion = null;
        DateTimeOffset? lastCiRun = null;
        string? ciStatus = "unknown";
        int openPrCount = 0;

        try
        {
            if (!string.IsNullOrWhiteSpace(releaseJson))
            {
                var rel = JsonDocument.Parse(releaseJson).RootElement;
                var tag = rel.TryGetProperty("tagName", out var tn) ? tn.GetString() : null;
                latestVersion = tag?.TrimStart('v');
            }
        }
        catch (Exception ex) { LogParseError(repoId, "release", ex.Message); }

        try
        {
            if (!string.IsNullOrWhiteSpace(prJson))
            {
                var prs = JsonDocument.Parse(prJson).RootElement;
                openPrCount = prs.ValueKind == JsonValueKind.Array ? prs.GetArrayLength() : 0;
            }
        }
        catch (Exception ex) { LogParseError(repoId, "pr", ex.Message); }

        try
        {
            if (!string.IsNullOrWhiteSpace(ciJson))
            {
                var runs = JsonDocument.Parse(ciJson).RootElement;
                if (runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0)
                {
                    var run = runs[0];
                    var conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
                    var status = run.TryGetProperty("status", out var s) ? s.GetString() : null;
                    ciStatus = conclusion ?? status ?? "unknown";

                    if (run.TryGetProperty("createdAt", out var ca) && ca.TryGetDateTimeOffset(out var dt))
                        lastCiRun = dt;
                }
            }
        }
        catch (Exception ex) { LogParseError(repoId, "ci", ex.Message); }

        return new GhRepoData(latestVersion, ciStatus, lastCiRun, openPrCount);
    }

    private async Task<string?> RunGhAsync(string[] arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in arguments)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            LogGhNotFound();
            return null;
        }
        catch (Exception ex)
        {
            LogGhError(string.Join(" ", arguments), ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "gh CLI not found in PATH. CI/version data will be unavailable.")]
    private partial void LogGhNotFound();

    [LoggerMessage(Level = LogLevel.Warning, Message = "gh command '{Command}' failed: {Error}")]
    private partial void LogGhError(string command, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse {DataType} JSON for repo {RepoId}: {Error}")]
    private partial void LogParseError(string repoId, string dataType, string error);
}

internal sealed record GhRepoData(
    string? LatestGhVersion,
    string? CiStatus,
    DateTimeOffset? LastCiRun,
    int OpenPrCount);

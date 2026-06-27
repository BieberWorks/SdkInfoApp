using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SdkInfoApp.Scanner;

/// <summary>
/// Builds a <see cref="RepoScanResult"/> per SDK repo by querying the GitHub API
/// exclusively via the <c>gh</c> CLI. No local filesystem reads.
/// </summary>
internal sealed partial class ReleaseScanner(ILogger<ReleaseScanner> logger)
{
    private const int MaxParallelism = 4;

    private static readonly JsonSerializerOptions JsonCi = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Discovers all <c>SDK-*</c> repos in <paramref name="org"/> and scans each one
    /// for csproj-based package/edge data and an optional <c>module.manifest.json</c>.
    /// When <paramref name="branch"/> is supplied, only that branch is read for every repo;
    /// repos where the branch does not exist are skipped with a warning.
    /// When <paramref name="branch"/> is <see langword="null"/>, the default branch of each repo is used.
    /// </summary>
    public async Task<Dictionary<string, RepoScanResult>> ScanOrgAsync(
        string org, CancellationToken ct, string? branch = null)
    {
        var repoNames = await ListSdkReposAsync(org, ct);
        LogFoundRepos(org, repoNames.Count);

        var results = new Dictionary<string, RepoScanResult>(StringComparer.OrdinalIgnoreCase);
        var semaphore = new SemaphoreSlim(MaxParallelism);

        var tasks = repoNames.Select(async repoId =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await ScanRepoAsync(org, repoId, branch, ct);
                if (result is not null)
                    lock (results) results[repoId] = result;
            }
            catch (Exception ex)
            {
                LogRepoScanError(repoId, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Pass 2: build package→repo map across ALL repos, then re-resolve edges
        var packageToRepo = BuildPackageToRepoMap(results);
        ResolveEdges(results, packageToRepo);

        return results;
    }

    private async Task<List<string>> ListSdkReposAsync(string org, CancellationToken ct)
    {
        // gh api --paginate orgs/{org}/repos returns JSON array of repo objects
        var json = await RunGhApiAsync(
            ["api", "--paginate", $"orgs/{org}/repos", "--jq", ".[].name"],
            ct);

        if (string.IsNullOrWhiteSpace(json)) return [];

        var names = new List<string>();
        foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // --jq output might be quoted strings or bare names depending on gh version
            var name = line.Trim('"');
            if (name.StartsWith("SDK-", StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
    }

    private async Task<RepoScanResult?> ScanRepoAsync(
        string org, string repoId, string? overrideBranch, CancellationToken ct)
    {
        // 1. Determine the branch to scan
        string defaultBranch;
        if (overrideBranch is not null)
        {
            // Check whether the requested branch exists in this repo
            var exists = await BranchExistsAsync(org, repoId, overrideBranch, ct);
            if (!exists)
            {
                LogBranchNotFound(repoId, overrideBranch);
                return null;
            }
            defaultBranch = overrideBranch;
        }
        else
        {
            defaultBranch = await GetDefaultBranchAsync(org, repoId, ct);
        }

        // 2. Get recursive file tree to find all *.csproj paths
        var csprojPaths = await GetCsprojPathsAsync(org, repoId, defaultBranch, ct);
        LogFoundCsproj(repoId, csprojPaths.Count);

        // 3. Get Directory.Build.props for package prefix (best-effort)
        var buildPropsText = await GetFileContentAsync(org, repoId, "Directory.Build.props", defaultBranch, ct);
        var packagePrefix = CsprojParser.ReadPackagePrefixFromText(buildPropsText);

        // 4. Parse each csproj (own packages + raw edges)
        var ownPackages = new List<string>();
        var rawEdges = new List<(string ownerPkg, string refPkg, string version)>();

        foreach (var csprojPath in csprojPaths)
        {
            if (CsprojParser.IsTestProjectPath(csprojPath)) continue;

            var text = await GetFileContentAsync(org, repoId, csprojPath, defaultBranch, ct);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var projectFileName = Path.GetFileNameWithoutExtension(csprojPath);
            var packageId = CsprojParser.ReadPackageIdFromText(text, projectFileName, packagePrefix);

            if (packageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                ownPackages.Add(packageId);

            if (string.IsNullOrEmpty(packageId)) continue;

            foreach (var (refId, version) in CsprojParser.ReadPackageReferencesFromText(text))
            {
                if (refId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                    rawEdges.Add((packageId, refId, version));
            }
        }

        // 5. Manifest
        var manifestText = await GetFileContentAsync(org, repoId, "module.manifest.json", defaultBranch, ct);
        var manifest = ParseManifest(repoId, manifestText);

        // Store raw edges; cross-repo resolution happens after all repos are scanned
        var pkgEdges = rawEdges
            .Where(e => e.ownerPkg != e.refPkg)
            .DistinctBy(e => $"{e.ownerPkg}→{e.refPkg}")
            .Select(e => new RawPackageEdge(e.ownerPkg, e.refPkg, e.version))
            .ToList();

        LogRepoScanned(repoId, ownPackages.Count, manifest is not null);
        return new RepoScanResult(repoId, ownPackages, [], pkgEdges, manifest);
    }

    private async Task<bool> BranchExistsAsync(
        string org, string repoId, string branch, CancellationToken ct)
    {
        var result = await RunGhApiAsync(
            ["api", $"repos/{org}/{repoId}/branches/{branch}", "--jq", ".name"],
            ct);
        return !string.IsNullOrWhiteSpace(result);
    }

    private async Task<string> GetDefaultBranchAsync(string org, string repoId, CancellationToken ct)
    {
        var json = await RunGhApiAsync(
            ["api", $"repos/{org}/{repoId}", "--jq", ".default_branch"],
            ct);
        return string.IsNullOrWhiteSpace(json) ? "main" : json.Trim().Trim('"');
    }

    private async Task<List<string>> GetCsprojPathsAsync(
        string org, string repoId, string branch, CancellationToken ct)
    {
        var json = await RunGhApiAsync(
            ["api", $"repos/{org}/{repoId}/git/trees/{branch}?recursive=1",
             "--jq", ".tree[] | select(.path | endswith(\".csproj\")) | .path"],
            ct);

        if (string.IsNullOrWhiteSpace(json)) return [];

        return json
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim('"'))
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
    }

    private async Task<string?> GetFileContentAsync(
        string org, string repoId, string path, string branch, CancellationToken ct)
    {
        // Use --jq '.content' to get base64 blob and decode it; ?ref= pins the branch explicitly
        var b64 = await RunGhApiAsync(
            ["api", $"repos/{org}/{repoId}/contents/{path}?ref={branch}", "--jq", ".content"],
            ct);

        if (string.IsNullOrWhiteSpace(b64)) return null;

        try
        {
            // GitHub returns base64 with newlines; strip them before decoding
            var clean = b64.Replace("\n", "").Replace("\r", "").Trim().Trim('"');
            var bytes = Convert.FromBase64String(clean);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            LogBase64DecodeError(repoId, path, ex.Message);
            return null;
        }
    }

    private ModuleManifest? ParseManifest(string repoId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ModuleManifest>(json, JsonCi);
        }
        catch (Exception ex)
        {
            LogManifestParseError(repoId, ex.Message);
            return null;
        }
    }

    private static Dictionary<string, string> BuildPackageToRepoMap(
        Dictionary<string, RepoScanResult> results)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repoId, scan) in results)
            foreach (var pkg in scan.OwnPackages)
                map.TryAdd(pkg, repoId);
        return map;
    }

    private static void ResolveEdges(
        Dictionary<string, RepoScanResult> results,
        Dictionary<string, string> packageToRepo)
    {
        // Replace the stub [] edges list in each RepoScanResult with resolved cross-repo edges
        var resolved = new Dictionary<string, RepoScanResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repoId, scan) in results)
        {
            // Collect distinct cross-repo references from raw package edges
            var repoEdges = scan.PackageEdges
                .Select(e => (e.RefPackageId, e.VersionRange))
                .DistinctBy(e => e.RefPackageId)
                .Where(e => packageToRepo.TryGetValue(e.RefPackageId, out var target) && target != repoId)
                .Select(e => new ParsedEdge(e.RefPackageId, packageToRepo[e.RefPackageId], e.VersionRange))
                .ToList();

            resolved[repoId] = new RepoScanResult(
                scan.RepoId,
                scan.OwnPackages,
                repoEdges,
                scan.PackageEdges,
                scan.Manifest);
        }

        // Mutate the dictionary in-place
        foreach (var (k, v) in resolved)
            results[k] = v;
    }

    private async Task<string?> RunGhApiAsync(string[] arguments, CancellationToken ct)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} SDK repos in org '{Org}'")]
    private partial void LogFoundRepos(string org, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Repo {RepoId}: found {Count} csproj files")]
    private partial void LogFoundCsproj(string repoId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Repo {RepoId}: {PackageCount} packages, hasManifest={HasManifest}")]
    private partial void LogRepoScanned(string repoId, int packageCount, bool hasManifest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repo {RepoId}: branch '{Branch}' not found — skipping")]
    private partial void LogBranchNotFound(string repoId, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to scan repo {RepoId}: {Error}")]
    private partial void LogRepoScanError(string repoId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decode base64 content for {RepoId}/{Path}: {Error}")]
    private partial void LogBase64DecodeError(string repoId, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse module.manifest.json in {RepoId}: {Error}")]
    private partial void LogManifestParseError(string repoId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "gh CLI not found in PATH. Release scan will return empty results.")]
    private partial void LogGhNotFound();

    [LoggerMessage(Level = LogLevel.Warning, Message = "gh command '{Command}' failed: {Error}")]
    private partial void LogGhError(string command, string error);
}

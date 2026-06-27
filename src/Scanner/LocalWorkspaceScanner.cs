using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Text.Json;
using System.Xml.Linq;

namespace SdkInfoApp.Scanner;

/// <summary>
/// Scans SDK-* repos under a workspace directory. Extracts package→repo mapping
/// and inter-module PackageReference edges via pure XML parse (no MSBuild eval).
/// </summary>
internal sealed partial class LocalWorkspaceScanner(ILogger<LocalWorkspaceScanner> logger)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Returns a dictionary keyed by repoId (e.g. "SDK-Auth") containing all
    /// parsed information needed by the SnapshotBuilder.
    /// </summary>
    public Dictionary<string, RepoScanResult> ScanWorkspace(string workspace)
    {
        var sdkDirs = Directory.GetDirectories(workspace, "SDK-*", SearchOption.TopDirectoryOnly);
        var results = new Dictionary<string, RepoScanResult>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: build package→repo map from all <PackageId> declarations
        var packageToRepo = BuildPackageToRepoMap(sdkDirs);
        LogPackageMapBuilt(packageToRepo.Count);

        // Pass 2: per repo, extract edges + manifest
        foreach (var dir in sdkDirs)
        {
            var repoId = Path.GetFileName(dir);
            var result = ScanRepo(dir, repoId, packageToRepo);
            results[repoId] = result;
        }

        return results;
    }

    private Dictionary<string, string> BuildPackageToRepoMap(string[] sdkDirs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in sdkDirs)
        {
            var repoId = Path.GetFileName(dir);
            var csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories);

            // Derive the PackagePrefix from Directory.Build.props if present
            var packagePrefix = ReadPackagePrefix(dir);

            foreach (var csproj in csprojFiles)
            {
                if (IsTestProject(csproj)) continue;

                var packageId = ReadPackageId(csproj, packagePrefix);
                if (!string.IsNullOrEmpty(packageId) && packageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                {
                    map.TryAdd(packageId, repoId);
                }
            }
        }

        return map;
    }

    private RepoScanResult ScanRepo(string dir, string repoId, Dictionary<string, string> packageToRepo)
    {
        var csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories);
        var packagePrefix = ReadPackagePrefix(dir);
        var ownPackages = new List<string>();
        var rawEdges = new List<(string packageRef, string versionRange)>();

        foreach (var csproj in csprojFiles)
        {
            if (IsTestProject(csproj)) continue;

            var packageId = ReadPackageId(csproj, packagePrefix);
            if (!string.IsNullOrEmpty(packageId) && packageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                ownPackages.Add(packageId);

            var refs = ReadPackageReferences(csproj);
            foreach (var (refId, version) in refs)
            {
                if (refId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                    rawEdges.Add((refId, version));
            }
        }

        // Convert raw edges: packageRef → target repoId
        var edges = new List<ParsedEdge>();
        foreach (var (pkgRef, version) in rawEdges.DistinctBy(e => e.packageRef))
        {
            if (packageToRepo.TryGetValue(pkgRef, out var targetRepo) && targetRepo != repoId)
            {
                edges.Add(new ParsedEdge(pkgRef, targetRepo, version));
            }
        }

        var manifest = TryReadManifest(dir, repoId);

        LogRepoScanned(repoId, ownPackages.Count, edges.Count);
        return new RepoScanResult(repoId, ownPackages, edges, manifest);
    }

    private static string ReadPackagePrefix(string repoDir)
    {
        var buildPropsPath = Path.Combine(repoDir, "Directory.Build.props");
        if (!File.Exists(buildPropsPath)) return "BieberWorks.SDK";

        try
        {
            var doc = XDocument.Load(buildPropsPath);
            var prefix = doc.Descendants("PackagePrefix").FirstOrDefault()?.Value;
            return string.IsNullOrEmpty(prefix) ? "BieberWorks.SDK" : prefix;
        }
        catch
        {
            return "BieberWorks.SDK";
        }
    }

    private static string ReadPackageId(string csprojPath, string packagePrefix)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);

            // Explicit <PackageId>
            var explicit_ = doc.Descendants("PackageId").FirstOrDefault()?.Value;
            if (!string.IsNullOrEmpty(explicit_)) return explicit_;

            // Derive from project file name: BieberWorks.SDK.<ProjectName>
            var projectName = Path.GetFileNameWithoutExtension(csprojPath);
            return $"{packagePrefix}.{projectName}";
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<(string packageId, string version)> ReadPackageReferences(string csprojPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(csprojPath); }
        catch { yield break; }

        foreach (var el in doc.Descendants("PackageReference"))
        {
            var include = el.Attribute("Include")?.Value;
            var version = el.Attribute("Version")?.Value ?? el.Element("Version")?.Value ?? "";
            if (!string.IsNullOrEmpty(include))
                yield return (include, version);
        }
    }

    private ModuleManifest? TryReadManifest(string repoDir, string repoId)
    {
        var manifestPath = Path.Combine(repoDir, "module.manifest.json");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<ModuleManifest>(json, ManifestJsonOptions);
        }
        catch (Exception ex)
        {
            LogManifestParseError(repoId, ex.Message);
            return null;
        }
    }

    private static bool IsTestProject(string csprojPath)
    {
        var dir = Path.GetDirectoryName(csprojPath) ?? "";
        return dir.Contains("tests", StringComparison.OrdinalIgnoreCase)
            || dir.Contains("test", StringComparison.OrdinalIgnoreCase)
            || csprojPath.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Built package-to-repo map with {Count} entries")]
    private partial void LogPackageMapBuilt(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Repo {RepoId}: {PackageCount} packages, {EdgeCount} edges")]
    private partial void LogRepoScanned(string repoId, int packageCount, int edgeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse module.manifest.json in {RepoId}: {Error}")]
    private partial void LogManifestParseError(string repoId, string error);
}

internal sealed record RepoScanResult(
    string RepoId,
    IReadOnlyList<string> OwnPackages,
    IReadOnlyList<ParsedEdge> Edges,
    ModuleManifest? Manifest);

internal sealed record ParsedEdge(string PackageRef, string TargetRepoId, string VersionRange);

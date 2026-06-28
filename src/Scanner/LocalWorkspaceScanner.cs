using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Text.Json;

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

        // Per-csproj: ownerPackageId → list of (refPackageId, version)
        var perCsprojEdges = new List<(string ownerPkg, string refPkg, string version)>();
        // Intra-repo ProjectReference edges (ownerPkg → siblingPkg)
        var projectEdges = new List<(string ownerPkg, string refPkg)>();

        // Map every non-test csproj path → its package id, so ProjectReference paths can be resolved.
        var pathToPkg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in csprojFiles)
        {
            if (IsTestProject(csproj)) continue;
            var pid = ReadPackageId(csproj, packagePrefix);
            if (!string.IsNullOrEmpty(pid))
                pathToPkg[CsprojParser.NormalizeCsprojKey(csproj)] = pid;
        }

        foreach (var csproj in csprojFiles)
        {
            if (IsTestProject(csproj)) continue;

            var packageId = ReadPackageId(csproj, packagePrefix);
            if (!string.IsNullOrEmpty(packageId) && packageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                ownPackages.Add(packageId);

            if (string.IsNullOrEmpty(packageId)) continue;

            var refs = ReadPackageReferences(csproj);
            foreach (var (refId, version) in refs)
            {
                if (refId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                    perCsprojEdges.Add((packageId, refId, version));
            }

            // Resolve sibling ProjectReferences to their package ids (intra-repo only)
            foreach (var include in ReadProjectReferences(csproj))
            {
                // Expand wildcard globs before lookup (e.g. `..\*.Contracts\*.csproj`)
                // Glob expansion yields absolute paths; non-glob paths are relative and need ResolveProjectReferencePath.
                bool isGlob = include.Contains('*') || include.Contains('?');
                IEnumerable<string> resolvedIncludes = isGlob
                    ? ExpandGlobProjectReference(csproj, include)
                    : [include];

                foreach (var resolved in resolvedIncludes)
                {
                    var targetKey = isGlob
                        ? CsprojParser.NormalizeCsprojKey(resolved)
                        : CsprojParser.ResolveProjectReferencePath(csproj, resolved);
                    if (pathToPkg.TryGetValue(targetKey, out var refPkg)
                        && !string.Equals(refPkg, packageId, StringComparison.OrdinalIgnoreCase)
                        && refPkg.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase)
                        && packageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase))
                    {
                        projectEdges.Add((packageId, refPkg));
                    }
                }
            }
        }

        // Repo-level edges (deduplicated by referenced package, cross-repo only)
        var rawEdgesDistinct = perCsprojEdges
            .Select(e => (e.refPkg, e.version))
            .DistinctBy(e => e.refPkg);

        var edges = new List<ParsedEdge>();
        foreach (var (pkgRef, version) in rawEdgesDistinct)
        {
            if (packageToRepo.TryGetValue(pkgRef, out var targetRepo) && targetRepo != repoId)
                edges.Add(new ParsedEdge(pkgRef, targetRepo, version));
        }

        // Package-level edges (deduplicated by ownerPkg+refPkg pair). PackageReferences first,
        // then sibling ProjectReferences as a distinct "project" edge type.
        var pkgEdges = new List<RawPackageEdge>();
        var seenPkgEdge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in perCsprojEdges.Where(e => e.ownerPkg != e.refPkg))
        {
            if (seenPkgEdge.Add($"{e.ownerPkg}→{e.refPkg}"))
                pkgEdges.Add(new RawPackageEdge(e.ownerPkg, e.refPkg, e.version));
        }
        foreach (var e in projectEdges)
        {
            if (seenPkgEdge.Add($"{e.ownerPkg}→{e.refPkg}"))
                pkgEdges.Add(new RawPackageEdge(e.ownerPkg, e.refPkg, "", "project"));
        }

        var manifest = TryReadManifest(dir, repoId);

        LogRepoScanned(repoId, ownPackages.Count, edges.Count);
        return new RepoScanResult(repoId, ownPackages, edges, pkgEdges, manifest);
    }

    /// <summary>
    /// Expands a glob-style ProjectReference Include (e.g. <c>..\*.Contracts\*.csproj</c>)
    /// into absolute paths by walking segments recursively.
    /// Returns absolute csproj paths; non-matching globs yield an empty enumerable.
    /// </summary>
    private static IEnumerable<string> ExpandGlobProjectReference(string ownerCsproj, string include)
    {
        var ownerDir = Path.GetDirectoryName(Path.GetFullPath(ownerCsproj)) ?? "";
        var segments = include.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return ExpandSegmented(ownerDir, segments);
    }

    private static IEnumerable<string> ExpandSegmented(string baseDir, string[] segments)
    {
        if (segments.Length == 0) yield break;

        var head = segments[0];
        var rest = segments[1..];

        if (rest.Length == 0)
        {
            // File segment — may contain wildcard
            foreach (var f in SafeGetFiles(baseDir, head))
                yield return f;
            yield break;
        }

        if (head == "..")
        {
            var parent = Path.GetDirectoryName(baseDir);
            if (parent is not null)
                foreach (var r in ExpandSegmented(parent, rest)) yield return r;
        }
        else if (head.Contains('*') || head.Contains('?'))
        {
            foreach (var d in SafeGetDirectories(baseDir, head))
                foreach (var r in ExpandSegmented(d, rest)) yield return r;
        }
        else
        {
            var next = Path.Combine(baseDir, head);
            foreach (var r in ExpandSegmented(next, rest)) yield return r;
        }
    }

    private static IEnumerable<string> SafeGetFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch { return []; }
    }

    private static IEnumerable<string> SafeGetDirectories(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : []; }
        catch { return []; }
    }

    private static string ReadPackagePrefix(string repoDir)
    {
        var buildPropsPath = Path.Combine(repoDir, "Directory.Build.props");
        if (!File.Exists(buildPropsPath)) return "BieberWorks.SDK";
        try { return CsprojParser.ReadPackagePrefixFromText(File.ReadAllText(buildPropsPath)); }
        catch { return "BieberWorks.SDK"; }
    }

    private static string ReadPackageId(string csprojPath, string packagePrefix)
    {
        try
        {
            var text = File.ReadAllText(csprojPath);
            return CsprojParser.ReadPackageIdFromText(text, Path.GetFileNameWithoutExtension(csprojPath), packagePrefix);
        }
        catch { return ""; }
    }

    private static IEnumerable<(string packageId, string version)> ReadPackageReferences(string csprojPath)
    {
        try { return CsprojParser.ReadPackageReferencesFromText(File.ReadAllText(csprojPath)); }
        catch { return []; }
    }

    private static IEnumerable<string> ReadProjectReferences(string csprojPath)
    {
        try { return CsprojParser.ReadProjectReferencesFromText(File.ReadAllText(csprojPath)).ToList(); }
        catch { return []; }
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
        => CsprojParser.IsTestProjectPath(csprojPath)
        || CsprojParser.IsTestProjectPath(Path.GetDirectoryName(csprojPath) ?? "");

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
    IReadOnlyList<RawPackageEdge> PackageEdges,
    ModuleManifest? Manifest);

internal sealed record ParsedEdge(string PackageRef, string TargetRepoId, string VersionRange);

internal sealed record RawPackageEdge(string OwnerPackageId, string RefPackageId, string VersionRange, string RefType = "package");

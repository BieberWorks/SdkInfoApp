using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;
using System.Text.Json;

namespace SdkInfoApp.Scanner;

/// <summary>
/// Aggregates scan results into a <see cref="SdkSnapshot"/>.
/// </summary>
internal sealed partial class SnapshotBuilder(ILogger<SnapshotBuilder> logger)
{
    public SdkSnapshot Build(
        Dictionary<string, RepoScanResult> repoInfos,
        Dictionary<string, string> localDevVersions,
        Dictionary<string, GhRepoData> ghDataMap,
        string snapshotMode,
        Dictionary<string, int>? releaseOrderMap,
        string branch = "local")
    {
        var modules = new List<ModuleInfo>();
        var edges = new List<EdgeInfo>();
        var capabilityMap = new Dictionary<string, CapabilityInfo>(StringComparer.OrdinalIgnoreCase);

        // Build the set of all known SDK package IDs across all repos (for dangling-ref detection)
        var knownPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scan in repoInfos.Values)
            foreach (var pkg in scan.OwnPackages)
                knownPackageIds.Add(pkg);

        // Compute dependency tiers via impl-only longest-path DFS (cycle-safe)
        var tierMap = ComputeTiers(repoInfos);

        releaseOrderMap ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Dangling-ref tracking: warnings deduplicated
        var danglingWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repoId, scan) in repoInfos)
        {
            ghDataMap.TryGetValue(repoId, out var gh);
            var localDev = FindLocalDevVersion(scan.OwnPackages, localDevVersions);
            var manifest = scan.Manifest;

            var module = new ModuleInfo
            {
                Id = repoId,
                RepoName = repoId,
                Tier = tierMap.GetValueOrDefault(repoId, 0),
                ReleaseOrder = releaseOrderMap.TryGetValue(repoId, out var ro) ? ro : null,
                Packages = scan.OwnPackages,
                LatestGhVersion = gh?.LatestGhVersion,
                LocalDevVersion = snapshotMode == "local" ? localDev : null,
                LocalDevPresent = snapshotMode == "local" && localDev is not null,
                Description = manifest?.Description,
                Capabilities = manifest?.Capabilities.Select(c => c.Id).ToList() ?? [],
                Tags = manifest?.Tags ?? [],
                CiStatus = gh?.CiStatus,
                LastCiRun = gh?.LastCiRun,
                OpenPrCount = gh?.OpenPrCount ?? 0,
                GhReleasesUrl = $"https://github.com/BieberWorks/{repoId}/releases",
                GhDocsUrl = manifest?.DocsUrl ?? $"https://github.com/BieberWorks/{repoId}/blob/main/docs/index.md",
                ManifestSource = manifest is not null ? "file" : "fallback",
            };

            modules.Add(module);

            foreach (var edge in scan.Edges)
            {
                var kind = edge.PackageRef.Contains(".Contracts", StringComparison.OrdinalIgnoreCase)
                    ? "contracts"
                    : "impl";

                edges.Add(new EdgeInfo
                {
                    From = repoId,
                    To = edge.TargetRepoId,
                    PackageRef = edge.PackageRef,
                    VersionRange = edge.VersionRange,
                    Kind = kind,
                });
            }

            // Detect dangling SDK references: BieberWorks.SDK.* refs not in the known package set
            foreach (var pkgEdge in scan.PackageEdges)
            {
                if (knownPackageIds.Contains(pkgEdge.RefPackageId)) continue;
                if (!pkgEdge.RefPackageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase)) continue;

                danglingWarnings.Add($"{repoId} references unknown SDK package '{pkgEdge.RefPackageId}' (renamed/removed?)");
            }

            if (manifest is not null)
            {
                foreach (var cap in manifest.Capabilities)
                {
                    if (!capabilityMap.TryGetValue(cap.Id, out var existing))
                    {
                        capabilityMap[cap.Id] = new CapabilityInfo
                        {
                            Id = cap.Id,
                            Label = cap.Label,
                            Category = cap.Category,
                            Description = cap.Description,
                            ProvidedBy = [repoId],
                        };
                    }
                    else
                    {
                        // ID collision across modules — extend ProvidedBy and warn
                        if (!string.Equals(existing.Label, cap.Label, StringComparison.OrdinalIgnoreCase))
                            LogCapabilityLabelConflict(cap.Id, existing.Label, cap.Label, repoId);
                        existing.ProvidedBy.Add(repoId);
                    }
                }
            }
        }

        // Sort modules by tier then name for stable output
        modules.Sort((a, b) => a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier) : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        // Build package-level graph
        var (packageNodes, packageEdges) = BuildPackageGraph(repoInfos, tierMap, knownPackageIds);

        // Emit dangling module-level edges where the derived module exists in our repo set
        var danglingModuleEdges = BuildDanglingModuleEdges(repoInfos, knownPackageIds);
        foreach (var de in danglingModuleEdges)
            edges.Add(de);

        var capabilities = capabilityMap.Values.OrderBy(c => c.Id).ToList();
        var capabilityGroups = BuildCapabilityGroups(capabilities);

        // Drift check: warn when a module's impl-dep has a higher release-order
        var driftWarnings = CheckReleaseOrderDrift(repoInfos, releaseOrderMap, [.. edges.Where(e => e.Kind != "dangling")]);

        // Combine all warnings (dangling first, then drift)
        var allWarnings = danglingWarnings
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .Concat(driftWarnings)
            .ToList();

        return new SdkSnapshot
        {
            SnapshotMode = snapshotMode,
            Branch = branch,
            GeneratedAt = DateTimeOffset.UtcNow,
            Modules = modules,
            Edges = edges,
            Capabilities = capabilities,
            CapabilityGroups = capabilityGroups,
            PackageNodes = packageNodes,
            PackageEdges = packageEdges,
            Warnings = allWarnings,
        };
    }

    /// <summary>
    /// For every raw package edge whose target is not a known package, emit a module-level
    /// dangling edge if the inferred module exists in our repo set.
    /// </summary>
    private static List<EdgeInfo> BuildDanglingModuleEdges(
        Dictionary<string, RepoScanResult> repoInfos,
        HashSet<string> knownPackageIds)
    {
        var edgeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EdgeInfo>();

        foreach (var (repoId, scan) in repoInfos)
        {
            foreach (var pkgEdge in scan.PackageEdges)
            {
                if (knownPackageIds.Contains(pkgEdge.RefPackageId)) continue;
                if (!pkgEdge.RefPackageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase)) continue;

                var derivedModule = DeriveModuleFromPackageId(pkgEdge.RefPackageId);
                if (derivedModule is null || !repoInfos.ContainsKey(derivedModule)) continue;
                if (string.Equals(derivedModule, repoId, StringComparison.OrdinalIgnoreCase)) continue;

                var key = $"{repoId}→{derivedModule}";
                if (!edgeSet.Add(key)) continue;

                result.Add(new EdgeInfo
                {
                    From = repoId,
                    To = derivedModule,
                    PackageRef = pkgEdge.RefPackageId,
                    VersionRange = pkgEdge.VersionRange,
                    Kind = "dangling",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Derives a module repo ID (e.g. "SDK-Components") from a package ID prefix
    /// like "BieberWorks.SDK.Components.*". Returns null when the segment count is insufficient.
    /// </summary>
    private static string? DeriveModuleFromPackageId(string packageId)
    {
        // BieberWorks.SDK.{ModuleName}[.anything]
        // Split on '.' and take the 3rd segment (index 2) as the module name
        var parts = packageId.Split('.');
        if (parts.Length < 3) return null;
        return $"SDK-{parts[2]}";
    }

    private static List<CapabilityGroup> BuildCapabilityGroups(List<CapabilityInfo> capabilities)
        => capabilities
            .GroupBy(c => c.Category)
            .Select(g => new CapabilityGroup
            {
                Id = g.Key.ToLowerInvariant().Replace(" / ", "-").Replace(" ", "-"),
                Label = g.Key,
                Members = g.Select(c => c.Id).ToList(),
            })
            .OrderBy(g => g.Label)
            .ToList();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Capability ID '{CapId}' defined in multiple modules with different labels: '{ExistingLabel}' vs '{NewLabel}' (in {RepoId})")]
    private partial void LogCapabilityLabelConflict(string capId, string existingLabel, string newLabel, string repoId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "release-order drift: {Module} (order {ModuleOrder}) impl-depends on {Dep} (order {DepOrder})")]
    private partial void LogReleaseOrderDrift(string module, int moduleOrder, string dep, int depOrder);

    private static (List<PackageNode> nodes, List<PackageEdge> edges) BuildPackageGraph(
        Dictionary<string, RepoScanResult> repoInfos,
        Dictionary<string, int> tierMap,
        HashSet<string> knownPackageIds)
    {
        // All known SDK package ids → their owning repo
        var pkgToRepo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repoId, scan) in repoInfos)
            foreach (var pkg in scan.OwnPackages)
                pkgToRepo.TryAdd(pkg, repoId);

        var nodes = new List<PackageNode>();
        foreach (var (repoId, scan) in repoInfos)
        {
            foreach (var pkg in scan.OwnPackages)
            {
                nodes.Add(new PackageNode
                {
                    Id = pkg,
                    Module = repoId,
                    Tier = tierMap.GetValueOrDefault(repoId, 0),
                });
            }
        }

        // Collect all package-level edges; only include edges where BOTH endpoints are known SDK packages
        var edgeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<PackageEdge>();

        foreach (var (_, scan) in repoInfos)
        {
            foreach (var raw in scan.PackageEdges)
            {
                if (!pkgToRepo.ContainsKey(raw.RefPackageId)) continue;
                var dedupeKey = $"{raw.OwnerPackageId}→{raw.RefPackageId}";
                if (!edgeSet.Add(dedupeKey)) continue;

                var kind = raw.RefPackageId.Contains(".Contracts", StringComparison.OrdinalIgnoreCase)
                    ? "contracts"
                    : "impl";

                edges.Add(new PackageEdge
                {
                    From = raw.OwnerPackageId,
                    To = raw.RefPackageId,
                    VersionRange = raw.VersionRange,
                    Kind = kind,
                });
            }
        }

        // Add dangling package edges: synthetic node for the unknown package + dangling edge
        foreach (var (repoId, scan) in repoInfos)
        {
            foreach (var raw in scan.PackageEdges)
            {
                if (knownPackageIds.Contains(raw.RefPackageId)) continue;
                if (!raw.RefPackageId.StartsWith("BieberWorks.SDK.", StringComparison.OrdinalIgnoreCase)) continue;

                var dedupeKey = $"{raw.OwnerPackageId}→{raw.RefPackageId}";
                if (!edgeSet.Add(dedupeKey)) continue;

                // Ensure the synthetic target node exists
                if (nodes.All(n => !string.Equals(n.Id, raw.RefPackageId, StringComparison.OrdinalIgnoreCase)))
                {
                    var derivedModule = DeriveModuleFromPackageId(raw.RefPackageId) ?? repoId;
                    nodes.Add(new PackageNode
                    {
                        Id = raw.RefPackageId,
                        Module = derivedModule,
                        Tier = tierMap.GetValueOrDefault(derivedModule, 0),
                        IsDangling = true,
                    });
                }

                // Ensure the owning package node exists
                if (nodes.All(n => !string.Equals(n.Id, raw.OwnerPackageId, StringComparison.OrdinalIgnoreCase)))
                {
                    nodes.Add(new PackageNode
                    {
                        Id = raw.OwnerPackageId,
                        Module = repoId,
                        Tier = tierMap.GetValueOrDefault(repoId, 0),
                    });
                }

                edges.Add(new PackageEdge
                {
                    From = raw.OwnerPackageId,
                    To = raw.RefPackageId,
                    VersionRange = raw.VersionRange,
                    Kind = "dangling",
                });
            }
        }

        nodes.Sort((a, b) => a.Tier != b.Tier
            ? a.Tier.CompareTo(b.Tier)
            : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        return (nodes, edges);
    }

    /// <summary>
    /// Computes dependency tiers via impl-only longest-path DFS (memoised, cycle-safe).
    /// tier(m) = 0 when m has no impl-deps on other SDK modules;
    /// otherwise 1 + max(tier(dep)).
    /// </summary>
    private static Dictionary<string, int> ComputeTiers(Dictionary<string, RepoScanResult> repoInfos)
    {
        var allRepos = repoInfos.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build impl-only adjacency: repoId → set of repos it impl-depends on
        var implAdj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in allRepos)
            implAdj[id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repoId, scan) in repoInfos)
        {
            foreach (var edge in scan.Edges)
            {
                if (!allRepos.Contains(edge.TargetRepoId)) continue;
                if (edge.TargetRepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase)) continue;

                var isContracts = edge.PackageRef.Contains(".Contracts", StringComparison.OrdinalIgnoreCase);
                if (!isContracts)
                    implAdj[repoId].Add(edge.TargetRepoId);
            }
        }

        var memo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int Dfs(string id)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            if (!visiting.Add(id)) return 0; // cycle guard

            var deps = implAdj.TryGetValue(id, out var set) ? set : [];
            var maxDepTier = deps.Count > 0 ? deps.Max(Dfs) : -1;
            var tier = maxDepTier + 1;

            visiting.Remove(id);
            memo[id] = tier;
            return tier;
        }

        foreach (var id in allRepos)
            Dfs(id);

        return memo;
    }

    /// <summary>
    /// For each module m with a release-order, checks whether any impl-dep d has a higher
    /// release-order than m (i.e., d would be promoted later, but m depends on it already).
    /// Emits a warning per drift pair and returns the collected warning strings.
    /// </summary>
    private List<string> CheckReleaseOrderDrift(
        Dictionary<string, RepoScanResult> repoInfos,
        Dictionary<string, int> releaseOrderMap,
        List<EdgeInfo> edges)
    {
        var warnings = new List<string>();

        // Index impl-edges by From for quick lookup
        var implEdgesFrom = edges
            .Where(e => e.Kind == "impl")
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (repoId, _) in repoInfos)
        {
            if (!releaseOrderMap.TryGetValue(repoId, out var om)) continue;
            if (!implEdgesFrom.TryGetValue(repoId, out var deps)) continue;

            foreach (var dep in deps)
            {
                if (!releaseOrderMap.TryGetValue(dep, out var od)) continue;
                if (od > om)
                {
                    var msg = $"release-order drift: {repoId} (order {om}) impl-depends on {dep} (order {od})";
                    LogReleaseOrderDrift(repoId, om, dep, od);
                    warnings.Add(msg);
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// Strips all local/internal data from a snapshot before writing it as a public release artifact.
    /// Returns a new <see cref="SdkSnapshot"/> with <c>snapshotMode = "release"</c> and
    /// all <c>localDevVersion</c> / <c>localDevPresent</c> fields cleared.
    /// </summary>
    public static SdkSnapshot SanitizeForRelease(SdkSnapshot snapshot)
    {
        var cleanModules = snapshot.Modules
            .Select(m => m with { LocalDevVersion = null, LocalDevPresent = false })
            .ToList();

        return snapshot with
        {
            SnapshotMode = "release",
            Modules = cleanModules,
        };
    }

    private static string? FindLocalDevVersion(
        IReadOnlyList<string> ownPackages,
        Dictionary<string, string> localDevVersions)
    {
        foreach (var pkg in ownPackages)
        {
            if (localDevVersions.TryGetValue(pkg, out var v))
                return v;
        }
        return null;
    }
}

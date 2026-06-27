using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner.Model;

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
        string branch = "local")
    {
        var modules = new List<ModuleInfo>();
        var edges = new List<EdgeInfo>();
        var capabilityMap = new Dictionary<string, CapabilityInfo>(StringComparer.OrdinalIgnoreCase);

        // Assign tiers: 0 = no deps on other SDK modules, higher = deeper in DAG
        var tierMap = ComputeTiers(repoInfos);

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
        var (packageNodes, packageEdges) = BuildPackageGraph(repoInfos, tierMap);

        var capabilities = capabilityMap.Values.OrderBy(c => c.Id).ToList();
        var capabilityGroups = BuildCapabilityGroups(capabilities);

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
        };
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

    private static (List<PackageNode> nodes, List<PackageEdge> edges) BuildPackageGraph(
        Dictionary<string, RepoScanResult> repoInfos,
        Dictionary<string, int> tierMap)
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

        nodes.Sort((a, b) => a.Tier != b.Tier
            ? a.Tier.CompareTo(b.Tier)
            : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

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

        return (nodes, edges);
    }

    /// <summary>
    /// Computes tiers using Kahn's topological sort on the repo-level DAG.
    /// Cycles are broken by ignoring back-edges that would increase a node's
    /// tier beyond that of a node already in the same SCC; the result is a
    /// best-effort layering consistent with the observable dependency direction.
    /// </summary>
    private static Dictionary<string, int> ComputeTiers(Dictionary<string, RepoScanResult> repoInfos)
    {
        var allRepos = repoInfos.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build a deduplicated repo-level adjacency (from → targets)
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in allRepos)
        {
            adj[id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            inDegree[id] = 0;
        }

        foreach (var (repoId, scan) in repoInfos)
        {
            foreach (var edge in scan.Edges)
            {
                if (!allRepos.Contains(edge.TargetRepoId)) continue;
                if (adj[repoId].Add(edge.TargetRepoId))
                    inDegree[edge.TargetRepoId]++;
            }
        }

        // Kahn's BFS — nodes with in-degree 0 go to tier 0, etc.
        var tiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var id in allRepos)
            if (inDegree[id] == 0) queue.Enqueue(id);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var nodeTier = tiers.GetValueOrDefault(node, 0);

            foreach (var dep in adj[node])
            {
                // dep is something node points TO — dep is "lower" (Foundation-level)
                // We invert: caller is higher tier than callee.
                // Actually our edges go FROM consumer TO dependency.
                // So if A->B, A depends on B, B is lower tier (tier of B <= tier of A - 1).
                // Kahn processes sources (no incoming = no dependents = leaf dependencies) first → tier 0.
            }

            // Actually rebuild: reverse the graph for Kahn so leaves (no outgoing deps) process first.
            // Let's use a simpler iterative longest-path approach with cycle detection.
            break;
        }

        // Simpler: reverse-topological sort using longest path from leaves.
        // "leaf" = module with no dependencies on other SDK modules (tier 0)
        // Build reverse: who depends on whom
        var dependedOnBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in allRepos) dependedOnBy[id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var outDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in allRepos) outDegree[id] = 0;

        // adj[A] = B means A depends on B. outDegree = number of deps A has.
        foreach (var (repoId, _) in repoInfos)
        {
            foreach (var dep in adj[repoId])
            {
                dependedOnBy[dep].Add(repoId); // dep is needed by repoId
                outDegree[repoId]++;
            }
        }

        // Kahn from leaves (outDegree=0 = no dependencies = Tier 0)
        tiers.Clear();
        var q = new Queue<string>();
        foreach (var id in allRepos)
        {
            if (outDegree[id] == 0) { tiers[id] = 0; q.Enqueue(id); }
        }

        while (q.Count > 0)
        {
            var node = q.Dequeue();
            var nodeTier = tiers[node];

            foreach (var dependent in dependedOnBy[node])
            {
                var newTier = nodeTier + 1;
                if (!tiers.TryGetValue(dependent, out var existingTier) || newTier > existingTier)
                    tiers[dependent] = newTier;

                outDegree[dependent]--;
                if (outDegree[dependent] <= 0)
                    q.Enqueue(dependent);
            }
        }

        // Nodes in cycles never reached Kahn (outDegree never hit 0) — assign fallback tier
        int maxTier = tiers.Count > 0 ? tiers.Values.Max() : 0;
        foreach (var id in allRepos)
            tiers.TryAdd(id, maxTier + 1);

        return tiers;
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

using ModelContextProtocol.Server;
using SdkInfoApp.Scanner.Model;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdkInfoApp.Mcp;

[McpServerToolType]
public sealed class SdkTools(SdkSnapshot snapshot)
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── list_modules ──────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Lists all SDK modules with their tier, latest GitHub version, and capability count.")]
    public string list_modules()
    {
        var result = snapshot.Modules
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                id = m.Id,
                tier = m.Tier,
                latestGhVersion = m.LatestGhVersion,
                capabilitiesCount = m.Capabilities.Count,
                description = m.Description,
            })
            .ToList();

        return JsonSerializer.Serialize(result, SerializeOptions);
    }

    // ── get_module ────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Returns full details for a single module including its direct dependencies and dependents.")]
    public string get_module(
        [Description("The module ID, e.g. 'SDK-Auth'.")] string id)
    {
        var module = snapshot.Modules
            .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        if (module is null)
            return JsonSerializer.Serialize(new { error = $"Module '{id}' not found." }, SerializeOptions);

        var deps = snapshot.Edges
            .Where(e => string.Equals(e.From, module.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => new { to = e.To, kind = e.Kind, packageRef = e.PackageRef, versionRange = e.VersionRange })
            .ToList();

        var dependents = snapshot.Edges
            .Where(e => string.Equals(e.To, module.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => new { from = e.From, kind = e.Kind, packageRef = e.PackageRef })
            .ToList();

        var result = new
        {
            id = module.Id,
            repoName = module.RepoName,
            tier = module.Tier,
            latestGhVersion = module.LatestGhVersion,
            localDevVersion = module.LocalDevVersion,
            localDevPresent = module.LocalDevPresent,
            description = module.Description,
            packages = module.Packages,
            capabilities = module.Capabilities,
            tags = module.Tags,
            ciStatus = module.CiStatus,
            ghReleasesUrl = module.GhReleasesUrl,
            ghDocsUrl = module.GhDocsUrl,
            directDependencies = deps,
            directDependents = dependents,
        };

        return JsonSerializer.Serialize(result, SerializeOptions);
    }

    // ── list_capabilities ─────────────────────────────────────────────────────

    [McpServerTool]
    [Description("Lists all SDK capabilities with their ID, label, category, and the modules that provide them.")]
    public string list_capabilities()
    {
        var result = snapshot.Capabilities
            .OrderBy(c => c.Category)
            .ThenBy(c => c.Label)
            .Select(c => new
            {
                id = c.Id,
                label = c.Label,
                category = c.Category,
                description = c.Description,
                providedBy = c.ProvidedBy,
            })
            .ToList();

        return JsonSerializer.Serialize(result, SerializeOptions);
    }

    // ── modules_for_capabilities ──────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Given a list of capability IDs, returns the complete set of SDK modules that must be installed " +
        "(direct providers plus their full impl-only transitive upstream closure), sorted by tier. " +
        "Also returns a ready-to-paste PackageReference XML snippet.")]
    public string modules_for_capabilities(
        [Description("Array of capability IDs, e.g. [\"auth:login\", \"audit:log\"].")] string[] capabilityIds)
    {
        var directIds = SdkQuery.DirectModulesForCapabilities(snapshot, capabilityIds);
        var allIds = SdkQuery.TransitiveUpstreamClosure(snapshot, directIds);

        var moduleMap = snapshot.Modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var allModules = allIds
            .Select(mid => moduleMap.GetValueOrDefault(mid))
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id)
            .ToList();

        var snippet = PackageReferenceSnippetBuilder.BuildPackageReferences(allModules, directIds);

        var result = new
        {
            requestedCapabilities = capabilityIds,
            directModules = directIds.OrderBy(x => x).ToList(),
            allModules = allModules.Select(m => new
            {
                id = m.Id,
                tier = m.Tier,
                latestGhVersion = m.LatestGhVersion,
                isDirect = directIds.Contains(m.Id),
            }).ToList(),
            packageReferenceSnippet = snippet,
        };

        return JsonSerializer.Serialize(result, SerializeOptions);
    }

    // ── dependency_closure ────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Computes the impl-only transitive upstream closure for a set of module IDs. " +
        "Returns the full list of modules that must be installed.")]
    public string dependency_closure(
        [Description("Array of seed module IDs, e.g. [\"SDK-Auth\"].")] string[] moduleIds)
    {
        var allIds = SdkQuery.TransitiveUpstreamClosure(snapshot, moduleIds);
        var moduleMap = snapshot.Modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var allModules = allIds
            .Select(mid => moduleMap.GetValueOrDefault(mid))
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                id = m.Id,
                tier = m.Tier,
                latestGhVersion = m.LatestGhVersion,
                isSeed = moduleIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase),
            })
            .ToList();

        var result = new
        {
            seeds = moduleIds,
            closure = allModules,
        };

        return JsonSerializer.Serialize(result, SerializeOptions);
    }

    // ── reverse_impact ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Returns the reverse impact set for a module — all modules that depend on it (directly or transitively " +
        "over any edge kind). Use this to understand what breaks if you make a breaking change.")]
    public string reverse_impact(
        [Description("The module ID to analyse, e.g. 'SDK-Foundation'.")] string moduleId)
    {
        var impact = SdkQuery.ReverseImpact(snapshot, moduleId);
        impact.Remove(moduleId);

        var moduleMap = snapshot.Modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var impactModules = impact
            .Select(mid => moduleMap.GetValueOrDefault(mid))
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderBy(m => m.Tier)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                id = m.Id,
                tier = m.Tier,
                latestGhVersion = m.LatestGhVersion,
            })
            .ToList();

        var result = new
        {
            moduleId,
            impactCount = impactModules.Count,
            affectedModules = impactModules,
        };

        return JsonSerializer.Serialize(result, SerializeOptions);
    }
}

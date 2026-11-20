namespace SdkInfoApp.Scanner.Model;

/// <summary>Known values of the manifest/snapshot <c>kind</c> field.</summary>
public static class ModuleKinds
{
    public const string Module = "module";
    public const string Service = "service";

    /// <summary>Repo-name prefix identifying service repos.</summary>
    public const string ServiceRepoPrefix = "SDK-Service-";

    /// <summary>Tag identifying a service when the manifest has no explicit kind.</summary>
    public const string ServiceTag = "service";

    public static bool IsKnown(string? kind)
        => string.Equals(kind, Module, StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, Service, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of <see cref="ModuleKindResolver.Resolve"/>.</summary>
/// <param name="Kind">Normalised kind (<c>module</c> or <c>service</c>).</param>
/// <param name="Warning">Set when the manifest declared an unknown kind (fell back to <c>module</c>).</param>
public readonly record struct ModuleKindResult(string Kind, string? Warning = null);

public static class ModuleKindResolver
{
    /// <summary>
    /// Fallback chain: manifest kind, tag <c>service</c>, repo prefix <c>SDK-Service-</c>, <c>module</c>.
    /// An unknown manifest kind yields <c>module</c> plus a warning.
    /// </summary>
    public static ModuleKindResult ResolveWithWarning(ModuleManifest? manifest, string repoId)
    {
        var declared = manifest?.Kind;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (ModuleKinds.IsKnown(declared))
                return new ModuleKindResult(declared.Trim().ToLowerInvariant());

            return new ModuleKindResult(
                ModuleKinds.Module,
                $"{repoId}: unknown manifest kind '{declared}', treated as '{ModuleKinds.Module}'");
        }

        return new ModuleKindResult(ResolveFallback(manifest?.Tags, repoId));
    }

    /// <summary>Convenience wrapper that drops the warning.</summary>
    public static string Resolve(ModuleManifest? manifest, string repoId)
        => ResolveWithWarning(manifest, repoId).Kind;

    /// <summary>Fallback only (tags, prefix); used for v1 snapshots without a <c>kind</c> field.</summary>
    public static string ResolveFallback(IEnumerable<string>? tags, string repoId)
    {
        if (tags is not null && tags.Any(t => string.Equals(t, ModuleKinds.ServiceTag, StringComparison.OrdinalIgnoreCase)))
            return ModuleKinds.Service;

        return repoId.StartsWith(ModuleKinds.ServiceRepoPrefix, StringComparison.OrdinalIgnoreCase)
            ? ModuleKinds.Service
            : ModuleKinds.Module;
    }
}

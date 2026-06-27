using SdkInfoApp.Scanner.Model;
using System.Text;

namespace SdkInfoApp.Web.Services;

/// <summary>Pure-static builder for consumer-facing PackageReference XML snippets.</summary>
public static class PackageReferenceSnippetBuilder
{
    public static string BuildPackageReferences(
        IReadOnlyList<ModuleInfo> modules,
        HashSet<string> directModuleIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!-- PackageReferences — copy into your host project (.csproj) -->");
        sb.AppendLine();

        var byTier = modules
            .GroupBy(m => m.Tier)
            .OrderBy(g => g.Key);

        foreach (var tierGroup in byTier)
        {
            var tierLabel = tierGroup.Key == 0 ? "Tier 0: Foundation"
                : $"Tier {tierGroup.Key}";

            sb.AppendLine($"<!-- {tierLabel} -->");

            foreach (var m in tierGroup.OrderBy(m => m.Id))
            {
                var version = m.LatestGhVersion ?? "0.0.0";
                var isDirect = directModuleIds.Contains(m.Id);
                if (!isDirect)
                    sb.AppendLine($"<!-- Transitively required by other modules: {m.Id} -->");

                // Main impl package: the package(s) that are NOT Contracts-only and NOT UI.
                // Strategy: emit each package. Mark UI packages with a comment.
                foreach (var pkg in m.Packages)
                {
                    var isUi = pkg.Contains(".UI.", StringComparison.OrdinalIgnoreCase)
                               || pkg.EndsWith(".UI", StringComparison.OrdinalIgnoreCase);
                    sb.Append($"<PackageReference Include=\"{pkg}\" Version=\"{version}\" />");
                    if (isUi)
                        sb.Append("  <!-- Blazor UI — remove if API-only host -->");
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildNugetConfigLine()
        => """<add key="bieberworks" value="https://nuget.pkg.github.com/BieberWorks/index.json" />""" + "\n" +
           "<!-- Requires a PAT with read:packages scope as your NuGet password -->";
}

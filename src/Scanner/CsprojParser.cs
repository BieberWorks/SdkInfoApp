using System.Xml.Linq;

namespace SdkInfoApp.Scanner;

/// <summary>
/// Stateless helpers for parsing csproj XML text.
/// Used by both LocalWorkspaceScanner (from disk) and ReleaseScanner (from gh API content).
/// </summary>
internal static class CsprojParser
{
    private const string DefaultPackagePrefix = "BieberWorks.SDK";

    /// <summary>
    /// Parses the <c>&lt;PackagePrefix&gt;</c> value from a Directory.Build.props XML text.
    /// Returns <c>"BieberWorks.SDK"</c> if the text is null/empty or the element is absent.
    /// </summary>
    public static string ReadPackagePrefixFromText(string? buildPropsText)
    {
        if (string.IsNullOrWhiteSpace(buildPropsText)) return DefaultPackagePrefix;
        try
        {
            var doc = XDocument.Parse(buildPropsText);
            var prefix = doc.Descendants("PackagePrefix").FirstOrDefault()?.Value;
            return string.IsNullOrEmpty(prefix) ? DefaultPackagePrefix : prefix;
        }
        catch
        {
            return DefaultPackagePrefix;
        }
    }

    /// <summary>
    /// Derives the NuGet package ID from a csproj XML text.
    /// Checks <c>&lt;PackageId&gt;</c> first; falls back to
    /// <c>{packagePrefix}.{projectFileName}</c>.
    /// </summary>
    public static string ReadPackageIdFromText(string csprojText, string projectFileName, string packagePrefix)
    {
        try
        {
            var doc = XDocument.Parse(csprojText);
            var explicit_ = doc.Descendants("PackageId").FirstOrDefault()?.Value;
            if (!string.IsNullOrEmpty(explicit_)) return explicit_;
        }
        catch { /* fall through to fallback */ }

        return $"{packagePrefix}.{projectFileName}";
    }

    /// <summary>
    /// Enumerates all <c>&lt;PackageReference&gt;</c> entries from csproj XML text.
    /// </summary>
    public static IEnumerable<(string PackageId, string Version)> ReadPackageReferencesFromText(string csprojText)
    {
        XDocument doc;
        try { doc = XDocument.Parse(csprojText); }
        catch { yield break; }

        foreach (var el in doc.Descendants("PackageReference"))
        {
            var include = el.Attribute("Include")?.Value;
            var version = el.Attribute("Version")?.Value ?? el.Element("Version")?.Value ?? "";
            if (!string.IsNullOrEmpty(include))
                yield return (include, version);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the project file name/path suggests a test project.
    /// </summary>
    public static bool IsTestProjectPath(string csprojPathOrName)
        => csprojPathOrName.Contains("tests", StringComparison.OrdinalIgnoreCase)
        || csprojPathOrName.Contains("test", StringComparison.OrdinalIgnoreCase)
        || csprojPathOrName.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
}

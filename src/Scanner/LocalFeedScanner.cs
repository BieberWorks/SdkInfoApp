namespace SdkInfoApp.Scanner;

/// <summary>
/// Scans the local-nuget-feed directory and extracts the highest prerelease version
/// per package from nupkg filenames.
/// </summary>
internal static class LocalFeedScanner
{
    /// <summary>
    /// Returns a map of packageId (lowercase) → highest local dev version string.
    /// Filenames follow the convention: {PackageId}.{Version}.nupkg
    /// </summary>
    public static Dictionary<string, string> ScanFeed(string feedDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(feedDirectory))
            return map;

        var nupkgFiles = Directory.GetFiles(feedDirectory, "*.nupkg", SearchOption.TopDirectoryOnly);

        foreach (var file in nupkgFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file); // e.g. BieberWorks.SDK.Auth.1.1.0-dev
            var parsed = ParseNupkgFileName(fileName);
            if (parsed is null) continue;

            var (packageId, version) = parsed.Value;

            // Keep highest version per package (simple string comparison is sufficient for -dev versions)
            if (!map.TryGetValue(packageId, out var existing)
                || CompareVersions(version, existing) > 0)
            {
                map[packageId] = version;
            }
        }

        return map;
    }

    private static (string packageId, string version)? ParseNupkgFileName(string fileName)
    {
        // Format: {PackageId}.{Major}.{Minor}.{Patch}[-prerelease]
        // We split from the right by finding the first segment that starts with a digit
        var parts = fileName.Split('.');
        if (parts.Length < 4) return null;

        // Find the index where the version starts (first all-digit segment)
        int versionStartIndex = -1;
        for (int i = 1; i < parts.Length; i++)
        {
            if (char.IsDigit(parts[i][0]))
            {
                versionStartIndex = i;
                break;
            }
        }

        if (versionStartIndex < 0) return null;

        var packageId = string.Join(".", parts[..versionStartIndex]);
        var versionParts = parts[versionStartIndex..];

        // Handle prerelease: last part might be "1-dev" → rejoin correctly
        var version = string.Join(".", versionParts);

        return (packageId, version);
    }

    private static int CompareVersions(string a, string b)
    {
        // Strip prerelease for numeric comparison, prefer higher numeric version
        static (int major, int minor, int patch) Parse(string v)
        {
            var clean = v.Split('-')[0];
            var parts = clean.Split('.');
            return (
                parts.Length > 0 && int.TryParse(parts[0], out var maj) ? maj : 0,
                parts.Length > 1 && int.TryParse(parts[1], out var min) ? min : 0,
                parts.Length > 2 && int.TryParse(parts[2], out var patch) ? patch : 0
            );
        }

        var (aMaj, aMin, aPatch) = Parse(a);
        var (bMaj, bMin, bPatch) = Parse(b);

        if (aMaj != bMaj) return aMaj.CompareTo(bMaj);
        if (aMin != bMin) return aMin.CompareTo(bMin);
        return aPatch.CompareTo(bPatch);
    }
}

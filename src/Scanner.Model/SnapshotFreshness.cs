using System.Globalization;

namespace SdkInfoApp.Scanner.Model;

/// <summary>Pure staleness rules for a loaded snapshot (testable via <see cref="TimeProvider"/>).</summary>
public static class SnapshotFreshness
{
    /// <summary>Snapshots older than this are reported as stale.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);

    /// <summary>
    /// Returns the timestamp to show in a stale banner, or <see langword="null"/> when the snapshot is fresh,
    /// is a local-mode snapshot, or has no usable timestamp and is not flagged stale.
    /// <see cref="DateTimeOffset.MinValue"/> means "stale, date unknown".
    /// </summary>
    /// <param name="branch">Snapshot branch; <c>local</c> never yields a banner.</param>
    /// <param name="generatedAt">Index entry timestamp (ISO 8601) or the snapshot's own timestamp.</param>
    /// <param name="flaggedStale">The index entry carries <c>stale:true</c>.</param>
    /// <param name="time">Time source.</param>
    public static DateTimeOffset? GetStaleSince(string? branch, string? generatedAt, bool flaggedStale, TimeProvider time)
    {
        if (string.Equals(branch, "local", StringComparison.OrdinalIgnoreCase)) return null;

        DateTimeOffset? parsed = DateTimeOffset.TryParse(generatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;

        if (parsed is null) return flaggedStale ? DateTimeOffset.MinValue : null;
        if (flaggedStale || time.GetUtcNow() - parsed.Value > MaxAge) return parsed;
        return null;
    }
}

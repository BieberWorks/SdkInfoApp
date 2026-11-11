using SdkInfoApp.Scanner.Model;
using Shouldly;
using Xunit;

namespace Scanner.Tests;

public sealed class SnapshotFreshnessTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly FixedTime Now = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Fresh_snapshot_is_not_stale() =>
        SnapshotFreshness.GetStaleSince("main", "2026-11-10T12:00:00Z", false, Now).ShouldBeNull();

    [Fact]
    public void Older_than_48h_is_stale() =>
        SnapshotFreshness.GetStaleSince("main", "2026-11-09T11:00:00Z", false, Now)
            .ShouldBe(new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Exactly_48h_is_not_stale() =>
        SnapshotFreshness.GetStaleSince("main", "2026-11-09T12:00:00Z", false, Now).ShouldBeNull();

    [Fact]
    public void Flag_forces_stale_even_when_recent() =>
        SnapshotFreshness.GetStaleSince("dev", "2026-11-11T06:00:00Z", true, Now).ShouldNotBeNull();

    [Fact]
    public void Local_branch_never_stale() =>
        SnapshotFreshness.GetStaleSince("local", "2020-01-01T00:00:00Z", true, Now).ShouldBeNull();

    [Fact]
    public void Missing_timestamp_is_stale_only_when_flagged()
    {
        SnapshotFreshness.GetStaleSince("main", null, false, Now).ShouldBeNull();
        SnapshotFreshness.GetStaleSince("main", null, true, Now).ShouldBe(DateTimeOffset.MinValue);
    }
}

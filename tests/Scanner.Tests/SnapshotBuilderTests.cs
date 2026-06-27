using SdkInfoApp.Scanner;
using SdkInfoApp.Scanner.Model;
using Shouldly;
using Xunit;

namespace Scanner.Tests;

public sealed class SnapshotBuilderSanitizeTests
{
    private static ModuleInfo MakeModule(string id, string? localDevVersion, bool localDevPresent) =>
        new()
        {
            Id = id,
            RepoName = id,
            Tier = 1,
            ReleaseOrder = 2,
            Packages = [$"BieberWorks.SDK.{id}"],
            LatestGhVersion = "1.0.0",
            LocalDevVersion = localDevVersion,
            LocalDevPresent = localDevPresent,
            Description = $"Description of {id}",
            Capabilities = ["cap-a"],
            Tags = ["tag1"],
        };

    private static SdkSnapshot BuildLocalSnapshot() =>
        new()
        {
            SnapshotMode = "local",
            Branch = "dev",
            GeneratedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Modules =
            [
                MakeModule("SDK-Auth", "1.1.0-dev", true),
                MakeModule("SDK-UI",   "2.0.0-dev", true),
                MakeModule("SDK-Foundation", null, false),
            ],
            Edges = [],
            Capabilities = [],
            PackageNodes = [],
            PackageEdges = [],
            Warnings = [],
        };

    [Fact]
    public void SanitizeForRelease_SetsSnapshotModeToRelease()
    {
        var snapshot = BuildLocalSnapshot();

        var result = SnapshotBuilder.SanitizeForRelease(snapshot);

        result.SnapshotMode.ShouldBe("release");
    }

    [Fact]
    public void SanitizeForRelease_ClearsLocalDevVersionOnAllModules()
    {
        var snapshot = BuildLocalSnapshot();

        var result = SnapshotBuilder.SanitizeForRelease(snapshot);

        foreach (var module in result.Modules)
            module.LocalDevVersion.ShouldBeNull();
    }

    [Fact]
    public void SanitizeForRelease_ClearsLocalDevPresentOnAllModules()
    {
        var snapshot = BuildLocalSnapshot();

        var result = SnapshotBuilder.SanitizeForRelease(snapshot);

        foreach (var module in result.Modules)
            module.LocalDevPresent.ShouldBeFalse();
    }

    [Fact]
    public void SanitizeForRelease_PreservesPublicModuleFields()
    {
        var snapshot = BuildLocalSnapshot();
        var original = snapshot.Modules[0];

        var result = SnapshotBuilder.SanitizeForRelease(snapshot);
        var sanitized = result.Modules[0];

        sanitized.Id.ShouldBe(original.Id);
        sanitized.Tier.ShouldBe(original.Tier);
        sanitized.LatestGhVersion.ShouldBe(original.LatestGhVersion);
        sanitized.Capabilities.ShouldBe(original.Capabilities);
        sanitized.Packages.ShouldBe(original.Packages);
        sanitized.ReleaseOrder.ShouldBe(original.ReleaseOrder);
    }

    [Fact]
    public void SanitizeForRelease_PreservesModuleCount()
    {
        var snapshot = BuildLocalSnapshot();

        var result = SnapshotBuilder.SanitizeForRelease(snapshot);

        result.Modules.Count.ShouldBe(snapshot.Modules.Count);
    }

    [Fact]
    public void SanitizeForRelease_PreservesSnapshotLevelFields()
    {
        var snapshot = BuildLocalSnapshot();

        var result = SnapshotBuilder.SanitizeForRelease(snapshot);

        result.Branch.ShouldBe(snapshot.Branch);
        result.GeneratedAt.ShouldBe(snapshot.GeneratedAt);
    }
}

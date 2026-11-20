using Microsoft.Extensions.Logging.Abstractions;
using SdkInfoApp.Scanner;
using SdkInfoApp.Scanner.Model;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace Scanner.Tests;

public sealed class SnapshotBuilderKindTests
{
    private static RepoScanResult Repo(string id, ModuleManifest? manifest = null) =>
        new(id, [$"BieberWorks.SDK.{id}"], [], [], manifest);

    private static SdkSnapshot Build(params RepoScanResult[] repos) =>
        new SnapshotBuilder(NullLogger<SnapshotBuilder>.Instance).Build(
            repos.ToDictionary(r => r.RepoId),
            new Dictionary<string, string>(),
            new Dictionary<string, GhRepoData>(),
            "release",
            null);

    [Fact]
    public void Build_SetsKindFromResolver()
    {
        var snapshot = Build(
            Repo("SDK-Auth"),
            Repo("SDK-Service-Identity"),
            Repo("SDK-Tagged", new ModuleManifest { Tags = ["service"] }));

        snapshot.Modules.Single(m => m.Id == "SDK-Auth").Kind.ShouldBe("module");
        snapshot.Modules.Single(m => m.Id == "SDK-Service-Identity").Kind.ShouldBe("service");
        snapshot.Modules.Single(m => m.Id == "SDK-Tagged").Kind.ShouldBe("service");
    }

    [Fact]
    public void Build_SortsModulesBeforeServices_ThenName()
    {
        var snapshot = Build(
            Repo("SDK-Service-B"), Repo("SDK-Z"), Repo("SDK-Service-A"), Repo("SDK-A"));

        snapshot.Modules.Select(m => m.Id)
            .ShouldBe(["SDK-A", "SDK-Z", "SDK-Service-A", "SDK-Service-B"]);
    }

    [Fact]
    public void Build_UnknownKind_AddsWarning_AndTreatsAsModule()
    {
        var snapshot = Build(Repo("SDK-Foo", new ModuleManifest { Kind = "plugin" }));

        snapshot.Modules.Single().Kind.ShouldBe("module");
        snapshot.Warnings.ShouldContain(w => w.Contains("plugin"));
    }

    [Fact]
    public void Build_SetsSchemaVersion2()
    {
        Build(Repo("SDK-Auth")).SchemaVersion.ShouldBe("2");
    }
}

public sealed class SnapshotDeserializationTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private const string V1Json = """
        {"schemaVersion":"1","modules":[
          {"id":"SDK-Auth","tags":[]},
          {"id":"SDK-Service-Identity","tags":[]},
          {"id":"SDK-Tagged","tags":["service"]}]}
        """;

    [Fact]
    public void V1Snapshot_WithoutKind_UsesFallbackChain()
    {
        var raw = JsonSerializer.Deserialize<SdkSnapshot>(V1Json, Options);

        var snapshot = SnapshotCompat.ApplyV1KindFallback(raw)!;

        snapshot.Modules.Single(m => m.Id == "SDK-Auth").Kind.ShouldBe("module");
        snapshot.Modules.Single(m => m.Id == "SDK-Service-Identity").Kind.ShouldBe("service");
        snapshot.Modules.Single(m => m.Id == "SDK-Tagged").Kind.ShouldBe("service");
    }

    [Fact]
    public void V2Snapshot_RoundtripKeepsKind_AndIsNotRewritten()
    {
        var original = new SdkSnapshot
        {
            Modules = [new ModuleInfo { Id = "SDK-Auth", Kind = "service" }],
        };

        var json = JsonSerializer.Serialize(original, Options);
        var back = SnapshotCompat.ApplyV1KindFallback(JsonSerializer.Deserialize<SdkSnapshot>(json, Options))!;

        json.ShouldContain("\"kind\":\"service\"");
        back.SchemaVersion.ShouldBe("2");
        back.Modules.Single().Kind.ShouldBe("service");
    }
}

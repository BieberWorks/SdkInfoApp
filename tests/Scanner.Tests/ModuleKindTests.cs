using SdkInfoApp.Scanner.Model;
using Shouldly;
using Xunit;

// NOTE: Tests intentionally stay on the Scanner.Model level. If Web components (ModuleCard,
// DashboardPage, ...) need their own tests later, add bUnit via a new project tests/Web.Tests.
namespace Scanner.Tests;

public sealed class ModuleKindResolverTests
{
    [Fact]
    public void ManifestKind_Wins_OverTagAndPrefix()
    {
        var manifest = new ModuleManifest { Kind = "module", Tags = ["service"] };

        ModuleKindResolver.Resolve(manifest, "SDK-Service-X").ShouldBe("module");
    }

    [Fact]
    public void ManifestKind_IsCaseInsensitive_AndNormalised()
    {
        var manifest = new ModuleManifest { Kind = "Service" };

        ModuleKindResolver.Resolve(manifest, "SDK-Auth").ShouldBe("service");
    }

    [Fact]
    public void NoKind_ServiceTag_ResolvesService()
    {
        var manifest = new ModuleManifest { Tags = ["docker", "service"] };

        ModuleKindResolver.Resolve(manifest, "SDK-Auth").ShouldBe("service");
    }

    [Fact]
    public void NoKind_NoTag_ServicePrefix_ResolvesService()
    {
        ModuleKindResolver.Resolve(new ModuleManifest(), "SDK-Service-Identity").ShouldBe("service");
    }

    [Fact]
    public void NoManifest_PlainRepo_ResolvesModule()
    {
        ModuleKindResolver.Resolve(null, "SDK-Auth").ShouldBe("module");
    }

    [Fact]
    public void UnknownKind_FallsBackToModule_WithWarning()
    {
        var result = ModuleKindResolver.ResolveWithWarning(new ModuleManifest { Kind = "plugin" }, "SDK-Foo");

        result.Kind.ShouldBe("module");
        result.Warning.ShouldNotBeNull();
        result.Warning.ShouldContain("plugin");
    }

    [Fact]
    public void KnownKind_HasNoWarning()
    {
        ModuleKindResolver.ResolveWithWarning(new ModuleManifest { Kind = "service" }, "SDK-Foo").Warning.ShouldBeNull();
    }
}

public sealed class ModuleDisplayNameTests
{
    [Theory]
    [InlineData("SDK-Service-Identity", "Identity")]
    [InlineData("SDK-Auth", "Auth")]
    [InlineData("SDK-Service-ObservabilityStack", "ObservabilityStack")]
    [InlineData("Other", "Other")]
    public void Of_StripsPrefix(string id, string expected)
    {
        ModuleDisplayName.Of(id).ShouldBe(expected);
    }
}

using SdkInfoApp.Scanner;
using Shouldly;
using Xunit;

namespace Scanner.Tests;

public sealed class CsprojParserProjectRefTests
{
    [Fact]
    public void ReadProjectReferences_ExtractsIncludePaths()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\UI.Contracts\UI.Contracts.csproj" />
                <ProjectReference Include="..\UI.Blazor\UI.Blazor.csproj" />
                <PackageReference Include="BieberWorks.SDK.Foundation" Version="*" />
              </ItemGroup>
            </Project>
            """;

        var refs = CsprojParser.ReadProjectReferencesFromText(csproj).ToList();

        refs.ShouldBe([@"..\UI.Contracts\UI.Contracts.csproj", @"..\UI.Blazor\UI.Blazor.csproj"]);
    }

    [Fact]
    public void ReadProjectReferences_IgnoresPackageReferences()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="BieberWorks.SDK.Foundation" Version="*" />
              </ItemGroup>
            </Project>
            """;

        CsprojParser.ReadProjectReferencesFromText(csproj).ShouldBeEmpty();
    }

    [Theory]
    // Sibling project up one level — the real UI.Blazor → UI.Contracts case (repo-relative, posix).
    [InlineData("src/UI.Blazor/UI.Blazor.csproj", @"..\UI.Contracts\UI.Contracts.csproj", "src/UI.Contracts/UI.Contracts.csproj")]
    // Forward-slash include resolves identically.
    [InlineData("src/UI.Blazor/UI.Blazor.csproj", "../UI.Contracts/UI.Contracts.csproj", "src/UI.Contracts/UI.Contracts.csproj")]
    // Windows absolute owner path with backslashes.
    [InlineData(@"C:\repo\src\A\A.csproj", @"..\B\B.csproj", "C:/repo/src/B/B.csproj")]
    // Two levels up.
    [InlineData("a/b/c/Owner.csproj", @"..\..\X\X.csproj", "a/X/X.csproj")]
    public void ResolveProjectReferencePath_ResolvesRelativePaths(string owner, string include, string expected)
    {
        CsprojParser.ResolveProjectReferencePath(owner, include).ShouldBe(expected);
    }

    [Fact]
    public void NormalizeCsprojKey_MatchesResolvedSiblingPath()
    {
        // The owner csproj and a resolved ProjectReference to its sibling must produce
        // the same key so the package-id lookup succeeds, regardless of separator style.
        var resolved = CsprojParser.ResolveProjectReferencePath(
            @"src\UI.Blazor\UI.Blazor.csproj", @"..\UI.Contracts\UI.Contracts.csproj");
        var siblingKey = CsprojParser.NormalizeCsprojKey(@"src\UI.Contracts\UI.Contracts.csproj");

        resolved.ShouldBe(siblingKey);
    }
}

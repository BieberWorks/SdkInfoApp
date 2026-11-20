namespace SdkInfoApp.Scanner.Model;

public static class ModuleDisplayName
{
    /// <summary>Strips the <c>SDK-Service-</c> or <c>SDK-</c> prefix from a repo/module id.</summary>
    public static string Of(string id)
    {
        if (id.StartsWith(ModuleKinds.ServiceRepoPrefix, StringComparison.OrdinalIgnoreCase))
            return id[ModuleKinds.ServiceRepoPrefix.Length..];
        if (id.StartsWith("SDK-", StringComparison.OrdinalIgnoreCase))
            return id["SDK-".Length..];
        return id;
    }
}

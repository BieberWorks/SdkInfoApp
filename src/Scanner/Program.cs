using Microsoft.Extensions.Logging;
using SdkInfoApp.Scanner;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger<ScannerApp>();

string mode = "local";
string? workspace = null;
string org = "BieberWorks";
string output = "sdk-snapshot.json";
string? branch = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mode" when i + 1 < args.Length:
            mode = args[++i];
            break;
        case "--workspace" when i + 1 < args.Length:
            workspace = args[++i];
            break;
        case "--org" when i + 1 < args.Length:
            org = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            output = args[++i];
            break;
        case "--branch" when i + 1 < args.Length:
            branch = args[++i];
            break;
    }
}

// Default the workspace root path-independently so the whole tree can be relocated
// (e.g. under repos\orgs\) without touching this file. Overridable via --workspace.
workspace ??= ResolveWorkspaceRoot();

var app = new ScannerApp(loggerFactory);

try
{
    await app.RunAsync(mode, workspace, org, output, branch, CancellationToken.None);
}
catch (Exception ex)
{
    logger.LogError(ex, "Scanner failed");
    return 1;
}

return 0;

// Walks up from the current directory to the BieberWorks workspace root, identified
// by the presence of an "Sdk" subfolder. Falls back to the current directory.
static string ResolveWorkspaceRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Sdk")))
            return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}

# Updating NuGet Packages

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), open a terminal in the repository root, and ensure the configured NuGet feeds are reachable. Review or commit existing changes before updating packages.

## Check for Updates

```powershell
dotnet list T.slnx package --outdated
dotnet list T.slnx package --include-transitive --vulnerable
```

The first command lists available updates; it does not install them. The second checks direct and transitive packages against available advisory data. Check compatibility and release notes before selecting versions.

## Update a Package

Update a specific direct dependency using the .NET CLI, or use Microsoft's C# Dev Kit in VS Code: open the command palette, select **NuGet: Update NuGet Package**, and choose the project, package and version.

```powershell
dotnet package update SSH.NET@2026.0.0 --project T.Services/T.Services.csproj
dotnet build T.slnx
```

SSH.NET is referenced by `T.Services`; the example version is already declared in the project. Select the project that actually references the package. Review the diff and warnings and test the affected features.

## Update Vulnerable Packages

Use the .NET SDK's vulnerability-based update for the solution, then restore and build it:

```powershell
dotnet package update --project T.slnx --vulnerable
dotnet restore T.slnx
dotnet build T.slnx --no-restore
```

The update can change project files and requires access to configured feeds and advisory data. It cannot guarantee a fix for every transitive dependency. If a step fails, earlier changes remain; review the diff before retrying. A successful build can still contain warnings.

Afterwards, inspect the changes and remaining vulnerabilities:

```powershell
git diff
dotnet list T.slnx package --include-transitive --vulnerable
```

For the latest documented review and historical findings, see [NUGET-SECURITY-FINDINGS.md](NUGET-SECURITY-FINDINGS.md). Test SSH, SFTP, database functionality and the user interface after relevant updates.

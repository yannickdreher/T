# Defining NuGet Package Dependencies

NuGet package dependencies are declared in a project's `.csproj` file. The solution contains several projects; the application entry point is `T.UI/T.UI.csproj` and SSH.NET is referenced by `T.Services/T.Services.csproj`. Add a package to the project that uses it.

## Add a Package

In a `.csproj` file, add a `PackageReference` inside an `ItemGroup`:

```xml
<ItemGroup>
  <PackageReference Include="SSH.NET" Version="2026.0.0" />
</ItemGroup>
```

The `Include` attribute is the NuGet package ID; `Version` specifies the requested version. This example reflects the dependency currently declared in `T.Services/T.Services.csproj`. See [NUGET-SECURITY-FINDINGS.md](NUGET-SECURITY-FINDINGS.md) for its security advisories.

Alternatively, run the .NET CLI from the repository root:

```powershell
dotnet add T.Services/T.Services.csproj package SSH.NET --version 2026.0.0
```

Replace `2026.0.0` with the chosen version. The command updates the project file and restores dependencies. To remove a package, run:

```powershell
dotnet remove T.Services/T.Services.csproj package SSH.NET
```

## Restore and Inspect Dependencies

Restore and build the services project:

```powershell
dotnet restore T.Services/T.Services.csproj
dotnet build T.Services/T.Services.csproj
```

Inspect direct and transitive dependencies, or check for known vulnerabilities:

```powershell
dotnet list T.Services/T.Services.csproj package --include-transitive
dotnet list T.Services/T.Services.csproj package --include-transitive --vulnerable
```

Transitive dependencies are packages required by other packages. For example, `Tmds.DBus.Protocol` is brought in through Avalonia. To inspect the dependency path:

```powershell
dotnet nuget why T.UI/T.UI.csproj Tmds.DBus.Protocol
```

If a transitive package needs an update, first check whether updating its parent package resolves the issue. A direct `PackageReference` can also influence the resolved version, but its compatibility with the parent package must be verified. Restore and test the application after changing versions.

## Project References

A reference to another project in this repository is different from a NuGet package reference. `T.UI/T.UI.csproj` references the `T.Services` project like this:

```xml
<ItemGroup>
  <ProjectReference Include="..\T.Services\T.Services.csproj" />
</ItemGroup>
```

## Version Selection

Prefer an explicit version when reproducible dependency updates matter. Review resolved versions and security warnings after updates. For stricter reproducibility, consider a NuGet lock file and restore in locked mode.

# NuGet Security Findings

## Current review — 22 November 2026

`dotnet list T.slnx package --include-transitive --vulnerable` completed successfully using `https://api.nuget.org/v3/index.json`. It reported **no known vulnerable packages** for any of the six projects in the solution: `T.Abstractions`, `T.Models`, `T.Services`, `T.Terminal`, `T.Tests`, and `T.UI`.

The check covers the resolved direct and transitive packages against the advisory data available from the configured source at the time of the review. It does not guarantee that the application or its dependencies are free of vulnerabilities. Repeat the check as versions and advisory data change.

## Previously reported advisories

| Package | Earlier version | Current resolved version | Review |
| --- | --- | --- | --- |
| SSH.NET | 2025.1.0 | 2026.0.0 | Both [SCP remote-path handling](https://github.com/advisories/GHSA-mggc-4xg6-vcxf) and [recursive-download path traversal](https://github.com/advisories/GHSA-q939-rpr3-3284) advisories list 2026.0.0 as a patched version. The package is referenced by `T.Services`. |
| Tmds.DBus.Protocol | 0.21.2 | 0.94.1 | The [D-Bus advisory](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9) lists 0.92.0 as patched. The current version is resolved through `Avalonia.Desktop` 12.1.3. |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.11 | 2.1.12 | The [SQLite advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) lists versions through 2.1.11 as affected and does not name a patched package version. The currently resolved 2.1.12 falls outside that stated affected range and NuGet did not flag it. This review did not independently verify the native SQLite library version. |

The current SQLite dependency path is `T.Services → Microsoft.Data.Sqlite 10.0.12 → SQLitePCLRaw.bundle_e_sqlite3 2.1.12 → SQLitePCLRaw.lib.e_sqlite3 2.1.12`. The current D-Bus path is `T.UI → Avalonia.Desktop 12.1.3 → Avalonia.X11 12.1.3 → Avalonia.FreeDesktop 12.1.3 → Tmds.DBus.Protocol 0.94.1`.

## Repeat the review

```powershell
dotnet list T.slnx package --include-transitive --vulnerable
dotnet nuget why T.Services/T.Services.csproj SQLitePCLRaw.lib.e_sqlite3
dotnet nuget why T.UI/T.UI.csproj Tmds.DBus.Protocol
```

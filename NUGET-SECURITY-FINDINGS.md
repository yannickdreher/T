# NuGet Security Findings

The restore of the former `T/T.csproj` on 22 November 2026 reported four `NU1903` warnings affecting three packages. NuGet classified all four advisories as high severity. The project restore succeeded.

The findings below describe the earlier project structure and versions. After rebasing onto `develop`, `T.Services/T.Services.csproj` references SSH.NET 2026.0.0 and Microsoft.Data.Sqlite 10.0.12; `T.UI/T.UI.csproj` uses Avalonia 12.1.3, which requires Tmds.DBus.Protocol 0.94.1 or later. Repeat the vulnerability check on the current solution to establish its present state.

## SSH.NET 2025.1.0 (historical)

SSH.NET is a direct dependency. Two advisories affect its `ScpClient`:

- [GHSA-mggc-4xg6-vcxf](https://github.com/advisories/GHSA-mggc-4xg6-vcxf): attacker-influenced remote paths may lead to command execution on a shell-based SSH server.
- [GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284): a malicious SCP server may write files outside the intended directory during a recursive download.

Both advisories identify **2026.0.0** as a patched version. Update SSH.NET and verify application compatibility. Whether these specific vulnerabilities are exploitable in this application depends on its use of `ScpClient` and the conditions described in the advisories.

## Tmds.DBus.Protocol 0.21.2 (historical)

The package is an indirect dependency:

```text
Avalonia.Desktop 11.3.12
  → Avalonia.X11 11.3.12
    → Avalonia.FreeDesktop 11.3.12
      → Tmds.DBus.Protocol 0.21.2
```

[GHSA-xrw6-gwf8-vvr9](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9) describes attacks by malicious peers on the same D-Bus, including signal spoofing and denial of service. The advisory identifies **0.21.3** as a patched version in the 0.21 series. Check for a compatible Avalonia update or evaluate an explicit dependency update and test it.

## SQLitePCLRaw.lib.e_sqlite3 2.1.11 (historical)

The package is an indirect dependency:

```text
Microsoft.Data.Sqlite 10.0.3
  → SQLitePCLRaw.bundle_e_sqlite3 2.1.11
    → SQLitePCLRaw.lib.e_sqlite3 2.1.11
```

[GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) describes a memory corruption vulnerability in SQLite versions before 3.50.2. At the time of this review, the advisory did not list a patched version of `SQLitePCLRaw.lib.e_sqlite3`. Investigate a compatible SQLite dependency that includes a fixed SQLite version and test the application's database functionality.

## Verification limits

The findings above come from `dotnet restore T/T.csproj` and the linked advisories. A separate online query using `dotnet list T/T.csproj package --include-transitive --vulnerable` could not complete because the network connection was refused. The solution build using `T.slnx` also failed in the review environment without a specific error message. Those results do not establish the state of the rebased solution.

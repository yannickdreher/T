# Build the Project in VS Code

## Prerequisites

- [Visual Studio Code](https://code.visualstudio.com/) and, optionally, the **C# Dev Kit** extension.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for Windows. The .NET runtime alone is not sufficient to build the project.
- An internet connection to download NuGet packages on the first build.

After installing the SDK, restart VS Code and check the installation in the integrated terminal:

```powershell
dotnet --list-sdks
```

The output must include an SDK version `10.0.x`.

## Build and Run

1. Open the project folder or `T.code-workspace` in VS Code.
2. Open a terminal in the project folder via **Terminal → New Terminal**.
3. Build the solution:

   ```powershell
   dotnet build T.slnx
   ```

   The command automatically restores NuGet dependencies as needed and builds the projects in the solution.

4. Run the application:

   ```powershell
   dotnet run --project T.UI/T.UI.csproj
   ```

For a Release build, run `dotnet build T.slnx -c Release`. With C# Dev Kit, you can also start the application through **Run and Debug**.

## Publish the Windows Application

To publish a self-contained Windows x64 version:

Run `build-windows.bat` from the project folder. It uses the numeric version from the nearest reachable Git release tag (for example, `v1.6.0-beta` becomes `1.6.0`), matching the release workflow. This version identifies the base tag, not an official release build. The About dialog also shows the local Git branch (or `detached` for a detached HEAD). If no matching tag is available, pass a version explicitly:

```powershell
.\build-windows.bat 1.6.0
```

Alternatively, publish directly with an explicit version:

```powershell
dotnet publish T.UI/T.UI.csproj -c Release -r win-x64 --self-contained -p:Version=1.6.0 -o publish
```

The application will be available at `publish/T.exe`. When distributing it, include the entire `publish` folder. Adjust the runtime identifier for other target platforms.

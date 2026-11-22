@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found. Install the .NET 10 SDK and restart the terminal.
    exit /b 1
)

set "T_PUBLISH_EXE=%~dp0publish\T.exe"
powershell -NoProfile -NonInteractive -Command "try { $target = [IO.Path]::GetFullPath($env:T_PUBLISH_EXE); $running = Get-Process -Name 'T' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [string]::Equals($_.Path, $target, [StringComparison]::OrdinalIgnoreCase) }; if ($running) { exit 1 }; exit 0 } catch { exit 2 }"
if errorlevel 2 (
    echo ERROR: Could not check whether the published application is running.
    exit /b 1
)
if errorlevel 1 (
    echo ERROR: The application at "%T_PUBLISH_EXE%" is running.
    echo Please close it before building and run this script again.
    exit /b 1
)

echo Publishing the Windows x64 application...
dotnet publish "T.UI\T.UI.csproj" -c Release -r win-x64 --self-contained -o "%~dp0publish"
if errorlevel 1 (
    echo ERROR: Publishing failed. Check the messages above.
    exit /b 1
)

echo Published successfully: %~dp0publish\T.exe
exit /b 0

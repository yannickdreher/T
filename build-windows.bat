@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found. Install the .NET 10 SDK and restart the terminal.
    exit /b 1
)

rem Match the release workflow: use the numeric part of the nearest reachable tag.
rem An explicit version can be supplied as the first argument.
set "T_BUILD_VERSION_INPUT=%~1"
set "T_BUILD_VERSION="
for /f "delims=" %%V in ('powershell -NoProfile -NonInteractive -Command "$v = $env:T_BUILD_VERSION_INPUT; if (-not $v) { $v = git describe --tags --abbrev=0 --match 'v[0-9]*'; if ($LASTEXITCODE -ne 0) { exit 1 }; if ($v -match '^v([0-9]+\.[0-9]+\.[0-9]+)(-.+)?$') { $v = $Matches[1] } else { exit 1 } }; if ($v -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { exit 1 }; Write-Output $v"') do set "T_BUILD_VERSION=%%V"
if not defined T_BUILD_VERSION (
    echo ERROR: No valid release tag found or version argument is invalid.
    echo Usage: build-windows.bat [major.minor.patch]
    exit /b 1
)

rem Record the local branch only for developer builds made with this script.
set "T_BUILD_BRANCH=detached"
for /f "delims=" %%B in ('git symbolic-ref --quiet --short HEAD 2^>nul') do set "T_BUILD_BRANCH=%%B"
powershell -NoProfile -NonInteractive -Command "if ($env:T_BUILD_BRANCH -notmatch '^[A-Za-z0-9_./-]+$') { exit 1 }"
if errorlevel 1 (
    echo ERROR: The branch name contains unsupported characters.
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

echo Publishing Windows x64 version %T_BUILD_VERSION% from %T_BUILD_BRANCH%...
dotnet publish "T.UI\T.UI.csproj" -c Release -r win-x64 --self-contained -p:Version=%T_BUILD_VERSION% -p:BuildBranch=%T_BUILD_BRANCH% -o "%~dp0publish"
if errorlevel 1 (
    echo ERROR: Publishing failed. Check the messages above.
    exit /b 1
)

echo Published successfully: %~dp0publish\T.exe
exit /b 0

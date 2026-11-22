@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found. Install the .NET 10 SDK and restart the terminal.
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

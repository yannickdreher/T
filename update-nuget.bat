@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found. Install the .NET 10 SDK and restart the terminal.
    exit /b 1
)

echo Updating packages with known vulnerabilities...
dotnet package update --project "T.slnx" --vulnerable
if errorlevel 1 goto :error

echo Restoring packages...
dotnet restore "T.slnx"
if errorlevel 1 goto :error

echo Building the solution...
dotnet build "T.slnx" --no-restore
if errorlevel 1 goto :error

echo Update and build completed. Review warnings and test the application.
exit /b 0

:error
echo ERROR: Update, restore, or build failed. Project files may already have changed.
exit /b 1

<#
.SYNOPSIS
    Lints (default) or formats the code base.
.DESCRIPTION
    C#   : dotnet format (whitespace, code style and analyzers configured in .editorconfig)
    XAML : XAML Styler with Settings.XamlStyler (all *.axaml files)
    Run with -Fix to apply the formatting instead of only checking it.
#>
param([switch]$Fix)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet tool restore | Out-Null

$failed = $false

$xaml = (Get-ChildItem -Recurse -Filter *.axaml | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }).FullName -join ","
if ($Fix) {
    dotnet xstyler -f $xaml -i -c Settings.XamlStyler -l Minimal
} else {
    dotnet xstyler -f $xaml -i -c Settings.XamlStyler -p -l Minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "XAML is not formatted - run ./lint.ps1 -Fix" -ForegroundColor Red; $failed = $true }
}

if ($Fix) {
    dotnet format T.slnx
} else {
    dotnet format T.slnx --verify-no-changes
    if ($LASTEXITCODE -ne 0) { Write-Host "C# is not formatted - run ./lint.ps1 -Fix" -ForegroundColor Red; $failed = $true }
}

if ($failed) { exit 1 }
Write-Host "Lint passed" -ForegroundColor Green

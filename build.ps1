<#
.SYNOPSIS
    Builds the solution.
#>
$ErrorActionPreference = "Stop"
dotnet build (Join-Path $PSScriptRoot "T.slnx")
exit $LASTEXITCODE

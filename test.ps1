<#
.SYNOPSIS
    Runs the tests with a private temporary directory on Windows.
#>
$ErrorActionPreference = "Stop"
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$testTemp = Join-Path ([System.IO.Path]::GetTempPath()) ("T.Tests-" + [guid]::NewGuid().ToString("N"))
$testExitCode = 1

try {
    $acl = [System.Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $userSid, "FullControl", "ContainerInherit, ObjectInherit", "None", "Allow"
    )
    $acl.AddAccessRule($rule)
    [System.IO.FileSystemAclExtensions]::Create([System.IO.DirectoryInfo]::new($testTemp), $acl) | Out-Null

    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    dotnet test --project (Join-Path $PSScriptRoot "T.Tests/T.Tests.csproj")
    $testExitCode = $LASTEXITCODE
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    if (Test-Path -LiteralPath $testTemp) {
        Remove-Item -LiteralPath $testTemp -Recurse -Force
    }
}

exit $testExitCode

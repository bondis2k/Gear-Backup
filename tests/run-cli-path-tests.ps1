# Tests installer PATH handling without changing the real user PATH or installing the app.
#requires -Version 7.0
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $IsccPath) { $IsccPath = Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe' }
$testOutput = Join-Path $PSScriptRoot '.build\cli-path'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$testKey = 'Software\GearBackup\PathTests\' + [Guid]::NewGuid().ToString('N')
$resultPath = Join-Path $testOutput 'result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
& $IsccPath "/DPathTestRoot=$testKey" "/DTestOutput=$testOutput" "/DTestResult=$resultPath" (Join-Path $PSScriptRoot 'cli-path-harness.iss')
if ($LASTEXITCODE -ne 0) { throw 'PATH test harness compilation failed' }
try {
    $process = Start-Process -FilePath (Join-Path $testOutput 'GearBackup.PathTests.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -WindowStyle Hidden -Wait -PassThru
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "PATH test harness did not produce a result (exit $($process.ExitCode))" }
    $result = Get-Content -LiteralPath $resultPath -Raw
    if (-not $result.StartsWith('PASS:')) { throw $result }
    Write-Output $result
} finally {
    $registryPath = 'HKCU:\' + $testKey
    if (Test-Path -LiteralPath $registryPath) { Remove-Item -LiteralPath $registryPath -Recurse }
}

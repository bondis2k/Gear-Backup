$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $testBuild -Force | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'Notifications.cs' | ForEach-Object FullName)
$guiSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'gui') -Filter '*.cs' -Recurse | ForEach-Object FullName)
$runnerPath = Join-Path $testBuild 'M8Backup.GuiTests.exe'
& $compilerPath /nologo /target:exe /main:GuiTestRunner "/out:$runnerPath" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/resource:$projectRoot\assets\Backup-256.png,Backup-256.png" "/resource:$projectRoot\assets\M8Backup-256.png,M8Backup-256.png" "/resource:$projectRoot\assets\OP1Field-256.png,OP1Field-256.png" "/resource:$projectRoot\assets\OPXY-256.png,OPXY-256.png" $testSources $guiSources (Join-Path $PSScriptRoot 'TestRunner.cs') (Join-Path $PSScriptRoot 'HelperTests.cs') (Join-Path $PSScriptRoot 'GuiTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $projectRoot
try { & $runnerPath; $testResult = $LASTEXITCODE }
finally { Pop-Location }
exit $testResult

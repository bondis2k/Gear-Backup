# Coverage of the core by the core test suite, measured with OpenCover (https://github.com/OpenCover/opencover). The core and
# the test program are built with debug information into tests\.build\coverage, the tests run under OpenCover, and the result
# is summarised per source file and for the whole core. OpenCover is not in the repository: unzip opencover.<version>.zip
# from its GitHub release into .tools\OpenCover (the folder is ignored by Git).
#
#   pwsh -File tests\run-coverage.ps1                # run everything
#   pwsh -File tests\run-coverage.ps1 -Filter zip    # only tests whose name contains the text
#
# Writes tests\.build\coverage\coverage.xml (full detail) and coverage-summary.csv. Coverage counts the sequence points
# (statements) the core test suite reaches, plus branches; the GUI tests are not included.
param([string[]]$Filter = @(), [int]$Minimum = 0)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot '.build\coverage'
Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null
. (Join-Path $projectRoot 'build\csharp-compiler.ps1')
$compilerPath = Get-CSharpCompiler

$opencover = Join-Path $projectRoot '.tools\OpenCover\OpenCover.Console.exe'
if (-not (Test-Path -LiteralPath $opencover)) { throw 'OpenCover is missing. Unzip opencover.<version>.zip from https://github.com/OpenCover/opencover/releases into .tools\OpenCover.' }

$sourceRoot = Join-Path $projectRoot 'src'
$cliSources = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Cli') -Filter '*.cs' | ForEach-Object FullName)
$coreSources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -Recurse | Where-Object { $cliSources -notcontains $_.FullName } | ForEach-Object FullName)
$references = @('/r:System.Management.dll', '/r:Microsoft.VisualBasic.dll', '/r:System.IO.Compression.dll', '/r:System.Web.Extensions.dll')
$core = Join-Path $out 'GearBackup.Core.dll'
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /debug:full /target:library "/out:$core" $references $coreSources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$testSources = @($cliSources | Where-Object { (Split-Path $_ -Leaf) -ne 'Notifications.cs' })
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object Name -notlike 'Gui*' | ForEach-Object FullName)
$runner = Join-Path $out 'GearBackup.Tests.exe'
& $compilerPath /nologo "/langversion:$CSharpLanguageVersion" /debug:full /main:TestRunner "/out:$runner" "/r:$core" $references $testSources $testFiles
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$targetArguments = ''; foreach ($text in $Filter) { $targetArguments += " --filter `"$text`"" }
$xml = Join-Path $out 'coverage.xml'
Push-Location $projectRoot
try {
    & $opencover -register:user "-target:$runner" "-targetargs:$targetArguments" "-output:$xml" '-filter:+[GearBackup.Core]*' -mergebyhash -returntargetcode | Tee-Object -FilePath (Join-Path $out 'test-output.log')
    $testResult = $LASTEXITCODE
} finally { Pop-Location }
if (-not (Test-Path -LiteralPath $xml)) { throw 'OpenCover produced no result.' }

# Sequence points (statements) per source file, covered when visited at least once.
[xml]$document = Get-Content -LiteralPath $xml -Raw
$files = @{}
foreach ($module in $document.CoverageSession.Modules.Module) {
    $names = @{}
    foreach ($file in $module.Files.File) { $names[$file.uid] = $file.fullPath }
    foreach ($class in $module.Classes.Class) {
        foreach ($method in $class.Methods.Method) {
            foreach ($point in $method.SequencePoints.SequencePoint) {
                $path = $names[$point.fileid]
                if (-not $path) { continue }
                if (-not $files.ContainsKey($path)) { $files[$path] = @{ Covered = 0; Total = 0 } }
                $files[$path].Total++
                if ([int]$point.vc -gt 0) { $files[$path].Covered++ }
            }
        }
    }
}
$rows = foreach ($path in $files.Keys) {
    $item = $files[$path]
    [pscustomobject]@{ File = $path.Substring([Math]::Min($path.Length, $sourceRoot.Length + 1)); Covered = $item.Covered; Statements = $item.Total
        Percent = if ($item.Total) { [Math]::Round(100.0 * $item.Covered / $item.Total, 1) } else { 100 } }
}
$rows = @($rows | Sort-Object Percent, File)
$rows | Export-Csv -LiteralPath (Join-Path $out 'coverage-summary.csv') -NoTypeInformation
Write-Output 'Lowest covered files:'
$rows | Select-Object -First 12 | Format-Table -AutoSize | Out-String | Write-Output
$covered = ($rows | Measure-Object Covered -Sum).Sum; $total = ($rows | Measure-Object Statements -Sum).Sum
$percent = if ($total) { [Math]::Round(100.0 * $covered / $total, 1) } else { 0 }
$summary = $document.CoverageSession.Summary
Write-Output "Core statement coverage: $percent% ($covered of $total). Branch coverage: $($summary.branchCoverage)%. Full list: $out\coverage-summary.csv"
if ($testResult -ne 0) { Write-Warning 'Some tests failed; coverage reflects that run.'; exit $testResult }
if ($percent -lt $Minimum) { Write-Error "Coverage $percent% is below the required $Minimum%."; exit 1 }
exit 0

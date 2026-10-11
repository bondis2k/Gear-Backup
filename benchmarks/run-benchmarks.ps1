#requires -Version 7.0
param([ValidateRange(1, 20)][int]$Iterations = 3, [string]$Output = 'benchmarks/results/latest.csv', [string]$SourceRef)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $projectRoot 'build/csharp-compiler.ps1')
$compiler = Get-CSharpCompiler
$build = Join-Path $projectRoot 'tests/.build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$sources = @(Get-ChildItem (Join-Path $projectRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'Notifications.cs' | ForEach-Object FullName)
# Reuse the test notification stub, so benchmarks never send desktop notifications.
$sources += @(Get-ChildItem (Join-Path $projectRoot 'tests') -Filter '*.cs' | Where-Object Name -ne 'GuiTests.cs' | ForEach-Object FullName)
if ($SourceRef) {
    # Compile a Git revision into an isolated folder without changing the working tree.
    $commit = & git -C $projectRoot rev-parse --verify --end-of-options "$SourceRef^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'Unknown benchmark source revision.' }
    $snapshot = Join-Path $build ('benchmark-source-' + [guid]::NewGuid().ToString('N'))
    $sources = @()
    $files = & git -C $projectRoot ls-tree -r --name-only $commit -- src tests
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate revision sources.' }
    foreach ($file in $files | Where-Object { $_ -like '*.cs' -and $_ -notmatch '(Notifications|GuiTests)\.cs$' }) {
        $destination = Join-Path $snapshot $file
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        $content = & git -C $projectRoot show "${commit}:$file"
        if ($LASTEXITCODE -ne 0) { throw "Could not read $file from revision." }
        Set-Content -LiteralPath $destination -Value $content -Encoding utf8
        $sources += $destination
    }
}
$sources += Join-Path $PSScriptRoot 'PerformanceBenchmarks.cs'
$runner = Join-Path $build 'GearBackup.Benchmarks.exe'
& $compiler /nologo "/langversion:$CSharpLanguageVersion" /main:PerformanceBenchmarks "/out:$runner" /r:System.Management.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Benchmark compilation failed.' }
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $projectRoot $Output }
New-Item -ItemType Directory -Path (Split-Path $Output -Parent) -Force | Out-Null
@{
    TimestampUtc = [datetime]::UtcNow.ToString('O')
    Source = $(if ($SourceRef) { $commit } else { 'Working tree' })
    Runtime = [Environment]::Version.ToString()
    OS = [Environment]::OSVersion.VersionString
    Processor = $env:PROCESSOR_IDENTIFIER
    Iterations = $Iterations
} | ConvertTo-Json | Set-Content -LiteralPath "$Output.environment.json" -Encoding utf8
$fixtureRoot = Join-Path $projectRoot ('.test-runs/benchmark-' + [guid]::NewGuid().ToString('N'))
& $runner $fixtureRoot $Iterations $Output
if ($LASTEXITCODE -ne 0) { throw "Benchmark failed; fixtures retained at $fixtureRoot" }
# Only delete the newly generated fixture, after validating its resolved location.
$resolved = [IO.Path]::GetFullPath($fixtureRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot '.test-runs')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture location.' }
Remove-Item -LiteralPath $resolved -Recurse -Force

# Exercise the coverage runner's real reporting/exit stage with a synthetic OpenCover report.
# No profiler or deliberately broken production test is needed.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'run-coverage.ps1') -Raw
$marker = '# Sequence points (statements) per source file'
$start = $source.IndexOf($marker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Cannot locate the coverage reporting stage.' }
$fixture = Join-Path $projectRoot ('.test-runs\coverage-exit-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$driver = Join-Path $fixture 'report.ps1'
$xmlPath = Join-Path $fixture 'coverage.xml'
$sampleFile = [Security.SecurityElement]::Escape((Join-Path $fixture 'src\sample.cs'))
$xml = @"
<CoverageSession><Summary branchCoverage="50"/><Modules><Module>
<Files><File uid="1" fullPath="$sampleFile"/></Files>
<Classes><Class><Methods><Method><SequencePoints>
<SequencePoint fileid="1" vc="1"/><SequencePoint fileid="1" vc="0"/>
</SequencePoints></Method></Methods></Class></Classes>
</Module></Modules></CoverageSession>
"@
Set-Content -LiteralPath $xmlPath -Value $xml -Encoding utf8
$setup = @'
param([int]$TestResult, [int]$Minimum)
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot
$sourceRoot = Join-Path $PSScriptRoot 'src'
$xml = Join-Path $PSScriptRoot 'coverage.xml'
'@
Set-Content -LiteralPath $driver -Value ($setup + [Environment]::NewLine + $source.Substring($start)) -Encoding utf8
$shell = (Get-Process -Id $PID).Path
$cases = @(
    @{ Result = 0; Minimum = 40; Expected = 0; Name = 'successful tests and sufficient coverage' },
    @{ Result = 7; Minimum = 40; Expected = 7; Name = 'failed tests despite sufficient coverage' },
    @{ Result = 0; Minimum = 60; Expected = 1; Name = 'successful tests but insufficient coverage' },
    @{ Result = 7; Minimum = 60; Expected = 7; Name = 'failed tests and insufficient coverage' }
)
foreach ($case in $cases) {
    $summary = Join-Path $fixture 'coverage-summary.csv'
    if (Test-Path -LiteralPath $summary) { Remove-Item -LiteralPath $summary }
    # Capture expected stderr directly; Windows PowerShell otherwise turns native stderr into terminating errors.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $shell
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $driver + '" -TestResult ' + $case.Result + ' -Minimum ' + $case.Minimum
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit(); $actual = $process.ExitCode
        $output = $stdout.Result + $stderr.Result
    } finally { $process.Dispose() }
    if ($actual -ne $case.Expected) { throw "$($case.Name): expected exit $($case.Expected), got $actual. $($output -join [Environment]::NewLine)" }
    if (-not (Test-Path -LiteralPath (Join-Path $fixture 'coverage-summary.csv'))) { throw 'The coverage report was not preserved.' }
    Write-Output "PASS: $($case.Name) (exit $actual)."
}
# Delete only this test's generated files; leave failed runs available for diagnosis.
foreach ($name in @('report.ps1', 'coverage.xml', 'coverage-summary.csv')) { Remove-Item -LiteralPath (Join-Path $fixture $name) }
Remove-Item -LiteralPath $fixture
exit 0

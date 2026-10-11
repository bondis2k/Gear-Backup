#requires -Version 7.0
param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
$ErrorActionPreference = 'Stop'
function Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'No samples.' }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
$baseline = Import-Csv -LiteralPath $Before
$updated = Import-Csv -LiteralPath $After
foreach ($group in $baseline | Group-Object Scenario) {
    $matches = @($updated | Where-Object Scenario -eq $group.Name)
    if (-not $matches.Count) { throw "Missing scenario: $($group.Name)" }
    $beforeMs = Median ($group.Group | ForEach-Object { [double]::Parse($_.Milliseconds, [cultureinfo]::InvariantCulture) })
    $afterMs = Median ($matches | ForEach-Object { [double]::Parse($_.Milliseconds, [cultureinfo]::InvariantCulture) })
    [pscustomobject]@{
        Scenario = $group.Name
        BeforeMs = [math]::Round($beforeMs, 2)
        AfterMs = [math]::Round($afterMs, 2)
        Speedup = [math]::Round($beforeMs / $afterMs, 2)
        BeforeAllocatedMB = [math]::Round((Median ($group.Group | ForEach-Object { [long]$_.AllocatedBytes })) / 1MB, 2)
        AfterAllocatedMB = [math]::Round((Median ($matches | ForEach-Object { [long]$_.AllocatedBytes })) / 1MB, 2)
    }
}

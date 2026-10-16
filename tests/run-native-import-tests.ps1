# Exercise the import policy against the built DLL and deliberately altered copies.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$checker = Join-Path $projectRoot 'build\check-native-imports.ps1'
$dll = Join-Path $projectRoot 'native\m8-reader\target\release\m8_reader.dll'
& $checker -Path $dll | Out-Null
$fixtureDirectory = Join-Path $projectRoot ('.test-runs\native-imports-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
$fixture = Join-Path $fixtureDirectory 'reader.dll'
function Assert-Rejected([byte[]]$Bytes, [string]$Expected) {
    [IO.File]::WriteAllBytes($fixture, $Bytes)
    $rejection = $null
    try { & $checker -Path $fixture | Out-Null } catch { $rejection = $_.Exception.Message }
    if (-not $rejection -or $rejection -notlike $Expected) { throw "Expected '$Expected', received '$rejection'." }
}
try {
    $original = [IO.File]::ReadAllBytes($dll)
    $modified = [byte[]]$original.Clone()
    $text = [Text.Encoding]::ASCII.GetString($modified)
    $replacement = [Text.Encoding]::ASCII.GetBytes('plantedd.dll') # same length as kernel32.dll
    $at = 0; $changed = 0
    while (($at = $text.IndexOf('kernel32.dll', $at, [StringComparison]::OrdinalIgnoreCase)) -ge 0) {
        [Array]::Copy($replacement, 0, $modified, $at, $replacement.Length)
        $at += $replacement.Length; $changed++
    }
    if ($changed -eq 0) { throw 'Could not find a dependency to mutate.' }
    Assert-Rejected $modified "Unexpected native dependency 'plantedd.dll'*"
    $modified = [byte[]]$original.Clone()
    $optional = [BitConverter]::ToInt32($modified, 0x3c) + 24
    [Array]::Copy([BitConverter]::GetBytes([uint32]1), 0, $modified, $optional + 112 + 13 * 8, 4)
    Assert-Rejected $modified 'Unexpected delay-loaded dependency*'
    Assert-Rejected ([byte[]]@(0, 1, 2)) 'Invalid PE import table*'
    Write-Output 'PASS: native import policy (valid reader, planted dependency, delay imports, truncated PE).'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
    Remove-Item -LiteralPath $fixtureDirectory
}

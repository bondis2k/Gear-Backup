# Fails when a catch block swallows an error without saying why.
#  - An empty catch is allowed only when a comment on its line, or on the line above it (for a run of catches), explains
#    what happens instead.
#  - A catch-all without a variable (`catch (Exception)`) must filter with `when`, or rethrow.
# See docs/ARCHITECTURE.md.
#
#   pwsh -File build\check-catches.ps1
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$problems = New-Object System.Collections.Generic.List[string]
$emptyCatch = 'catch\s*(\([^)]*\))?\s*\{\s*(/\*.*?\*/)?\s*\}'
$bareCatchAll = 'catch\s*(\(\s*Exception\s*\))?\s*(\{|$)'
foreach ($folder in 'src', 'gui') {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Filter '*.cs' -Recurse) {
        $name = $file.FullName.Substring($projectRoot.Length + 1)
        $lines = Get-Content -LiteralPath $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match $bareCatchAll -and $line -notmatch 'catch\s*\(\s*Exception\s+\w+\s*\)' -and $line -notmatch '^\s*//') {
                $window = ($lines[$i..([Math]::Min($i + 4, $lines.Count - 1))]) -join ' '
                if ($window -notmatch '\bthrow\s*;') { $problems.Add("${name}:$($i + 1): catch-all without a filter; use when (ExpectedFailure.Is(ex))") }
            }
            if ($line -notmatch $emptyCatch) { continue }
            $explained = $line -match '//|/\*'
            # A run of catches shares the comment above the first one.
            for ($j = $i - 1; -not $explained -and $j -ge 0; $j--) {
                if ($lines[$j] -match '^\s*(//|catch\b)') { if ($lines[$j] -match '^\s*//') { $explained = $true } } else { break }
            }
            if (-not $explained) { $problems.Add("${name}:$($i + 1): empty catch without a comment") }
        }
    }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Output 'Catches OK: every empty catch says why, and no catch-all hides bugs.'
exit 0

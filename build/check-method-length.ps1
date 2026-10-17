# Fails when a method or constructor in src or gui is longer than the limit (signature line to closing brace).
# Long methods hide several steps in one body; split them into named helpers.
# See docs/ARCHITECTURE.md.
#
#   pwsh -File build\check-method-length.ps1
param([int]$Limit = 60)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$problems = New-Object System.Collections.Generic.List[string]
# A member declared at class level (8 spaces) with its opening brace alone on the next line, as the code is written throughout.
$signature = '^\s{8}(static |internal |public |private |async |override |protected )*[\w<>\[\],\. ]+\s+\w+\(.*\)\s*(:\s*base\(.*\))?\s*$'
foreach ($folder in 'src', 'gui') {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Filter '*.cs' -Recurse) {
        $name = $file.FullName.Substring($projectRoot.Length + 1)
        $lines = Get-Content -LiteralPath $file.FullName
        $start = -1
        for ($i = 0; $i -lt $lines.Count - 1; $i++) {
            if ($lines[$i] -match $signature -and $lines[$i + 1] -match '^\s{8}\{') { $start = $i }
            elseif ($start -ge 0 -and $lines[$i] -match '^\s{8}\}$') {
                if ($i - $start -gt $Limit) { $problems.Add("${name}:$($start + 1): $($i - $start) lines (limit $Limit); split it into helpers") }
                $start = -1
            }
        }
    }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Output "Method length OK: no method is longer than $Limit lines."
exit 0

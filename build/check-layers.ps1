# Checks that the core (src) keeps its layers: a file may use types from its own layer and the layers below it, never
# from a layer above. See docs/ARCHITECTURE.md. The structure is not clean yet, so today's known violations are listed in
# build\layer-baseline.txt and only NEW ones fail the check; fix one and delete its line (or run -UpdateBaseline).
#
#   pwsh -File build\check-layers.ps1                 # check (what CI runs)
#   pwsh -File build\check-layers.ps1 -UpdateBaseline # rewrite the baseline from the current code
#
# This is a text scan, not a compiler: a file "uses" a type when it mentions the type's name, so a common word that is
# also a type name can add a false entry. Such an entry is harmless in the baseline.
param([switch]$UpdateBaseline)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $projectRoot 'src'
$baselinePath = Join-Path $PSScriptRoot 'layer-baseline.txt'

# Lower layers first. A file's layer is set by its top-level folder under src.
$layers = [ordered]@{
    'Foundation' = 0   # paths, records, files, the operation context and its front-end interfaces
    'Settings'   = 1   # configuration and profile naming
    'Devices'    = 1   # what each device is, and how to talk to it (USB, MTP, Fuji, M8 songs)
    'Catalog'    = 2   # what a backup contains: snapshots, checksums, the SQLite catalog, integrity checks
    'Backup'     = 3   # backing up
    'Restore'    = 3   # restoring
    'Zip'        = 3   # ZIP archives
    'Workflow'   = 4   # steps shared by the CLI and the GUI
    'Cli'        = 5   # the command-line front end
}

# Comments and string/char literals removed, so words in them are not mistaken for types.
$noise = [regex]'@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])''|//[^\r\n]*|/\*[\s\S]*?\*/'
function Clean([string]$text) { $noise.Replace($text, ' ') }

$files = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -Recurse)
$info = @{}
$declaredIn = @{}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
    $top = $relative.Split('/')[0]
    if (-not $layers.Contains($top)) { throw "src/$relative is not in a known layer folder ($($layers.Keys -join ', '))." }
    $text = Clean (Get-Content -LiteralPath $file.FullName -Raw)
    $info[$relative] = @{ Layer = $layers[$top]; Text = $text }
    foreach ($m in [regex]::Matches($text, '\b(?:class|struct|enum|interface)\s+(\w+)')) {
        if (-not $declaredIn.ContainsKey($m.Groups[1].Value)) { $declaredIn[$m.Groups[1].Value] = $relative }
    }
}

$violations = [System.Collections.Generic.SortedSet[string]]::new()
foreach ($relative in $info.Keys) {
    $layer = $info[$relative].Layer
    $text = $info[$relative].Text
    foreach ($type in $declaredIn.Keys) {
        $target = $declaredIn[$type]
        if ($target -eq $relative -or $info[$target].Layer -le $layer) { continue }
        if ($text -match "\b$type\b") { [void]$violations.Add("$relative -> $target") }
    }
}

if ($UpdateBaseline) {
    Set-Content -LiteralPath $baselinePath -Value @($violations) -Encoding ascii
    Write-Output "Baseline written: $($violations.Count) known violations."
    exit 0
}

$known = [System.Collections.Generic.HashSet[string]]::new()
if (Test-Path -LiteralPath $baselinePath) { foreach ($line in Get-Content -LiteralPath $baselinePath) { if ($line.Trim()) { [void]$known.Add($line.Trim()) } } }
$new = @($violations | Where-Object { -not $known.Contains($_) })
$fixed = @($known | Where-Object { -not $violations.Contains($_) } | Sort-Object)
if ($fixed.Count -gt 0) {
    Write-Output "$($fixed.Count) baseline entries are no longer violations; remove them (or run -UpdateBaseline):"
    $fixed | ForEach-Object { Write-Output "  $_" }
}
if ($new.Count -gt 0) {
    Write-Output "$($new.Count) new layer violation(s). A file may only use its own layer and the layers below it:"
    $new | ForEach-Object { Write-Output "  $_" }
    Write-Output 'Move the code to the right layer, or pass the value in instead. See docs/ARCHITECTURE.md.'
    exit 1
}
Write-Output "Layers OK: $($violations.Count) known violations remain in the baseline, no new ones."
exit 0

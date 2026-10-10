#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# Use PowerShell's Markdown renderer to provide an offline manual that opens in any browser.
$manualBody = (ConvertFrom-Markdown -LiteralPath (Join-Path $projectRoot 'MANUAL.md')).Html
$manualHtml = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Gear Backup Manual</title>
<style>
body{margin:0;background:#f3f6f6;color:#1d2d36;font:16px/1.65 "Segoe UI",sans-serif}
main{max-width:1080px;margin:32px auto;padding:32px 40px;background:white;border-radius:18px}
h1,h2,h3{color:#00544c;line-height:1.3}h2{margin-top:2em}a{color:#00756a}
img{display:block;max-width:100%;height:auto;margin:24px auto;border-radius:12px}
pre{padding:16px;background:#f3f6f6;border-radius:8px;overflow:auto}code{font-family:Consolas,monospace}
table{border-collapse:collapse;width:100%}td,th{padding:10px;text-align:left;border:1px solid #dce7e5}
th{background:#e7f5f2}blockquote{border-left:4px solid #f2546e;padding-left:16px}
@media(max-width:700px){main{margin:0;padding:20px;border-radius:0}table{display:block;overflow:auto}}
</style></head><body><main>$manualBody</main></body></html>
"@
Set-Content -LiteralPath (Join-Path $OutputDirectory 'Manual.html') -Value $manualHtml -Encoding utf8

# Keep illustrations usable beside executables built into another folder.
if ([IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') -ne [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')) {
    $imagePath = Join-Path $OutputDirectory 'assets\manual'
    New-Item -ItemType Directory -Path $imagePath -Force | Out-Null
    Copy-Item -Path (Join-Path $projectRoot 'assets\manual\*.png') -Destination $imagePath
}

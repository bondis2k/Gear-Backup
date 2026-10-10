#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# Use PowerShell's Markdown renderer to provide an offline manual that opens in any browser.
$manualBody = (ConvertFrom-Markdown -LiteralPath (Join-Path $projectRoot 'MANUAL.md')).Html
# Share the Markdown index with every HTML theme.
$contentsSection = [regex]::Match($manualBody, '(?s)<h2 id="contents">Contents</h2>\s*(.*?)\s*(?=<h2 )')
if (-not $contentsSection.Success) { throw 'The manual Contents section is missing.' }
$contents = $contentsSection.Groups[1].Value
$manualBody = $manualBody.Remove($contentsSection.Index, $contentsSection.Length)
$modernStyles = @'
body{font:16px/1.6 "Segoe UI",Arial,sans-serif;background:var(--desktop);color:var(--ink)}
.help-window{background:var(--background);border:1px solid var(--border);border-radius:10px;box-shadow:0 6px 24px #0002;overflow:hidden}
.title-bar{background:var(--accent);color:var(--title-ink);font:bold 15px/1.5 "Segoe UI",Arial,sans-serif;padding:12px 20px}
.menu-bar,.status-bar{font-family:"Segoe UI",Arial,sans-serif}.menu-bar a,.menu-bar a:visited{color:var(--ink)}
main{background:var(--surface);border:0}h1,h2,h3{font-family:"Segoe UI",Arial,sans-serif;color:var(--heading)}
h1{border-bottom:1px solid var(--border)}h2{background:var(--soft);border:0;border-radius:5px}
a,.contents summary{color:var(--link)}a:visited{color:var(--visited)}a:focus-visible,summary:focus-visible{outline-color:var(--link)}
img,pre,td,th,blockquote,.contents,.status-bar{border:1px solid var(--border)}
pre,td,blockquote,.contents{background:var(--surface)}img,th{background:var(--background)}
p code,li code,td code{background:var(--soft)}code{font-family:Consolas,"Courier New",monospace}
.contents summary,th{font-family:"Segoe UI",Arial,sans-serif}.status-bar{border-radius:4px}
'@
$themeStyles = @{
    retro = ''
    original = ':root{--desktop:#f3f5f7;--background:#f3f5f7;--surface:#fff;--ink:#1c2a33;--border:#dde3e8;--accent:#00756a;--heading:#005e55;--soft:#e2f2ef;--link:#00756a;--visited:#635286;--title-ink:#fff;color-scheme:light}'
    'original-dark' = ':root{--desktop:#13191e;--background:#1a2228;--surface:#202a31;--ink:#e8eef1;--border:#465660;--accent:#22a090;--heading:#6ddccb;--soft:#1f403d;--link:#6ddccb;--visited:#c5abe8;--title-ink:#13191e;color-scheme:dark}'
}
foreach ($theme in @('retro', 'original', 'original-dark')) {
    $themeCss = $themeStyles[$theme]
    if ($theme -ne 'retro') { $themeCss += "`n$modernStyles" }
    $themedBody = $manualBody
    if ($theme -ne 'retro') {
        $themedBody = [regex]::Replace($manualBody, 'assets/manual/(gui-[a-z-]+)\.png', "assets/manual/`$1-$theme.png")
    }
    $manualHtml = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Gear Backup Manual</title>
<style>
*{box-sizing:border-box}
body{margin:0;padding:24px 16px;background:#008080;color:#000;font:17px/1.5 "Times New Roman",Times,serif}
.help-window{max-width:1040px;margin:0 auto;padding:4px;background:#c0c0c0;border:2px solid;border-color:#fff #404040 #404040 #fff;box-shadow:6px 6px 0 rgba(0,0,0,.35)}
.title-bar{padding:5px 9px;background:#000080;color:#fff;font:bold 14px/1.4 Tahoma,"MS Sans Serif",Arial,sans-serif;letter-spacing:.2px}
.menu-bar{display:flex;flex-wrap:wrap;gap:8px 22px;padding:8px 12px;font:14px/1.4 Tahoma,"MS Sans Serif",Arial,sans-serif}
.menu-bar a,.menu-bar a:visited{color:#000}
main{padding:28px 36px;background:#fffff0;border:2px solid;border-color:#808080 #fff #fff #808080;overflow-wrap:anywhere}
h1,h2,h3{color:#000080;font-family:Tahoma,"MS Sans Serif",Arial,sans-serif;line-height:1.3}
h1{margin:0 0 24px;font-size:28px;border-bottom:3px double #808080;padding-bottom:14px}
h2{margin-top:2em;font-size:22px;padding:8px 10px;background:#e0e0d8;border:1px solid #808080}
h3{margin-top:1.6em;font-size:18px}a{color:#0000ee;text-decoration:underline}a:visited{color:#551a8b}
a:focus-visible,summary:focus-visible{outline:2px dotted #000;outline-offset:3px}
img{display:block;max-width:100%;height:auto;margin:24px auto;border:1px solid #808080;background:#fff}
pre{padding:14px;background:#fff;border:2px solid;border-color:#808080 #fff #fff #808080;overflow:auto;overflow-wrap:normal;line-height:1.45}
code{font-family:"Courier New",Courier,monospace;font-size:.88em}p code,li code,td code{background:#e8e8e0;padding:1px 3px}
table{border-collapse:collapse;width:100%;margin:18px 0;font-size:16px}td,th{padding:9px;text-align:left;vertical-align:top;border:1px solid #808080}
th{background:#c0c0c0;font:bold 14px/1.5 Tahoma,Arial,sans-serif}td{background:#fff}
blockquote{margin:20px 0;padding:10px 18px;border:1px solid #808080;background:#fff}
.contents{margin:0 0 24px;padding:10px 14px;border:1px solid #808080;background:#fff}
.contents summary{cursor:pointer;color:#000080;font:bold 14px/1.5 Tahoma,Arial,sans-serif}
.contents>ul{columns:2;column-gap:28px;margin:14px 0 4px;padding-left:22px}.contents ul ul{columns:1;margin:4px 0;padding-left:20px}.contents li{break-inside:avoid;margin:4px 0}
.status-bar{display:flex;justify-content:space-between;gap:12px;margin-top:4px;padding:4px 10px;border:1px solid;border-color:#808080 #fff #fff #808080;font:12px/1.5 Tahoma,Arial,sans-serif}
@media(max-width:700px){body{padding:8px}.help-window{box-shadow:none}main{padding:20px 16px}h1{font-size:24px}h2{font-size:20px}table{display:block;overflow:auto}.contents ul{columns:1}.menu-bar{gap:8px 16px}}
$themeCss
@media print{body{padding:0;background:#fff;font-size:12pt}.help-window{max-width:none;border:0;padding:0;box-shadow:none}.title-bar,.menu-bar,.contents,.status-bar{display:none}main{padding:0;border:0;background:#fff}h1,h2,h3{color:#000}h2{background:#fff;break-after:avoid}img{max-height:75vh;object-fit:contain}a,a:visited{color:#000}pre,td,th,blockquote,p code,li code,td code{background:#fff;color:#000;border-color:#aaa}}
</style></head><body><div class="help-window">
<header class="title-bar">Gear Backup Help — User's Manual</header>
<nav class="menu-bar" aria-label="Manual sections"><a href="#contents">Contents</a><a href="#getting-started">Getting started</a><a href="#graphical-interface">Using the GUI</a><a href="#full-and-incremental-backup">Backup</a><a href="#restore">Restore</a><a href="#all-ini-settings">Settings</a></nav>
<nav class="menu-bar" aria-label="Color themes"><span>Color theme:</span><a href="Manual-original.html">Original</a><a href="Manual-original-dark.html">Original (dark)</a><a href="Manual-retro.html">Retro</a></nav>
<main><details class="contents" id="contents" open><summary>Contents — click to collapse or expand</summary>$contents</details>$themedBody</main>
<footer class="status-bar"><span>Gear Backup · Offline reference</span><a href="#gear-backup-manual">Back to top</a></footer>
</div></body></html>
"@
    Set-Content -LiteralPath (Join-Path $OutputDirectory "Manual-$theme.html") -Value $manualHtml -Encoding utf8
    if ($theme -eq 'retro') { Set-Content -LiteralPath (Join-Path $OutputDirectory 'Manual.html') -Value $manualHtml -Encoding utf8 }
}

# Keep illustrations usable beside executables built into another folder.
if ([IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') -ne [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')) {
    $imagePath = Join-Path $OutputDirectory 'assets\manual'
    New-Item -ItemType Directory -Path $imagePath -Force | Out-Null
    Copy-Item -Path (Join-Path $projectRoot 'assets\manual\*.png') -Destination $imagePath
}

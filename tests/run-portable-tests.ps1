# Exercises portable extraction, updates and relocation without using any device.
#requires -Version 7.0
param([string]$PortablePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $PortablePath) {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $projectRoot 'dist\installer-payload\GearBackup.exe')).ProductVersion
    $PortablePath = Join-Path $projectRoot "dist\GearBackup-Portable-$version.exe"
}
$PortablePath = (Resolve-Path -LiteralPath $PortablePath).Path
$testRoot = Join-Path $projectRoot ('.test-runs\portable-' + [Guid]::NewGuid().ToString('N'))
$incoming = Join-Path $testRoot 'package with spaces'
$applicationPath = Join-Path $incoming 'Gear Backup'
$movedPath = Join-Path $testRoot 'moved portable app'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$regularKey = Join-Path $registration '{1B17F726-12B1-4C2A-B26A-68E307B7EC49}_is1'
$portableKey = Join-Path $registration 'GearBackup.Portable_is1'
$assertions = 0
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:assertions++
}
function ExistingIntegration {
    $records = @()
    foreach ($key in @($regularKey, $portableKey)) {
        if (Test-Path $key) { $records += ($key + ':' + ((Get-ItemProperty $key | Select-Object DisplayName, DisplayVersion, InstallLocation, UninstallString | ConvertTo-Json -Compress))) }
    }
    foreach ($folder in @(([Environment]::GetFolderPath('Programs')), ([Environment]::GetFolderPath('DesktopDirectory')))) {
        foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -Filter '*Gear Backup*.lnk' -ErrorAction SilentlyContinue) {
            $records += ($file.FullName + ':' + (Get-FileHash -LiteralPath $file.FullName).Hash)
        }
    }
    return (($records | Sort-Object) -join "`n")
}
New-Item -ItemType Directory -Path $incoming -Force | Out-Null
$packageCopy = Join-Path $incoming (Split-Path $PortablePath -Leaf)
Copy-Item -LiteralPath $PortablePath -Destination $packageCopy
$originalIntegration = ExistingIntegration
function Extract([string]$logName) {
    # No /DIR: test the portable default beside the package, even with spaces in that path.
    $process = Start-Process -FilePath $packageCopy -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $testRoot $logName)`"" -WindowStyle Hidden -Wait -PassThru
    Assert ($process.ExitCode -eq 0) "Extraction failed: $($process.ExitCode). See $testRoot."
    Assert ((ExistingIntegration) -eq $originalIntegration) 'Portable extraction changed shortcuts or uninstall registration.'
    Assert (@(Get-ChildItem -LiteralPath $applicationPath -Filter 'unins*').Count -eq 0) 'Portable extraction created an uninstaller.'
}
Extract 'extract.log'
Assert (-not (Test-Path -LiteralPath (Join-Path $applicationPath 'GearBackup.installed'))) 'Portable extraction has an installed settings marker.'
$packageVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($PortablePath).ProductVersion.Trim()
foreach ($file in @('GearBackup.GUI.exe', 'GearBackup.exe', 'Manual.html', 'Manual-original.html', 'Manual-original-dark.html', 'Manual-windows.html', 'Manual-retro.html', 'MANUAL.md', 'README.md')) {
    Assert (Test-Path -LiteralPath (Join-Path $applicationPath $file)) "Missing portable file: $file"
}
foreach ($file in @('GearBackup.GUI.exe', 'GearBackup.exe')) {
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $applicationPath $file))
    Assert ($info.ProductVersion -eq $packageVersion) "Portable executable version differs from package: $file"
}
$html = Get-Content -LiteralPath (Join-Path $applicationPath 'Manual.html') -Raw
foreach ($image in [regex]::Matches($html, '<img[^>]+src="([^"]+)"')) {
    Assert (Test-Path -LiteralPath (Join-Path $applicationPath $image.Groups[1].Value)) 'Missing portable manual image.'
}
Assert (-not (Test-Path -LiteralPath (Join-Path $applicationPath 'GearBackup.ini'))) 'Portable package shipped settings.'
$source = Join-Path $testRoot 'source'
$backups = Join-Path $testRoot 'backups'
New-Item -ItemType Directory -Path $source -Force | Out-Null
Set-Content -LiteralPath (Join-Path $source 'portable-test.txt') -Value 'Portable fixture'
$iniPath = Join-Path $applicationPath 'GearBackup.ini'
Set-Content -LiteralPath $iniPath -Value "; Preserve my preferences`r`nBackupFolder=$backups`r`nMode=full`r`nDevice=m8`r`nProfile=portable-test`r`nCustomKey=preserved`r`n"
$iniHash = (Get-FileHash -LiteralPath $iniPath).Hash
$userFile = Join-Path $applicationPath 'keep-me.txt'
Set-Content -LiteralPath $userFile -Value 'User file must survive extraction'
$userHash = (Get-FileHash -LiteralPath $userFile).Hash
$guiPath = Join-Path $applicationPath 'GearBackup.GUI.exe'
$guiHash = (Get-FileHash -LiteralPath $guiPath).Hash
Set-Content -LiteralPath $guiPath -Value 'Simulated outdated executable'
Extract 'update.log'
Assert ((Get-FileHash -LiteralPath $iniPath).Hash -eq $iniHash) 'Portable update changed the INI.'
Assert ((Get-FileHash -LiteralPath $userFile).Hash -eq $userHash) 'Portable update changed a user file.'
Assert ((Get-FileHash -LiteralPath $guiPath).Hash -eq $guiHash) 'Portable update did not replace the old executable.'

# Validate both absolute paths before moving the test directory.
foreach ($path in @($applicationPath, $movedPath)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith([IO.Path]::GetFullPath($testRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A relocation path escaped the isolated test folder.'
    }
}
Move-Item -LiteralPath $applicationPath -Destination $movedPath
Assert ((Get-FileHash -LiteralPath (Join-Path $movedPath 'GearBackup.ini')).Hash -eq $iniHash) 'Relocation changed the INI.'
& (Join-Path $movedPath 'GearBackup.exe') --preview --no-pause --source $source
Assert ($LASTEXITCODE -eq 0) 'Relocated CLI preview failed.'
Assert (-not (Test-Path -LiteralPath $backups)) 'Portable preview created backups.'
$renderPath = Join-Path $testRoot 'portable-gui.png'
$guiProcess = Start-Process -FilePath (Join-Path $movedPath 'GearBackup.GUI.exe') -ArgumentList '--render-preview', "`"$renderPath`"" -WindowStyle Hidden -Wait -PassThru
Assert ($guiProcess.ExitCode -eq 0 -and (Test-Path -LiteralPath $renderPath)) 'Relocated GUI failed.'
Assert ((ExistingIntegration) -eq $originalIntegration) 'Portable operation changed shortcuts or uninstall registration.'
Write-Output "PASS: $assertions portable assertions (extraction, versions, manual, updates, settings, relocation, CLI/GUI, no integration)."
Write-Output "Portable test files and logs: $testRoot"

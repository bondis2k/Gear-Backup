# Installs, upgrades and uninstalls in an isolated folder. No devices are used.
# Refuses to run if Gear Backup is already registered as installed for this user.
#requires -Version 7.0
param([string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $InstallerPath) {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $projectRoot 'dist\installer-payload\GearBackup.exe')).ProductVersion
    $InstallerPath = Join-Path $projectRoot "dist\GearBackup-Setup-$version.exe"
}
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{1B17F726-12B1-4C2A-B26A-68E307B7EC49}_is1'
if (Test-Path $registration) { throw 'An existing Gear Backup installation is registered. Run this smoke test in a separate Windows user account.' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $projectRoot ".test-runs\installer-$testId"
$installPath = Join-Path $testRoot 'app with spaces'
$groupName = 'Gear Backup'
$groupPath = Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
if (Test-Path -LiteralPath $groupPath) { throw 'Gear Backup shortcuts already exist. Run this smoke test in a separate Windows user account.' }
$assertions = 0
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:assertions++
}
function RunSetup([string]$logName) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=""',
        "/DIR=`"$installPath`"", "/LOG=`"$(Join-Path $testRoot $logName)`"")
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Assert ($process.ExitCode -eq 0) "Setup failed with exit code $($process.ExitCode). See $testRoot."
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$uninstaller = Join-Path $installPath 'unins000.exe'
try {
    RunSetup 'install.log'
    foreach ($file in @('GearBackup.GUI.exe', 'GearBackup.exe', 'm8_reader.dll', 'Manual.html', 'Manual-original.html', 'Manual-original-dark.html', 'Manual-windows.html', 'Manual-retro.html', 'MANUAL.md', 'README.md')) {
        Assert (Test-Path -LiteralPath (Join-Path $installPath $file)) "Missing installed file: $file"
    }
    $setupMetadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
    $setupVersion = $setupMetadata.ProductVersion.Trim()
    $setupBinaryVersion = '{0}.{1}.{2}.{3}' -f $setupMetadata.FileMajorPart, $setupMetadata.FileMinorPart, $setupMetadata.FileBuildPart, $setupMetadata.FilePrivatePart
    foreach ($file in @('GearBackup.GUI.exe', 'GearBackup.exe')) {
        $metadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installPath $file))
        Assert ($metadata.ProductVersion.Trim() -eq $setupVersion) "Product version differs from Setup: $file"
        $binaryVersion = '{0}.{1}.{2}.{3}' -f $metadata.FileMajorPart, $metadata.FileMinorPart, $metadata.FileBuildPart, $metadata.FilePrivatePart
        Assert ($binaryVersion -eq $setupBinaryVersion) "File version differs from Setup: $file"
    }
    Assert (-not (Test-Path -LiteralPath (Join-Path $installPath 'GearBackup.ini'))) 'The installer shipped an INI file.'
    Assert (Test-Path -LiteralPath (Join-Path $installPath 'GearBackup.installed')) 'Installed settings marker is missing.'
    Assert (Test-Path $registration) 'The per-user uninstall registration is missing.'
    Assert (((Get-ItemProperty $registration).InstallLocation).TrimEnd('\') -eq $installPath) 'Incorrect registered install location.'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @('Gear Backup', 'Gear Backup (CLI)', 'Gear Backup Manual', 'Uninstall Gear Backup')) {
        $shortcutPath = Join-Path $groupPath "$shortcut.lnk"
        Assert (Test-Path -LiteralPath $shortcutPath) "Missing shortcut: $shortcut"
        Assert ($shell.CreateShortcut($shortcutPath).TargetPath.StartsWith($installPath, [StringComparison]::OrdinalIgnoreCase)) "Shortcut points outside the installation: $shortcut"
    }
    $html = Get-Content -LiteralPath (Join-Path $installPath 'Manual.html') -Raw
    foreach ($image in [regex]::Matches($html, '<img[^>]+src="([^"]+)"')) {
        Assert (Test-Path -LiteralPath (Join-Path $installPath $image.Groups[1].Value)) 'An offline manual picture is missing.'
    }
    foreach ($anchor in [regex]::Matches($html, 'href="#([^"]+)"')) {
        Assert ($html.Contains('id="' + $anchor.Groups[1].Value + '"')) "Missing manual anchor: $($anchor.Groups[1].Value)"
    }

    $sourcePath = Join-Path $testRoot 'source'
    $backupPath = Join-Path $testRoot 'backups'
    New-Item -ItemType Directory -Path $sourcePath -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sourcePath 'installer-test.txt') -Value 'Local installer test fixture'
    $configPath = Join-Path $installPath 'GearBackup.ini'
    $settings = "; Settings must survive upgrades and uninstall`r`nBackupFolder=$backupPath`r`nMode=full`r`nVerify=no`r`nThorough=no`r`nRecovery=no`r`nDevice=m8`r`nProfile=installer-test`r`nCustomKey=preserve-me`r`n"
    Set-Content -LiteralPath $configPath -Value $settings -NoNewline
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash
    & (Join-Path $installPath 'GearBackup.exe') --preview --no-pause --source $sourcePath
    Assert ($LASTEXITCODE -eq 0) 'The installed CLI preview failed.'
    Assert (-not (Test-Path -LiteralPath $backupPath)) 'Preview created a backup folder.'
    Assert ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Preview changed the INI.'
    $renderPath = Join-Path $testRoot 'installed-gui.png'
    $guiProcess = Start-Process -FilePath (Join-Path $installPath 'GearBackup.GUI.exe') -ArgumentList '--render-preview', "`"$renderPath`"" -WindowStyle Hidden -Wait -PassThru
    Assert ($guiProcess.ExitCode -eq 0 -and (Test-Path -LiteralPath $renderPath)) 'The installed GUI preview failed.'

    RunSetup 'upgrade.log'
    Assert ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Upgrade changed the INI.'
} finally {
    if (Test-Path -LiteralPath $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $testRoot 'uninstall.log')`"" -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Uninstall failed: $($process.ExitCode). See $testRoot." }
    }
}
Assert (-not (Test-Path $registration)) 'Uninstall registration was left behind.'
Assert (-not (Test-Path -LiteralPath (Join-Path $installPath 'GearBackup.GUI.exe'))) 'Uninstall left the GUI executable.'
Assert (-not (Test-Path -LiteralPath (Join-Path $installPath 'GearBackup.exe'))) 'Uninstall left the CLI executable.'
Assert (-not (Test-Path -LiteralPath (Join-Path $installPath 'GearBackup.installed'))) 'Uninstall left the settings marker.'
Assert ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Uninstall removed or changed the INI.'
Assert (-not (Test-Path -LiteralPath $groupPath)) 'Uninstall left the test shortcuts.'
Write-Output "PASS: $assertions installer assertions (install, shortcuts, offline manual, CLI/GUI, upgrade, uninstall)."
Write-Output "Test logs and preserved settings: $testRoot"

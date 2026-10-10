# Gear Backup

A Windows tool with graphical and command-line interfaces for backing up and restoring a Dirtywave M8 SD card, a card reader, or a local copy of the card. It also backs up the Polyend Tracker Mini, and over MTP the Teenage Engineering OP-1 Field and OP-XY and the Fujifilm X100VI camera.

- Full and incremental backups with dated snapshots and separate card profiles.
- Restore all files, a folder bundle, a single file, or a song with its samples.
- Preview changes, check backup integrity, and optionally keep recovery copies.

## Build and run

Requires Windows 10 or 11 and the built-in .NET Framework.

For an installer build, run `GearBackup-Setup-<version>.exe`. It installs both interfaces for the current Windows user and adds Start menu shortcuts, an offline illustrated manual, and an uninstaller. The desktop shortcut is optional. The default location is `%LOCALAPPDATA%\Programs\Gear Backup`; settings remain writable beside the executables. Upgrades and uninstall preserve `GearBackup.ini`, backups, and recovery files.

For a portable copy, run `GearBackup-Portable-<version>.exe` and select a writable folder, including a USB drive. It extracts the same executables and manual without shortcuts or an uninstall entry. Open `GearBackup.GUI.exe` from that folder. To update, close the app and extract the newer package into the same folder; existing INI settings are preserved. Backup paths remain as configured, and MTP mirrors still use the PC's Local AppData folder.

For the graphical interface:

```cmd
build-gui.cmd --no-pause
GearBackup.GUI.exe
```

For the command-line interface:

```cmd
build.cmd --no-pause
GearBackup.exe
```

You can also specify the card or folder:

```cmd
GearBackup.exe --source E:\
```

Configure backup locations and options in `GearBackup.ini`. SHA-256 comparison (`Thorough`), automatic integrity verification (`Verify`), and recovery copies (`Recovery`) default to `no`. Change previews default to `yes`; incremental backups with no file changes skip the proceed prompt.

Existing settings and backup histories from the previous app name remain usable; see the manual's upgrade notes. New backups use `Gear Backups` and `.gearbackup` metadata.

Executable builds are ignored by Git; build from source locally. See [MANUAL.md](MANUAL.md) for all settings, restore options, and command-line arguments.

To create an installer, install [PowerShell 7](https://aka.ms/powershell) and [Inno Setup 6.3+ or 7](https://jrsoftware.org/isdl.php), then run:

```cmd
build-installer.cmd
```

This rebuilds both executables and generates `dist\GearBackup-Setup-<version>.exe` and `dist\GearBackup-Portable-<version>.exe`, each with a SHA-256 checksum file. The release version is defined once in `src\AppIdentity.cs`; executable metadata, the GUI's **About** dialog, and both packages all use it. An optional `-Version` must match that version. For a compiler in a custom location, add `-IsccPath "C:\Tools\Inno Setup\ISCC.exe"`. The compiler and generated files are not committed. The packages are unsigned unless you arrange code signing separately.

Long operations report progress. Use Cancel in the GUI or Ctrl+C in the CLI; interrupted backups can be resumed. Both interfaces use the same backup, restore, and integrity-check engines and INI settings.

# Gear Backup

Back up your music gear, camera and local folders on Windows. Gear Backup provides a graphical app and a command-line app with the same backup, restore and integrity-check engine.

Make full or incremental backups, review changes before copying, and restore the files you need. Every run saves a restore point, so you can return to an earlier state of the source.

Restore does not require a connected device. Choose any available backup and recover it to a PC folder; detecting a device filters the list to that device type.

[Quick start guide](QuickStartGuide.md) · [User manual](MANUAL.md) · [FAQ](FAQ.md) · [Complete feature list](FEATURES.md) · [Downloads](https://github.com/bondis2k/Gear-Backup/releases)

Before version 1.0.0, only the current settings and backup formats are supported. No automatic migration or compatibility with earlier formats is provided.

## Supported devices

| Device or source | What you can back up | Connection |
| --- | --- | --- |
| <img src="assets/manual/device-m8.png" alt="" width="40" height="40"> Dirtywave M8 | Songs, samples, instruments and bundles | USB drive mode or SD card reader |
| <img src="assets/manual/device-tracker-mini.png" alt="" width="40" height="40"> Polyend Tracker Mini | Projects, patterns, instruments and samples | USB drive mode or SD card reader |
| <img src="assets/manual/device-op1-field.png" alt="" width="40" height="40"> Teenage Engineering OP-1 Field | Synths, drums, tapes and mixdowns | MTP |
| <img src="assets/manual/device-opxy.png" alt="" width="40" height="40"> Teenage Engineering OP-XY | Projects and other device files | MTP |
| <img src="assets/manual/device-x100vi.png" alt="" width="40" height="40"> Fujifilm X100VI | Photos and videos; C1–C7 recipes and current shooting settings | MTP for media; USB RAW CONV./BACKUP RESTORE mode for settings |
| <img src="assets/manual/device-local-folders.png" alt="" width="40" height="40"> Local folders and memory cards | Files and folders | Any accessible filesystem path |

MTP devices are read through a local PC mirror. Their backups restore to a PC folder for manual transfer. X100VI settings are saved as XML exports: full-camera settings export and restoring settings to the camera are not supported.

## What it offers

- **Flexible backups:** full and incremental copies, separate source profiles, optional ZIP archives, and resumable interrupted backups.
- **History built in:** every backup run is a restore point. Each distinct file content is stored once within a backup set, so history costs little space.
- **Selective restore:** the GUI lists backups for the detected device, defaults to the latest saved run and uses SQLite for full restore or a checked folder/file browser. The CLI also offers M8 bundles, songs with samples and Tracker Mini projects.
- **ZIP restore:** the GUI can restore a standalone ZIP to a matching detected device or PC folder, with mandatory checksum validation and free-space checks. MTP files are restored to a PC folder for manual transfer.
- **Checks and control:** change previews, backup integrity checks, optional SHA-256 change comparison, optional copy verification and recovery copies before restore.
- **Backup file catalog:** SQLite supplies the primary inventory for incremental backups and records snapshot membership and integrity results. Checksum lists and manifests provide recovery records if the database is missing, stale or damaged.
- **Backup management:** a dashboard showing backup health and age, a storage breakdown, and previewed cleanup to the Recycle Bin.
- **Device preferences:** save different backup locations and retention choices for each device or profile.
- **Readable X100VI recipes:** browse saved recipes offline, see film simulation names and setting values, and copy or export them as text.

## Install and get started

Requires **Windows 10 or 11** with **.NET Framework 4.6 or newer**, normally included with Windows.

Download a package from [GitHub Releases](https://github.com/bondis2k/Gear-Backup/releases):

- **Setup:** `GearBackup-Setup-<version>.exe` installs both apps for your Windows user, with Start menu shortcuts and an uninstaller. Select **Add CLI to PATH** (unchecked by default) to run `GearBackup.exe` from any folder in a newly opened terminal.
- **Portable:** `GearBackup-Portable-<version>.exe` extracts both apps into a writable folder of your choice, including a USB drive.

Both packages include the illustrated offline manual and preserve existing INI settings when updated. Portable backups use the configured backup location; MTP mirrors are stored in the PC's Local AppData folder.

1. Open `GearBackup.GUI.exe` and connect your device, insert its card, or choose a source folder.
2. On **Device**, click **Detect device** and review its connection and available storage details. Open **Backup**, choose **Full backup** or **Incremental backup**, then **Back up**. Backups default to `%USERPROFILE%\Backup`. Use **More options** to choose a source folder, change the destination, set an optional profile or add a zip copy. Use **Restore** for a separate restore destination or integrity checks.
   If the destination folder is missing, starting a backup asks whether to create it; choosing No cancels.
3. Choose a full or incremental backup, review the preview, and start copying.
4. Use **Dashboard** to review your backups, **Storage** to manage space, or the restore operation to recover files.

For X100VI recipes, select the camera-settings operation while the camera is in **USB RAW CONV./BACKUP RESTORE** mode. To view a saved settings backup, select it in **Dashboard** or **Storage** and click **View recipes**.

**Help** or **F1** opens the offline HTML manual in your browser. The [repository manual](MANUAL.md) explains connection modes, restore choices and troubleshooting.

## Command line

Run `GearBackup.exe` for the interactive menu, or specify an operation and source:

```cmd
GearBackup.exe --source E:\
GearBackup.exe --source D:\MyGear --profile "Studio" --preview
GearBackup.exe --zip-archive --source D:\MyGear --output D:\Archives --name "Live set" --no-pause
GearBackup.exe --restore --source E:\
GearBackup.exe --check
GearBackup.exe --camera-settings
GearBackup.exe --rebuild-catalog "D:\Backups\Gear Backups\profiles\Studio\2026-10-11" --no-pause
```

Use **Cancel** in the GUI or **Ctrl+C** in the CLI to stop an operation. See the [command-line reference](MANUAL.md#command-line-and-exit-codes) for all arguments and exit codes.

## Settings and storage

Installed copies store settings in `%APPDATA%\GearBackup`; portable copies and local builds store them beside the executables. Installed copies read settings only from AppData and do not import INI files from the executable folder. Global preferences are stored in `GearBackup.ini`. Device and profile overrides are stored in `GearBackup.Profiles.ini`; edit them through **Device & backup settings** in the GUI. Profile preferences take priority over device defaults, which take priority over global settings. Both apps share these files.

Choose **Original**, **Original (dark)** or **Retro** under **Settings → Appearance → Color theme**. Saving applies the theme immediately and remembers it for the next launch. Original is the default.

Change previews are enabled by default. SHA-256 change comparison (`Thorough`), automatic copy verification (`Verify`) and recovery copies (`Recovery`) are optional and default to off.

Backups use a content store: each distinct file content is stored once within a backup set, and every run, including runs with no changes, saves a restore point dated by the backup run. Restore or export files through the app; a backup has no browsable file tree. Separate full backups remain independent. ZIPs and MTP mirrors also occupy space; use **Storage** to review them. Cleanup moves selected items to the Recycle Bin, which must be emptied to release their space.

See the [settings reference](MANUAL.md#all-ini-settings) for backup paths, retention, recovery locations and other options.

## Build from source

Building needs **Visual Studio Build Tools** with the ".NET desktop build tools" workload (for the C# compiler); no .NET SDK or NuGet packages are needed, and the programs still run on .NET Framework 4.6 or newer. Install **PowerShell 7** to build the GUI and generate its HTML manual, and **Rust** ([rustup.rs](https://rustup.rs)) to build `m8_reader.dll`, the M8 song reader the GUI's Song tab uses.

From the repository folder:

```cmd
build.cmd --no-pause
build-gui.cmd --no-pause
```

These produce `GearBackup.exe`, `GearBackup.GUI.exe`, the shared `GearBackup.Core.dll` (keep it beside the executables) and five offline manuals (`Manual.html`, `Manual-original.html`, `Manual-original-dark.html`, `Manual-windows.html`, `Manual-retro.html`). Generated executables and personal settings are excluded from Git.

To build both installer packages, also install **Inno Setup 6.3 or newer**, then run:

```cmd
build-installer.cmd
```

The packages and their SHA-256 checksum files are written to `dist`. See the [build documentation](MANUAL.md#building-and-testing) for compiler options and packaging details.

## Tests

Run the core and Windows Forms integration suites from the repository folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-gui-tests.ps1
```

The suites use isolated temporary fixtures and hidden GUI windows. They cover backup, restore, history, integrity, profiles, storage management and recipe parsing without writing to connected devices. Hardware behavior still needs testing with the relevant device.

Repeatable [performance benchmarks](benchmarks/README.md) measure file copying, verification, unchanged incrementals and catalog scans, with elapsed time and managed allocation recorded to CSV.

## License

Gear Backup is free software, released under the [GNU General Public License, version 3](LICENSE) (GPL-3.0). Copyright (C) 2026 Thomas Bondal.

You can use, study, share and change it. If you distribute a changed version, you must do so under the same license and make its source code available. The program comes with no warranty; see the [full license text](LICENSE) for the details.

# Gear Backup Manual

This manual describes Gear Backup 0.9.4 for Windows. Gear Backup saves device files, keeps backup history, checks integrity, and restores whole backups or selected files. The graphical app and command-line app use the same backup engine and settings.

All app pictures in this manual use **Original**, the default light gray and teal theme. They show example devices, paths, and backup data rather than a live connected device.

## Contents

- [Getting started](#getting-started)
- [Installing and finding your files](#installing-and-finding-your-files)
- [Connecting a device](#connecting-a-device)
- [Graphical interface](#graphical-interface)
- [Full and incremental backup](#full-and-incremental-backup)
- [How backups are stored](#how-backups-are-stored)
- [Restore](#restore)
- [Checking and managing backups](#checking-and-managing-backups)
- [X100VI settings and recipe viewer](#x100vi-settings-and-recipe-viewer)
- [Settings and profiles](#settings-and-profiles)
- [All INI settings](#all-ini-settings)
- [Command line and exit codes](#command-line-and-exit-codes)
- [Troubleshooting](#troubleshooting)
- [Building and testing](#building-and-testing)

## Getting started

1. Open **Gear Backup** from the Start menu, or run `GearBackup.GUI.exe` from your portable folder.
2. Connect your device in the mode listed under [Connecting a device](#connecting-a-device).
3. On **Device**, click **Detect device**. Check the device name and connection mode.
4. Open **Backup**, leave **Full backup** selected, and optionally enter a name such as `Live set`.
5. Click **Back up**, review the planned changes, and accept them. If the backup location does not exist, the app asks whether to create it.

The default backup location is `%USERPROFILE%\Backup`. Backups are content stores: files are stored once within a backup set, and each successful backup run saves a restore point. Use the app to restore files; a backup does not create a separate folder of ordinary backup files.

For a single file you can extract with Windows, choose **Zip archive** on Backup instead, then choose where to save it.

For later backups, choose **Incremental backup** and select the full backup to update. This adds a restore point to that set while copying only new or changed content.

## Installing and finding your files

Gear Backup requires **Windows 10 or 11** and **.NET Framework 4.6 or newer**. The framework is normally included with Windows. SQLite support uses Windows' built-in library; a separate database installation is unnecessary.

| Package | What it does |
| --- | --- |
| `GearBackup-Setup-<version>.exe` | Installs for the current Windows user, with Start menu shortcuts and an uninstall entry. |
| `GearBackup-Portable-<version>.exe` | Extracts the app and manual into your chosen folder. No shortcuts, uninstall entry, or PATH change. |

The normal installer offers optional **Create a desktop shortcut** and **Add CLI to PATH** checkboxes. Both start unchecked. If you enable PATH, open a new terminal before running `GearBackup.exe` by name.

| Item | Default location |
| --- | --- |
| Installed executables and offline manuals | `%LOCALAPPDATA%\Programs\Gear Backup\` |
| Installed global settings | `%APPDATA%\GearBackup\GearBackup.ini` |
| Installed device/profile preferences | `%APPDATA%\GearBackup\GearBackup.Profiles.ini` |
| Portable/local-build settings | Beside the executables |
| Backup root | `%USERPROFILE%\Backup\Gear Backups\profiles\` |
| Recovery copies, when enabled | `<BackupFolder>\Gear Recovery\` |
| Temporary working mirrors for MTP devices | `%LOCALAPPDATA%\GearBackup\Devices\` |

An installed copy is identified by `GearBackup.installed` beside its executables. It reads settings from AppData; it does not import INI files from the executable folder. The installer does not overwrite your personal INI files.

**Help** opens the offline HTML manual matching your chosen appearance. `Manual.html` is the Original edition; `Manual-original-dark.html`, `Manual-windows.html` and `Manual-retro.html` use the other document styles. The Windows edition follows the browser's system light/dark preference. App pictures remain in Original in every edition.

## Connecting a device

| Device | Connection for file backup | Restore guidance |
| --- | --- | --- |
| <span class="device-cell"><img class="device-icon" src="assets/manual/device-m8.png" alt="" width="40" height="40"><span>Dirtywave M8</span></span> | USB disk mode, or its SD card in a reader. | Restore to the connected drive/card or a PC folder. |
| <span class="device-cell"><img class="device-icon" src="assets/manual/device-tracker-mini.png" alt="" width="40" height="40"><span>Polyend Tracker Mini</span></span> | USB drive mode, or its SD card in a reader. | Restore to the drive/card or a PC folder. |
| <span class="device-cell"><img class="device-icon" src="assets/manual/device-op1-field.png" alt="" width="40" height="40"><span>OP-1 Field</span></span> | MTP mode: **shift + COM**, then **T4**. | Restore to a PC folder, then transfer files to the device with Explorer. |
| <span class="device-cell"><img class="device-icon" src="assets/manual/device-opxy.png" alt="" width="40" height="40"><span>OP-XY</span></span> | MTP mode: **COM**, then **T4**. | Restore to a PC folder, then transfer files with Explorer. |
| <span class="device-cell"><img class="device-icon" src="assets/manual/device-x100vi.png" alt="" width="40" height="40"><span>Fujifilm X100VI photos/videos</span></span> | USB file-transfer mode (MTP), or the camera's SD card in a reader. | Restore to a PC folder for viewing/editing. Putting photos back on the card is usually unnecessary. |
| <span class="device-cell"><img class="device-icon" src="assets/manual/device-x100vi.png" alt="" width="40" height="40"><span>Fujifilm X100VI recipes/settings</span></span> | **USB RAW CONV./BACKUP RESTORE**. | Read and export saved recipes; the app does not write settings back to the camera. |

You can also back up an ordinary local folder. Open **More options** on Backup and choose its source. For a card reader or folder, set the appropriate device type when automatic identification is insufficient. An unknown folder defaults to the M8 device profile.

MTP devices have no drive letter. Before file backup, the app synchronizes their exposed files into a local mirror, then backs up that mirror. This temporary working copy uses additional PC space. It is separate from the backup's content store and can be removed through Storage after use.

OP-XY backups contain content exposed over MTP; factory content that the device does not expose cannot be included. Camera file backups and camera settings backups are separate histories.

## Graphical interface

### Device tab

Click **Detect device** before backing up a connected device. You can open Restore without connecting or detecting a device. The device card shows its name, icon, and connection mode. For USB drives, it can show capacity, used space, and free space. Available information depends on the connection; an MTP mirror's disk capacity is not presented as the device's capacity.

![Device tab with an example detected M8 in Original](assets/manual/current-device-original.png)

Use **Go to Backup** or **Go to Restore**, or click the corresponding tab. When an MTP device is detected, a previously entered USB source folder is cleared so the operation uses the detected device.

### Backup tab

The main controls are **Type**, the backup name or full-backup selector, **Preview**, and **Back up**. **More options** reveals the source folder, backup location, history/profile name, and ZIP option. Normally, detection provides the source and you can leave the history name blank.

**Browse…** opens the shared folder picker for source, backup, restore, and recovery locations. Use the Home, Desktop, and Documents shortcuts, expand folders in the tree, or enter a path and click **Go**. **Up** opens the parent folder; **Select folder** accepts your choice. **New folder…** appears where creating a destination is allowed. The picker follows your selected appearance.

![Full backup controls in Original](assets/manual/current-backup-original.png)

**Preview** shows the plan without carrying out the backup. Clicking **Back up** also requests approval when **Review changes before copying** is enabled, as it is by default. The review window supports searching and filtering the planned actions; unchanged files are excluded from the incremental action list.

### Song tab

The Song tab opens a Dirtywave M8 song file (`.m8s`) from anywhere on the PC and shows what is inside: name, firmware version, tempo, transpose and quantize (in hex, as on the M8), song length, how many chains, phrases and tables are in use, the rows each track uses, and a list of instruments with their slot, type, name and sample path. The file is only read, never changed. It works without a connected device and needs `m8_reader.dll`, which is installed next to the application.

### Restore tab and activity

Restore offers **Restore full backup**, **Restore individual files**, and **Check backups**. The selected operation changes the controls shown. See [Restore](#restore) for the backup and restore-point selectors.

The activity area records operation details. Progress, totals, elapsed time, and the final summary show the result. Full backups display overall progress rather than updating the display for every copied file. **Cancel** requests a safe stop; wait for the operation to finish stopping before disconnecting the device.

**Dashboard**, **Storage**, and **Device & backup settings** open management windows. **Open backup folder** or **Open restore folder** becomes available after an operation with an output location.

## Full and incremental backup

### Full backup: start a separate set

A full backup creates a new dated folder under the source's history. An optional name helps distinguish it in the app, for example `Before firmware update`. The display name does not replace the dated folder name.

Each full backup is independent. Content is shared between restore points **inside one set**, not between separate full backups. Creating several full backups therefore uses more space even though each set stores content once.

With **Also save as zip**, a successful full backup additionally creates a ZIP beside the backup folder. It contains ordinary exported files, not the internal content-store history. It is useful for transfer or manual extraction; it uses extra space and does not preserve the set's restore points. Incremental backups do not update or create ZIP archives.

### Zip archive: save a standalone full backup

1. Detect your device, or choose the source folder under **More options**.
2. Select **Zip archive** in **Type**.
3. Optionally enter a **ZIP filename**. Blank uses the device name plus date and time, for example `M8_2026-10-11_12.34.56.zip`.
4. Click **Create ZIP** and choose the ZIP file's save location outside the source folder. The suggested name can be changed in the save window.
5. Review the contents and wait for **Complete**. Replacing an existing archive requires confirmation.

This creates one ZIP containing the source files and empty folders, using the normal backup exclusions. A root-level `checksums.sha256` lists the SHA-256 hash and ZIP-relative path of every backed-up file and `gearbackup-info.txt` in sha256sum format. Hashes are always included, even with verification disabled; the manifest excludes itself and folders. The source must not contain a root-level file or folder named `checksums.sha256` or `gearbackup-info.txt`. It does not create a full backup set or point-in-time history; extract it with Windows or another ZIP tool. MTP sources are refreshed through their PC mirror first. **Preview** asks for a save location and shows the contents without creating the archive. **Verify copied files** also verifies archived content when enabled. Cancellation or a failure removes the temporary archive and leaves any existing destination archive intact.

`gearbackup-info.txt` identifies the backup device profile and source name, connection type, available device name/identifier and volume details, profile name, application version, UTC backup time, and source file/folder counts. Device identifiers are recorded when reported by detection; a manually chosen folder does not establish a physical device's identity. **Backup content size (bytes)** counts the backed-up source files. **Total extracted size (bytes)** includes every uncompressed file in the ZIP, including the information file and checksum manifest; it excludes filesystem allocation overhead.

The CLI supports the same operation with `--zip-archive`. Supply `--output` with an existing folder or a `.zip` file path, or omit it to be asked where to save; a blank answer cancels. When selecting a folder, `--name` supplies the filename, otherwise the device name plus date/time is used. An explicit `.zip` output path takes precedence over `--name`. The destination's parent folder must already exist. `--profile` still selects device/profile preferences without creating a backup history. `--preview` shows the plan without writing the archive.

### Incremental backup: update a chosen set

1. Detect the device or choose its source folder.
2. On Backup, select **Incremental backup**.
3. Choose **Full backup to update**. The newest available set is initially selected.
4. Optionally enter **Name for this incremental backup**.
5. Click **Preview** to inspect changes, or **Back up** to run it.

![Incremental backup controls in Original](assets/manual/current-incremental-original.png)

The app uses the backup's SQLite inventory as the primary baseline. Normally, size and modified time determine whether a file changed, with a two-second tolerance for filesystem timestamp precision. Files without usable timestamps may require content comparison. Enable **Compare file contents** for SHA-256 comparison even when size and date match.

In the default single-store mode, every successful incremental run saves a restore point, even if no files changed. An unchanged run does not ask you to approve an empty list. Files deleted from the source disappear from the new point but remain recoverable from earlier points.

An unnamed incremental point uses `yyyy-MM-dd_HH.mm.ss`, such as `2026-10-11_14.32.08`. Repeated names receive a numeric suffix. The app lists points using the recorded backup capture date and time, not source-file dates or the point name. A named point retains its recorded capture time as well.

## How backups are stored

Every backup is a content store: one immutable store of file contents plus a complete inventory for each restore point. Each distinct file content is stored once per backup set, and every successful run, including an unchanged incremental, creates a point. Restore or export through the app; a backup has no browsable file tree.

All points remain until you remove the whole backup set. There is no individual restore-point pruning control. **Backups to keep** limits full backup sets; zero means keep all. Retention runs after successful backups and asks before moving excess items to the Recycle Bin.

Backups made by earlier versions in plain, browsable folders are not supported. Make a new full backup; the old folder can stay for reference or be removed from **Storage**.

## Restore

Open Restore to recover files even when your device is unavailable or broken. Without a detected device, the tab lists **all available backups**, labelled with their device type, and you can restore to an ordinary PC folder. If a device is detected, the list is filtered to its **device type**, rather than one physical unit's serial number. Both lists include histories at the current backup location and locations configured in device/profile preferences. The newest backup and its latest restore point are selected by default.

### Restore a full backup

1. Select **Restore full backup**.
2. Choose the named full backup set.
3. Choose its full or incremental **Restore point**. Each point represents the complete file membership for that backup run, so you do not need to restore a chain of increments.
4. Choose the **Restore destination**: a connected card/drive or an ordinary PC folder.
5. Click **Preview**, or **Restore** and accept the reviewed changes.

![Full restore and restore-point selection in Original](assets/manual/current-restore-original.png)

The default is a **merge**: files in the selected point are restored as needed, while other target files remain. **Remove extra target files** makes a full saved-point restore exact, removing files and empty folders absent from that point. It is available only when complete saved membership exists. Review its removal list carefully.

Enable **Keep recovery copies before restoring** under Settings → Restore if you want PC copies of files about to be overwritten or removed. Recovery is off by default. An enabled recovery copy that fails stops the restore before the associated target changes.

MTP device restores go to a PC folder; Gear Backup does not upload to the device. Restore an OP-1 Field or OP-XY backup to a folder and transfer the desired files with Explorer. The X100VI card warning explains why returning old photos to a camera card may be undesirable.

### Restore a ZIP archive

1. On **Device**, detect the destination device.
2. On **Restore**, select **Restore ZIP archive**. The file picker opens; choose a standalone ZIP created by Gear Backup. **Browse…** lets you choose another archive later.
3. The app reads `gearbackup-info.txt` and displays its device type, backup time, file count and extracted size. Restore is enabled only when the detected device type matches. Archives missing the information file or checksum manifest are rejected.
4. Leave **Restore destination** blank to use the detected USB drive/card, or select a PC folder. For MTP devices you must choose a PC folder and transfer restored files manually; direct MTP upload is not supported.
5. Click **Preview** or **Restore**. The app checks target and temporary extraction space, verifies every archived file against `checksums.sha256`, then shows the restore plan. Device identity is checked again before copying.

ZIP restore merges the archive's files and empty folders into the destination. Extra target files remain, and the two generated metadata files are not restored. Existing overwrite choices, comparison settings and optional recovery copies still apply. Checksum verification of ZIP content is mandatory even when **Verify copied files** is off. A preview may extract files into temporary PC storage for verification, then removes them; it does not change the target. Temporary extraction files are also removed after completion, cancellation or failure. Completed target copies remain if copying is interrupted.

Free-space checks use the ZIP's validated file sizes and include temporary replacement space. If the extraction folder and destination share a drive, their simultaneous space requirements are combined. This check measures the PC folder's drive when restoring for an MTP device; it cannot measure the MTP device's available capacity.

The checksums in a ZIP detect damage and incomplete copies. They do not prove who made the ZIP, because anyone can change a ZIP and its checksum list together. Only restore ZIPs you made yourself or otherwise trust, and note that a ZIP listing more than a million entries is refused.

### Restore individual files or folders

1. Select **Restore individual files** and choose the backup and point.
2. Click **Choose files…**.
3. Tick the folders or files you want. Selecting a folder includes its descendants, including empty folders.
4. Accept the selection, choose the destination, and preview or start the restore.

![Individual restore controls in Original](assets/manual/current-restore-files-original.png)

![Checkbox file and folder browser in Original](assets/manual/current-browser-original.png)

Changing the backup or restore point clears the previous file selection. Individual restores do not remove unrelated target files.

In the file browser, folders load their visible contents when you open them, keeping large backups quick to browse. **Expand all** and **Collapse all** control the tree without changing your checks. **Select all** and **Clear selection** change what will be restored. The summary counts the selected files and folders, including unopened descendants of checked folders; **Use selection** is enabled when at least one item is checked.

The CLI additionally offers device-specific choices: an M8 bundle, an M8 song with its referenced samples, and a Tracker Mini whole project folder. The GUI checkbox browser restores what you select; it does not automatically add sample dependencies when you tick a song.

## Checking and managing backups

### Integrity checks

On Restore, choose **Check backups** and click **Check**. This reads backup content and compares SHA-256 hashes against recorded checksums and snapshot manifests. It reports OK, damaged, missing, and unchecked content. Results are recorded for the dashboard and SQLite catalog.

Three separate features use checksums:

| Feature | Purpose | Default |
| --- | --- | --- |
| **Compare file contents** (`Thorough`) | Decide whether source/target content changed, including files whose size and timestamp match. | Off |
| **Verify copied files** (`Verify`) | Automatically read back copied data and check it during backup/restore. | Off |
| **Check backups** | Explicitly verify saved backup content after the backup. | Run on demand |

Hashes are recorded during backup even when the two optional settings are off. SQLite is the primary inventory for incremental planning and restore selection; the independent checksum lists/manifests remain integrity and catalog-recovery records. A catalog entry alone does not prove that its content is undamaged: run a check to verify it.

### Dashboard

**Dashboard** groups histories and shows their latest backup status and last check. Backups older than seven days are marked overdue; that label is a reminder, not evidence of damage. Filter by device, refresh the list, open a folder, check the latest backup, or rename a history's display label.

### Storage and cleanup

**Storage → Space usage** separates stored content and metadata. ZIP exports and MTP mirrors also use space.

Under **Cleanup**, tick the items to remove, then click **Preview cleanup**. Nothing is selected initially. Review the effect before accepting; selected items are moved to the Recycle Bin after safety checks. Removing an entire set removes every restore point stored in it. Do not manually remove individual content objects: several points can reference the same object.

### Moving backups and repairing a catalog

Copy the **entire backup set**, including hidden `.gearbackup` metadata, to preserve its points and content. To keep automatic discovery, retain the `Gear Backups\profiles\<history>\<dated backup>` layout under your chosen backup location. Update the global or relevant profile backup location afterward.

Each set contains `.gearbackup\catalog.sqlite`, with inventory, hashes, point membership, capture times, and check results. Damaged or missing catalogs can be rebuilt from the set's current-format records:

```powershell
.\GearBackup.exe --rebuild-catalog "D:\Backups\Gear Backups\profiles\Studio\2026-10-11" --no-pause
```

Add `--preview` to check the requested rebuild without writing it. Rebuilding recreates the index; it does not recover missing content or accept damaged content as a new baseline. Older pre-release layouts and names are not migrated automatically. Keep intact sets and their metadata together.

## X100VI settings and recipe viewer

For recipes, set the camera to **USB RAW CONV./BACKUP RESTORE**, connect one X100VI, and close other applications using it. Click Detect device. **Back up X100VI settings** appears in the Backup type list only when the detected camera is in this mode.

The capture saves **C1–C7** recipe XML files, **Shooting settings.xml**, and **About this backup.txt**, with camera model, firmware, serial, supported values, and capture information. It temporarily selects recipe banks while reading, then restores the selector and verifies the shooting values. Settings captures create separate full backups in a settings history.

This is a recipe and shooting-settings snapshot, not a complete camera-configuration export or a Fujifilm X Acquire backup. Gear Backup cannot apply the captured settings back to the camera.

Select an eligible settings backup in Dashboard or Storage and click **View recipes**. The viewer presents decoded, readable settings such as film simulation, white balance shifts, grain, and tone values. You can select a recipe or current shooting settings, **Copy recipe**, or **Export text**. **Open backup folder** and **Open XML** also let you inspect a settings capture directly, including a single content-store backup.

For photo/video backup, switch the camera back to USB file-transfer mode and detect it again. Settings mode is not a source for photo backup.

## Settings and profiles

**Settings** controls shared global preferences. Its tabs cover Backup, Restore, Device, and Appearance. Choose **Original**, **Original (dark)**, or **Retro** under Appearance; saving applies the theme and changes which offline manual Help opens.

![Global Backup settings with current defaults in Original](assets/manual/current-settings-original.png)

**Device & backup settings** sets preferences for a device type or a named backup history. An inherited/default option uses the broader setting; a custom value overrides it. Priority is **global → device → profile**. Save changes to persist them; resetting preferences returns the selected scope to inheritance when saved.

The optional **History name** field on Backup supplies the profile identity. Leave it empty for an automatic volume-serial or folder/device identity. Use a fixed name such as `Studio` when you want a stable history across card readers or source paths. Allowed profile characters are letters, digits, spaces, hyphens, and underscores.

A history/profile name groups backup sets. A full or incremental backup's optional name labels one set or point. Renaming a history's display label in Dashboard/Storage does not change its identity or settings scope.

## All INI settings

`GearBackup.ini` is created or completed with explanatory comments on a normal launch. Unknown global keys are ignored; invalid values may produce a warning or error depending on the setting. Paths accept environment variables. Edit the file while the app is closed to avoid competing with a GUI save.

| Key | Default | Meaning / accepted values |
| --- | --- | --- |
| `Theme` | `original` | `original`, `original-dark`, `windows`, or `retro`. Windows follows the current Windows app light/dark mode and accent color, updating open windows when system preferences change. |
| `BackupFolder` | Empty in generated INI | Empty uses `%USERPROFILE%\Backup`; otherwise a backup parent folder. |
| `Verify` | `no` | Automatically verify copied/restore content with SHA-256. |
| `Thorough` | `no` | Compare contents rather than only size and usable timestamps. |
| `Preview` | `yes` | Review and approve planned backup/restore actions before writing. |
| `KeepBackups` | `0` | Full backup sets to retain; nonnegative integer, zero keeps all. |
| `Recovery` | `no` | Save recovery copies before restore overwrites/removals. |
| `RecoveryFolder` | Empty | Empty uses `<BackupFolder>\Gear Recovery`; otherwise your recovery folder, outside the source card/folder. |
| `MaxNameLength` | `64` | Device name-length warning threshold; zero disables length checks. |
| `Eject` | `no` | `no`, `ask`, or `yes`; applies to eligible detected USB drives after completion. |
| `Mode` | `ask` | `ask`, `full`, or `incremental`; selects the CLI backup workflow and initial GUI backup type. |
| `SourceFolder` | Empty | Optional local source/card path; empty uses detection or manual selection. |
| `Profile` | Empty | Optional fixed history/profile identity. |
| `Device` | `auto` | `auto`, `m8`, `tracker-mini`, `op1-field`, `op-xy`, or `x100vi`. |

`Also save as zip` is a run option, not a saved INI key. Optional full/incremental names are also supplied for the run.

Device/profile overrides belong in `GearBackup.Profiles.ini`, for example:

```ini
[device:m8]
Verify=yes

[profile:Studio]
BackupFolder=D:\Backups
Recovery=yes
RecoveryFolder=D:\RestoreRecovery
```

Omit a key to inherit it. Overridable keys are `BackupFolder`, `Verify`, `Thorough`, `Preview`, `KeepBackups`, `Recovery`, `RecoveryFolder`, `MaxNameLength`, and `Eject`. Theme, source, identity, device selection, and Mode are global settings. Unknown override scopes or keys are rejected rather than silently ignored.

## Command line and exit codes

Run `GearBackup.exe` in a terminal, or double-click it for interactive menus. Without arguments, it loads settings, finds the source, and offers the appropriate operations. If CLI PATH was not enabled during installation, use the executable's full path or run it from its folder.

| Option | Behavior |
| --- | --- |
| `--source PATH` | Use a local folder/card; also supplies the local restore target. |
| `--profile NAME` | Use a fixed history/profile identity for this run. |
| `--restore` | Open interactive backup/point/file selection for restore. |
| `--check` | Select histories/backups to check without requiring a source. |
| `--camera-settings` | Capture X100VI recipes and shooting settings in the required camera mode. |
| `--zip` | Also export a successful full backup as ZIP. |
| `--zip-archive` | Create a standalone full ZIP without a backup set or content store. |
| `--output PATH` | With `--zip-archive`, save to a `.zip` file or existing folder; omitted prompts for a destination. |
| `--name NAME` | With `--zip-archive`, set the suggested filename when choosing a folder; omitted uses device name plus date/time. |
| `--preview` | Show a backup/restore plan without applying it, or preview a catalog rebuild. |
| `--rebuild-catalog PATH` | Rebuild one backup set's SQLite catalog. |
| `--no-pause` | Do not wait for a key before exiting; interactive operation choices still apply. |

Examples in PowerShell, from the executable folder:

```powershell
# Detect a device and use interactive choices
.\GearBackup.exe

# Preview a local source using a fixed history
.\GearBackup.exe --source "E:\" --profile "Studio" --preview --no-pause

# Back up a folder and export a full backup to ZIP
.\GearBackup.exe --source "D:\M8Card" --profile "Studio" --zip --no-pause

# Save a standalone ZIP; D:\Archives must already exist
.\GearBackup.exe --zip-archive --source "D:\M8Card" --output "D:\Archives" --name "Live set" --no-pause

# Restore into a PC folder
.\GearBackup.exe --restore --source "D:\RestoredFiles" --profile "Studio" --no-pause

# Check saved backup content
.\GearBackup.exe --check --no-pause

# Capture camera recipes/settings
.\GearBackup.exe --camera-settings --no-pause
```

Use `Mode=full` or `Mode=incremental` to choose a CLI backup mode without its mode menu. CLI incremental backup updates the latest set in the resolved history; selecting a different set is a GUI feature. There are no `--full` or `--incremental` flags.

`--restore` and `--check` cannot be combined. `--zip` cannot be combined with either. `--camera-settings` cannot be combined with restore, check, or a local source. Catalog rebuild accepts only its path, `--preview`, and `--no-pause`. `--check --preview` prints an explanation and exits without performing the check; run checks without preview.

`--zip-archive` cannot be combined with `--restore`, `--check`, `--camera-settings`, `--rebuild-catalog` or `--zip`. `--output` and `--name` require `--zip-archive`.

| Exit code | Meaning |
| --- | --- |
| `0` | Completed, previewed, declined, or cancelled without an operational failure. Check the summary to distinguish these outcomes. |
| `1` | Could not start/complete the requested workflow because of an error, invalid input, or prerequisite failure. |
| `2` | Backup/restore reported failed work, or an integrity check found damaged/missing content. |

**Ctrl+C** requests cancellation. Do not treat exit code zero alone as proof that a scheduled backup completed: review the output/status. `--no-pause` does not make the app fully noninteractive.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Device not detected | Check its USB mode and data cable. Retry Detect device. For a drive/card, browse to it under More options. For a local source, select the appropriate device type. |
| X100VI settings option missing | Connect one X100VI in USB RAW CONV./BACKUP RESTORE and detect again. Close camera applications that may hold the connection. |
| Restore list empty | Confirm the backup location/profile locations. If a device is detected, only matching device types appear; with no detected device, all types appear. Backups need valid explicit device metadata; folder names alone do not identify them. |
| Restore points cannot load | Inspect the error in the app. Keep the entire metadata/content folder intact; try a catalog rebuild if the index is damaged. Rebuild does not restore missing objects. |
| Files unexpectedly marked changed | Check size and timestamp differences. The normal comparison tolerates two seconds. Enable content comparison when timestamps are unreliable; MTP mirroring can add read time. |
| Backup occupies more space than expected | Count separate full sets, retained changed content, ZIP exports, and MTP mirrors. Content is stored once inside each set. |
| Settings ignored | Check installed AppData versus portable executable-folder INIs, then device/profile overrides. Both interfaces share the same effective settings. |
| Path/name warning | Shorten the backup location or source names. Name warnings depend on the device profile. Excessive Windows paths can prevent an operation. |
| Eject not offered or fails | Only eligible positively detected USB drives can be ejected. Close applications using the drive. MTP devices and manually selected folders are not ejected this way. |
| Interrupted backup | Check its Incomplete/Failed status and operation report. The app can offer to resume eligible incomplete full backups; an uncommitted single-store run does not replace the previous completed point. |

Source, backup, and recovery locations must not overlap in unsafe ways. Junctions/symbolic links and paths that escape their expected roots are rejected. Use ordinary folders rather than trying to bypass those checks.

## Building and testing

For source builds, install **Visual Studio Build Tools** with **.NET desktop build tools**. The shared compiler script selects a supported C# compiler (C# 7.3); no NuGet packages or .NET SDK are required for the app. **PowerShell 7** is required for HTML manual generation. The GUI also needs the **Rust** toolchain (https://rustup.rs, default settings) to build `m8_reader.dll`, the M8 song reader in `native/m8-reader`; `build\build-native.ps1` builds it, and `-Test` runs its tests. Packaging also requires the **Inno Setup** compiler.

From the repository root:

```powershell
# CLI and GUI; --no-pause is for the build wrapper
.\build.cmd --no-pause
.\build-gui.cmd --no-pause

# Standard and portable installer packages, plus SHA-256 files in dist
.\build-installer.cmd

# Regenerate all offline manual styles into a chosen folder
pwsh -NoProfile -File build\build-manual.ps1 -OutputDirectory dist\manual

# Core and GUI tests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-gui-tests.ps1

# Only some tests (names or method names containing the text), or list them
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Filter zip,mtp
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1 -List

# Packaging and PATH checks
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-installer-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-portable-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-cli-path-tests.ps1
```

The installer build refreshes both executables and the manuals before packaging them. App version information comes from `src/Foundation/AppIdentity.cs`. The code layout and its layering rules are described in `docs/ARCHITECTURE.md`. Gear Backup is licensed under **GPL-3.0**; see the included `LICENSE` and the [project repository](https://github.com/bondis2k/Gear-Backup).

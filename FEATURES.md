# Gear Backup features

Gear Backup is a Windows application for backing up music gear, camera content, memory cards and local folders. This document describes the capabilities implemented in the current source tree. For installation, see the [README](README.md); for instructions, see the [manual](MANUAL.md).

## Available operations

| Capability | Graphical app | Command-line app |
| --- | --- | --- |
| Full and incremental file backups | Yes | Yes |
| Restore files and saved snapshots | Yes | Yes |
| Check backup integrity | Yes | Yes |
| Capture X100VI recipes and shooting settings | Yes | Yes |
| Preview backup and restore plans | Yes | Yes |
| Select sources, destinations and profiles | Yes | Yes, through arguments and INI settings |
| Review backup health in a dashboard | Yes | — |
| Inspect storage and select cleanup items | Yes | — |
| Edit device and profile preferences | Yes | Edit the shared INI file |
| View recipes, copy them and export readable text | Yes | — |

Both apps use the same backup engine and settings. The GUI provides file and folder pickers, a responsive activity log, progress, cancellation and shortcuts to open operation results. The CLI provides an interactive menu and arguments for source, profile, restore, checks, camera capture, ZIP export and preview.

## Supported sources

| Source | Connection | Device-specific behavior |
| --- | --- | --- |
| **Dirtywave M8** | USB disk mode or SD card reader | Song change summaries, bundle restore, song-and-samples restore, filename warnings |
| **Polyend Tracker Mini** | USB drive mode or SD card reader | Card recognition through `trackerConfig`, project change summaries, whole-project restore |
| **Teenage Engineering OP-1 Field** | MTP | Local mirroring of exposed device files, including synths, drums, tapes and mixdowns |
| **Teenage Engineering OP-XY** | MTP | Local mirroring and `.xy` project summaries; automatic project backups are copied but omitted from project summaries |
| **Fujifilm X100VI** | MTP for media; USB RAW CONV./BACKUP RESTORE mode for settings | Photo/video backup, connection-mode display, recipe and shooting-settings capture |
| **Memory cards and local folders** | Accessible filesystem path | Manual source selection and separate backup histories |

Supported connected devices are detected automatically. USB-drive detection includes drive-label fallbacks for the M8 and Tracker Mini. A device type can be assigned explicitly when using a card or local copy.

MTP transfers first refresh a local PC mirror, downloading new or changed content before backup. MTP backups restore to a local folder for manual transfer; Gear Backup does not write files back to MTP devices. OP-XY backup covers the content exposed by the device, rather than factory content that is unavailable over MTP.

## File backup

- **Full backup:** copy the source into a new dated folder.
- **Incremental backup:** copy new and changed files into the latest backup, retaining previous copies of changed files under dated names.
- **Comparison choices:** use size and modified time, with a two-second timestamp tolerance, or enable SHA-256 content comparison. Files without usable timestamps are compared byte by byte.
- **Resume:** continue an incomplete or failed backup in the same folder, using the selected comparison mode. Detected suspect copies are moved aside.
- **Folder preservation:** keep empty folders and skip common operating-system leftovers such as `.DS_Store`, `Thumbs.db` and trash folders.
- **Space planning:** estimate required space for copies, history and optional ZIP output before copying.
- **Path-length check:** stop before copying, with the offending path named, when a backup or restore would create a path over Windows' 260-character limit.
- **ZIP export:** optionally create a separate archive after a successful full backup. It contains files, empty folders, checksums and a report, with local paths hidden in the report and internal history omitted.

Incremental previews list actions rather than skipped files. If there are no new or changed files to copy, the normal run skips the proceed prompt. Full-backup progress suppresses individual file paths.

## History and storage choices

Point-in-time history records the files and folders present during each backup, so a saved state can be selected later. Identical content is shared within a backup folder's history store.

| Storage choice | Behavior |
| --- | --- |
| **History enabled** | Keep restorable snapshots and their content alongside the browsable backup files. |
| **History disabled** | Keep backup files and checksums without an additional history content store. Incremental backup, checks and current-file restore remain available. |
| **NTFS hard-linked history** | Share content between backup files and history instead of making separate copies. Requires history to be enabled; other filesystems use copies. |

Turning history off preserves existing snapshots. Dated old versions, separate full backups, ZIPs and MTP mirrors have separate storage costs. Hard-linked files share content, so editing or damaging one can also affect its history copy.

## Restore and recovery

Restore can use current backup files or a saved snapshot. Available selections include all files, a single file, an M8 bundle, an M8 firmware-6 song with its referenced samples, or a Tracker Mini project folder. Retained old versions can be restored under their original or dated names.

Current-file restores distinguish recorded old versions from genuine filenames containing dates. Device-specific restore choices and preferences follow the selected backup; new backups record the device even when a custom profile name is used.

- **Merge:** restore selected content while keeping extra target files.
- **Exact:** remove extra target content after successful copying and a separate confirmation.
- **Overwrite choices:** replace differing files, keep the target versions or cancel; newer target files are highlighted. Keeping differing files disables exact removal.
- **Target checks:** recheck files before replacement or removal to detect changes since planning.
- **Staged replacement:** write replacement files beside their destinations and use Windows file replacement with rollback handling.
- **Optional recovery:** copy files that will be replaced or removed to a configurable PC location before any target changes begin. A recovery-copy failure stops the restore.

Restore progresses file by file. Completed restores remain in place if a later step fails or is cancelled. X100VI media restore recommends a PC folder and asks before restoring onto a memory card.

## Integrity checks

Every backup records SHA-256 checksums in `checksums.sha256`. **Check backups** rereads selected backups and snapshot content for missing or damaged files without needing the source device.

Two independent options control automatic comparison:

| Option | Purpose |
| --- | --- |
| **Compare file contents** (`Thorough`) | Use SHA-256 for change detection during backup, resume and restore. |
| **Verify copies** (`Verify`) | Read copied files back and compare checksums; also verify restore sources, history objects and recovery copies. |

Both default to off. With size/time comparison, an edit preserving both can be missed. Recording checksums does not itself mean a backup has been verified; explicit integrity checks use them even when automatic verification is disabled.

Backups record complete, incomplete or failed status. Path validation rejects linked or escaping paths, history locks prevent concurrent writes, and MTP mirror locks coordinate transfers with cleanup. Windows system DLLs are loaded from System32.

## X100VI settings and recipes

The camera-settings operation captures **C1–C7 names and 23 raw properties per recipe**, plus current shooting settings, in **USB RAW CONV./BACKUP RESTORE** mode. XML exports retain raw bytes, numeric values, model, serial and firmware; unavailable properties are marked explicitly.

Each capture creates a dated full backup in its own history, never the photo history: `x100vi-settings-<serial>` automatically, or `<profile>-settings` for a named profile, using that profile's preferences. Checksums, optional history and ZIP export are supported. Camera capture restores and verifies the original recipe read selector, checks that shooting values stayed unchanged, and prevents concurrent Gear Backup captures of the same camera.

The **X100VI recipes** viewer opens a saved backup folder or individual XML export without a connected camera. It shows readable film simulations, white balance, grain strength/size, tone values and noise reduction alongside protocol numbers and raw bytes. Selected recipes can be copied or exported as text. Unmapped values remain labelled as protocol values.

Settings capture does not provide incremental backup or resume. It does not export full-camera setup menus or native Fujifilm backup files, and it cannot restore settings to the camera. Settings preview temporarily selects recipe read banks and restores the selector.

For media, Gear Backup copies `DCIM`, preserves camera-reported capture times and reports JPG, RAF, HIF and MOV files. A JPG/RAF pair counts as one shot in summaries.

## Profiles and backup management

Dashboard uses a metadata-only scan for backup health, success times, integrity results and device filters. Detailed size accounting, hard-link identities and cleanup scans run in Storage. Unchanged completed backups without history or retention avoid redundant checksum/statistics writes while still checking file/folder inventories and updating the Dashboard success time.

Automatic profiles separate histories by source: card volume identity, folder path or MTP device identity. A card can retain its history after a reader or drive-letter change. Named profiles provide an explicit history name.

Restore and check history selection covers all configured backup locations, with location labels distinguishing profiles that share a name.

Preferences are shared by both interfaces through `GearBackup.ini` and `GearBackup.Profiles.ini`. Installed copies use `%APPDATA%\GearBackup`; portable copies and local builds keep settings beside the executables. Existing installed INI files migrate on first normal launch without overwriting destination settings or deleting originals; previews remain read-only. Device defaults override global settings; profile preferences override device defaults. The GUI profile editor can set backup destinations, history, comparison, verification, retention, recovery locations, previews, filename warnings and ejection, or leave values inherited. Values are checked when saved; an invalid value in the file is reported and the inherited value is used.

The profile editor separates device settings from backup profiles, groups options into Backup, Restore and Advanced tabs, and shows each setting's default. Uncheck **Use default** to customize a value; **New profile**, **Save changes** and **Reset to defaults** provide explicit actions.

The GUI provides two management views across configured backup locations:

- **Dashboard:** last successful backup, latest run status, latest backup's integrity-check time/result, profile storage total and attention indicators. Backups older than seven days are marked overdue. A **Device** filter (default: all devices) lists one device's profiles. Actions open folders, check backups and view saved recipes.
- **Storage:** sizes for current files, known old versions, history objects, metadata, ZIPs and MTP mirrors. The unique-content total counts hard-linked content once; it measures file content rather than allocated disk space.

Cleanup starts with nothing selected. Choose older backup folders, known old versions, ZIPs or mirrors, then review the plan before moving them to the Recycle Bin. The latest backup folder and individual history objects are protected. Cleanup rechecks scanned items, respects operation locks and updates checksum records after removing old versions.

Retention options can limit whole backup folders or dated old versions per file, with confirmation before recycling. Snapshot contents are retained separately from browsable old versions. Recycled items occupy disk space until the Recycle Bin is emptied; removing a mirror requires downloading it again on the next backup.

## Defaults and everyday controls

Backups default to `%USERPROFILE%\Backup`. Starting a backup asks before creating a missing destination, including device/profile overrides and X100VI settings backups. Choosing No cancels; previews do not create the folder.

| Setting | Default |
| --- | --- |
| Change previews | On |
| Point-in-time history | Off |
| SHA-256 change comparison | Off |
| Automatic copy verification | Off |
| Restore recovery copies | Off |
| NTFS hard links | Off |
| Backup and old-version retention limits | Unlimited |
| Automatic ejection | Off |
| Backup location | `%USERPROFILE%\Backup` |

The GUI preview table supports search and action filters without changing the execution plan. Cancel or Ctrl+C stops further work and requests cleanup; interrupted file backups can be resumed. Progress includes stages, processed bytes and elapsed time where available.

Backup reports contain counts, sizes, speed, warnings, failures and free space, with device-aware change summaries. The GUI shows results and output-folder shortcuts; the CLI sends Windows result notifications. Detected USB-drive devices can be offered for safe ejection or ejected automatically after confirming they still own the selected source.

Keyboard shortcuts, accessible control names, tooltips and DPI-aware layouts support GUI navigation. **Help** and **F1** open the illustrated offline HTML manual.

The main window focuses on **Detect device**, the operation choice and **Back up**, with a compact destination summary. **More options** holds the source folder, backup location, optional profile and zip choice. Restore automatically reveals its destination; Cancel appears while work runs.

Detected devices have a large highlighted name and picture, with the connection/camera mode shown separately. Detecting an MTP device clears the previous USB source path and drive selection.

**Settings → Appearance** offers **Original** (light gray and teal), **Original (dark)** (charcoal and teal) and **Retro** (teal, navy, gray and cream; default). Saving applies the theme immediately and persists it as `Theme=original`, `Theme=original-dark` or `Theme=retro` in the global INI. Help opens the matching themed manual. Windows 11 title bars, title text and borders match the selected palette. Supported earlier Windows versions use standard dark title bars for Original (dark). App dialogs share the Gear Backup icon.

## Distribution

Per-user setup and portable extraction packages include both apps and the offline manual. Updates preserve personal INI settings. Portable extraction creates no shortcuts or uninstall entry; backup paths remain configurable and MTP mirrors still use the PC's Local AppData folder.

Setup offers an **Add CLI to PATH** checkbox, unchecked by default, for running `GearBackup.exe` from any folder in a new terminal. It updates the current user's PATH, avoids duplicate entries and remembers the choice on upgrades. Deselection or uninstall removes installer-added entries while preserving pre-existing entries. Portable extraction leaves PATH unchanged.

Existing settings and histories from the previous application name remain supported. Build and automated-test instructions are in the [manual](MANUAL.md#building-and-testing).

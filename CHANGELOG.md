# Changelog

All notable changes to Gear Backup are listed here, newest first. Versions follow the releases on
[GitHub](https://github.com/bondis2k/Gear-Backup/releases). The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Device-filtered Restore tab with SQL-backed full/incremental restore-point selection, latest defaults and a checked folder/file browser. Saved runs record whether they came from a full or incremental backup; restores revalidate device, inventory and selections under the history lock. Exact restore remains optional.
- SQLite-first incremental inventory for both storage layouts, with current-row queries, stale/incomplete inventory detection, recovery-record fallbacks, live browsable-file checks and automatic catalog repair after successful fallback runs.
- Per-set full backup names in the Backup tab, switching to a named full-backup dropdown for incremental updates. Explicit selections are checked under the history lock; older unnamed backups remain selectable. The separate grouping field is labelled History name.
- Device, Backup and Restore tabs in the main window, with a drive storage overview, connection details and independent backup-source/restore-destination fields.
- Optional single content store (`SingleContentStore=yes`), saving each distinct content once within a backup set and recording a restore point for every successful run, including unchanged incrementals. Settings, restore, integrity checks, SQLite inventory, ZIP export and the X100VI recipe viewer support this layout. New full backups select the layout; existing sets remain usable.
- A rebuildable `.gearbackup\catalog.sqlite` index in each backup, recording file/folder inventory,
  snapshot membership and per-file verification results, plus the last 100 integrity-check summaries.
  Backup, check and old-version cleanup operations refresh it; checksum lists/manifests stay authoritative.
- `--rebuild-catalog BACKUP_FOLDER` rebuilds the index without creating new checksum baselines.
- **Storage** has a **Device** filter, like the Dashboard's (default: All devices). It filters both
  Space usage and Cleanup; MTP mirrors belong to the device they were read from, and changing the
  device clears checked cleanup items so nothing hidden from view is recycled.
- **View recipes** in Storage, working on the backup selected in Space usage.
- Backup histories are shown by readable names ("M8 · card DCE92C2E", "X100VI · photos", "X100VI · settings")
  in Dashboard, Storage, settings and the restore/check pickers (GUI and CLI), instead of folder IDs.
- **Rename…** in Dashboard and Storage gives a history a nickname (e.g. "Studio card"), stored with its
  backups; the folder on disk keeps its name.
- The main window remembers its position, size and maximized state (`GearBackup.Window.ini` beside
  `GearBackup.ini`) and opens there next time; it opens centred if that place is no longer on a screen.

### Changed
- The **Choose files to restore** window starts with all folders collapsed and opens at a narrower, more appropriate size. Tool windows (Storage, Dashboard, Device & backup settings and others) no longer leave dead space under their bottom buttons.
- **Use a single content store** is now on by default for new full backups (`SingleContentStore=yes`). Turn it off in Settings for plain, browsable backup folders. Files are stored by content, so a file with the same name but different content never overwrites another; each restore point records the original names and restores them.
- Incremental restore points use `yyyy-MM-dd_HH.mm.ss` names (date and time), with collision suffixes to preserve existing points. Capture timestamps are stored independently in metadata and SQLite, keeping latest defaults correct across days and midnight.
- Pre-1.0 builds support the current settings, metadata, snapshot and SQLite formats only. Removed former product-name aliases, settings-location migrations, history renaming/reuse, device inference from history names and snapshot-based old-version inference.
- "Profiles" are now called what they are: the main window's field is **Backup name (optional)**, which
  starts a separate history, and the **Profiles** window is **Device & backup settings**. `GearBackup.ini`,
  `GearBackup.Profiles.ini` and `--profile` are unchanged.
- Color themes are listed as **Original**, **Original (dark)**, **Retro**, and **Original** is now the
  default for new settings. `Manual.html` is the Original-themed manual.
- **More options** in the main window always starts collapsed. The summary line under the buttons
  also shows a source folder when one is set (or the restore destination).
- **View recipes** in the Dashboard is greyed out unless the selected backup holds saved X100VI recipes.
- The **X100VI recipes** link was removed from the main window; open recipes from a settings backup
  in Dashboard or Storage (the viewer can still open any folder or XML export).
- Storage's **Cleanup** tab describes each item in plain words ("Older backup · M8 · card … · date",
  "Downloaded device copy · X100VI · photos") with an **If removed** column, a **Remove** tick column, a running
  selection total, and **Preview cleanup** available only once something is ticked. MTP mirrors are called
  downloaded device copies.
- Dashboard and Storage start filtered to the device detected in the main window (if it has anything to
  show), instead of All devices.
- Tool windows and dialogs open centred on the main window instead of the screen.
- Dashboard reads only health metadata, about 1.8× faster; sizes and cleanup are in Storage. Copying
  and hashing reuse buffers, and unchanged backups without history skip redundant checksum and
  statistics writes (about 2× faster for small files). (#50)

### Fixed
- The **Back up** and **Preview** buttons are available again after visiting the Restore tab and going back to Backup; Restore had left them disabled.
- Restore picker background reads no longer overwrite the main operation display or leave a stale “Scanning backup” status. Idle/completed operation displays ignore late queued progress.
- Dark theme: drop-down buttons and scroll bars use Windows' dark look, and the selected row in the backup chooser uses the theme's selection colour instead of Windows blue. The Cleanup tab's **Remove** header is no longer cut off, and Original's grey secondary text is slightly darker for readability. Disabled check boxes and radio buttons (such as Restore's **Remove extra target files**) no longer show near-black text on the dark background.
- SQLite restore loading validates repeated inventory paths and content objects once per read, avoiding thousands of redundant disk checks across saved runs. Restore rechecks source paths before opening files.
- Restore checks the target's folders again right before committing files, instead of trusting
  checks cached during the preview. (#51)
- Writing statistics can no longer truncate an unrelated file hard-linked in its place. (#51)
- ZIP export rejects links in backup metadata, so it can't include files from outside the backup. (#51)

### Security
- Every read and write of metadata files (status, records, checksum lists, manifests, settings) goes
  through one place that rejects links and enforces size limits. Checksum lists, status files,
  snapshots, recovery records and settings were previously written without a link check. Settings
  files over 1 MB or reached through a link are now refused. (#53)
- Listing an MTP device checks for Cancel between batches, stops when a faulty device keeps repeating
  the same items, and refuses more than 200,000 items in a folder or 2,000,000 in total, so a device
  that never stops listing can't loop forever or fill memory.

### Internal
- Stages, statuses, restore modes and storage kinds are typed values instead of text. (#52)
- No hidden, long-lived operation context; every operation starts its own, and device switches are
  scoped. (#54)

## [0.9.2] - 2026-10-11

### Added
- **Dashboard** has a **Device** filter (default: All devices) that lists only the chosen device's
  profiles. (#40)

### Changed
- The code is split into small, focused classes instead of one large class. Backups, restores and
  settings work exactly as before. (#42–#48)
- Builds use the modern C# compiler (C# 7.3) from Visual Studio Build Tools; the programs still run
  on .NET Framework 4.6 or newer. (#41)

## [0.9.1] - 2026-10-11

### Changed
- Verifying copied files reads the next part of a file while checking the current one (about 1.4–2×
  faster on an SSD). (#39)
- With Verify on, incremental backups no longer re-read the whole point-in-time history; existing
  history gets a size check, and **Check backups** still reads everything. (#39)
- Listing MTP devices (OP-1 Field, OP-XY, X100VI) is about 30% faster. (#37)
- Storage cleanup and MTP mirror syncing do less repeated work. (#36)
- The manual has a clickable contents index shared by all themes. (#34)

### Fixed
- **KeepOldVersions** only offers old versions Gear Backup kept itself, never your own files with a
  date in their name. (#36)
- Cleaning up storage right after a backup no longer reports "Storage changed since the preview"
  when nothing changed. (#38)
- The Profiles window opens even if one history has a damaged device record or is a linked folder. (#35)
- Backups recording a device this version doesn't know can still be restored. (#35)
- Safer handling of faulty MTP devices and oversized backup records. (#36)

## [0.9.0A] - 2026-10-10

### Added
- Setup has an optional **Add CLI to PATH** checkbox (unchecked by default); uninstall removes only
  the entry it added. (#32)

### Changed
- Installed settings live in `%APPDATA%\GearBackup`; existing INI files migrate without overwriting
  newer settings. Portable copies keep settings beside the executables. (#32)
- All windows use the Gear Backup icon and themed title bars; checkbox labels stay readable in the
  dark theme. (#32)

## [0.9.0] - 2026-10-10

First release of Gear Backup.

### Added
- Backups for the Dirtywave M8, Polyend Tracker Mini, Teenage Engineering OP-1 Field and OP-XY, and
  Fujifilm X100VI, with device detection.
- Full and incremental backups, restore previews, and optional SHA-256 verification.
- X100VI recipe and shooting-settings backups with a readable recipe viewer.
- Backup profiles, device and profile preferences, Dashboard, Storage management and optional
  point-in-time history.
- Graphical and command-line interfaces, Setup and Portable packages, and an illustrated offline
  manual in three themes (Original, Original (dark), Retro).

[Unreleased]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.2...HEAD
[0.9.2]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.1...v0.9.2
[0.9.1]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.0A...v0.9.1
[0.9.0A]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.0...v0.9.0A
[0.9.0]: https://github.com/bondis2k/Gear-Backup/releases/tag/v0.9.0

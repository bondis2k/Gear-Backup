# Changelog

All notable changes to Gear Backup are listed here, newest first. Versions follow the releases on
[GitHub](https://github.com/bondis2k/Gear-Backup/releases). The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- The Tracker Mini view can open a pattern from the **Project inspector** (**View pattern**) as a table of steps with notes, instruments and effects, and the Device tab shows a summary of the detected Tracker card's projects and any that need attention.
- The Tracker Mini view has a **Project inspector** and an **Instrument check**: they read a Tracker project (`project.mt`, patterns and instrument files) and list its instruments and patterns, and the instruments that notes play without a usable instrument file. Checked against a real Tracker Mini card: `N name.pti` instrument names, the 392-byte header and original-Tracker `.mti` projects with `samples\instrNN.wav` are understood.
- Optional **Link replacement card to the original backup history** during M8 migration, saved after successful verification and song checks. Future backups use the original history, location and preferences across drive-letter changes; explicit profiles override linking.
- M8 **Card migration** restores a selected complete backup point to an empty replacement card or test folder, with mandatory preview/checksums, target identity checks, post-copy file verification, song dependency checks, progress/cancellation and a saveable migration report. It does not format storage or delete existing content.
- M8 mode now has a Sample check tab: inspect a song's sample references against a card folder and search M8 backup histories, including older restore points. Results include referencing instrument slots, exact-path and filename-only matches, and SHA-256 verification of matching saved objects. Checking can be cancelled and does not restore or modify song/sample files.
- Double-clicking a verified sample backup copy asks to restore it and opens individual restore with the matching backup, saved point and file selected for destination review.
- M8 Sample check has a **Check all songs on card** button, which scans songs recursively, reports missing samples per song, distinguishes unreadable songs and invalid references, and supports selection of per-song details, progress and cancellation.

### Fixed
- Tracker patterns now include the final played step when viewing notes and checking missing instruments; stored track lengths are zero based. Invalid lengths are refused.
- MTP mirroring checks cancellation before publishing transfers even when a device sends no progress updates, and replaces existing files atomically with rollback protection.
- Backup and restore orchestration passes its operation context explicitly for interaction, preview, device/profile resolution, history selection, hashing and recovery. Restore planning and GUI operation dispatch are split into focused steps; nested restores keep separate contexts, and late cancellation preserves an existing restore failure.
- GUI inventory and inspection requests share cancellation, stale-result and disposal handling. Core and GUI tests use separate build directories, and fixture cleanup removes links without traversing their targets; cleanup failures now fail the run.
- Cancelled restore/check selections and read-only previews now have explicit operation outcomes. Only successful operations qualify for automatic device eject; notification titles no longer determine success.
- Coverage reporting returns a failing test process's exit code even when coverage meets the minimum. Restore inventory reads are cancelled when superseded or closed, and obsolete results cannot replace the current backup selection.
- Tracker checks use the 392-byte Mini instrument layout, retain legacy `.mti` sample checks, and lock scan inputs to prevent stale results. Project/file discovery streams bounded entries and reports incomplete checks instead of claiming all instruments are usable.
- M8 migration locks its path inputs while copying and cancels superseded inventory loads. Saved card-history links reject ambiguous relative paths and report duplicate identities clearly.
- The sample backup restore confirmation uses the application's theme, including its title bar, details and action buttons.
- Toggling M8 backup-history search preserves card results. Re-enabling it refreshes the selected song's backup copies automatically, including selections from the whole-card list.
- Sample check path fields retain readable theme colours after a check and when changing themes while the tab is inactive. Detecting an M8 also fills an empty card-root field.
- Individual and whole-card M8 sample checks resolve relative sample paths beside the actual song, fixing false missing-sample reports for demo/bundle songs such as SNOWYDAY. Paths beginning with `/` remain rooted at the card; backup matching uses the resolved location.

## [0.9.5] - 2026-10-16

### Added
- The header shows gear icons between the title and Help, About and Settings: Gear Backup, M8, Tracker Mini, OP-XY and OP-1 Field. Gear Backup is the whole app as before (Device, Backup, Restore). Choosing a device icon enters that device's mode: it looks for a connected device of that type, keeps the Device tab without the Go to Backup and Restore shortcuts, and replaces the Backup and Restore tabs with Coming soon and Planned placeholders until the dedicated tools exist. Detecting a device from Gear Backup mode leaves every backup tool in place.
- A **Song** tab after Restore opens a Dirtywave M8 song (`.m8s`) and shows its name, firmware, tempo, transpose, quantize, song length, chain/phrase/table counts, rows used per track and every instrument with its type, name and sample. Songs are only read, never changed.
- M8 songs are read by `m8_reader.dll`, a small Rust library (`native/m8-reader`, built on the `m8-file-parser` crate) that ships next to the GUI. The app talks to it through a reader interface and a versioned JSON summary, so the reader can be replaced later without changing the window.
- The Restore tab supports standalone ZIP archives, with a file picker, device-type matching, mandatory checksum verification, validated extracted sizes, target/temporary-space checks, merge restore and existing recovery preferences. MTP restores use a PC folder for manual transfer.
- Standalone ZIP backups include `gearbackup-info.txt` identifying the source device and recording backup details, source-content size and exact total extracted size, with checksum coverage for the information file.
- The CLI supports standalone ZIP backups with `--zip-archive`, an optional output file/folder and suggested filename, using the same archive engine as the GUI.
- Standalone ZIP archives include `checksums.sha256` with hashes of all backed-up files, computed while archiving regardless of the verification setting.
- **Zip archive** backup type creates a standalone full ZIP at a chosen save location. Uses the optional name or device name plus date/time, preserves empty folders, supports preview and optional verification, and safely publishes the archive without a separate backup set.

### Changed
- The Song tab (reading an M8 song) now appears only in the M8 view; the Gear Backup view and the other device views no longer have it.
- Rewritten manual, FAQ and feature inventory, including the complete CLI switch reference, worked examples, current device workflows and format/safety limitations.
- The shared code is now `GearBackup.Core.dll`, installed beside `GearBackup.exe` and `GearBackup.GUI.exe` (and included in the portable package), instead of being compiled into each program. Source files are in namespaces (`GearBackup`, `GearBackup.Cli`, `GearBackup.Gui`).
- Backups copy small files (up to 4 MB) four at a time and large files one at a time, so a card or network share with thousands of small files backs up much faster (5,000 files of 3 KB: 45 s down to 17 s on a local disk, with Verify on). The restore point lists files in the same order as before.
- A failed backup now says why in the CLI output.
- Failures of a known kind now come with a suggestion: damaged backup content (run Check backups), a history in use by another window, a backup in an older layout (make a new full backup) and refused links or unsafe paths. The error kinds are `DamagedContentException`, `HistoryBusyException`, `UnsupportedBackupException` and `UnsafePathException`, all still `IOException`s.
- The catalog is updated from the previous one after each backup run instead of being rebuilt from every restore point, so a backup of a set with many restore points no longer slows down as the history grows. It is still rebuilt in full when it is missing, damaged, out of date, or after a check.
- Every backup is now a content store. Backups can no longer be made as plain, browsable folders, so each file's content is stored once per backup set and every run saves a restore point. An incremental backup of a backup made by an earlier version is refused with a message to make a new full backup. Restore through the app, or export a ZIP for ordinary files.
- Retention applies to whole backups only. All restore points of a backup remain until the backup is removed.
- The name check (names the device may not handle well) now runs on every backup and lists up to ten names.
- Updating a backup's catalog is faster, and the gain grows with the number of restore points: each distinct file name is validated once instead of once per restore point (about half the manifest-reading time at 30 restore points of 2,000 files), the temporary catalog is written in one transaction without its own journal or disk syncs, since it is flushed to disk when published, and the old catalog is read only for the rows that carry a recorded integrity check instead of in full (35 ms instead of about 500 ms at 30 restore points of 2,000 files). Catalog contents are unchanged.
- Restore works without a connected device: all available device backups are listed and can be restored to a folder. Detection still filters the list to that device type; restore requests retain and revalidate the selected backup's identity.

### Fixed
- The command-line app crashed with "No operation is running" instead of printing an error message when a run failed.

### Removed
- The settings `SingleContentStore`, `PointInTimeHistory`, `HardLinks` and `KeepOldVersions`, their controls in Settings and Device & backup settings, and the per-file ("browsable") backup layout they selected, including dated old versions, NTFS hard-linked history, the per-file `checksums.sha256` list, `backup-statistics` reports and old-version cleanup in Storage.

### Security
- Refuse restore destinations overlapping the source backup, configured backup repository or recovery storage; CLI and GUI use the same checks, repeated after approval.
- Enforce MTP download sizes while streaming, including empty files, and reject invalid read counts before writing.
- Inspect ZIP/ZIP64 central directories with bounded buffers before allocating archive entries (32 MB directory, 1 KB entry names, 4 KB per-entry extra data/comments). Replaced inspections and window closure cancel pending reads.
- Enforce metadata byte limits on the opened stream, and revalidate retention folder identity, contents and latest-backup protection after confirmation and before recycling.
- Restrict native reader dependency loading to System32 and check its PE import table during builds; CI tests reject unexpected and delay-loaded dependencies.
- The M8 reader DLL is built with source paths remapped, so it no longer contains the builder's user name or folders, and the build refuses to produce a DLL that does.
- `m8_reader.dll` is loaded by full path with its own dependencies restricted to System32. The `GEARBACKUP_M8_READER` override is removed, so the load path cannot be redirected through the environment.
- Windows device names (CON, PRN, AUX, NUL, COM0-9, LPT0-9, CONIN$, CONOUT$, with any extension) are refused in every path from a backup, ZIP or MTP device, not only in ZIPs.
- Song names, instrument names and sample paths shown on the Song tab have control, zero-width and bidirectional characters removed, and an & is shown literally instead of as a shortcut key.
- Reading an MTP device checks free space before the first download, and refuses a device that lists more than a million items, negative sizes or overlong paths. A ZIP with more than a million entries is refused.
- The M8 reader DLL is built with Control Flow Guard, and the build checks that it has ASLR, DEP and Control Flow Guard.
- The Rust reader has a pinned toolchain, weekly Dependabot updates (crates and GitHub Actions), clippy, a deeper fuzz run and `cargo audit` in CI.
- Added `SECURITY.md` with how to report a vulnerability, and a manual note that ZIP checksums detect damage, not tampering.

## [0.9.4] - 2026-10-11

### Added
- **Windows** appearance, listed above Retro, follows Windows app light/dark mode, accent color and high-contrast colors. System preference changes update open windows, and Help has a matching manual that follows the browser's system light/dark preference.
- Gear Backup is released under the GNU General Public License, version 3: a `LICENSE` file in the repository and the installer, a License section in the README and a line in the About window.
- An incremental backup can be given an optional name on the Backup tab. The Restore tab lists it beside the date and time (`Incremental · Studio jam · 2026-10-11_14.32.08`), and the command-line restore picker shows it too. It is saved with the restore point in both storage layouts.

### Changed
- The manual's **Connecting a device** table shows each device's icon beside its name.
- The shared folder picker has a clearer header, quick-access shortcuts, folder and drive icons, larger selection rows, and Up/Go navigation. Restore, backup, recovery and other folder Browse buttons use the same design in all three themes.
- Restore's **Choose files…** window uses the matching browser style, with file/folder icons, a selection summary, and Select/Clear and Expand/Collapse controls above the tree. Expanded folders and selection survive font/DPI changes.
- The About window is larger and shows the app icon beside the name, version and license.
- Backups with a single content store are much faster, most of all when little has changed. Every backup rebuilds its catalog from all saved restore points, and each entry was re-checked for links by walking its whole path, so the time grew with every restore point saved. Each content object and folder is now checked once. A backup of an M8 card with 3,167 unchanged files went from 76 s to about 2 s.

### Fixed
- Restore's file selector creates file rows when their folder is opened, avoiding startup delays on large backups. Folder selections and selection counts include unopened contents.
- File and folder browsers open faster by caching native icons and preparing restore nodes before displaying them. Native checkbox/icon placement prevents overlap during scaling.
- Switching between the Restore and Backup tabs no longer makes the window flicker. The window now holds its repainting while the new tab is arranged and paints once, which cut the number of pictures shown during a switch from about 9 to 2 or 3.
- The **Browse…** buttons (restore destination, source folder, backup and recovery locations, per-device settings and the X100VI settings folder) open a folder picker that follows the colour theme, instead of the light-only Windows folder dialog. It shows your folders and drives as a tree with an editable path, and a **New folder…** button where a new folder makes sense. The **Open XML** file chooser is still the standard Windows one.

## [0.9.3] - 2026-10-11

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

[Unreleased]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.5...HEAD
[0.9.5]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.4...v0.9.5
[0.9.4]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.3...v0.9.4
[0.9.3]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.2...v0.9.3
[0.9.2]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.1...v0.9.2
[0.9.1]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.0A...v0.9.1
[0.9.0A]: https://github.com/bondis2k/Gear-Backup/compare/v0.9.0...v0.9.0A
[0.9.0]: https://github.com/bondis2k/Gear-Backup/releases/tag/v0.9.0

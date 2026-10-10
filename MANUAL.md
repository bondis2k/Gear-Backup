# M8 Backup — Manual

M8 Backup is a Windows program with graphical and command-line interfaces for backing up and restoring a Dirtywave M8 SD card. It also works with a card in a reader or a folder containing a copy of a card. It makes full or incremental backups, keeps dated snapshots, verifies copies, and restores all files, a bundle, a single file, or a song with its samples.

Requires Windows 10 or 11 and the built-in .NET Framework. No installation or extra runtime is needed.

Executable builds are ignored by Git. If you downloaded or cloned the repository, run `build.cmd --no-pause` to create `M8Backup.exe` before following the steps below.

## Getting started

1. Connect the M8 by USB and enable USB drive mode. Save your song first and leave the M8 idle while copying.
2. Double-click `M8Backup.exe`.
3. Review the selected source and profile. If a backup exists, choose full backup, incremental backup, restore, or check. With no backup in the current profile, the program prepares a full backup.
4. Review the file preview and press Y to proceed.

The settings file is `M8Backup.ini` beside the executable. Missing options are added with their defaults and explanations on an ordinary run. A `--preview` run does not write settings or backups.

If no M8 is detected, enter a drive or folder path, such as `E:\` or `D:\M8Card`. Press Enter to cancel. You can also set `SourceFolder` or pass `--source`.

Automatic detection finds a USB disk reporting an M8 vendor/model, with a fallback to a removable drive labelled M8. Drives selected manually are not automatically ejected.

## Graphical interface

Run `build-gui.cmd --no-pause`, then open `M8Backup.GUI.exe`. The existing `M8Backup.exe` command-line interface remains available separately. Both executables use `M8Backup.ini` beside them and the same backup histories and file operations.

1. Select the M8 card/source folder, or click **Detect M8**. Leaving the source empty also attempts detection when starting a backup or restore.
2. Select the backup location. An empty location uses the Windows Music folder. Leave Profile blank for automatic identity, or specify a named history.
3. Choose **Full backup**, **Incremental backup**, **Restore files**, or **Check backups**. For a full backup, tick **Also save as zip** to get a [zip copy](#zip-copy-of-a-full-backup) too.
4. Click **Preview** for a backup/restore plan without writing files, or use the main action button to run the operation. Review changes when prompted.

An incremental run creates a full backup if this profile has no backups yet. An incomplete or failed latest backup offers Resume, New full backup, or Cancel. An unchanged incremental run needs no acceptance prompt. Full backups show overall progress without individual file progress.

Restore opens dialogs for history, backup folder, saved snapshot/current files, and restore mode. All, bundle, single-file, dependent-sample, and merge/exact choices use the same rules described below. The current-files option is explicit in the snapshot selector; cancelling a dialog cancels the operation. Check backups needs no source card and lets you select a backup or all backups in a history.

**Settings** saves the visible paths/profile and verification, comparison, preview, recovery, retention, name-warning, and eject preferences to the shared INI file. Comments and unrelated keys are preserved. Editing the paths in the main window affects the next operation; they are persisted when Settings is saved. `Verify`, `Thorough`, and `Recovery` remain off by default. The GUI operation dropdown controls the current action; the INI `Mode` setting remains the CLI default and selects the initial GUI backup mode.

Copying and checking run on a worker thread so the window stays responsive. **Cancel** requests safe cancellation. Closing a busy window requests cancellation and waits for cleanup before closing. Results and errors appear in the status area and activity log. Automatic ejection follows `Eject` only for a source identified through detection; cancellation does not trigger ejection.

## Separate card and folder histories

New backups are stored here:

```
<BackupFolder>\M8 Backups\profiles\<profile>\<date>
```

`BackupFolder` defaults to your Windows Music folder.

With `Profile=` empty, a drive root uses its Windows volume serial number, for example `card-1234ABCD`. A folder uses an identity derived from its full path. A card formatted again normally gets a new serial, and a folder moved to another path gets a new identity. If Windows cannot read a volume serial, the program falls back to a path identity. Set a named profile when automatic identity is unsuitable:

```ini
Profile=Studio M8
```

Use different names for different cards or collections. A profile name can contain letters, digits, spaces, hyphens, and underscores, must start with a letter or digit, and can be up to 64 characters. Windows reserved names are rejected. An explicit profile deliberately overrides automatic separation: do not reuse it for unrelated cards.

Restore and check offer a history selector, including existing backups directly under `M8 Backups` as **Legacy backups**. They are not moved or silently combined with new profiles. `--profile` selects the history directly for checks; restore still allows choosing another history, such as when replacing a failed card.

## Full and incremental backup

**Full:** copy the card's user files into a new dated folder. If the date is already used, the time and fractional seconds are added to create a distinct folder.

**Incremental:** add and update files in the latest backup of the current profile. Changed files keep their previous browsable copy under a dated name, such as `Songs\Jam_2026-10-01_143000.m8s`. Deleted source files remain in the browsable backup. A dated snapshot separately records which files actually existed during this run.

### Zip copy of a full backup

Add `--zip` to the command line, or tick **Also save as zip** in the GUI, to also save a successful full backup as a zip file next to its folder, for example `...\profiles\card-1A2B3C4D\2026-10-10.zip`. Handy for archiving or copying to cloud storage.

- The zip holds the card's files and folders (empty ones included) plus `checksums.sha256` and the statistics file. The `.m8backup` history store is left out; it's only useful to this program and would double the size.
- It's written under a temporary name and renamed when complete, so a zip that exists is never half-written.
- The free-space check reserves room for it (up to the card's size, as WAV samples barely compress).
- It's skipped, with a note, for incremental backups and resumes, and when any file failed to back up. If the zip itself can't be saved, the backup still counts as complete but the run ends with exit code 2.
- The option applies to that run only; it isn't saved in `M8Backup.ini`. Deleting old backups with `KeepBackups` leaves their zip files in place.

Both modes preserve empty folders. Copies are hashed with SHA-256. With `Verify=yes`, each new copy is read back and compared; a checksum mismatch retries once. Ordinary copy errors are reported and other files continue. If the source's size or modified time changes during copying, that copy fails rather than claiming a stable result.

The program skips computer-generated leftovers:

- Names beginning with a dot, such as `.DS_Store`, `.Trashes`, and `.Trash-1000`.
- `lost+found`, `FSCK0000.REC`-style files, and filenames ending in `~`.
- `Thumbs.db`, `ehthumbs.db`, `desktop.ini`, `System Volume Information`, and `$RECYCLE.BIN`.

Linked files and folders (Windows reparse points) are rejected. An unreadable source folder fails the scan; it is not silently treated as empty. The source and backup folder must not contain one another.

### Fast or thorough comparison

```ini
Thorough=no
```

The default comparison uses file size and modified time, allowing two seconds for FAT timestamps. This can miss an edit that leaves both unchanged.

Some M8/FAT files have no usable timestamp (Windows reports 1 January 1601). For those files, the program compares the actual bytes instead of the date. Identical undated files are skipped; same-size edits are still detected. This fallback also applies during restore and does not enable SHA-256 comparison. Copies with missing timestamps retain the destination filesystem's normal timestamps.

For SHA-256 comparison of all files, use:

```ini
Thorough=yes
```

Thorough mode compares content hashes. Identical contents can be skipped even when timestamps differ. The setting applies to backups, restores, and resume. With the default `Thorough=no`, those operations compare size and modified time, which can miss changes preserving both.

A full backup copies every file anyway, so with `Thorough=yes` it doesn't hash the card beforehand: the content comparison for the change summary uses the checksums calculated while copying and the previous backup's `checksums.sha256`. The card is read once. Incremental backups and resume still hash both sides before copying, which also repairs a damaged copy in the backup.

Automatic SHA-256 integrity comparison is independently controlled by `Verify=no` (the default). Set `Verify=yes` to check copied files, restore sources, snapshot objects, and recovery copies. Each copy is read back from the disk itself, bypassing Windows' file cache, so the check sees what was actually written rather than what is still in memory; file systems that don't allow this (some network shares) get a normal read-back.

Copying reads the next 1 MB from the source while the current 1 MB is hashed and written, so the card and the backup drive work at the same time. This mainly helps when the backup drive is about as slow as the card, such as an external HDD or USB stick. Hashes are still generated for integrity records and history object identities when comparison is off. Selecting **Check backups** explicitly runs checksum checks regardless of either setting.

### Completion status and resume

Each new backup has one of these statuses:

| Status | Meaning |
|---|---|
| `incomplete` | A run started but did not reach its final status, for example because the process stopped or USB was disconnected. |
| `failed` | The run reached the end with file, history, or checksum-list failures. |
| `complete` | The run finished with its files, snapshot, and checksum list saved, using the selected verification settings. |
| `legacy (status unknown)` | An older backup without status metadata. |

When the latest backup is incomplete or failed, choose **Resume**, **New full backup**, or **Cancel**. Resume uses the selected `Thorough` comparison, repairs partial or missing copies it detects, and refreshes checksum records. Use `Thorough=yes` to detect partial/corrupt copies that retain the correct size and time. It resumes against the source as it exists now; it cannot reconstruct unsaved files or a previous source state that was never copied.

With `Verify=yes`, existing copies that fail their recorded checksum are preserved under `.m8backup\quarantine` rather than presented as valid old versions. Changed copies from an interrupted run without a recorded checksum are also quarantined. Quarantine is excluded from normal restore and integrity lists; inspect those files manually if needed.

Older completed snapshots remain selectable even if the latest incremental run failed. Browsing the current files of an incomplete or legacy backup requires an explicit acknowledgement of its status.

## Point-in-time history

Every successful backup run saves a dated manifest under:

```
<backup>\.m8backup\snapshots\<timestamp>.manifest
```

The manifest records file membership, hashes, sizes, timestamps, and empty folders. File contents are retained separately in `.m8backup\objects`, keyed by SHA-256. Identical contents share an object within that backup folder, so successive snapshots only need additional objects for new contents. This protects earlier snapshots when browsable files are renamed, updated, or pruned.

**Storage:** the first snapshot adds a separate history copy of each unique file's contents, alongside the normal browsable backup. Plan for up to roughly twice the card's data for a first full backup. Objects are shared within one backup folder, not between separate full backups. The free-space check includes missing history objects.

**Hard links (optional):** with `HardLinks=yes` and a backup folder on NTFS, history objects are hard links to the browsable backup files instead of copies: no extra space and no extra copying. The trade-off is that both names then refer to one copy on disk, so editing a backup file by hand, a tool rewriting it, or disk damage also changes the history; a check still detects it. The program itself never edits backup files in place. On FAT, exFAT and most network shares, or if linking fails, copies are used. The default is `HardLinks=no`.

Space planning reuses hashes already available from thorough comparison or existing checksum records. For contents whose hashes are not yet known, it reserves their full size without reading them just to estimate space. This estimate can exceed the eventual requirement when identical contents share an object. Copying still generates SHA-256 identities, and optional read-back verification still runs when enabled. A shared existing history object is verified once per backup run.

`KeepOldVersions` limits only browsable dated copies. Snapshot history and its objects remain until the whole backup folder is removed. Existing backups gain history only from subsequent successful runs; earlier states cannot be reconstructed retroactively. Keep the entire backup folder, including `.m8backup`, when moving it to another disk.

Fast comparison has the same limitations for snapshot content as for ordinary incremental copying. Choose `Thorough=yes` when capturing edits that may preserve size and time.

## Restore

Start with menu option 3 or `M8Backup.exe --restore`.

1. Select a target card/folder and a backup history.
2. Select a backup folder. Its completion status is displayed.
3. Select a saved snapshot, or press Enter to browse the current backup files.
4. Select one of the restore modes below.
5. Review any overwrite choice and the file preview. Confirm to start.

### Restore modes

| Choice | What it restores |
|---|---|
| **A — All** | All files and empty folders in the snapshot. When browsing current files, metadata and recognisable dated old versions are excluded. |
| **B — Bundle** | One `Bundles\<name>` folder, including its project, samples, instruments, and empty subfolders. |
| **S — Single file** | Search by name/path and select one song, instrument, sample, or old version. |
| **D — Song with dependent samples** | Select a song and include the sample paths referenced by its sampler instruments. |

Historical manifests distinguish real dated filenames from generated old versions, so a song whose original name contains a date is retained in historical restores. When browsing a dated old version directly, choose its original or dated name.

### Song dependencies

Dependency extraction supports the known **firmware-6 `.m8s` layout**. It reads all sampler slots, including unused slots, and restores referenced samples at their original card paths. Songs already contain their instrument data; separate instrument preset exports are not dependencies of a song.

Missing samples in the selected backup/snapshot are listed and cancel dependency restore. Unsupported firmware, malformed paths, or unknown instrument types are refused. Use a bundle or full restore for those files. Nothing in the song is rewritten, and this feature does not create or relocate bundles.

The implementation follows the format information in [pym8](https://github.com/jhw/pym8) and [m8-file-parser](https://github.com/Twinside/m8-file-parser). The M8's own Bundle command gathers a project, samples, and instruments, as described in the [Dirtywave operation manual](https://dirtywave.com/pages/resources-downloads).

### Merge versus exact snapshot

When restoring all files from a dated snapshot, choose:

- **Merge:** restore its files while keeping extra files already on the card.
- **Exact:** also remove extra user files that were not present in that snapshot, and remove extra folders if they become empty.

Exact removal is listed in the preview and requires a separate confirmation. If you choose to keep differing target files, exact removal is disabled. Extra files are removed only after all selected copies succeed. Ignored OS files/system folders are left alone; this is a restore of backed-up user content, not a byte-for-byte SD-card image.

### Overwrites and copy safety

Files considered unchanged by the selected `Thorough` comparison are skipped. If files differ, choose to replace them, keep them and copy only missing files, or cancel. A warning identifies target files newer than the selected backup.

With `Verify=yes`, every selected file with a stored checksum is checked before target changes; a mismatch aborts the plan. With `Verify=no`, checksum comparisons are skipped. Missing files and size mismatches still abort the plan. Files in legacy backups without checksums can be copied but have no historical integrity guarantee.

Restored files are staged beside the destination under a unique `.m8restore-*.tmp` name. Replacements use Windows' file-replacement operation rather than deleting the destination first. If the filesystem cannot support replacement, that file fails and its existing copy is retained. A normal failure removes the temporary file. An abruptly stopped process can leave a dot-prefixed temporary file; rerunning restore uses a fresh temporary name.

Replacement also uses a transient `.m8rollback-*.tmp` copy of the original on the target volume. It is removed after a successful replacement and used to restore the original name if replacement fails. This is separate from optional persistent PC recovery: `Recovery=no` still uses replacement rollback. If interrupted or rollback itself fails, keep any remaining rollback file until you have checked its contents. This follows [Windows ReplaceFile failure handling](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).

The working-space estimate includes added files, growth of replacements, and staging space. With recovery on the same volume, it also reserves space for all recovery copies before starting.

The target is checked again before replacement/removal. With `Thorough=no` this guard uses size and modified time; with `Thorough=yes` it uses SHA-256. `Verify` does not change this guard; it controls checksum checks of the backup files and copies. Detected changes stop replacement. This is not a transaction across the entire card: already completed file restores remain if a later file fails. Keep the card idle until completion.

### Optional recovery copies

Recovery is **off by default**:

```ini
Recovery=no
RecoveryFolder=
```

To save the target's existing files before replacing or removing them:

```ini
Recovery=yes
RecoveryFolder=D:\M8 Recovery
```

With `RecoveryFolder=` empty, the location is `<BackupFolder>\M8 Recovery`. Environment variables work, for example `%USERPROFILE%\Documents\M8 Recovery`.

Each restore makes a distinct dated recovery folder and preserves original relative paths. **All recovery copies must finish before any target files or folders are changed.** They are also read back and checksum-verified when `Verify=yes`. An inaccessible destination, insufficient space, or a failed recovery copy cancels the restore. The recovery folder and target must not contain one another. Files considered unchanged or only being added need no recovery copy.

Recovery metadata is under `.m8backup`: `recovery-status.txt` records completion and the original target; `recovery-checksums.sha256` records the copied file hashes. Copy recovered files back to their original card paths when needed. Recovery folders are separate from normal backup histories and are not automatically pruned or offered in the backup selector.

## Preview

`Preview=yes` displays the file plan and asks before copying. Set `Preview=no` to suppress that general prompt. Restore overwrite choices, exact-removal confirmations, backup selection, and retention questions still apply.

Incremental backup previews list only new and changed files. Unchanged files are shown as a total count, without listing their names.

When an incremental backup has no new or changed files to copy, it skips the Proceed prompt automatically. It still records the current snapshot membership, including deleted files and empty folders, and updates the backup status and statistics.

`--preview` displays the plan and exits without writing files. It can be combined with `--restore`. Selection questions remain so you can choose the intended operation. Preview reads and hashes files; it is not an unattended operation.

## Checking backups

Run `M8Backup.exe --check` without connecting the M8, or choose menu option 4. Select a history, then one backup or all backups. This checks both the browsable files against `checksums.sha256` and the objects referenced by all saved snapshots. An object shared across snapshots is checked once per backup folder. With `HardLinks=yes`, an object that is the same file on disk as a backup file already checked in that run isn't read a second time.

Missing or damaged objects and malformed manifests are reported. Backups without a checksum list offer to create one from their current contents; that establishes a future baseline, not proof that those files were originally undamaged.

Checksum files use the standard `sha256sum` format with forward slashes. Checksum and manifest paths are validated to prevent paths escaping the selected backup/target. The files are integrity baselines, not signed authenticity records.

## Progress and cancellation

The console reports progress during scanning, planning, hashing, copying, saving history, recovery, and integrity checks. Interactive progress updates are throttled; redirected output retains the regular messages and statistics without continuous progress lines.

Full backups show overall copy progress and stage labels, without individual file paths or per-file progress. The acceptance preview and any errors still list relevant files.

Press **Ctrl+C** to request cancellation. The program checks between files and during copy/hash/byte-comparison loops. A blocking filesystem or device operation may need to return before cancellation can take effect.

Cancelled backups keep completed copies and leave an interrupted run marked `incomplete` so it can be resumed. A cancelled changed-file copy restores its previous browsable backup copy when cleanup succeeds. Already completed snapshots remain available. Cancelling after the backup has completed, such as during retention questions, leaves that completed status intact.

Cancelled restores clean up the staged copy and retain its existing target file. Files already restored remain restored; cancellation stops further copying and removal. Recovery copies must still finish before any target changes. A cancelled integrity check reports cancellation rather than treating unchecked files as valid or damaged. Cancellation does not automatically eject the M8.

The backup statistics duration includes scanning, planning, copying, snapshot creation, and saving checksum records. It excludes time waiting for plan acceptance and subsequent report writing or retention prompts. The average speed uses copied data divided by that duration.

## Retention, statistics, and notifications

`KeepBackups=N` offers to move excess older backup folders of the current profile to the Recycle Bin after a successful backup. This removes each folder's snapshots too. `KeepBackups=0` keeps all.

`KeepOldVersions=N` offers to move excess browsable dated versions of each file to the Recycle Bin after a successful incremental run. `KeepOldVersions=0` keeps all. Snapshot contents are retained separately. Files that also exist on the source under their dated names are not pruned.

Every backup writes statistics with copied/unchanged files, song changes, skipped leftovers, data volume, speed, warnings, failures, and free space. Incremental reports use distinct timestamps so successive reports are retained.

The CLI uses Windows notifications to summarize backup, restore, or check results; the GUI shows results in its window. `Eject=no` leaves the M8 connected, `Eject=ask` asks in a notification/console or GUI dialog, and `Eject=yes` requests safe removal. Automatic ejection only applies to a device identified through the M8 USB detection path.

## All INI settings

| Setting | Default | Meaning |
|---|---|---|
| `BackupFolder` | empty | Windows Music folder; base for backups and default recovery location. |
| `Mode` | `ask` | `ask`, `full`, or `incremental`. |
| `Verify` | `no` | Enable automatic SHA-256 integrity comparisons for copies, restore sources, history objects, and recovery copies. |
| `KeepBackups` | `0` | Number of backup folders to retain per profile; 0 keeps all. |
| `KeepOldVersions` | `0` | Browsable old versions per file; 0 keeps all. |
| `MaxNameLength` | `64` | Warn about longer file/folder names; 0 disables length warnings. |
| `Eject` | `no` | `no`, `ask`, or `yes`. |
| `Recovery` | `no` | Save existing target files before restore overwrites/removals. |
| `RecoveryFolder` | empty | Defaults to `<BackupFolder>\M8 Recovery`. |
| `SourceFolder` | empty | Detect M8, with manual fallback; otherwise use this drive/folder. |
| `Profile` | empty | Automatic card/folder identity; otherwise use this named history. |
| `Thorough` | `no` | Enable content-hash comparison for backup/restore/resume change detection; otherwise use size and modified time. |
| `Preview` | `yes` | Show file plans and ask before backups/restores. |
| `HardLinks` | `no` | On NTFS, store history as hard links to the backup files instead of second copies (see Point-in-time history). |

## Command line and exit codes

```
M8Backup.exe [--restore | --check] [--source PATH] [--profile NAME] [--zip] [--preview] [--no-pause]
```

Examples:

```
M8Backup.exe --source E:\ --profile "Studio M8"
M8Backup.exe --source D:\M8Card --preview
M8Backup.exe --source E:\ --zip
M8Backup.exe --restore --source E:\ --preview
M8Backup.exe --check --profile "Studio M8" --no-pause
```

| Exit code | Meaning |
|---|---|
| `0` | Success, preview completed, or user cancelled. |
| `1` | Could not start/plan the operation: bad arguments, inaccessible files, no usable source/history, insufficient space, or an unexpected error. |
| `2` | A backup, restore, or integrity check finished with failures. |

Unknown arguments and missing argument values are errors. The program remains interactive even with `--no-pause`; that option only suppresses the final exit prompt. A history lock prevents two instances writing the same history concurrently. It does not prevent another application or another history from accessing the same card.

## Building and testing

Run `build.cmd --no-pause` to build `M8Backup.exe` from `src\*.cs` using Windows' .NET Framework C# compiler and Windows notification metadata.

Run `build-gui.cmd --no-pause` to build `M8Backup.GUI.exe` from `src\*.cs`, `gui\*.cs`, and `gui\Dialogs\*.cs`, with Windows Forms and the same built-in compiler. To build under another name while the GUI is running, use `build-gui.cmd --no-pause M8Backup.GUI.updated.exe`.

If `M8Backup.exe` is running and Windows locks it, use `build.cmd --no-pause M8Backup.updated.exe` to build a separate executable. It uses the same INI beside it. Close the running program before replacing the usual executable.

Run `tests\run-tests.ps1` to compile and run the filesystem integration tests. They create isolated fixtures under `.test-runs` (removed after a passing run; kept after a failure, or always with `M8_KEEP_TEST_FIXTURES=1`), use synthetic songs/card folders, and exclude desktop notification code. They cover backup/resume, history, corruption, restore, bundles, dependency parsing, previews, profiles, and recovery settings/failures. `tests\HelperTests.cs` adds focused tests for the helpers behind those workflows: change summaries, name warnings, leftover and old-version detection, the 2-second time tolerance, path safety, checksum lists, settings, retention, the resume question, and zip failures. Retention tests answer "no", so nothing is moved to the Recycle Bin. They do not write to a connected M8 or test hardware ejection.

Run `tests\run-gui-tests.ps1` for hidden Windows Forms integration tests. They drive the actual dialogs and background workers against temporary card folders, exercising preview, backup, incremental history, restore, checking, cancellation, and shared-settings persistence. They do not use a connected M8 or modify the project's INI file.

The source uses a partial `M8Backup` container for shared helpers and models. Instance engines accept an `OperationContext` containing the frontend interaction adapter and a cancellation token, and return an `OperationResult` with exit status, summary, cancellation state, and elapsed time. Plan reviews and structured progress go through `IOperationInteraction`; the engines do not read or write the console directly. `ConsoleInteraction` supplies the CLI frontend, and `GuiInteraction` supplies Windows Forms dialogs, marshals decisions to the UI thread, and batches progress/log updates. Each synchronous operation activates its context on its worker thread. Both frontends hold the history lock around mutations.

Backup file traversal skips metadata folders before descending into them, so normal browsing, checksum-list writing, and retention do not scan history objects as user files.

| File | Responsibility |
|---|---|
| `Program.cs` | CLI arguments, source selection and menu. |
| `Session.cs` | Workflow steps shared by the CLI and GUI: history per source, check target, history lock, resume question, same-device check before ejecting. |
| `Config.cs` | INI defaults and parsing. |
| `Workflow.cs` | Profiles, previews, status, atomic metadata writes. |
| `PathValidation.cs` | Path normalization, containment, link rejection, and safe relative paths. |
| `BackupTraversal.cs` | User-content traversal that skips history metadata folders. |
| `Backup.cs` | CLI backup entry, retention, source scanning, version naming. |
| `BackupEngine.cs` | Full/incremental execution, resume, space planning, reports. |
| `Snapshots.cs` | Manifests, deduplicated objects, historical selection and integrity checks. |
| `Restore.cs` | CLI restore selection, bundles, file validation, recovery helpers. |
| `RestoreEngine.cs` | Restore planning and execution, staged replacements, exact removal. |
| `IntegrityEngine.cs` | Integrity-check execution and result handling. |
| `Operations.cs` | Frontend interface, plans, progress/results, cancellation context, output adapter. |
| `Dependencies.cs` | Bounded firmware-6 sample-reference extraction. |
| `Checksums.cs` | Browsable-file checksum lists and check menu. |
| `FileCopy.cs` | Copying/hashing, verification, free-space checks. |
| `Changes.cs` | Song summaries and filename warnings. |
| `M8Device.cs` | Windows M8 detection and safe ejection. |
| `Notifications.cs` | Windows notifications. |
| `ConsoleUi.cs` | Console prompts and formatting. |
| `gui\GuiProgram.cs` | Windows Forms startup and preview rendering. |
| `gui\MainForm.cs` | Window layout, UI events, display updates, and settings capture. |
| `gui\GuiOperationRunner.cs` | Background-worker lifetime, cancellation, operation coordination, history locks, detection/ejection. |
| `gui\GuiInteraction.cs` | Adapts engine decisions and progress to GUI dialogs and display callbacks. |
| `gui\GuiTheme.cs` | Shared colors and control styling. |
| `gui\GuiProgressBar.cs` | Custom progress rendering. |
| `gui\Dialogs\` | Shared dialog frame, selection, preview, choices, search, settings, and folder picker. |

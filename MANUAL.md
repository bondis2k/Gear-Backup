# M8 Backup — Manual

M8 Backup is a Windows console program for backing up and restoring a Dirtywave M8 SD card. It also works with a card in a reader or a folder containing a copy of a card. It makes full or incremental backups, keeps dated snapshots, verifies copies, and restores all files, a bundle, a single file, or a song with its samples.

Requires Windows 10 or 11 and the built-in .NET Framework. No installation or extra runtime is needed.

## Getting started

1. Connect the M8 by USB and enable USB drive mode. Save your song first and leave the M8 idle while copying.
2. Double-click `M8Backup.exe`.
3. Review the selected source and profile. If a backup exists, choose full backup, incremental backup, restore, or check. With no backup in the current profile, the program prepares a full backup.
4. Review the file preview and press Y to proceed.

The settings file is `M8Backup.ini` beside the executable. Missing options are added with their defaults and explanations on an ordinary run. A `--preview` run does not write settings or backups.

If no M8 is detected, enter a drive or folder path, such as `E:\` or `D:\M8Card`. Press Enter to cancel. You can also set `SourceFolder` or pass `--source`.

Automatic detection finds a USB disk reporting an M8 vendor/model, with a fallback to a removable drive labelled M8. Drives selected manually are not automatically ejected.

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

**Full:** copy the card's user files into a new dated folder. If the date is already used, a numbered suffix creates a distinct folder.

**Incremental:** add and update files in the latest backup of the current profile. Changed files keep their previous browsable copy under a dated name, such as `Songs\Jam_2026-10-01_143000.m8s`. Deleted source files remain in the browsable backup. A dated snapshot separately records which files actually existed during this run.

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

The default comparison uses file size and modified time, allowing two seconds for FAT timestamps. This can miss an edit that leaves both unchanged. Use:

```ini
Thorough=yes
```

Thorough mode compares content hashes. Identical contents can be skipped even when timestamps differ. Restore always compares contents, regardless of this setting. Resuming an incomplete backup always uses content comparison.

### Completion status and resume

Each new backup has one of these statuses:

| Status | Meaning |
|---|---|
| `incomplete` | A run started but did not reach its final status, for example because the process stopped or USB was disconnected. |
| `failed` | The run reached the end with file, history, or checksum-list failures. |
| `complete` | The run finished with its files, snapshot, and checksum list saved. |
| `legacy (status unknown)` | An older backup without status metadata. |

When the latest backup is incomplete or failed, choose **Resume**, **New full backup**, or **Cancel**. Resume compares the current source with existing copies using hashes, repairs partial or missing copies, and refreshes checksums. It resumes against the source as it exists now; it cannot reconstruct unsaved files or a previous source state that was never copied.

Existing copies that fail their recorded checksum, or changed copies from an interrupted run without a recorded checksum, are preserved under `.m8backup\quarantine` rather than presented as valid old versions. Quarantine is excluded from normal restore and integrity lists; inspect those files manually if needed.

Older completed snapshots remain selectable even if the latest incremental run failed. Browsing the current files of an incomplete or legacy backup requires an explicit acknowledgement of its status.

## Point-in-time history

Every successful backup run saves a dated manifest under:

```
<backup>\.m8backup\snapshots\<timestamp>.manifest
```

The manifest records file membership, hashes, sizes, timestamps, and empty folders. File contents are retained separately in `.m8backup\objects`, keyed by SHA-256. Identical contents share an object within that backup folder, so successive snapshots only need additional objects for new contents. This protects earlier snapshots when browsable files are renamed, updated, or pruned.

**Storage:** the first snapshot adds a separate history copy of each unique file's contents, alongside the normal browsable backup. Plan for up to roughly twice the card's data for a first full backup. Objects are shared within one backup folder, not between separate full backups. The free-space check includes missing history objects.

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

Files with identical contents are skipped. If files differ, choose to replace them, keep them and copy only missing files, or cancel. A warning identifies target files newer than the selected backup.

Every selected file with a stored checksum is checked before target changes. Damaged, missing, or truncated files abort the plan. Files in legacy backups without checksums can be copied but have no historical integrity guarantee.

Restored files are staged beside the destination under a unique `.m8restore-*.tmp` name. Replacements use Windows' file-replacement operation rather than deleting the destination first. If the filesystem cannot support replacement, that file fails and its existing copy is retained. A normal failure removes the temporary file. An abruptly stopped process can leave a hidden temporary file; rerunning restore uses a fresh temporary name.

The target is checked again before replacement/removal. A file changed since the preview is not overwritten. This is not a transaction across the entire card: already completed file restores remain if a later file fails. Keep the card idle until completion.

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

Each restore makes a distinct dated recovery folder and preserves original relative paths. **All recovery copies must finish and verify before any target files or folders are changed.** An inaccessible destination, insufficient space, or a failed recovery copy cancels the restore. The recovery folder and target must not contain one another. Files that are identical or only being added need no recovery copy.

Recovery metadata is under `.m8backup`: `recovery-status.txt` records completion and the original target; `recovery-checksums.sha256` records the copied file hashes. Copy recovered files back to their original card paths when needed. Recovery folders are separate from normal backup histories and are not automatically pruned or offered in the backup selector.

## Preview

`Preview=yes` displays the file plan and asks before copying. Set `Preview=no` to suppress that general prompt. Restore overwrite choices, exact-removal confirmations, backup selection, and retention questions still apply.

`--preview` displays the plan and exits without writing files. It can be combined with `--restore`. Selection questions remain so you can choose the intended operation. Preview reads and hashes files; it is not an unattended operation.

## Checking backups

Run `M8Backup.exe --check` without connecting the M8, or choose menu option 4. Select a history, then one backup or all backups. This checks both the browsable files against `checksums.sha256` and the objects referenced by all saved snapshots. An object shared across snapshots is checked once per backup folder.

Missing or damaged objects and malformed manifests are reported. Backups without a checksum list offer to create one from their current contents; that establishes a future baseline, not proof that those files were originally undamaged.

Checksum files use the standard `sha256sum` format with forward slashes. Checksum and manifest paths are validated to prevent paths escaping the selected backup/target. The files are integrity baselines, not signed authenticity records.

## Retention, statistics, and notifications

`KeepBackups=N` offers to move excess older backup folders of the current profile to the Recycle Bin after a successful backup. This removes each folder's snapshots too. `KeepBackups=0` keeps all.

`KeepOldVersions=N` offers to move excess browsable dated versions of each file to the Recycle Bin after a successful incremental run. `KeepOldVersions=0` keeps all. Snapshot contents are retained separately. Files that also exist on the source under their dated names are not pruned.

Every backup writes statistics with copied/unchanged files, song changes, skipped leftovers, data volume, speed, warnings, failures, and free space. Incremental reports use distinct timestamps so successive reports are retained.

Windows notifications summarize backup, restore, or check results. `Eject=no` leaves the M8 connected, `Eject=ask` asks in a notification/console, and `Eject=yes` requests safe removal. Automatic ejection only applies to a device identified through the M8 USB detection path.

## All INI settings

| Setting | Default | Meaning |
|---|---|---|
| `BackupFolder` | empty | Windows Music folder; base for backups and default recovery location. |
| `Mode` | `ask` | `ask`, `full`, or `incremental`. |
| `Verify` | `yes` | Read back newly copied files and compare hashes. History objects and recovery copies are always verified. |
| `KeepBackups` | `0` | Number of backup folders to retain per profile; 0 keeps all. |
| `KeepOldVersions` | `0` | Browsable old versions per file; 0 keeps all. |
| `MaxNameLength` | `64` | Warn about longer file/folder names; 0 disables length warnings. |
| `Eject` | `no` | `no`, `ask`, or `yes`. |
| `Recovery` | `no` | Save existing target files before restore overwrites/removals. |
| `RecoveryFolder` | empty | Defaults to `<BackupFolder>\M8 Recovery`. |
| `SourceFolder` | empty | Detect M8, with manual fallback; otherwise use this drive/folder. |
| `Profile` | empty | Automatic card/folder identity; otherwise use this named history. |
| `Thorough` | `no` | Content-hash comparison for backup change detection. |
| `Preview` | `yes` | Show file plans and ask before backups/restores. |

## Command line and exit codes

```
M8Backup.exe [--restore | --check] [--source PATH] [--profile NAME] [--preview] [--no-pause]
```

Examples:

```
M8Backup.exe --source E:\ --profile "Studio M8"
M8Backup.exe --source D:\M8Card --preview
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

Run `tests\run-tests.ps1` to compile and run the filesystem integration tests. They create isolated fixtures under `.test-runs`, use synthetic songs/card folders, and exclude desktop notification code. They cover backup/resume, history, corruption, restore, bundles, dependency parsing, previews, profiles, and recovery settings/failures. They do not write to a connected M8 or test hardware ejection.

Source files are parts of the same `M8Backup` class:

| File | Responsibility |
|---|---|
| `Program.cs` | Arguments, source/profile selection, menu, history locking. |
| `Config.cs` | INI defaults and parsing. |
| `Workflow.cs` | Path validation, profiles, previews, status, atomic metadata writes. |
| `Backup.cs` | Full/incremental copy, resume, reports, retention, scanning. |
| `Snapshots.cs` | Manifests, deduplicated objects, historical selection and integrity checks. |
| `Restore.cs` | Restore planning, bundles, recovery copies, staged replacements, exact removal. |
| `Dependencies.cs` | Bounded firmware-6 sample-reference extraction. |
| `Checksums.cs` | Browsable-file checksum lists and check menu. |
| `FileCopy.cs` | Copying/hashing, verification, free-space checks. |
| `Changes.cs` | Song summaries and filename warnings. |
| `M8Device.cs` | Windows M8 detection and safe ejection. |
| `Notifications.cs` | Windows notifications. |
| `ConsoleUi.cs` | Console prompts and formatting. |

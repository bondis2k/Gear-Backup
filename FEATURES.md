# M8 Backup — Features

A one-line summary of each feature. See [MANUAL.md](MANUAL.md) for details and settings.

## Interfaces

- **Graphical app** — `M8Backup.GUI.exe` with source and backup folder pickers, an operation list, a preview button, progress, and an activity log.
- **Command-line app** — `M8Backup.exe` with a numbered menu and options for scripting (`--restore`, `--check`, `--source`, `--profile`, `--zip`, `--preview`, `--no-pause`).
- **Shared engine** — both apps use the same backup, restore and check code and the same `M8Backup.ini`.
- **App icon** — a tracker-style icon embedded in both programs.

## Sources

- **M8 detection** — finds the M8 in USB disk mode automatically, with a removable drive labelled `M8` as fallback.
- **Card reader or folder** — back up an SD card in a reader or any local folder (`SourceFolder`, `--source`).
- **Manual fallback** — asks for a source path when no M8 is detected.
- **Profiles** — each card gets its own history, identified by its volume serial so it survives a new reader or drive letter.
- **Named profiles** — choose a fixed history name with `Profile` or `--profile`.
- **Legacy histories** — backups made before profiles existed stay available for restore and checks.

## Backup

- **Full backup** — copies the whole card to a new dated folder.
- **Incremental backup** — copies only new and changed files into the latest backup.
- **Fast or thorough comparison** — detects changes by size and modified time, or by SHA-256 content (`Thorough`).
- **Undated files** — files without a usable timestamp are compared byte by byte.
- **Old versions kept** — a changed file's previous backup copy is renamed with its date and time instead of overwritten.
- **Point-in-time history** — every backup saves a snapshot of the card's files and folders that can be restored later.
- **Content deduplication** — identical content is stored once per backup folder's history.
- **Hard-linked history** — optionally stores history as hard links on NTFS, halving disk use (`HardLinks`).
- **Empty folders kept** — folders like `Bundles` are recreated even when empty.
- **Leftover skipping** — skips macOS, Linux and Windows junk (`.DS_Store`, `.Trash-1000`, `lost+found`, `Thumbs.db` and similar).
- **Name warnings** — flags names the M8 may not handle well: special characters, long names, leading or trailing spaces.
- **Free-space check** — checks the backup drive has room before copying anything.
- **Resume** — an interrupted or failed backup can be resumed into the same folder.
- **Quarantine** — a partial or unverified copy found during resume is moved aside, not trusted.
- **Overlapped copying** — reads the next chunk from the card while the current one is written.
- **Zip copy** — optionally also saves a full backup as a zip next to its folder (`--zip`, or the GUI's "Also save as zip").

## Restore

- **Restore all** — restores every file and empty folder from a backup or snapshot.
- **Restore a bundle** — restores one `Bundles\<name>` folder.
- **Restore a single file** — search by name and restore one song, instrument, sample or old version.
- **Song with samples** — restores a song plus every sample its instruments reference (firmware-6 songs).
- **Pick a point in time** — restore from any saved snapshot or from the current backup files.
- **Original or dated name** — an old version can be restored under its original name or its dated one.
- **Merge or exact** — keep extra files on the card, or remove files that weren't in the snapshot.
- **Differing files** — choose to replace, keep the card's versions, or cancel, with a warning for newer files.
- **Safe replacement** — new files are written beside the old ones and swapped in, so a failure never loses the original.
- **Change guard** — refuses to replace a card file that changed since the preview.
- **Recovery copies** — optionally saves every file a restore will replace or remove to the PC first (`Recovery`).
- **Damaged files refused** — a backup file whose checksum no longer matches is not restored.

## Integrity

- **Checksum list** — every backup has a `checksums.sha256` file in the standard `sha256sum` format.
- **Check backups** — re-reads backups and their history to find damaged or missing files, without the M8 connected.
- **Copy verification** — optionally reads each copy back from the disk, bypassing the cache, and compares checksums (`Verify`).
- **Backup status** — each backup is marked `incomplete`, `complete` or `failed`.
- **Path safety** — rejects links and paths that would escape the backup or target folder, and never treats a linked folder as a backup.
- **Safe DLL loading** — Windows DLLs are loaded only from System32, so a planted DLL next to the exe is ignored.
- **Private zips** — a zip's copy of the statistics hides your local folder paths and user name.
- **History lock** — prevents two programs or windows writing the same history at once.

## Previews and progress

- **Preview** — lists planned actions and asks before copying (`Preview`).
- **Preview only** — `--preview` shows the plan and changes nothing.
- **Progress** — shows scanning, copying, hashing and checking progress.
- **Cancel** — Cancel in the app or Ctrl+C stops safely, with no partial files left behind.

## Reports and notifications

- **Statistics** — every backup saves a report of files, sizes, speed, warnings, failures and free space.
- **Change summary** — lists new, edited and removed songs since the last backup.
- **Notifications** — the command-line app shows a Windows notification with the result.
- **Eject** — leave the M8 connected, ask, or eject it automatically, only if it's still the same device (`Eject`).

## Housekeeping

- **Keep the last N backups** — moves older backup folders to the Recycle Bin, after asking (`KeepBackups`).
- **Keep the last N old versions** — limits dated old versions per file, moving extras to the Recycle Bin (`KeepOldVersions`).
- **Self-documenting settings** — `M8Backup.ini` is created with every option explained, and new options are added automatically.

## Development

- **No install needed** — builds with the C# compiler included in Windows (`build.cmd`, `build-gui.cmd`).
- **Automated tests** — integration tests for the engines and the GUI (`tests\run-tests.ps1`, `tests\run-gui-tests.ps1`).

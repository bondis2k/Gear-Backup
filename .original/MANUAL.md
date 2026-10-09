# M8 Backup — Manual

M8 Backup is a small Windows program that backs up the SD card of a Dirtywave M8 to your PC and restores it again when needed. It can make full or incremental backups, check every copy, keep a checksum list to detect damaged backups later, show which songs changed, and safely eject the M8 when it's done.

- [Getting started](#getting-started)
- [The start menu](#the-start-menu)
- [Full backup](#full-backup)
- [Incremental backup](#incremental-backup)
- [Restore](#restore)
- [Copy verification](#copy-verification)
- [Checksum list and checking backups](#checksum-list-and-checking-backups)
- [Free space check](#free-space-check)
- [What changed since the last backup](#what-changed-since-the-last-backup)
- [Name warnings](#name-warnings)
- [Keeping only the last N backups](#keeping-only-the-last-n-backups)
- [Statistics file](#statistics-file)
- [Eject and notifications](#eject-and-notifications)
- [Settings (M8Backup.ini)](#settings-m8backupini)
- [Command line and exit codes](#command-line-and-exit-codes)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Code overview](#code-overview)

---

## Getting started

**Requirements:** Windows 10 or 11. Nothing else needs installing; the program uses the .NET Framework that comes with Windows.

1. Connect the M8 to the PC by USB and turn on disk mode on the M8.
2. Double-click `M8Backup.exe`.
3. The program finds the M8 by itself. If a backup already exists, it shows the [start menu](#the-start-menu); otherwise it makes a first full backup straight away.
4. When it's finished, a Windows notification shows a summary. By default the M8 stays connected; you can have the program [ask, or always eject it](#eject-and-notifications).

On the first run, the program creates `M8Backup.ini` next to the exe, holding all the [settings](#settings-m8backupini) with explanations.

### How the M8 is found

The program looks for a USB disk whose vendor or model is "M8" (the M8 in disk mode reports itself that way). If none is found, it falls back to any removable drive labelled `M8`. A drive found by label alone works for backup and restore, but can't be [ejected automatically](#eject-and-notifications).

If no M8 is found, you get a red message and a notification, and the program stops. ([Checking backups](#checking-backups) works without the M8: run `M8Backup.exe --check`.)

---

## The start menu

When at least one backup already exists, and `Mode=ask` (the default), you choose what to do:

```
  Last backup: C:\Users\you\Music\M8 Backups\2026-10-09

    1) Full backup to a new folder
    2) Incremental backup into the last backup (only new and changed files)
    3) Restore from a backup to the M8
    4) Check backups for damaged files
  Choose 1, 2, 3 or 4:
```

Press the number key; no Enter needed. Option 4 is described under [Checking backups](#checking-backups).

- With `Mode=full` or `Mode=incremental` in the settings, the menu is skipped and that kind of backup always runs. To restore or check in that case, start the program with `--restore` or `--check` (see [Command line](#command-line-and-exit-codes)).
- With no backups yet, there's nothing to update or restore, so a full backup runs without asking.

---

## Full backup

Copies everything on the M8 to a new folder:

```
<BackupFolder>\M8 Backups\2026-10-09
```

`BackupFolder` is your Music folder unless you [change it](#settings-m8backupini). If a backup from today already exists, the time is added: `2026-10-09_143000`.

**What gets copied:** every file and folder on the M8, including empty folders (such as `Bundles`), so the backup has exactly the same structure as the card.

**What gets skipped:**
- Leftovers that other computers write to memory cards. The M8 doesn't use them, and they're counted in the statistics.
  - **Names starting with `.`**: from macOS (`.DS_Store`, `._Song.m8s`, `.Spotlight-V100`, `.fseventsd`, `.Trashes`) and from Linux (`.Trash-1000`, `.directory`, `.hidden`, `.xdg-volume-info`, `.goutputstream-…`, `.fuse_hidden…`).
  - **Other Linux leftovers**: the `lost+found` folder, `FSCK0000.REC`-style files (fragments recovered by the Linux disk checker), and editor backup files ending in `~` (such as `notes.txt~`).
  - **Windows leftovers**: `Thumbs.db` and `ehthumbs.db` (thumbnail caches) and `desktop.ini` (folder view settings).
  - The Linux and Windows ones don't stand out like dot-files, so each one is also listed by name in the statistics.
- The Windows system folders `System Volume Information` and `$RECYCLE.BIN`.

Files are copied one at a time with a progress line:

```
   63%  412/655 files  1.2 GB copied
```

If a file can't be copied, it is reported in red, the backup carries on with the rest, and the file is listed under "Failed files" in the [statistics](#statistics-file).

---

## Incremental backup

Updates the **most recent** backup folder instead of creating a new one. Only files that are new or changed since that backup are copied, so it's much faster than a full backup.

### How a file counts as unchanged

A file on the M8 is considered the same as the backed-up copy when both:
- have the same size, **and**
- have the same last-modified time, allowing up to 2 seconds of difference (the M8's FAT-formatted card stores times in 2-second steps).

Anything else is either **new** (not in the backup yet) or **changed**.

### Changed files keep their old version

When a file changed on the M8, the copy already in the backup is **not overwritten**. It is renamed first, by adding the date and time it was last modified, then the new version is copied in:

```
Songs\MySong.m8s                       ← new version, from the M8
Songs\MySong_2026-10-01_143000.m8s     ← previous version, kept
```

- The date and time are the old file's own modified time, so they show when that version was saved.
- If that name is already taken, `_2`, `_3`, … is added.
- If copying the new version fails, the old file is renamed back, so the backup never loses a file.
- Every renamed file is listed in the statistics under "Old versions of updated files".

You can restore any old version with [Restore](#restore).

### Limiting the number of old versions

Old versions pile up over time. With `KeepOldVersions` set to a number above 0, only that many old versions of each file are kept. After each incremental backup, the oldest extra ones can go:

```
  KeepOldVersions is 3, so these 2 older version(s) can go:
    Songs\Jam_2026-08-01_120000.m8s
    Songs\Jam_2026-08-15_093000.m8s
  Move them to the Recycle Bin? (y/n)
```

- You're always asked first, and they go to the **Recycle Bin**, so you can get them back.
- The limit is per file: with `KeepOldVersions=3`, every song keeps its 3 most recent old versions, plus the current one.
- A dated file that also exists on the M8 under that same name (for example an old version you [restored with its dated name](#step-2-everything-or-a-single-file)) is treated as a normal file and never removed.
- If at most 15 files can go, they're all listed; otherwise the first 15 are shown.
- The statistics show how many were moved.
- `KeepOldVersions=0` (the default) keeps all old versions.

### Things to know

- **Files deleted from the M8 stay in the backup.** Incremental backups only add and update files; they never remove anything. The [change summary](#what-changed-since-the-last-backup) does list songs that are no longer on the M8.
- Each incremental run writes its own statistics file with the date in its name (`backup-statistics_2026-10-09_143000.txt`), so earlier statistics are kept.
- The backup folder keeps its original date as its name, even after being updated.

---

## Restore

Copies files from a backup back to the M8. Choose option **3** in the start menu, or start the program with `--restore`.

### Step 1: pick a backup

The 20 most recent backups are listed, newest first. Type the number and press Enter (or just Enter to cancel).

### Step 2: everything or a single file

Press **A** to restore all files, or **S** to restore a single file.

**All files** restores every file in the backup except:
- the statistics files and the checksum list, and
- old versions of updated files (the dated copies, such as `MySong_2026-10-01_143000.m8s`).

Folders that exist in the backup but not on the M8 are recreated, including empty ones.

**A single file:** type part of a file name to search, or press Enter to list all songs (`.m8s` files). Type `q` to cancel.

```
  Type part of the file name (Enter = list all songs, q = cancel): jam
     1) Songs\Jam.m8s  (48.2 KB, 2026-10-09 14:30)
     2) Songs\Jam_2026-10-01_120000.m8s  (45.0 KB, 2026-10-01 12:00)
  Restore which file? Number, or Enter to search again:
```

The search covers every file in the backup (songs, instruments, samples, …), including old versions. At most 40 matches are shown; type more of the name to narrow it down.

If you pick an **old version**, you're asked how to name it on the M8:
- **O**: restore it under its original name (`Jam.m8s`), replacing the current song if there is one (you're asked first, see below).
- **D**: keep the dated name (`Jam_2026-10-01_120000.m8s`), so it appears next to the current song and nothing is replaced.

### Step 3: what happens to files already on the M8

Before copying, the program compares each file with what's on the M8 and shows a summary:

```
  From 2026-10-09: 3 file(s) missing on the M8, 2 that differ, 640 already identical.
```

- Files already **identical** are skipped.
- **Missing** files are copied.
- If some files **differ**, you choose:
  - **R**: replace the M8's versions with the backup's
  - **K**: keep the M8's versions and copy only the missing files
  - **C**: cancel

If any of the M8's differing files are *newer* than the backup's, you get a yellow warning first. Back them up before replacing them if you want to keep them.

If nothing differs, you're simply asked to confirm (`y/n`) before anything is copied.

### Safe replacement

A file being replaced on the M8 is never deleted first. The backup's copy is written next to it as `<name>.restoring`; only once that copy is complete (and [verified](#copy-verification)) is the old file removed and the new one renamed into place. If anything fails, the temporary file is removed and the M8's original file is left as it was.

### Damaged backup files are never restored

If the backup has a [checksum list](#checksum-list-and-checking-backups), each file's checksum is compared with the one saved when it was backed up. A file that has changed since then (damaged on the backup drive) is **not** put on the M8. It's reported as failed with *"the backup copy is damaged"*, and the M8's own file, if there is one, is left untouched. Look for an undamaged copy of that file in another backup.

Backups made before checksum lists existed can't be checked this way; you get a yellow note about this before copying starts.

Restore also runs the [free space check](#free-space-check) on the M8, and handles [ejecting](#eject-and-notifications) the same way as a backup.

Restoring files the M8 doesn't touch is harmless, but files the M8 has open could be affected, so it's best to restore while the M8 is in disk mode and not playing.

---

## Copy verification

With `Verify=yes` (the default), every copied file is checked:

1. While copying, the program calculates a SHA-256 checksum (a fingerprint of the content) of the original.
2. After copying, it reads the copy back and calculates its checksum.
3. If the two differ, the copy is deleted and made again. If the second try also differs, the file is reported as failed: *"the copy didn't match the original twice; check the USB cable and SD card"*.

Copies are written with write-through, so data goes to the disk rather than staying in Windows' memory. The read-back can still come from Windows' memory cache, so verification is best at catching bad reads from the M8 and errors during copying, and less certain to catch a failing backup drive. [Checking backups](#checking-backups) later covers that.

Verification applies to both backups and restores. It makes copying slower; turn it off with `Verify=no`. The statistics show whether it was on. The [checksum list](#checksum-list-and-checking-backups) is saved either way.

---

## Checksum list and checking backups

### The checksum list

Every backup folder gets a file called `checksums.sha256`. It holds a SHA-256 checksum (a fingerprint of the content) for every file in the backup, taken while the file was copied from the M8:

```
3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b  Samples/kick.wav
9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  Songs/Jam.m8s
```

- The format is the same as the standard `sha256sum` tool, so on Linux or macOS you can also check a backup with `sha256sum -c checksums.sha256`, run inside the backup folder.
- It's updated after every incremental backup. New and changed files get their new checksum, and [old versions](#changed-files-keep-their-old-version) keep the checksum they had under their original name.
- If an incremental backup updates a folder made before checksum lists existed, the files already in it are read and hashed once to complete the list. That can take a little longer the first time.
- The statistics show how many files are in the list.

### Checking backups

A file on a backup drive can get damaged long after it was copied, for example through a failing disk. A check reads every file in a backup again and compares it with the checksum list. Start it with option **4** in the [start menu](#the-start-menu), or with `M8Backup.exe --check`. The check doesn't need the M8 to be connected.

```
  Check which backup? Number, A = all 7 backups, Enter = cancel: 1

  2026-10-09
    100%  655/655 files checked
    All 655 files OK.
```

Type a backup's number to check one, or **A** to check all of them. Problems are listed in red:

| Result | Meaning |
|---|---|
| `damaged: Songs\Jam.m8s` | The file's content has changed since it was backed up. |
| `missing: Songs\Jam.m8s` | The file was in the backup but has been deleted or moved. |
| `unreadable: …` | Windows couldn't read the file at all; the reason is shown. |

- Files in the backup folder that aren't in the list (for example ones you added by hand) are counted and mentioned, but not checked.
- A backup made before checksum lists existed has nothing to compare with. You're offered to create a list from the files as they are now, so future checks can spot changes. It can't tell whether those files are already damaged.
- At the end, a notification shows the totals.

If a file is damaged, restore that file from another backup if you have one. Checking all your backups every few months is a good habit, especially for backups on external drives.

---

## Free space check

Before copying anything, the program works out how much space is needed and checks the target drive:

- **Full backup:** the size of everything to be copied.
- **Incremental backup:** only the new and changed files.
- **Restore:** the files to copy, minus the space freed by files being replaced, plus room for the largest replaced file (because a replaced file briefly exists twice during [safe replacement](#safe-replacement)).

If there isn't enough room, nothing is copied and you get a message such as:

```
  Not enough space on D:\: needs 4.2 GB, but only 1.1 GB is free.
```

Network folders (`\\server\share`) can't be checked this way, so for those the check is skipped.

---

## What changed since the last backup

Every backup compares the M8 with the most recent backup and reports, in the [statistics](#statistics-file):

```
  Changes since the last backup (2026-10-09):
    New songs (1):
      Idea7
    Edited songs (2):
      Jam
      Live\Track01
    Songs no longer on the M8 (1):
      OldSketch
    Other files: 4 new, 1 changed
```

- **Songs** are `.m8s` files. Names are shown without the `Songs\` folder and the extension; songs in subfolders keep their subfolder (`Live\Track01`).
- **Songs no longer on the M8** are in the last backup but not on the M8 any more (deleted or renamed).
- **Other files** counts everything else: instruments, samples, themes, etc.
- On the very first backup there's nothing to compare with, so only the number of songs is shown.

The final notification includes a short version, for example: *"Songs: 1 new (Idea7), 2 edited (Jam, Track01)."* or *"No songs changed."*

The comparison uses the same rule as [incremental backups](#how-a-file-counts-as-unchanged) (size and modified time).

---

## Name warnings

The program checks every file and folder name on the M8 and warns about names the M8 may not show or load properly:

- characters other than plain letters, digits and common symbols (for example accented letters like `é`, or emoji)
- names longer than `MaxNameLength` characters (64 by default)
- names that start or end with a space

If any are found, a yellow line appears at the start, and the full list is in the statistics:

```
  Names the M8 may not handle well (1):
    Samples\Café drum loop.wav: characters other than plain letters, digits and symbols
```

These are only warnings; the files are backed up normally. **The 64-character limit is a cautious guess, not a documented M8 limit.** Change it with `MaxNameLength`, or set it to `0` to switch off the length check.

---

## Keeping only the last N backups

With `KeepBackups` set to a number above 0, the program tidies up old backups after each **successful** backup (one with no failed files):

```
  KeepBackups is 5, so these 2 old backup(s) can go:
    2026-08-01
    2026-08-15
  Move them to the Recycle Bin? (y/n)
```

- You're always asked first.
- Backups go to the **Recycle Bin**, not deleted permanently, so you can get them back. (Folders on network drives can't go to the Recycle Bin and are deleted permanently.)
- The count includes the backup just made, which is never removed.
- Only folders named by date (`yyyy-MM-dd` or `yyyy-MM-dd_HHmmss`) in `M8 Backups` count as backups. Anything else you put in that folder is left alone.
- `KeepBackups=0` (the default) keeps all backups.

---

## Statistics file

Every backup shows its statistics on screen and saves them in the backup folder:

- `backup-statistics.txt` for a full backup
- `backup-statistics_<date>_<time>.txt` for each incremental backup

Contents:

| Section | What it shows |
|---|---|
| Summary | Date, type (full or incremental), M8 drive, backup folder, files copied (for incremental: new, updated and unchanged), folders, data copied, time taken, average speed, whether copies were verified, the [checksum list](#checksum-list-and-checking-backups), largest file |
| Changes | [What changed since the last backup](#what-changed-since-the-last-backup) |
| Per folder | Number of files and size for each top-level folder (Songs, Samples, Instruments, …) |
| Skipped | macOS, Linux and Windows leftover files and folders that weren't copied (ones without a leading `.` are listed by name) |
| Free space | On the M8 and on the backup drive |
| Name warnings | [Names the M8 may not handle well](#name-warnings) |
| Old versions | Files renamed during an [incremental backup](#changed-files-keep-their-old-version), and how many older ones were moved to the Recycle Bin by [`KeepOldVersions`](#limiting-the-number-of-old-versions) |
| Failed files | Files that couldn't be copied, with the reason |

The last line says whether the backup completed or how many files failed.

---

## Eject and notifications

At the end of a backup or restore, a Windows notification shows a summary. What happens to the M8 depends on the `Eject` setting:

| `Eject=` | What happens |
|---|---|
| `no` *(default)* | The M8 stays connected, and you aren't asked. The notification just shows the summary. |
| `ask` | The notification has two buttons: **Eject M8** safely removes the M8 (the same as "Safely Remove Hardware"), and **Not now** leaves it connected. |
| `yes` | The M8 is ejected straight away, without asking. The notification shows the summary and whether the eject worked. |

With `Eject=ask`, you can also press **Y** or **N** in the program window instead. If notifications don't work on your PC, the program asks in the window. If nobody answers within 15 minutes, the M8 is left connected.

If the eject fails, you're told why. Usually something still has the M8 open, such as an Explorer window; close it and eject from Windows instead.

Notifications show as "M8 Backup". To do that, the program registers its name for your user account in the registry, under `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\Dirtywave.M8Backup`.

---

## Settings (M8Backup.ini)

The settings file sits next to `M8Backup.exe`. It's created on the first run, and any setting missing from it (for example after an update adds a new one) is added automatically with its default value and an explanation. Edit it with Notepad.

```ini
BackupFolder=
Mode=ask
Verify=yes
KeepBackups=0
MaxNameLength=64
Eject=no
KeepOldVersions=0
```

| Setting | Values | Default | What it does |
|---|---|---|---|
| `BackupFolder` | a folder path | *(empty)* = your Music folder | Where backups go. They're saved in `<BackupFolder>\M8 Backups\<date>`. Environment variables work, e.g. `%USERPROFILE%\Documents`. Quotes around the path are optional. |
| `Mode` | `ask`, `full`, `incremental` | `ask` | What to do when a backup already exists. See [the start menu](#the-start-menu). |
| `Verify` | `yes` / `no` | `yes` | Whether to [check every copy](#copy-verification). `true`/`false`, `on`/`off` and `1`/`0` also work. |
| `KeepBackups` | a number | `0` (keep all) | How many backups to [keep](#keeping-only-the-last-n-backups). |
| `MaxNameLength` | a number | `64` | Name length that triggers a [name warning](#name-warnings). `0` = don't check length. |
| `Eject` | `no`, `ask`, `yes` | `no` | Whether to [eject the M8](#eject-and-notifications) when finished: never, ask, or always. |
| `KeepOldVersions` | a number | `0` (keep all) | How many [old versions of each file](#limiting-the-number-of-old-versions) to keep in a backup. |

Lines starting with `;` or `#` are comments. Setting names are not case-sensitive. An invalid value (such as `KeepBackups=many`) gives a yellow warning and the default is used.

---

## Command line and exit codes

```
M8Backup.exe [--restore | --check] [--no-pause]
```

| Option | Effect |
|---|---|
| `--restore` | Go straight to [Restore](#restore), whatever `Mode` is set to. Handy as a second shortcut. |
| `--check` | Go straight to [Checking backups](#checking-backups). The M8 doesn't need to be connected. |
| `--no-pause` | Don't wait for a key press before closing. For running from scripts or Task Scheduler. |

The program also asks questions at several points (menu, Recycle Bin, restore and check choices, and eject with `Eject=ask`), so it isn't suited to running completely unattended.

**Exit codes**

| Code | Meaning |
|---|---|
| `0` | Success (for a check: no problems found), or cancelled by you, or nothing needed copying |
| `1` | Couldn't run: no M8 found, not enough space, no backups to restore or check, or an unexpected error |
| `2` | Finished, but some files failed to copy, or a check found damaged or missing files |

---

## Troubleshooting

| Problem | What to do |
|---|---|
| "No M8 found" | Check the USB cable (some cables only charge), make sure disk mode is on, and check the M8 appears in Explorer. |
| A file failed with "the copy didn't match the original twice" | The data read from the M8 wasn't the same twice. Try another USB cable or port; if it keeps happening, check the SD card. |
| "Could not eject the M8: it is in use" | Close Explorer windows and programs using the M8, then eject from Windows. |
| "Not enough space" | Free up space, choose a different `BackupFolder`, or use `KeepBackups` to remove old backups. |
| No notification appears | Check Windows notification settings and Focus / Do not disturb. You can always answer with Y/N in the window. |
| Settings ignored | Check the yellow warnings at the start for invalid values, and that `M8Backup.ini` is in the same folder as the exe. |
| A check reports damaged files | The backup drive may be failing. Restore those files from another backup if needed, make a fresh full backup to a different drive, and check the drive's health. |
| A restore fails with "the backup copy is damaged" | That file in the backup no longer matches what was backed up. Restore it from another backup; the M8's own file was left untouched. |
| A `.restoring` file on the M8 | Left over from a restore that was interrupted (e.g. the cable was pulled). The original file is untouched; delete the `.restoring` file. |

---

## Building from source

The source code is in `src\`. To rebuild `M8Backup.exe`, double-click `build.cmd`, or from a terminal:

```
build.cmd --no-pause
```

It uses the C# compiler that comes with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so there's nothing to install. The program is C# 5 for .NET Framework 4, using the Windows notification API (WinRT) for notifications.

---

## Code overview

All files are parts of one class, `M8Backup` (a C# `partial class`), split by topic.

### `src\Program.cs`: start-up

| Function | What it does |
|---|---|
| `Main` | Reads the command-line options, runs the program, catches unexpected errors, and waits for a key press at the end (unless `--no-pause`). |
| `Run` | Loads the settings, lists existing backups, finds the M8, shows the start menu, then runs `Backup` or `Restore` and finally `Finish` (or `Check`, which doesn't need the M8). |
| `Check` | Runs `CheckBackups` and shows the result as a notification. |

### `src\Backup.cs`: backups

| Function | What it does |
|---|---|
| `Backup` | The whole backup: picks the destination folder, scans the M8, compares each file with the last backup, checks names and free space, copies files (renaming old versions in incremental mode), runs `PruneOldVersions`, saves the checksum list, writes the statistics, and runs `PruneBackups`. |
| `VersionedName` | Builds the dated name for an old version, e.g. `Song_2026-10-01_143000.m8s`, adding `_2`, `_3`, … if needed. |
| `IsOldVersion` | Whether a file name has the dated old-version suffix. |
| `OriginalName` | The original name of an old version (`Song_2026-10-01_143000.m8s` → `Song.m8s`). |
| `PruneOldVersions` | Moves the oldest old versions of each file to the Recycle Bin when there are more than `KeepOldVersions`, after asking. |
| `IsStatisticsFile` | Whether a file is one of the program's statistics files. |
| `ListBackups` | Lists the backup folders (named by date), oldest first. |
| `PruneBackups` | Moves the oldest backups to the Recycle Bin when there are more than `KeepBackups`, after asking. |
| `CollectFiles` | Walks the M8's folders and collects files and folders to back up, skipping macOS and Linux leftovers and Windows system folders. |
| `IsLeftover` | Whether a file or folder name is a macOS, Linux or Windows leftover that shouldn't be backed up. |
| `ScanResult` *(class)* | The scan result: files, folders, and the skipped counts. |

### `src\Restore.cs`: restoring

| Function | What it does |
|---|---|
| `Restore` | The whole restore: choose a backup, all files or a single file, compare with the M8, ask about differing files, check free space, copy with safe replacement, and refuse files whose checksum no longer matches the backup's checksum list. |
| `PickFile` | The search-and-pick list for restoring a single file. |

### `src\Changes.cs`: change summary and name checks

| Function | What it does |
|---|---|
| `FindChanges` | Works out new, edited and removed songs and counts other new and changed files. |
| `WriteChanges`, `WriteNameList` | Write the change summary into the statistics. |
| `ShortSongSummary`, `FirstNames` | The short song summary for the notification. |
| `IsSong`, `SongName` | Whether a file is a song (`.m8s`), and its display name. |
| `CheckNames` | Finds names the M8 may not handle well. |
| `Changes` *(class)* | Holds the change lists and counts. |

### `src\FileCopy.cs`: copying and space

| Function | What it does |
|---|---|
| `Compare` | Compares a file with another copy: `New`, `Changed` or `Same` (size and modified time within 2 seconds). |
| `CopyFile` | Copies one file and returns its SHA-256 checksum, with or without verification; retries once on a checksum mismatch. Keeps the original's created and modified times, and never leaves a partial file behind. |
| `CopyAndHash` | Copies a file while calculating its SHA-256 checksum. |
| `HashFile` | Calculates a file's SHA-256 checksum. |
| `ToHex`, `TryDelete` | Small helpers: a checksum as hex text, and deleting a file without failing. |
| `CheckFreeSpace` | Returns an error message if a drive doesn't have enough free space. |
| `FreeSpaceText` | Free space on a drive as text, for the statistics. |
| `FileState` *(enum)* | `New`, `Changed` or `Same`. |

### `src\Checksums.cs`: checksum list and checks

| Function | What it does |
|---|---|
| `LoadChecksums` | Reads a backup's `checksums.sha256` (or returns nothing if it has none). |
| `WriteChecksums` | Writes `checksums.sha256` for every file in a backup folder, hashing any file without a known checksum. Writes to a temporary file first. |
| `IsBackupMetaFile` | Whether a file is one the program writes into backups (statistics or checksum list). |
| `CheckBackups` | Lets you pick one or all backups to check, and totals the results. |
| `CheckBackup` | Checks one backup against its checksum list, or offers to create a list if it has none. |

### `src\Config.cs`: settings

| Function | What it does |
|---|---|
| `LoadConfig` | Reads `M8Backup.ini`, and adds any missing settings to the file with explanations. |
| `ApplySetting` | Checks and applies one setting. |
| `WarnSetting` | Prints the warning for an invalid value. |
| `Config` *(class)* | The settings and their defaults. |
| `SettingDocs` | The explanation text written into the ini file for each setting. |

### `src\M8Device.cs`: the M8 itself

| Function | What it does |
|---|---|
| `FindM8Drive` | Finds the M8's drive letter via Windows' device information (WMI), with the `M8` drive label as a fallback. |
| `EjectDrive` | Safely ejects the M8 through Windows' device manager functions (`cfgmgr32.dll`), retrying a few times. |

### `src\Notifications.cs`: notifications

| Function | What it does |
|---|---|
| `Finish` | Shows the final notification and, depending on the `Eject` setting, leaves the M8 connected, asks (Eject / Not now), or ejects it. |
| `ShowToast` | Shows a Windows notification; for the eject question, waits for a button click or a Y/N key press. |
| `RegisterAppId` | Registers the name "M8 Backup" for notifications. |
| `Escape` | Makes text safe to put into the notification's XML. |

### `src\ConsoleUi.cs`: the console

| Function | What it does |
|---|---|
| `AskKey`, `AskInConsole` | Wait for one of a set of keys (e.g. Y/N, 1/2/3). |
| `AskNumber` | Asks for a number from a list; Enter means cancel / none. |
| `PrintProgress` | The progress line during copying. |
| `FormatSize`, `FormatTime` | Sizes like `1.2 GB` and times like `3m 12s`. |
| `WriteColored` | Prints a line in colour. |

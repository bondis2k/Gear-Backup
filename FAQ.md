# Gear Backup FAQ

Short answers to common questions. The [user manual](MANUAL.md) has the full details and the [feature list](FEATURES.md) has the complete list of what Gear Backup does.

- [Integrity and safety](#integrity-and-safety)
- [Moving and storing backups](#moving-and-storing-backups)
- [How changes are detected](#how-changes-are-detected)

## Integrity and safety

### How do you validate the integrity of files in the backup?

Gear Backup uses **SHA-256 checksums** at three points.

1. **While copying.** Every file is hashed as it is read from the device, and that checksum is saved in the backup next to the file's name, size and dates. A copy is refused if the source file changes while it is being copied (its size or date moves), so a half-changed file is never recorded as good.
2. **Verify copied files** (a setting, off by default). After each file is written, Gear Backup reads it back from the backup drive and compares it with the checksum. If the two differ it copies the file once more; if they differ again it stops and tells you to check the USB cable and the SD card.
3. **Check backups** (on the **Restore** tab, or `GearBackup.exe --check`). This reads every stored file again, recalculates its SHA-256 and compares it with the saved checksum, long after the backup was made. Each file ends up as **OK**, **damaged** (the content no longer matches) or **missing**. The result is saved and the **Dashboard** shows the outcome of the last check.

With the single content store, each stored file is checked once, even if many restore points use it. With plain, browsable backup folders, the files in the folder are checked against its `checksums.sha256` list, and any saved restore points against their own records.

Check does not change or repair the backed-up files; it only records its result. If it finds a damaged file, restore from another copy of the backup if you have one, and start a new full backup from the device. With the single content store, an incremental backup refuses to build on content that is known to be damaged ("Damaged stored content").

### Do I need both Verify and Check?

They answer different questions. **Verify copied files** proves that a file was written correctly *at the moment of the backup*. **Check backups** proves that it is *still* intact today, which catches a failing drive or a damaged copy. Running Check now and then, and after moving a backup, is a good habit.

## Moving and storing backups

### Can a backup folder be moved?

Yes. Everything inside a backup refers to its files by a path relative to the backup, never by where the backup happens to be stored, so a backup works from any folder or drive.

1. Close Gear Backup.
2. Move or copy the whole **Gear Backups** folder (or a single history or backup folder inside it) to the new location.
3. Set **Backup location** to the folder that *contains* `Gear Backups` (Settings, or **More options** on the Backup tab).

Things to know:

- Move the complete backup folder, including its hidden `.gearbackup` folder. That folder holds the restore points, the stored content and the catalog.
- Keep the layout `<Backup location>\Gear Backups\profiles\<history>\<backup>`. Gear Backup finds histories by looking there.
- A copy usually gets new file dates. Gear Backup notices that its catalog (the SQLite index) no longer matches and rebuilds it from the saved records the next time it needs it. Nothing is lost, but that first backup or restore may take a little longer.
- If you use plain backup folders with **Hard-linked history** on, the history shares its content with the backup files. Moving within the same NTFS drive keeps that sharing. Copying to another drive, or to a FAT/exFAT drive, makes ordinary separate files: it works, but uses more space.
- Run **Check backups** after moving to confirm that everything arrived intact.

Tested on a copy of a real M8 backup in a different folder: **Check backups** reported all 3,157 stored files OK, and all of the restore points were listed.

### Can I open my backup files in Explorer?

Not with the default single content store. Each distinct file content is stored once under a checksum name, and the original file names, folders and dates are recorded in each restore point, so you restore through Gear Backup (the **Restore** tab), which puts everything back under its original names. If you would rather have plain, browsable folders, turn off **Use a single content store** in **Settings → Backup** before making a new full backup.

### Is a deleted file removed from my backups?

With the single content store, no. Each backup run saves a restore point. A file deleted from the device is left out of *new* restore points, but earlier restore points still contain it, so you can restore an older point to get it back.

## How changes are detected

### How do you identify if a file is new?

By its **path inside the source**, compared with the previous backup (or, for the single content store, the previous restore point), ignoring upper and lower case. If nothing exists at that path, the file is **New**. A full backup simply copies everything.

A file that is renamed or moved to another folder therefore counts as a new file at its new path, and the old path is left out of the new restore point. With the single content store that costs no extra space: the content is recognised by its checksum and stored only once.

### How do you identify if a file has new content?

For a file whose path already exists in the previous backup, Gear Backup compares these in order:

1. **Size.** A different size means the content changed.
2. **Modified time.** With the same size, the file counts as unchanged if its modified time is within 2 seconds of the recorded one. The 2 seconds allow for the coarse clocks of FAT and exFAT cards. Anything further apart counts as changed.
3. **Missing dates.** Some devices (the M8, for example) can report no date at all, which Windows shows as the year 1601, or a date before 1980. Such a date cannot be trusted, so the file's actual contents are compared instead.
4. **Compare file contents** (`Thorough=yes`, off by default). Gear Backup always reads the file and compares its SHA-256 with the saved one, even when size and date match. That also catches a change that keeps the same size and the same date, and it recognises identical content that only has a new date. It is slower, because every file is read.

By default (steps 1 to 3) a change that leaves both the size and the modified time exactly as they were goes unnoticed. That is rare, but if you want to be certain, turn on **Compare file contents**.

When a file is judged changed, its new content is hashed while it is copied. If that content is already stored, nothing new is stored. With **Preview** on (the default), Gear Backup lists every **New** and **Changed** file before it copies anything, so you can see what it found.

### What is the downside of enabling Compare file contents?

Time, and nothing else.

Normally an incremental backup only looks at each file's size and modified time, which takes a moment even for thousands of files. With **Compare file contents** on, Gear Backup has to **read every file in full** on every backup, even when nothing has changed, to calculate its SHA-256 and compare it with the saved one. The extra time is therefore however long it takes to read the whole device at its read speed. It also applies when you restore or resume an interrupted backup. With plain, browsable backup folders it reads the backup's copy of each file too.

For example, an M8 card with about 1 GB in 3,169 files, read through a USB card reader: a backup with nothing changed took under 3 seconds normally. With **Compare file contents** on, the first one took about 2.5 minutes, because the whole card was read. A repeat straight afterwards took about 4 seconds, since Windows still had the files cached. Your numbers will depend on the card, the reader and how much is stored.

Other than the time, it costs no extra space and does not change what is stored. It is only used to *decide* what to copy, so it never makes a backup less safe. A **full** backup is not slowed down, because it already reads every file once.

It also has an upside: a file whose date changed but whose content did not (for example one that was opened and saved without changes) is recognised as unchanged and not copied again.

A good approach is to leave it off for everyday backups and turn it on for an occasional backup, or when you suspect a file changed without its size or date changing.

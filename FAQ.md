# Gear Backup FAQ

Answers to common questions about Gear Backup 0.9.5, its backup format, supported devices, restores, settings and command line, including the October 2026 security fixes.

For complete procedures and the reference for every command-line switch, use the [user manual](MANUAL.md). For a first backup, use the [quick start guide](QuickStartGuide.md).

## Contents

- [Getting started and installation](#getting-started-and-installation)
- [Devices and connections](#devices-and-connections)
- [Histories and restore points](#histories-and-restore-points)
- [Making backups](#making-backups)
- [How changes are detected](#how-changes-are-detected)
- [Integrity and safety](#integrity-and-safety)
- [Restoring files](#restoring-files)
- [ZIP archives](#zip-archives)
- [Moving and storing backups](#moving-and-storing-backups)
- [Space, retention and cleanup](#space-retention-and-cleanup)
- [Settings and preferences](#settings-and-preferences)
- [Command line](#command-line)
- [X100VI settings and recipes](#x100vi-settings-and-recipes)
- [M8 Song viewer](#m8-song-viewer)
- [M8 card migration](#m8-card-migration)
- [Troubleshooting and reporting problems](#troubleshooting-and-reporting-problems)

## Getting started and installation

### What does Gear Backup do?

It backs up supported music gear, camera files and ordinary folders, records complete restore points, checks saved content for damage, and restores files to a drive or PC folder. It also creates standalone ZIP archives, reads X100VI recipe/settings exports, and displays information from M8 songs.

It is a file-backup application, not a disk imager, music editor or general device-firmware tool. See [Getting started](MANUAL.md#getting-started) for the first-backup workflow.

### Which Windows versions are supported?

The application targets Windows 10 or 11 with .NET Framework 4.6 or newer. Windows supplies the SQLite library; you do not need a database server. The native M8 song reader needs a 64-bit application process.

### Should I use Setup or Portable?

Setup installs for your Windows user and creates Start menu/uninstall entries. It offers optional desktop shortcuts and CLI access through PATH. Portable extracts into a writable folder without adding those integrations.

Both provide the same backup features. Portable settings live beside the programs; installed settings live in AppData. Portable does not mean that every working file stays on the portable drive: MTP mirrors still live in the PC's Local AppData folder.

### Do I need administrator privileges?

Routine use and the standard per-user installation do not require administrator privileges. You need ordinary read access to sources and write access to destinations.

Running as administrator does not fix an incorrect device mode or unsafe source/destination overlap. Development security tests that create Windows links have different permission requirements from ordinary backups.

### How do I update without losing my settings?

Run the newer Setup package, or extract a newer portable package over the portable application folder. The packages preserve personal INI files.

Keep the matching EXEs and DLLs together. Updating only one executable can produce an incompatible installation. Backups live at the configured storage location rather than automatically beside the new program.

### Why does the package have two EXEs and two DLLs?

`GearBackup.GUI.exe` is the graphical application and `GearBackup.exe` is the CLI. Both use `GearBackup.Core.dll`. The GUI's M8 Song feature also uses `m8_reader.dll`.

Keep the required libraries beside the programs. Obtain a matching application package instead of downloading a replacement DLL from an unrelated source.

### Where can I find the manual offline?

Use Help or F1 in the GUI. It opens the HTML edition matching the selected appearance. `Manual.html` is the default Original edition. Keep the `assets\manual` folder with the HTML files for illustrations.

The [Markdown manual](MANUAL.md) contains the same source content, including all CLI commands, switches and examples.

## Devices and connections

### Which devices are supported?

The current profiles cover Dirtywave M8, Polyend Tracker Mini, OP-1 Field, OP-XY and Fujifilm X100VI. You can also choose an ordinary source folder.

M8 and Tracker Mini can use disk/card access. OP-1 Field and OP-XY use MTP. X100VI file backup and X100VI settings capture use different camera modes. The [connection table](MANUAL.md#connecting-a-device) explains each workflow.

### Why can Explorer see my device when Gear Backup cannot use a drive letter for it?

An MTP portable device is not an ordinary filesystem drive. Explorer displays it through Windows' device interface rather than assigning a path such as `E:\`.

Leave the explicit source empty and let Gear Backup detect the device. Do not type its Explorer display name as `--source`. Close other device applications and check file-transfer mode if detection fails.

### What is an MTP mirror?

It is a persistent PC working copy downloaded from a portable device before backup. Gear Backup synchronizes it, then backs it up using the normal engine.

The mirror uses additional disk space and is not an independent history. Files removed from the device can be removed from the mirror during synchronization, while older backup points remain available. Mirrors are stored under `%LOCALAPPDATA%\GearBackup\Devices`.

### Can Gear Backup restore directly to OP-1 Field or OP-XY?

Direct MTP upload is not implemented. Restore to a separate PC folder, inspect the result, then transfer the desired files through Explorer.

Choose a folder outside the MTP mirror. Editing the mirror is not a substitute for restoring to the device, and later synchronization can replace or remove edits there.

### Can I use a memory-card reader or a copied card folder?

Yes. Choose its drive/folder as the source. For an unrecognized manual source, set the appropriate Device preference so backup metadata and device-specific restore choices are correct.

The Tracker Mini marker can identify its card. An otherwise unrecognized source defaults to M8. The presence of photographs alone does not select the X100VI profile.

### Will my history change if Windows gives the card another drive letter?

For a whole drive, automatic identity normally uses its volume serial number, so a letter change can retain the same history. Folder sources use an identity derived from the full path.

Reformatting a card or moving a source folder can change that identity. Use a deliberate History name/Profile if you need fixed grouping across such changes. Do not reuse one name for unrelated sources accidentally.

### Are factory sounds and hidden device content included?

Only content exposed through the selected filesystem or device interface can be backed up. OP-XY factory content not exposed over MTP cannot be included.

The normal source scan also excludes common filesystem leftovers. Gear Backup is not a sector-by-sector copy of all device storage.

### Why do the device header tabs say Coming soon or Planned?

You selected a device-specific header mode. M8 mode includes **Sample check** and **Card migration** in builds containing those features; other dedicated tools are still placeholders. Select the **Gear Backup** header icon to return to the complete Backup and Restore interface.

Detection from Gear Backup mode keeps those normal tools available. A placeholder tab does not mean the installation is broken.

## Histories and restore points

### What is the difference between a history, a full set and a restore point?

A history groups backups from one automatic source identity or explicit Profile. A full backup starts an independent set within that history. An incremental run adds another restore point inside a set.

Each point records complete membership for its run. You restore the chosen point directly rather than applying a full backup followed by increments in sequence.

### Are History name and Name for this backup the same thing?

No. History name controls grouping and corresponds to `--profile`. Name for this backup labels a full set; Name for this incremental backup labels one point.

A Dashboard/Storage history nickname changes its readable display name without changing the underlying folder identity. CLI `--name` has another specific purpose: suggesting a standalone ZIP filename.

### Does an unchanged incremental run create a point?

Yes. A successful run creates a point even if it needs no new content objects. This records the captured state for that run without duplicating unchanged contents inside the set.

### Do I need every earlier incremental to restore the latest one?

You do not apply increments in order. Each point has its own complete inventory.

However, the content store belongs to the set, and objects can be shared between points. Keep the entire set and its metadata together; copying just one manifest is not a usable backup.

### Can I delete individual points to reclaim space?

There is no individual restore-point pruning control. Points remain until the whole full-backup set is removed.

Do not manually delete content objects: several points can depend on the same object. Use full-set retention or reviewed Storage cleanup instead.

### Why is the latest backup protected in Storage?

Storage cleanup protects the latest set of each history so normal cleanup does not remove the most recent set. Automatic retention also rechecks latest-set protection before recycling.

This is a guard against accidental removal, not an assurance that the latest set is your only necessary copy. Check its completion/health and keep independent copies as appropriate.

## Making backups

### When should I choose Full instead of Incremental?

Choose Full for a new independent set, a milestone, a new source/history, or replacement of an unusable old-format set. Choose Incremental for routine points within an existing set.

Deduplication is within a set. Two full sets do not share their content stores, even when their source files are identical.

### Can I choose which full set receives an incremental update?

The GUI provides **Full backup to update**. Select the intended set before starting.

The CLI uses the latest set in the resolved history. There is no CLI switch for selecting another dated incremental target. With no previous set, the first CLI backup can be full even when Mode is incremental.

### What happens if a file was deleted from the device?

It is omitted from the new point's membership. Older points that included it remain able to restore it while their set and required content are intact.

Mirror synchronization can also remove a deleted device file from the working MTP mirror. That does not remove it from earlier saved points.

### What happens if I rename or move a file?

The new relative path is new membership and the old path disappears from the new point. If its bytes match content already in the set, the object can be reused rather than stored again.

Membership and content storage are separate: a changed pathname does not necessarily mean another full copy of the bytes.

### Does backup change files on my device?

Ordinary file backup reads the source and writes to PC/backup storage. It does not upload files to an MTP device.

X100VI settings capture is a separate protocol workflow: it temporarily selects recipe banks for reading and restores the selector. Leave the source idle so the captured files/settings remain consistent.

### Why are dotfiles, Thumbs.db or lost+found missing?

The scanner deliberately excludes common filesystem leftovers, including dot-prefixed items, `lost+found`, `FSCK0000.REC`-style fragments, files ending in `~`, `Thumbs.db`, `ehthumbs.db` and `desktop.ini`, as well as Windows system-volume/Recycle Bin folders.

This makes Gear Backup unsuitable as a forensic image or an indiscriminate copy of every filesystem item. Review the [backup behavior](MANUAL.md#full-and-incremental-backup) if exclusions matter to your source.

### Can an interrupted backup be resumed?

An unfinished latest set can be offered for resume. Completed work can be reused where appropriate; a complete point is published only after capture succeeds.

Wait for cancellation/failure to finish, keep the source idle, and retry. Do not delete metadata to make an incomplete folder look complete. A dated folder's existence alone does not prove successful completion.

## How changes are detected

### How is a new file identified?

Its relative path is compared with the previous inventory. A path absent from that inventory is new. The app's inventories treat filename case without distinguishing upper/lower case for membership comparisons.

A full backup starts a separate set instead of updating an older set's membership. Renames and moves therefore have different membership effects from edits at an unchanged path.

### How does the default comparison detect changed content?

It uses size and usable modified time, allowing a two-second timestamp tolerance. A different size or sufficiently different time indicates change. Files with unusable dates, such as missing/pre-1980 timestamps, are compared by bytes instead.

A same-size edit retaining its usable timestamp can be missed by the default comparison. Enable **Compare file contents** when that possibility matters.

### What does Compare file contents / Thorough do?

`Thorough=yes` compares content using SHA-256 instead of relying on matching size/time for applicable comparisons. It can detect same-size, same-timestamp edits and recognize identical content with changed dates.

It affects backup/restore/resume comparisons. It is a decision about whether contents differ, separate from optional verification of copies after they are written.

### What does enabling Thorough cost?

More source and/or target reading, hashing, time and device activity. On a slow card or network path, extra reads can be substantial. Operating-system caching can make an immediate repeat much faster, so one timing does not predict every later run.

It does not require a second historical copy of unchanged data simply because the comparison is thorough. Avoid assuming a fixed slowdown or that every full backup has identical performance with it enabled.

### What is the difference between Thorough, Verify and Check?

| Feature | Main purpose |
| --- | --- |
| Thorough | Decide whether existing content differs during comparison. |
| Verify | Apply automatic integrity/read-back checks during supported copy/restore operations. |
| Check backups | Read saved objects later and compare them with recorded hashes. |

You can enable the first two independently. Check is an explicit workflow rather than a setting that must stay enabled.

## Integrity and safety

### How are file checksums recorded?

Captured content is hashed with SHA-256. Restore-point records associate original paths and metadata with content hashes. The set can reuse identical objects across points.

Recording a hash is not the same as reading the written copy back. That extra automatic checking is controlled by Verify for applicable operations. Explicit Check reads saved content later.

### What happens when Verify finds a bad copy?

The copy helper reads back copied content and compares hashes when verification is enabled. A mismatching copy is retried once; repeated mismatch is reported as a failure. Other damaged-content checks can also stop an operation.

Read the specific error and preserve usable sources. Check the connection/storage and retry into a trustworthy destination rather than assuming an existing object or folder is healthy.

### Do I need both Verify and periodic Check?

They provide evidence at different times. Verify checks applicable copies during an operation. A later Check tests whether saved content still matches its records after storage, movement or other changes.

Neither guarantees a drive cannot fail tomorrow. Keep an independent usable copy and test representative restores as well.

### Does Check repair damaged files?

No. It reads content, reports results and can update recorded check/catalog information. It does not reconstruct missing bytes.

Recover affected content from the original source, another valid set/point or an independent copy. Rebuilding the SQLite catalog repairs an index, not damaged objects.

### Are checksums proof that an archive is authentic?

No. They detect differences relative to recorded hashes. Someone who replaces an archive's content and checksum manifest together can replace the hashes too.

Restore archives from sources you trust. Mandatory ZIP-content verification detects inconsistencies/damage; it does not identify the person who created the archive.

### Are backups encrypted or password-protected?

Gear Backup does not encrypt its backups, ZIPs, XML exports, mirrors or recovery copies. Metadata can also reveal filenames and device identifiers.

Use appropriate storage permissions and disk encryption where needed. Portable application storage does not make its Local AppData MTP mirror private or portable.

### Why are links and junctions refused?

They can redirect a seemingly contained path outside the folder you selected. The app refuses linked/unsafe paths rather than following them through backup/restore operations.

Select a direct ordinary path to the actual data. This can require choosing the underlying folder instead of a convenience link used elsewhere on your PC.

### Can two windows write the same history at once?

History locking prevents concurrent writers to the same history. Wait for the other operation or close its process if a history is busy.

A lock file remaining on disk does not itself mean a lock is active. Deleting the filename is not the appropriate first response; the open operating-system handle is what holds the lock.

### Does Preview mean absolutely nothing is written anywhere?

No. The selected backup/restore plan is not committed, but preparing a source or verifying an archive can require other activity.

MTP preparation can create/update its persistent mirror, including removing stale mirror files. Camera-settings preview communicates with the camera and prepares temporary exports. GUI ZIP restore preview extracts temporary files for verification. See [Preview behavior](MANUAL.md#cli-switch-reference) before treating it as a zero-side-effect dry run.

### Are the application packages code-signed?

They are not currently Authenticode-signed. Obtain packages and their reference SHA-256 files through a trusted release source.

A matching checksum is useful only when the reference hash itself comes from a trustworthy channel. This is distinct from the checksums stored inside a backup or ZIP.

## Restoring files

### Can I restore without connecting the original device?

Yes, for content-store backups. Choose an existing PC destination. With no device detected, GUI restore lists backups for all recognized device types at configured locations.

When a device is detected, filtering is by device type rather than one physical serial number. Standalone ZIP restore has a separate detected-device-type requirement.

### Do I restore a full backup and then each incremental?

No. Select the point you want; it has complete membership for that run. Gear Backup resolves the required content within its set.

Keep that whole set intact. A point's manifest alone does not contain its file bytes.

### What is a merge restore?

It restores selected saved files as needed while leaving unrelated target files in place. Differing existing files can produce replacement choices.

Merge is useful for a PC inspection folder or a target containing files you are not trying to remove. It is different from an exact point restore.

### What does Remove extra target files do?

It requests an exact full saved-point restore: after successful copying, scanned target files/empty folders absent from the chosen membership can be removed. Review the removal list carefully.

It is not formatting or a byte-for-byte disk clone, and the scanner's exclusions still matter. Choosing to keep differing files also disables exact removals. Copy failures prevent extra-file cleanup from proceeding as if the copy succeeded.

### Can I restore only one project or folder?

Use **Restore individual files → Choose files…** in the GUI and select the folder. Its contents are included even if not expanded.

The CLI also offers device-specific scopes, such as an M8 bundle or a whole Tracker project folder. Choose a whole project when its supporting samples/patterns are needed, rather than only its project file.

### Can I restore an M8 song with all referenced samples?

The CLI has a supported M8 **Song with dependent samples** choice. Dependency extraction supports the firmware-6 song layout and includes references from unused sampler slots.

Missing referenced samples stop a complete dependency restore. For unsupported layouts, use bundle/full restore or select files yourself. The Song viewer is a separate inspection feature and does not gather/copy samples.

### What are recovery copies?

With Recovery enabled, existing target files about to be overwritten/removed are saved to a dated PC recovery folder before target mutation. Recovery is off by default.

The default location is `<BackupFolder>\Gear Recovery`; an explicit RecoveryFolder can use another location. It must be outside the target and needs extra free space. A recovery failure stops the restore. Recovery does not replace an independent copy of the source backup.

### Why cannot I restore into a backup folder?

That could overwrite or delete objects needed by the backup you are using, or corrupt another set. Content-store restores reject overlapping backup/destination paths and protect configured repositories/recovery storage.

Use a separate target such as `D:\RestoredFiles`, outside `Gear Backups` and `Gear Recovery`. In CLI Restore, `--source` names this destination, not the saved backup.

### Will cancellation undo every restored file?

No. Completed target copies can remain. Staged temporary copies are cleaned where possible, and the final summary reports cancellation/failure.

Read the result before retrying. Merge, recovery copies and an empty test destination can make it easier to inspect a partial outcome.

### Why am I warned about restoring photos onto a camera card?

Old photos are normally restored to a PC for viewing/editing. Putting them back on a camera card can clash with its numbering and does not restore camera settings.

The warning explains this consequence; a photo-file restore is not a camera configuration restore.

## ZIP archives

### How do Zip archive and Also save as ZIP differ?

| Choice | Result |
| --- | --- |
| Zip archive / `--zip-archive` | Standalone full ZIP without a content-store set, including device information and checksums. |
| Additional ZIP / `--zip` | Full content-store backup followed by a ZIP export beside the set; not applied to incrementals. |

The additional export does not contain the standalone information file required by GUI ZIP restore. Extract it with a ZIP tool, or restore its original set through Gear Backup.

### Can a standalone ZIP be opened without Gear Backup?

Yes. It holds ordinary relative file paths and empty folders, plus `gearbackup-info.txt` and `checksums.sha256`. A normal ZIP tool can inspect/extract it.

The application adds metadata/verification for its own restore workflow. Independently extracting an archive does not automatically perform all of Gear Backup's device, space and checksum checks.

### Can I restore any ZIP through the GUI?

No. **Restore ZIP archive** expects a valid standalone Gear Backup archive with both required metadata files, complete checksum coverage, consistent sizes/counts and safe paths.

Ordinary ZIPs and the extra `--zip` export may not qualify. Use a ZIP tool separately when appropriate; do not edit metadata merely to bypass an integrity failure.

### Does standalone ZIP restore need a detected device?

Yes. Its current GUI workflow requires detected device-type matching, including when the chosen final target is a PC folder.

This differs from offline content-store restore. For MTP, choose a PC folder outside the mirror and transfer recovered files manually afterward.

### Is ZIP-content verification optional?

No, not for standalone ZIP restore. The archive's contents are checked before target changes even with `Verify=no`.

Verify still controls other applicable automatic copy checks. The generated information/checksum files are not restored as device content, and ZIP restore merges rather than removing unrelated target extras.

### Why can ZIP preview need significant temporary space?

It extracts content into a temporary workspace to verify it before presenting the target plan. That workspace may be on another drive from your destination.

Checks combine simultaneous requirements when both locations share a volume. Logical file sizes exclude allocation overhead, so leave additional room. Temporary cleanup warnings identify workspace that could not be removed.

### Why is a very large or unusual ZIP refused?

Inspection bounds both actual central-directory records and metadata before the framework allocates all entries. Current limits include 1,000,000 entries, a 32 MiB central directory, 1,024-byte names and 4,096-byte per-entry extra data/comments. A byte budget can be reached before the count limit.

Single-disk ZIP/ZIP64 structures are validated. Inconsistent/unsupported structures, unsafe names, duplicates, invalid sizes and hash mismatches are also refused. See [format limits](MANUAL.md#safety-privacy-and-limits).

### Can I create a ZIP inside the folder being archived?

No. Save it outside the source so it cannot become part of its own input. The source's top-level `gearbackup-info.txt` and `checksums.sha256` names are also reserved for generated metadata.

An explicit output file must end in `.zip` and its parent must exist; an output directory must already exist. Existing-archive replacement still needs confirmation.

## Moving and storing backups

### Can I move a backup to another disk?

Yes. Stop operations and copy the complete set/history, including hidden `.gearbackup` metadata. Keep the discovery layout under `<BackupFolder>\Gear Backups\profiles\<history>\<set>`.

Set BackupFolder to the parent containing `Gear Backups`, and update device/history preferences pointing at the old location. Run Check and test a restore from the new storage before deleting the original. Catalog repair may be needed if its recorded state is stale; do not assume movement alone proves successful copying.

### Can I open original backup filenames directly in Explorer?

A content store holds hash-named objects, with original names recorded by restore points. Use Restore to recreate ordinary files, or extract an appropriate ZIP export.

Do not rename objects to guessed filenames or manually rearrange the metadata. That can break the references several points depend on.

### Is it enough to copy catalog.sqlite or the snapshots folder?

No. Those are indexes/records, not all file content. A usable set needs its stored objects and associated metadata too.

Copy the whole set. Ensure hidden folders are included and verify the destination afterward.

### Can I use old plain-folder or hard-linked backups?

The current engine supports content stores rather than the former browsable/hard-linked layouts. Start a new full backup in the current format.

Keep old ordinary files for reference or recover them separately. Removed layout settings and catalog rebuilding do not convert them. Old hard-link space-sharing advice does not describe current sets.

### What if catalog.sqlite is missing or corrupt?

It is rebuildable from backup records. The app can use recovery records where appropriate and provides `--rebuild-catalog` for one set.

Rebuilding cannot recreate missing objects or establish new trusted hashes for damaged content. See [catalog repair](MANUAL.md#cli-command-reference) for the exact command and restrictions.

### Can I keep backups on a network or removable drive?

Choose accessible storage supported by the app's filesystem/path checks, with enough space and appropriate permissions. Ensure the location is connected before starting and keep an independent usable copy.

Not every operation can query capacity through every path type. Some strict free-space checks can refuse a location whose capacity cannot be determined. Do not assume all network paths behave identically; test backup, check and restore using the actual storage.

## Space, retention and cleanup

### Why is my backup not exactly the same size as the source?

Content is deduplicated within a set, metadata occupies space, and older points retain older content. Filesystem allocation differs from logical byte totals. Independent full sets duplicate their own content, while ZIP compression changes archive size.

Mirrors, recovery copies and Recycle Bin contents also occupy disk space outside the selected set. Storage helps distinguish these categories.

### Does KeepBackups count incremental points?

No. It counts full sets. All points of a set remain until the set is removed. `0` keeps all sets.

Retention can offer excess older sets after a successful backup, but still requires confirmation. It rechecks directory identity, metadata and latest-set protection before recycling.

### Is automatic retention completely unattended?

No. Confirmation remains required, including when a count is configured. A folder changed after preview can be refused rather than recycled.

The CLI's `--no-pause` does not approve retention. Account for that before scheduling jobs.

### Why is free space unchanged after cleanup?

Cleanup normally moves data to the Recycle Bin. Space may not be released until you empty it through Windows.

Also check for other full sets, archive exports, mirrors and recovery copies. Removing one set does not remove those independent categories.

### Can I delete an MTP mirror safely?

Storage can remove the working mirror; the next file backup must download device content again. Confirm you have a real usable backup first.

A mirror is not historical storage. Do not keep the only copy of edited/recovered files there and expect synchronization to preserve them.

### Why does restore need more room than the final file sizes?

Replacing a file can temporarily require both old and new copies. Recovery can save previous target data elsewhere. ZIP restore also needs extraction space, and filesystem overhead adds room beyond logical sizes.

The final content size is therefore not the whole working-space requirement.

## Settings and preferences

### Where are my settings?

Installed copies use `%APPDATA%\GearBackup`. Portable/local builds use the executable folder. Global values are in `GearBackup.ini`; overrides are in `GearBackup.Profiles.ini`.

An installed copy is identified by `GearBackup.installed`. It does not import an INI placed beside its executable. See the [location table](MANUAL.md#installing-and-finding-your-files).

### Why is a backup going somewhere other than my global BackupFolder?

Supported device/history overrides take precedence. Effective preferences resolve from global settings, then device defaults, then history preferences.

Review Device & backup settings and its Use default selections. Changing only the global location does not erase an explicit override pointing at another disk.

### What are the defaults for verification and recovery?

Verify, Thorough and Recovery are off. Preview/review is on. KeepBackups is zero, meaning keep all, and Eject defaults to no.

Enable stronger comparisons, copy checks or recovery deliberately and allow their time/space costs. The [complete INI reference](MANUAL.md#all-ini-settings) lists every setting.

### Can I make different devices use different preferences?

Yes. Device & backup settings supports preferences such as BackupFolder, Verify, Thorough, Preview, retention, recovery, name-warning length and ejection.

Identity/global controls such as Device, SourceFolder, Profile, Mode and Theme are not override keys. Use the editor rather than inventing unsupported sections.

### Are command-line overrides saved back into my INI?

`--source` and `--profile` override the current invocation; they are not saved as new global SourceFolder/Profile values. Other GUI settings workflows can save preferences deliberately.

Ordinary settings loading can append missing documented defaults. CLI `--preview` suppresses that update. This is separate from source-preparation activity during preview.

### Can I restore the older backup format by setting SingleContentStore=no?

No. Removed keys such as SingleContentStore, PointInTimeHistory, HardLinks and KeepOldVersions do not re-enable old layouts.

The current format is a content store. Start a new full backup when moving from an unsupported old set.

## Command line

### Where is the complete command reference?

The manual has [command forms and exit codes](MANUAL.md#command-line-and-exit-codes), [workflow descriptions](MANUAL.md#cli-command-reference), [every switch](MANUAL.md#cli-switch-reference) and [worked examples](MANUAL.md#cli-examples-and-operating-recipes).

The current parser accepts these 12 switches:

| Switch | Main use |
| --- | --- |
| `--restore` | Restore saved content-store files. |
| `--check` | Check saved backups without requiring a device. |
| `--camera-settings` | Capture X100VI recipes/settings. |
| `--zip-archive` | Create a standalone full ZIP. |
| `--rebuild-catalog PATH` | Rebuild one set's SQLite index. |
| `--source PATH` | Select backup source or restore destination. |
| `--profile NAME` | Set history grouping/context. |
| `--zip` | Add ZIP export after an applicable full backup. |
| `--output PATH` | Standalone ZIP file or existing output directory. |
| `--name NAME` | Suggest a standalone ZIP filename. |
| `--preview` | Prepare/show instead of committing the selected operation. |
| `--no-pause` | Suppress final exit-key wait. |

### How do I launch the CLI from PowerShell?

From its executable folder:

```powershell
.\GearBackup.exe --no-pause
```

If you selected Add CLI to PATH, open a new terminal and use `GearBackup.exe` by name. Without PATH, a quoted installed path needs PowerShell's call operator:

```powershell
& "$env:LOCALAPPDATA\Programs\Gear Backup\GearBackup.exe" --check --no-pause
```

Quote spaces and supply option values separately; `--key=value` forms are not implemented.

### How do I force Full or Incremental from the CLI?

Use `Mode=full` or `Mode=incremental` in the shared INI, or choose interactively with `Mode=ask`. There are no `--full`/`--incremental` switches.

```powershell
.\GearBackup.exe --source "E:\" --profile "Studio" --no-pause
```

Incremental targets the latest set. Review, resume, location creation and retention can still ask questions even after Mode bypasses its menu.

### Why does --source name the destination during restore?

Restore uses the selected device/folder path as its target. The saved history/set/point is chosen separately through the restore prompts.

Create a destination first, then run:

```powershell
New-Item -ItemType Directory -Path "D:\RestoredFiles" -Force
.\GearBackup.exe --restore --source "D:\RestoredFiles" --profile "Studio" --no-pause
```

Do not put the saved backup's folder in Source expecting it to select that set.

### What does --profile actually select?

It controls history grouping and preference context, not one full-set label or point name. For Check, it identifies the exact history folder name; multiple matching locations can still prompt.

Names are limited to 1–64 characters, start with an ASCII letter/digit, and use ASCII letters/digits, spaces, hyphens or underscores. Trailing spaces and reserved names are refused. Use distinct names for unrelated sources.

### How do I check backups without a connected device?

```powershell
.\GearBackup.exe --check --profile "Studio" --no-pause
```

Choose one set or all sets. Omit Profile to choose a history. Source is not used to select what Check reads.

### Why does --check --preview not perform verification?

That combination prints an explanation and returns without running the check. Run Check without Preview for actual content verification.

Preview behavior differs by workflow: it does not mean “run every read-only operation exactly as normal.”

### How do I create a standalone ZIP with a chosen filename?

Ensure the parent directory exists:

```powershell
New-Item -ItemType Directory -Path "D:\Archives" -Force
.\GearBackup.exe --zip-archive --source "D:\M8Card" --output "D:\Archives\Before tour.zip" --no-pause
```

For a naming suggestion in an existing directory:

```powershell
.\GearBackup.exe --zip-archive --source "D:\M8Card" --output "D:\Archives" --name "Live set" --no-pause
```

Name does not override an explicit `.zip` output file. Output/Name are not normal backup-location or point-label switches.

### Why did --zip not create a ZIP after my incremental?

It applies to full backups, not incrementals, and does not force Full mode. Choose Full or set `Mode=full` when you want a content-store set plus an additional export.

Choose `--zip-archive` instead for a standalone full ZIP without a normal set. Do not combine the two switches.

### How do I preview an operation?

```powershell
.\GearBackup.exe --source "E:\" --profile "Studio" --preview --no-pause
```

The selected backup/restore plan is not committed. Mode/source/history choices may remain, and device preparation can have working-file/device activity as explained under [Preview safety](#does-preview-mean-absolutely-nothing-is-written-anywhere).

`Preview=yes` is different: it enables normal review and can continue after approval.

### Does --no-pause make the CLI unattended?

No. It removes only the final Press any key wait. The workflow can still ask for source, mode, history, point, overwrite, review, resume or retention decisions.

There are no `--yes`, `--force` or `--silent` switches. Test the actual workflow before scheduling it, and do not assume output redirection answers its prompts.

### Does exit code zero guarantee a backup was saved?

No. Zero can mean success, preview, declined work or cancellation. Read the summary and inspect backup state.

Code `1` indicates a workflow/input/prerequisite error; `2` indicates reported failed work or damaged/missing content found by a check. There is no separate cancellation code. An unsupported unchecked set is not proven healthy by the process exit code.

### How do I inspect the exit code or save a log?

PowerShell exposes `$LASTEXITCODE`. For a workflow whose prompts you understand:

```powershell
.\GearBackup.exe --check --profile "Studio" --no-pause > ".\check.log" 2>&1
$checkExit = $LASTEXITCODE
Get-Content ".\check.log"
"Exit code: $checkExit"
```

Prompts can be hidden in the redirected log while the program still waits for input. In Command Prompt, inspect `%ERRORLEVEL%` immediately after the relevant command instead.

### How do I rebuild a catalog?

Pass one existing dated backup-set folder:

```powershell
.\GearBackup.exe --rebuild-catalog "D:\Backups\Gear Backups\profiles\Studio\2026-10-16" --no-pause
```

Only Preview/No-pause may accompany this operation. Do not pass Profile, Source, another operation, the repository root or `catalog.sqlite` itself. It repairs an index, not content or old formats.

### Can I restore a ZIP, choose a device type, or request help with a CLI switch?

There are no `--restore-zip`, `--device`, `--help`, `--version`, `-h` or `-?` flags. Use GUI ZIP restore, the Device INI preference, this manual, and About/package metadata as appropriate.

There are also no `--verify`, `--thorough` or `--backup-folder` flags. Those preferences are configured through the supported settings. Unknown CLI arguments are rejected rather than silently accepted.

## X100VI settings and recipes

### Does a photo backup include my camera recipes?

No. File backup and settings capture are separate operations and histories. Use the X100VI settings workflow for C1–C7 recipes and current shooting settings.

For the CLI:

```powershell
.\GearBackup.exe --camera-settings --profile "Camera" --no-pause
```

Connect exactly one X100VI in USB RAW CONV./BACKUP RESTORE mode. Do not combine camera settings with Source, Restore or Check.

### Is settings capture a full-camera or X Acquire backup?

No. It creates the app's XML recipe/shooting-setting exports, not a full-camera configuration image or Fujifilm X Acquire backup.

Settings capture uses temporary recipe-bank selection for reading and restores the selector. It does not provide camera-settings upload/restoration.

### Can I view recipes when the camera is disconnected?

Yes. Select a supported settings backup in Dashboard/Storage and use View recipes, or open an appropriate XML export/folder in the viewer.

The viewer displays recognized film simulation names and values; unknown values remain explicit protocol values. Copy/export creates a readable text reference, not a camera import file.

### How can I get the saved XML files back?

Restore the settings backup's files to an ordinary PC folder. Keep them as exports or open them in the recipe viewer.

Restoring the XML files does not write settings into the camera. Apply settings manually if that is your intended next step.

## M8 Song viewer

### What can the Song tab show?

The Song tab is part of the M8 view (choose the M8 icon in the header). It reads a `.m8s` and shows available name, firmware, tempo, transpose, quantize, length, used track rows, chain/phrase/table counts, and instruments with names/types/sample references.

It does not play, edit or save the song. Viewing a sample path does not prove the sample is present.

### Can I inspect a song stored in a content-store backup?

Restore it into an ordinary folder, then open that `.m8s` in Song. Stored objects use hashes rather than the original filenames, so the restore view is the appropriate route back to named files.

The viewer does not gather samples or perform dependency restore. Those are separate restore choices.

### Why does a song fail to open?

It may be truncated, damaged, unsupported or too large; inputs above 16 MiB are refused. The native reader also needs a 64-bit process and the matching bundled DLL.

Try a known-good file and re-extract/reinstall the matching package if library loading fails. The viewer reports an error rather than repairing the song.

### Can I check which samples an M8 song is missing?

Select the **M8** header icon and open **Sample check**. Choose the song and the root of its card or extracted card folder, then choose **Check samples**. References include unused sampler slots and are grouped with their instrument slot numbers. Card presence checks accessibility, not audio validity. This feature is available in builds after 0.9.5.

### Can I check all songs on the M8 at once?

Choose the card root on **M8 → Sample check**, then click **Check all songs on card**. It finds `.m8s` songs recursively and reports which songs have missing sample references. Select a song to see its references; double-click it for an individual check with the current backup-search setting. The whole-card scan only checks card contents. Unreadable songs, invalid references and incomplete scans are reported separately. Cancel stops the scan without changing songs or samples.

### How are sample paths in demo or bundled songs resolved?

`/Samples/kick.wav` starts at the card root. `Samples/kick.wav` starts in the folder containing the song, so a song in `Songs\Demos` uses `Songs\Demos\Samples\kick.wav`. Both individual and whole-card checks use the song's actual location. An outdated embedded song directory does not change that lookup. Hover over a reference to see the resolved path; keep the song inside the selected card tree when checking relative references.

### How do I search older backups for sample copies?

If you turn **Search M8 backup histories** off, card results stay visible and history copies are hidden. Turn it back on to automatically search again for the selected song. This also works for selections in the whole-card results list.

Enable backup search and select your backup location. The checker searches M8 histories and their saved restore points, including configured device/history locations. Select a reference to see matching backup folders, restore points and original paths. Exact-path matches and filename-only candidates are distinguished; matching objects are verified against saved hashes. A matching path or name does not prove that it is the version intended by the song. Warnings indicate incomplete searches or the 100-match limit per reference.

### Does Sample check repair the song or restore files automatically?

No. Double-click a verified copy in the lower table and confirm to open **Restore individual files** with its set, saved point and file selected. Review the destination and click **Restore** to recover it. Filename-only candidates retain their original folder path. You can search backups without a card by leaving the card-root field empty, or check only the card by disabling backup search. Cancel stops the current check; changing inputs clears old results.

## M8 card migration

### Can Gear Backup help move to a replacement M8 card?

Yes. Open **M8 → Card migration**, choose a completed M8 backup and full restore point, then choose an empty replacement card root or empty test folder. Review the preview before copying. The assistant always verifies the restored files and then checks all restored songs for missing samples. Save the detailed report if you want a record. This feature is available in builds after 0.9.5.

### Does Card migration format the card or overwrite my existing files?

No. Prepare the card separately. Migration requires an empty destination and refuses ordinary existing content. At a drive root it tolerates Windows' system-volume and recycle-bin directories. Use normal Restore for a destination already containing files. Migration does not flash firmware, edit song files or automatically eject storage.

### Can the replacement card continue the original backup history?

Yes. Check **Link replacement card to the original backup history** before migrating to the card's drive root. After a successful copy, verification and song scan, the app saves the new card's volume ID against the selected original history. Future backups use that history, its backup location and preferences, even if the card's drive letter changes. Old restore points remain available. An explicit Profile/History name takes priority. Linking is off by default and cannot be used with rehearsal folders.

The report confirms whether linking succeeded. Cancelled, failed or needs-attention migrations do not save a link. Links live in `GearBackup.CardLinks.ini` with the app's settings; preserve that file when moving PCs and keep the original backup path accessible. Without linking, the new card gets its own automatic history, and you can still restore old backups normally.

### Why does migration need attention when all files were copied?

The chosen backup may already have missing song samples or unreadable songs. The report separates file verification from dependency checks and records incomplete scans. A complete copy does not fix missing dependencies in the backup.

### Can I resume a cancelled migration?

Completed copies can remain, so the destination is no longer empty. Use ordinary Restore to continue into it, or choose another empty destination. Cancellation does not roll back completed files, and final verification/song checks may not have run.

## Troubleshooting and reporting problems

### Why is a backup or restore slower than expected?

Consider device/read speed, number of files, Thorough comparisons, Verify read-back, MTP synchronization, changed large files and storage/network latency. First-time mirror downloads can dominate an MTP workflow.

Check the reported stage before assuming it is stuck. An immediate repeat can benefit from cache and is not a reliable prediction of a cold run.

### Why does a previously used destination now fail?

Check free space, permissions, connection, effective preferences and whether its path now contains a link/junction. It may also overlap protected backup/recovery storage.

Read the exact error. Changing a drive letter or reconnecting a disk can change what a path refers to; do not retry a destructive restore without verifying the target.

### What should I do when an operation reports partial failure?

Keep the source and usable backups intact. Read copied/removed/failed counts and activity text. Check whether the set is complete, and inspect any recovery or temporary-cleanup paths reported.

Do not treat a partial restore as an all-or-nothing transaction. Some copies may have completed. Retry deliberately after resolving the underlying storage, connection or content issue.

### Where should I report a bug or suspected vulnerability?

For an ordinary bug, provide package/version, operation, connection mode, relevant settings, exact message and safe reproduction steps. Save activity text and redact personal paths/device identifiers from public reports.

For a potential vulnerability, follow the private reporting process in [SECURITY.md](SECURITY.md). The [troubleshooting chapter](MANUAL.md#troubleshooting) has more diagnostic guidance.

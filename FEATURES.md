# Gear Backup features

Gear Backup is a Windows application for backing up music gear, camera files, memory cards and ordinary folders. It combines full and incremental content-store backups, complete restore points, standalone ZIP archives, integrity checks and device-aware recovery workflows.

This inventory describes capabilities implemented in Gear Backup 0.9.5, including the October 2026 security changes. Device-specific placeholder tabs and other unimplemented capabilities are identified separately below.

For setup and step-by-step procedures, see the [user manual](MANUAL.md). For common questions, see the [FAQ](FAQ.md). For a first backup, see the [quick start guide](QuickStartGuide.md).

## Contents

- [Available operations](#available-operations)
- [Supported sources](#supported-sources)
- [File backup](#file-backup)
- [History and storage](#history-and-storage)
- [ZIP archives and export](#zip-archives-and-export)
- [Restore and recovery](#restore-and-recovery)
- [Integrity checks](#integrity-checks)
- [MTP transfers and mirrors](#mtp-transfers-and-mirrors)
- [X100VI settings and recipes](#x100vi-settings-and-recipes)
- [M8 Song viewer](#m8-song-viewer)
- [Profiles and backup management](#profiles-and-backup-management)
- [Graphical interface](#graphical-interface)
- [Command-line capabilities](#command-line-capabilities)
- [Safety and resource controls](#safety-and-resource-controls)
- [Defaults and everyday controls](#defaults-and-everyday-controls)
- [Distribution](#distribution)
- [Current limitations](#current-limitations)

## Available operations

The graphical application and CLI share backup engines and settings, with different selection and management interfaces.

| Capability | Graphical application | Command line |
| --- | --- | --- |
| Full content-store backup | New independent set, optional display name | Normal backup workflow with menu or Mode setting |
| Incremental backup | Choose an existing set and optionally name its new point | Update the latest set in the resolved history |
| Standalone full ZIP | Zip archive with save-location picker | `--zip-archive`, optional output/name arguments |
| Additional ZIP after full backup | Additional-ZIP option | `--zip` |
| Restore a complete saved point | Set/point selectors and target picker | History/set/point and scope prompts |
| Restore selected content | Checked file/folder browser | Search-and-pick and device-specific scopes |
| Merge or exact point restore | Full-point option to remove target extras | Merge/Exact choice for applicable saved-point restores |
| Restore a standalone ZIP | Archive picker and detected device-type validation | Not implemented |
| Check saved content | Check backups operation and management actions | `--check` |
| Rebuild a catalog explicitly | Recovery/fallback behavior in normal workflows | `--rebuild-catalog` |
| Preview backup/restore work | Preview action and review dialogs | `--preview` and normal review settings |
| Capture X100VI recipes/settings | Camera-settings operation for the detected camera | `--camera-settings` |
| View recipes and export readable text | Recipe viewer | Not implemented |
| Inspect M8 song structure | Song tab | Not implemented |
| Dashboard, storage accounting and selected cleanup | Dedicated management windows | No equivalent dashboard/cleanup command |
| Edit global and device/history preferences | Settings and Device & backup settings | Shared INI configuration |
| Cancellation | Cancel action and close-to-cancel behavior | Ctrl+C |
| Result reporting | Activity, status, counts and output-folder shortcuts | Console output, summaries, exit codes and applicable Windows notifications |

The CLI remains interactive where source selection, mode, review, replacement, resume or retention decisions are required. `--no-pause` suppresses the final exit wait rather than making every operation unattended.

## Supported sources

### Device profiles

| Source | Connection | Implemented device-specific behavior |
| --- | --- | --- |
| **Dirtywave M8** | USB disk mode or SD card reader | `.m8s` song identification, song-change summaries, bundle restore, supported song-with-samples restore and filename compatibility warnings |
| **Polyend Tracker Mini** | USB drive mode or SD card reader | Recognition through the `trackerConfig` card marker, `.mt` project identification, project summaries and whole-project-folder restore |
| **OP-1 Field** | MTP | Local mirroring of exposed files, including device folders for synths, drums, tapes and mixdowns |
| **OP-XY** | MTP | Local mirroring, `.xy` project summaries, and exclusion of automatic project-backup folders from the project summary while still backing up their exposed files |
| **Fujifilm X100VI media** | MTP file transfer or card reader | Photo/video file backup, connection-mode information, camera capture timestamps where reported, and camera-specific restore guidance |
| **Fujifilm X100VI settings** | USB RAW CONV./BACKUP RESTORE mode | C1–C7 recipe and current shooting-setting capture as XML, stored separately from media histories |
| **Local folders and memory cards** | Accessible filesystem paths | Manual source selection, explicit device interpretation and separate source histories |

### Source selection and identity

- Automatic discovery of supported connected USB-drive and MTP sources.
- Drive-label fallback detection for supported M8 and Tracker Mini cards.
- Manual folder/drive selection when discovery is unavailable or another source is required.
- Explicit Device preference for manual sources that cannot be identified automatically.
- Whole-drive history identity normally based on volume serial number, helping a card retain its history after a drive-letter/reader change.
- Folder identity derived from its full path; MTP identity derived from its device mirror naming.
- Optional History name/Profile to replace automatic grouping with an explicit name.

An unrecognized manual source defaults to the M8 device profile. A manual path takes precedence over automatic source selection. Detection selects an available supported device; users should confirm the displayed source when several are connected.

### Scope of device support

Device backup covers files exposed by the selected filesystem or protocol. OP-XY factory content unavailable through MTP is outside that scope. Normal file backup does not upload files to a device.

MTP-backed content is restored to an ordinary PC folder and transferred back manually with Explorer. X100VI settings capture is distinct from both photo backup and camera-settings write-back.

See [Connecting a device](MANUAL.md#connecting-a-device) for operating modes and connection procedures.

## File backup

### Independent full backups

Full backup creates a new dated content-store set and records its complete captured membership. The GUI can assign a display name without changing the history or physical set-folder identity.

Separate full sets are independent. They do not share their content objects across set boundaries, even when their source data is identical. This provides independent sets at the cost of additional space.

### Incremental capture

- Adds a complete point within an existing set.
- Stores newly required content while reusing identical content already in that set.
- Records changed file membership, including additions, renamed paths and removals from the latest state.
- Retains earlier points and their referenced content.
- Creates a point even when no new file content is needed.
- Offers a GUI selector for the full set to update, with optional incremental-point naming.
- Uses the latest set in the resolved history for CLI incremental work.

Restore points carry capture times for chronological selection. Date/time names and collision suffixes prevent a newly saved point from overwriting an existing one.

### Comparison behavior

| Comparison | Behavior |
| --- | --- |
| Default | Compare sizes and usable modified times, with a two-second timestamp tolerance. |
| Missing/unusable timestamps | Compare bytes rather than trusting a missing or pre-1980 date. |
| Thorough content comparison | Use SHA-256 for applicable change comparisons, including when size/time alone might match. |

Thorough comparison is separate from optional verification of copies. It can identify same-size, same-timestamp edits and avoid treating a timestamp-only difference as different content. Extra reading/hashing can increase operation time.

### Resume and publication

An unfinished latest file-backup set can be offered for resume. Completed work can be reused according to the comparison/checking behavior, and a complete new point is published only after capture succeeds.

Backup status distinguishes complete, incomplete and failed work. Interrupted capture does not deliberately publish a partial point as complete. Unreferenced objects can remain after interruption; the existence of a dated set folder alone does not establish successful completion.

### Folder handling and exclusions

The source inventory preserves empty directories while excluding common operating-system leftovers: dot-prefixed items, `lost+found`, `FSCK0000.REC`-style fragments, editor files ending in `~`, `Thumbs.db`, `ehthumbs.db`, `desktop.ini`, Windows system-volume folders and the Recycle Bin.

Device-specific summaries report recognized songs, projects or camera files. Compatibility warnings identify applicable device filename issues without automatically renaming the data.

### Planning and performance

- Change previews describe planned actions before the selected backup operation is committed.
- Space estimation accounts for planned content and applicable extra ZIP output.
- Path-length checks identify planned writes that would exceed classic Windows limits.
- Small content captures, up to 4 MiB each, can use four parallel workers; larger files are copied sequentially.
- Catalog updates reuse existing inventory where valid rather than rereading all previous points after every ordinary run.
- Aggregate progress limits per-file display overhead during full captures.

These optimizations do not change point membership or make separate full sets share objects. Throughput depends on storage, device, file sizes, verification and comparison settings.

## History and storage

### Content-store format

Every current-format backup set uses a content store. File bytes are addressed by SHA-256, while point records retain original relative paths, metadata and membership.

The layout under a configured backup location is:

```text
<BackupFolder>\Gear Backups\profiles\<history>\<dated set>\
  .gearbackup\
    objects\
    snapshots\
    catalog.sqlite
    current-snapshot.txt
    device.txt
    storage-mode.txt
    status.txt
```

Additional records support names, completion/check information and catalog freshness. Dated folders/points can include suffixes where needed to keep names unique.

### Point-in-time membership

Each successful run records a complete inventory. A file removed from the source disappears from the new point while remaining recoverable through older points that reference its content. A rename changes the inventory path without necessarily requiring a new content object.

All points remain until the whole set is removed. The app does not offer individual point pruning. A normal set is not an Explorer-browsable copy of the original tree: restore or export reconstructs ordinary files under their recorded names.

### Rebuildable catalog

SQLite supports efficient current inventory, point membership, file browsing and recorded integrity results. Catalogs are built/updated locally and published as closed files rather than edited in place on a backup share.

Missing, stale or unreadable indexes can fall back to authoritative backup records where supported and can be rebuilt. Explicit CLI catalog rebuild operates on one set. An index rebuild cannot recreate missing objects or convert an unsupported storage format.

### Portability of complete sets

Complete histories/sets can be copied to other storage. Discovery uses the configured `Gear Backups\profiles` layout. Hidden metadata and content objects must move with the set; a catalog or manifest by itself is insufficient.

Changing backup locations and device/history overrides makes the new storage discoverable. Post-move checks and test restores provide evidence that a copied set is usable.

## ZIP archives and export

### Standalone full ZIP

Both interfaces create standalone archives without first creating a normal history/set. The GUI offers a save picker; the CLI uses `--zip-archive` with optional `--output` and `--name`.

Features include:

- Original relative file paths and empty directories, subject to ordinary source exclusions.
- SHA-256 manifest generation while archiving, even with automatic Verify disabled.
- `gearbackup-info.txt` identifying the device profile, source/connection, capture time, version, counts, sizes and identifiers where available.
- Checksum coverage for the information file itself.
- Exact logical extracted-size metadata including generated files, while excluding filesystem allocation overhead.
- Optional post-write archive verification.
- Confirmation before replacing an existing output.
- Staging/publication so incomplete output is not presented as the completed archive.

The two metadata filenames are reserved at the source root. Output must be outside the source. CLI output may be an explicit `.zip` file or an existing directory receiving a suggested filename. Name supplies a suggestion, not an override for an explicit file path.

### Additional ZIP export

An additional option / `--zip` exports the current point after an applicable full content-store backup. It writes a ZIP beside the set, with ordinary files, empty directories and a checksum manifest. Internal backup metadata is omitted.

This does not run after incrementals and does not force the CLI into Full mode. It also does not add the standalone information file required by the app's ZIP-restore workflow. The archive can be extracted with ordinary ZIP tools, or the original set can be restored through the app.

### ZIP interoperability

Standalone ZIPs can be opened by other ZIP tools. The GUI's **Restore ZIP archive** supports the app's validated standalone format rather than accepting every ordinary ZIP. ZIP-content verification is mandatory in that workflow.

See [ZIP workflows](MANUAL.md#restore) and the [CLI reference](MANUAL.md#cli-command-reference) for the distinctions between creation, export and restore.

## Restore and recovery

### Content-store restore

GUI restore provides full-set and individual-file modes, saved-point selectors and a separate destination field. Without a detected device, it lists recognized backups for all device types across configured locations. Detection filters by device type rather than physical-unit serial number.

The latest applicable set/point is selected initially. Selecting another point reconstructs that point's full membership directly; there is no sequential application of incremental archives. The selected backup's recorded device/history determines device-specific choices and preferences.

An original device is not required to restore a content-store set to an ordinary PC folder. This supports recovery from unavailable devices and inspection before copying files back.

### Individual selection

The GUI's checked browser supports folder/subtree and individual-file selection, including empty folders. Folder selections include unopened descendants. Lazy population reduces startup work for large inventories. Selecting another set/point clears obsolete file selections.

The CLI has search-and-pick plus applicable device-specific scopes:

- All files.
- M8 bundle and its contents.
- Tracker whole-project folder.
- Single file.
- Supported M8 song with referenced samples.

Firmware-6 M8 dependency extraction includes references from unused sampler slots and refuses a complete dependency restore when referenced samples are unavailable. Other layouts can use bundle/full restore or explicit selection.

### Merge and exact behavior

| Behavior | Implemented effect |
| --- | --- |
| Merge | Recover selected saved content while retaining unrelated target files. |
| Replace | Permit differing target files to be replaced by saved versions. |
| Keep | Preserve differing existing target versions and copy missing files; exact removals are disabled. |
| Exact full point | Remove scanned files/empty directories absent from complete saved membership after successful copying and confirmation. |

Newer target versions are highlighted. Target state is rechecked to detect changes since planning. Individual-file restore does not remove unrelated target contents. Exact cleanup follows normal scan exclusions and is not disk formatting or a sector clone.

### Staged writes and recovery copies

Replacements are staged beside targets and use Windows replacement/rollback handling. Copy failures prevent exact cleanup from proceeding as if copying succeeded. Completed files remain when later work fails or is cancelled.

Optional Recovery saves existing target files that will be replaced/removed to a configurable PC location before mutation. A recovery failure stops the restore. The default is `<BackupFolder>\Gear Recovery`, and recovery storage must be outside the target.

### Destination isolation

CLI and GUI content-store restores share guards against equal/overlapping backup and destination paths. Configured repositories and recovery storage are protected too. Relevant checks repeat after approval, alongside fresh link/path checks before writes.

Use a separate destination outside the backup repository. MTP restores use a PC folder outside the working mirror, followed by manual transfer. Camera-media restore recommends a PC folder and warns about putting old files on a card.

### Standalone ZIP restore

The GUI archive picker reads/validates device metadata and requires a matching detected device type, including for a PC-folder target. USB drive/card targets can use the detected filesystem destination; MTP requires a separate PC folder.

Before modifying the target, the workflow validates names, structure, metadata, counts, lengths and checksum coverage, checks destination/temporary space, extracts into staging, and verifies content. Device validation and applicable space checks repeat before execution.

ZIP restore merges original content and empty folders, excludes generated archive metadata and retains unrelated target files. Ordinary comparison/replacement/recovery preferences still apply. Temporary workspaces are cleaned where possible after completion, cancellation or failure.

Preview can require temporary extraction space but does not commit target changes. This workflow is GUI-only; the CLI does not implement a ZIP-restore switch.

## Integrity checks

### Recorded SHA-256 and explicit checking

Captured objects have recorded SHA-256 values. **Check backups** reads saved content and compares it with restore-point hashes, checking shared objects once rather than once per reference.

Users can choose one set or all sets within a history. Checks do not require a connected source device. Results include good, damaged, missing and unchecked states where applicable, and recorded outcomes support later management views.

Checking reports damage rather than repairing content. Unsupported older sets can be reported as unchecked; a successful process return does not prove that skipped content was verified.

### Independent controls

| Control | Purpose | Default |
| --- | --- | --- |
| Thorough / Compare file contents | Content-based change comparison during supported operations | Off |
| Verify / Verify copied files | Applicable automatic integrity/read-back checking | Off |
| Explicit Check | Later verification of saved content against point records | User-invoked operation |

Hashes are captured even with Verify off. Recording hashes alone is not proof of a read-back check. Default size/time comparison can miss edits retaining both values, while Thorough performs additional content reads.

### Failure reporting

Known failure categories distinguish damaged content, a busy history, an unsupported old layout and unsafe paths. CLI errors include targeted advice where available. Operation summaries identify reported failures rather than relying only on the presence of a set folder.

Checksums establish consistency with recorded hashes, not authorship: content and manifests can be replaced together by someone controlling the backup. The app does not encrypt backup data.

## MTP transfers and mirrors

### Read-only device file access

Portable device APIs provide listing and file download. There is no device file creation, upload, rename or deletion workflow. Windows Explorer can transfer files back independently after a PC-folder restore.

The mirror is persistent for efficient later captures. New/changed device files are downloaded; unchanged eligible files can be reused. Files no longer exposed by the device can be removed from the mirror without changing historical points in the saved set.

### Transfer safeguards

- Free-space preflight uses validated listed sizes before download work.
- Running byte budgets refuse an over-declared chunk before writing.
- Final byte counts must match, including advertised empty files.
- Invalid read counts, incomplete downloads and cancellation are reported.
- Downloads use unique temporary files and clean incomplete staging where possible.
- Path/link checks protect mirror writes and stale-file cleanup.
- Enumeration limits bound item counts, folder listing/depth and implausible paths.
- Mirror operations coordinate locking with application cleanup.

Preparing an MTP source can create/update this working mirror even for a later preview. Preview is therefore scoped to the selected backup/restore plan, not a guarantee that preparation performs no local writes.

## X100VI settings and recipes

### Settings capture

The camera-settings operation reads C1–C7 recipe names and supported raw properties, plus current shooting settings, in USB RAW CONV./BACKUP RESTORE mode. XML exports retain values and available model, serial and firmware information; unavailable properties are identified explicitly.

Each capture creates a full settings backup in a separate history. Automatic naming distinguishes it from media; explicit profiles use a corresponding settings history. Device/profile storage, verification preferences and applicable full-backup ZIP export can be used.

The camera reader temporarily selects recipe banks, restores/verifies the read selector, checks shooting values for unintended changes and prevents concurrent Gear Backup captures of the same camera.

This is not a full camera-configuration image or Fujifilm X Acquire backup. It does not provide incremental/resume settings capture or write saved settings back to the camera.

### Offline recipe viewer

**View recipes** is available for supported settings backups in Dashboard/Storage. The viewer can also open an appropriate folder or individual XML export without a camera attached.

Readable film simulations, white balance, grain, tones and other mapped values are displayed alongside protocol/raw information. Unknown values remain explicit protocol values. Selected recipes can be copied/exported as readable text, which is a reference rather than a camera import format.

### Camera media

File backup captures exposed photo/video data separately from settings. Camera-aware summaries recognize JPG, RAF, HIF and MOV content; matching image/raw shot names can be grouped in summaries. Camera-reported dates are retained where provided.

Restoring XML exports to a PC or restoring photos does not apply camera settings.

## Tracker Mini project tools

The Tracker Mini view has two read-only tabs. The **project inspector** reads a project's `project.mt`, its patterns (`.mtp`) and its instrument files (`.pti`) and shows the name, tempo, firmware, song length, each instrument's sample, length and channels, and the instruments each pattern plays. The **instrument check** lists instruments that notes play but that have no usable instrument file, song positions without a pattern file, and unreadable files. Instruments hold their sample inside the `.pti`, so the check is by instrument number rather than by sample path. A **pattern viewer** opens any pattern as a table of steps (note, instrument, up to two effects) per track. When a Tracker card is detected the Device tab summarises its projects (count, newest, and any that need attention). Readers follow Polyend's tracker-lib file layouts and only the header of an instrument file is read.

## M8 Song viewer

The Song tab, available in the M8 view only, opens a Dirtywave M8 `.m8s` and presents a read-only summary through the bundled native reader.

Available information includes:

- Song name, firmware and tempo.
- Transpose, quantize and related song information supplied by the reader.
- Song length and used rows per track.
- Used chain, phrase and table counts.
- Instrument slot, type, name and sample reference.

The viewer does not play, edit, save or repair songs. Sample references do not establish that sample files are available. Restore a song to an ordinary named file if it is only present as a content-store object.

### M8 sample dependency checker (after 0.9.5)

M8 header mode has a **Sample check** tab for a chosen `.m8s` song:

- Check card-root references against a connected card or extracted folder, including unused sampler slots.
- Resolve paths beginning with `/` at the card root and relative references beside the actual song file, including demo and bundle sample folders. Tooltips show the resolved card-relative path; missing location context is reported explicitly.
- **Check all songs on card** recursively inspects `.m8s` songs, including bundles, and lists songs with missing samples, unreadable songs and invalid references. Select a song for reference details or double-click for its individual check. Whole-card scanning checks card contents only and supports progress, cancellation and incomplete-scan warnings.
- Group duplicate references and show all referencing instrument slots.
- Search M8 backup histories and older saved restore points in the selected and configured backup locations.
- Distinguish exact-path copies from filename-only candidates and show set folder, restore point and original path.
- Verify matching saved objects against their recorded size and SHA-256 hash; mark unavailable/damaged objects explicitly.
- Double-click a verified backup copy and confirm to open individual restore with its saved point and sample selected; normal destination review and Restore remain required.
- Support card-only and backup-only checks, background execution, cancellation, bounded match lists and warnings for incomplete searches.
- Preserve card results when backup search is disabled; re-enabling it refreshes history for the selected song, including selections in the whole-card list.

The checker does not alter songs or recover samples automatically. Card presence does not validate audio contents; matching paths/names do not identify the intended historical sample version. Recover selected copies through the existing restore interface.

Native input is bounded, parser panics are converted to error results where caught, and displayed file-derived text is sanitized for control/bidirectional formatting characters. Missing/incompatible libraries and unreadable inputs produce errors. A 64-bit application process is required.

### M8 card migration assistant (after 0.9.5)

The **M8 → Card migration** tab restores a complete saved point from a completed M8 content-store backup to an existing empty replacement card or test folder.

- Backup-set and restore-point selection, including older points and configured backup locations.
- Optional replacement-card link to the original history: future backups retain its location and preferences, using the new card's volume ID across drive-letter changes. Saved only after successful migration checks; explicit profiles take priority.
- Mandatory copy-plan review, checksum validation and content comparison without changing saved preferences.
- Empty-destination checks and directory/volume identity revalidation; existing files are never removed to make room.
- Post-restore size/SHA-256 comparison for every expected file, plus expected-folder checks.
- A song dependency scan using both card-root and song-relative paths.
- Per-song results, detailed reports and text-report export; missing dependencies or incomplete checks produce a needs-attention result.
- Shared operation progress and cancellation. Completed files can remain after cancellation; ordinary Restore can continue into a partial destination.

This is file migration, without partitioning/formatting, firmware flashing, song editing, hardware compatibility testing or automatic ejection. Prepare the replacement card separately. A PC folder can be used to rehearse recovery before using another card.

## Profiles and backup management

### History identity and naming

Automatic histories distinguish sources through drive volume identity, folder path or MTP device identity. Explicit History name/Profile provides controlled grouping. Readable names and user nicknames make histories easier to distinguish without changing physical folder identities.

Full-set labels and incremental-point labels are separate from history grouping. Location labels help distinguish histories sharing names across configured storage roots.

### Shared preference hierarchy

Installed copies use AppData; portable/local builds use settings beside the executables. The two apps share global `GearBackup.ini` and device/history `GearBackup.Profiles.ini` preferences.

Supported overrides resolve from global values to device defaults to history preferences. The GUI editor groups relevant controls, shows inherited defaults and offers explicit save/reset actions. Available overrides include storage, comparison, verification, review, retention, recovery, filename-warning length and ejection.

Global identity controls and Theme are not arbitrary per-history override keys. CLI Source/Profile overrides apply to the invocation rather than saving a new global identity setting.

### Dashboard

Dashboard uses metadata-oriented scanning for backup completion/health, last-success and recorded integrity information. It supports device filtering, readable history names/nicknames and actions to open storage, check backups and view supported recipes.

Recorded results reflect the most recent applicable operation, not continuous disk monitoring. Detailed storage accounting belongs to Storage rather than making every Dashboard refresh read all content bytes.

### Storage accounting and selected cleanup

Storage reports categories for saved objects, metadata, ZIPs and MTP mirrors. Logical/unique content totals describe file bytes rather than guaranteed allocated-disk consumption. Device filtering applies to both usage and cleanup.

Cleanup starts without selected removals. Older full sets, archive copies and mirror caches can be selected, previewed and recycled. The latest set in a history and individual internal objects are protected from ordinary selected cleanup. Fresh state/path checks reject changed items and operations respect relevant locks.

### Automatic retention

KeepBackups limits full sets, with zero meaning unlimited. Extra old sets can be offered after successful backup. Confirmation remains required.

Retention revalidates folder identity and metadata after approval and immediately before recycling, and rechecks latest-set protection. Removing a set removes its points together. Recycle Bin contents may continue occupying space until emptied; removing a mirror requires a later download.

## Graphical interface

### Main workflow

The complete Gear Backup mode provides Device, Backup, Restore and Song workflows with shared progress/activity. Backup offers full, incremental and standalone ZIP choices; Restore offers full, individual, standalone ZIP and integrity-check choices.

Device-specific header modes select a device type and reserve dedicated placeholder tabs. They are navigational modes, not a claim that unimplemented device tools are already available. The Gear Backup icon returns to the complete workflow.

### Selection and review tools

- Device/source detection with connection details and available storage information.
- Distinct backup-source and restore-destination fields.
- More options for secondary workflow inputs.
- Set and point selectors with readable labels and completion information.
- Themed folder browsing, path entry and applicable folder creation.
- Checked restore browser with lazy descendants and selection summaries.
- Preview tables with search/action filters that do not change the underlying execution plan.
- Responsive activity, operation stages, counts and elapsed time where available.
- Cancel and output-folder shortcuts.

MTP connection details do not substitute the PC mirror's disk statistics for the device's own capacity. Clearing/changing detection updates the available operation context rather than retaining an unrelated USB drive as an MTP source.

### Appearance and usability

Four shared appearances are available: Original, Original (dark), Windows and Retro. Original is the default. Windows follows supported system light/dark, accent and high-contrast settings, including changes while windows are open.

The GUI includes tooltips, accessible control names, keyboard navigation, DPI-aware layouts and remembered main-window placement. Help/F1 opens the matching offline manual; screenshots remain Original across manual editions.

## Command-line capabilities

### Implemented switches

| Switch | Capability |
| --- | --- |
| `--restore` | Enter content-store restore rather than normal backup selection. |
| `--check` | Select and verify saved backups without requiring the source device. |
| `--camera-settings` | Capture X100VI recipes/current shooting settings. |
| `--zip-archive` | Create a standalone full archive. |
| `--rebuild-catalog PATH` | Rebuild one set's SQLite catalog from existing records. |
| `--source PATH` | Override filesystem source, or target in a restore workflow. |
| `--profile NAME` | Use explicit history grouping and preference context. |
| `--zip` | Request additional ZIP export after an applicable full backup. |
| `--output PATH` | Choose standalone archive file or existing output directory. |
| `--name NAME` | Supply the standalone archive naming suggestion. |
| `--preview` | Prepare/display the selected work without committing its plan. |
| `--no-pause` | Remove the final exit-key wait. |

The parser accepts these 12 switches with their documented argument/combination rules. There are no short aliases, positional source arguments or `--key=value` forms. Full/incremental mode, verification, device choice and backup storage use settings/menu behavior rather than invented flags.

### Interactive operation and scripting visibility

Normal CLI backup resolves settings/source/history, then uses Mode or prompts to choose work. Restore/check provide history/set selection; restore adds point/scope/replacement decisions. Source paths are quoted normally, and an installed CLI can be added to the current user's PATH.

Console output reports stages, results and targeted errors. Exit codes distinguish ordinary return (`0`), workflow errors (`1`) and reported failed work/content problems (`2`). Zero can also mean preview, cancellation or declined work, so it is not proof that a new backup was written.

Output can be logged by the shell, but redirection does not answer hidden prompts. There is no universal unattended confirmation-bypass flag. Ctrl+C requests cancellation; No-pause affects only the final wait.

### Preview scope

Normal review can continue after approval; explicit Preview does not commit the selected plan. Catalog-rebuild preview prints the intended action. Check with Preview exits without performing verification.

Device preparation may still download/update a mirror, and camera-settings preview can communicate with the camera and prepare temporary files. GUI ZIP restore preview uses extraction space. These preparation effects are distinct from committing backup/restore target changes.

The [manual CLI reference](MANUAL.md#command-line-and-exit-codes) documents syntax, every switch, invalid combinations and examples.

## Safety and resource controls

### Filesystem writes

- Canonical relative-path validation and containment checks.
- Rejection of traversal, alternate-stream forms, unsafe Windows reserved names, junctions and symbolic links in protected operations.
- Fresh path checks after review and before applicable file commits/removals.
- Unique CreateNew staging files instead of truncating predictable temporary names.
- Staged replacement/rollback handling rather than deliberate delete-before-copy replacement in restore.
- Source/target state checks and operation cancellation checks at meaningful boundaries.
- History/mirror coordination for concurrent Gear Backup operations.

These controls are defensive checks, not a reason to select an untrusted destination or discard independent backups.

### Bounded input processing

| Input | Implemented bounds/checks |
| --- | --- |
| Metadata records | One opened read handle, size checks and running byte limits; bounded stream decoding/line reading. |
| ZIP directory | Preflight validates actual central-directory structure/counts before framework entry allocation; independent 32 MiB directory budget. |
| ZIP entries | At most 1,000,000 entries, names up to 1,024 bytes, per-entry extra data/comments up to 4,096 bytes. |
| ZIP restore content | Validated lengths/counts/coverage, checked arithmetic, staging and mandatory hash verification. |
| MTP | Listing/path/depth bounds and exact streamed byte budgets, including empty files. |
| M8 songs | Bounded native file reads; inputs above 16 MiB refused. |
| Recipe XML | Document limits, prohibited DTDs and disabled external entity resolution. |
| SQLite | Local read copies, bound parameter values, defensive/untrusted-schema configuration and execution/resource checks. |

ZIP/ZIP64 support is for validated single-disk structures. Unusual legitimate input may be refused by safety budgets. Inputs below a limit are not automatically valid or supported.

### Native-library loading

Windows system imports are restricted to System32. The M8 reader is loaded by its full application-directory path with System32-only dependency resolution. Its environment-path override is absent.

Native builds check PE protection flags and import dependencies. The reader is built with source-path remapping and Control Flow Guard alongside ASLR/DEP checks. CI tests the import policy against unexpected, delay-loaded and malformed dependency data. These are component safeguards, not a claim of application signing or a complete security guarantee.

### Privacy and trust boundaries

Backups, archives, settings exports, mirrors and recovery copies are not encrypted by the application. Metadata can include filenames, device identifiers and source information. Users control storage permissions, encryption and independent copies.

Hash verification detects inconsistency with records rather than proving authorship. Packages are not currently Authenticode-signed; release/reference checksum trust remains separate from backup-content verification.

## Defaults and everyday controls

| Behavior | Default |
| --- | --- |
| Backup location | `%USERPROFILE%\Backup` |
| New set storage | Content store |
| Restore points | Every successful run, including unchanged incrementals |
| Change review | Enabled |
| SHA-256 content comparison | Disabled |
| Automatic copy verification | Disabled |
| Recovery copies | Disabled |
| Retention limit | Unlimited (`KeepBackups=0`) |
| Ejection | Disabled |
| CLI normal backup Mode | Ask |
| Explicit source/history | Empty; automatic selection/grouping where available |
| Device interpretation | Auto, with M8 fallback for unrecognized sources |
| Appearance | Original |

Starting an ordinary backup asks before creating a missing configured destination. A refusal cancels rather than creating that destination. Preview avoids committing the backup plan, though device/source preparation can have its own working-copy effects.

Supported detected USB-drive devices can be offered for ejection or ejected according to settings after checking that the detected device still owns the selected source. Manual folders and MTP connections do not provide an equivalent general drive-ejection action.

The [INI reference](MANUAL.md#all-ini-settings) lists all 14 global settings and which support device/history overrides.

## Distribution

### Runtime packages

Per-user Setup and portable extraction packages include both applications, matching shared/native libraries, the offline manual and its image assets. Setup adds Start menu/uninstall integration and optional desktop/PATH choices. Portable extraction does not add shortcuts, uninstall registration or PATH changes.

Updates preserve personal INI settings. Installed copies read AppData configuration; portable/local copies read beside the programs. Neither implies that backups or MTP mirrors live entirely within the application folder.

### CLI integration

Setup's Add CLI to PATH option starts unchecked. It operates on the current user's environment and takes effect in new terminals. Installer-owned entries are managed on upgrade/deselection/uninstall while pre-existing entries are preserved by the integration logic.

Without PATH, invoke the executable using its full path or from its folder. PowerShell requires `.\` for a local executable or the call operator for a quoted full path.

### Documentation and development checks

The repository includes the manual, FAQ, quick start, changelog and security reporting policy. HTML Help is generated from the manual in all four themes, with illustrations kept beside it.

Build/test workflows cover core behavior, GUI workflows, native parsing and dependency policy. CI also configures Rust linting, seeded corruption testing and dependency auditing. Test coverage and configured checks are evidence mechanisms, not guarantees that every device or hostile input has been exercised.

See [Building and testing](MANUAL.md#building-and-testing) for developer prerequisites and commands.

## Current limitations

The following are not implemented or deliberately outside the current feature scope:

| Area | Limitation |
| --- | --- |
| Platform | Windows application, not a cross-platform desktop package. |
| Disk imaging | File-tree backup, not a sector image or forensic clone. |
| Source scope | Only exposed/accessible content; normal scan exclusions apply. |
| Old layouts | No current support or automatic conversion for former plain-folder/hard-linked sets. |
| Deduplication | Within a full set, not between independent full sets. |
| Point cleanup | No individual restore-point pruning control. |
| MTP write-back | Restore to PC plus manual transfer; no device upload workflow. |
| ZIP restore | GUI-only and requires a valid standalone archive plus detected matching device type. |
| Generic ZIP acceptance | Additional full-set exports and arbitrary ZIPs do not automatically satisfy standalone restore metadata requirements. |
| Camera settings | XML recipe/shooting snapshots, not full-camera/X Acquire backup or settings upload. |
| M8 Song | Inspection only, without playback/edit/save/repair. |
| Sample dependency extraction | Supported firmware-6 M8 layout, not every possible song format. |
| Header device modes | M8 has Sample check and Card migration in builds after 0.9.5; other dedicated tools remain Coming soon/Planned placeholders. |
| Unattended operation | No universal Yes/Force/Silent confirmation-bypass switch. |
| Cancellation | Completed restore copies can remain; the whole operation is not rolled back. |
| Encryption/authentication | No built-in backup encryption or cryptographic proof of archive authorship. |
| Release signing | Packages are not currently Authenticode-signed. |
| Input size/path freedom | Resource budgets, format validation and classic Windows path checks can refuse input. |

The [manual](MANUAL.md) explains supported alternatives and operating procedures; the [FAQ](FAQ.md) addresses common expectations and misunderstandings.

# Architecture

How Gear Backup is put together, and where new code goes. For how to use the program, see the [manual](../MANUAL.md).

## The shape of it

Gear Backup is a Windows program for .NET Framework 4.x, written in C# 7.3, built with the C# compiler alone (no project files, no NuGet packages). It backs up and restores music gear and cameras: a USB drive or an MTP device is read, its files are stored in a backup folder, and the backup can be checked, browsed and restored later. Nothing is ever written to a device while backing it up.

There are two programs built on one core library, `GearBackup.Core.dll` (everything under `src` except `src\Cli`, namespace `GearBackup`):

- `GearBackup.exe`, the command line (`src\Cli`, namespace `GearBackup.Cli`).
- `GearBackup.GUI.exe`, the Windows Forms window (`gui\`, namespace `GearBackup.Gui`).

The core's types are `internal`; `InternalsVisibleTo` in `src\Foundation\AssemblyInfo.cs` lets the two programs and the test programs use them. `AssemblyInfo.cs` is compiled into every program, so each carries the same version and the System32-only DLL search rule. The core cannot refer to the CLI or the GUI, which the compiler now enforces; `build\check-layers.ps1` still checks the layers inside the core.

A small Rust library, `m8_reader.dll` (`native\m8-reader`), reads Dirtywave M8 songs for the Song tab.

```
  front ends        Cli (console)              gui (Windows Forms)
                         \                      /
  shared steps              Workflow (Session, SourceSelection)
                                    |
  engines            Backup     Restore     Zip
                                    |
  what a backup is              Catalog
                                    |
  what things are        Settings     Devices
                                    |
  basics                        Foundation
```

A layer may use its own layer and the ones below it, never the ones above. `build\check-layers.ps1` checks this (see [Layers](#layers)).

## Folders under `src`

| Folder | What lives there |
|---|---|
| `Foundation` | Everything else builds on this: path and link safety (`PathSafety`), reading and writing the small files kept beside backups (`Records`, `AtomicFile`), file copying and hashing with verification (`FileCopy`), free space (`DiskSpace`), sizes and times (`Formatting`), the named vocabulary (`Names`: stages, statuses, restore modes), the operation context and the interfaces front ends implement (`Operations`), the question helpers (`ConsoleUi`), plan preview (`PlanReview`), source scanning (`SourceScan`), the history lock (`HistoryLock`), the device profiles (`DeviceProfiles`: one `DeviceProfile` per device) and the app's names and folder layout (`AppIdentity`). |
| `Settings` | `GearBackup.ini` and per-device and per-source overrides (`Config`, `ProfileSettings`, `SettingsLocation`), and how a source is named in the backup folders (`ProfileNaming`). |
| `Devices` | Which device an operation works with (`DeviceSelection`), what a source is (`SourceChoice`), song naming rules (`Changes`), USB detection (`UsbDevices`), the M8 song reader seam (`M8SongReader`, `M8SongJson`, `M8SongModel`), and the song-to-sample lookup (`Dependencies`). `Devices\Mtp` talks to MTP devices and mirrors them to a PC folder. `Devices\Fuji` handles the X100VI's PTP protocol, settings and film recipes. |
| `Catalog` | What a backup contains: restore points (`Snapshots`, `RestorePointNames`), reading a content store (`ContentStore`), the backup folders of a history (`BackupFolders`, `Histories`, `HistoryNames`), set names and status records (`BackupSetNames`, `BackupStatuses`), the SQLite catalog (`BackupDatabase`, `SqliteDatabase`, `BackupCatalog`) and integrity checks (`IntegrityEngine`). |
| `Backup` | Making backups: `BackupEngine` and `ContentStoreBackup`, which writes a content store. |
| `Restore` | Restoring: `RestoreEngine`, the restore flow and its catalog, and the sample files a song needs (`SongRestoreDependencies`). |
| `Zip` | Standalone ZIP archives: writing (`ArchiveBackup`, `Zip`), reading and verifying (`ZipRestore`). |
| `Workflow` | Steps the CLI and the GUI share: detecting and preparing a source, running a backup or restore, ejecting (`Session`), and the CLI's source choice (`SourceSelection`). |
| `Cli` | The command-line front end: `Program` (arguments and the main flow), `ConsoleInteraction`, `Notifications`. |

## The operation context

Engines never read the console or show windows. They talk to an `IOperationInteraction` (questions, output, plan approval, progress), which the CLI implements with the console, the GUI with dialogs, and the tests with a scripted fake.

The caller supplies an OperationContext to engines and shared workflows. It carries interaction, cancellation, preview mode, device and per-operation caches. Engine code calls the context's output, question and plan-review methods directly, and passes it through file comparisons, recovery, history selection and checks. Device/profile resolution accepts the device explicitly, so another active operation cannot change the selected history or preferences.

OperationContext.Current is retained as a compatibility bridge at optional-context entry points and in legacy frontend/hardware adapters. An adapter resolves it once when no context was supplied; code that already has a context passes it. Engines and hardware workflow boundaries activate their supplied context while calling adapters that still need the bridge. ConsoleUi, OperationOutput and PlanReview remain convenience adapters for frontend code. New business logic should use the explicit context instead of adding ambient lookups. The CLI, GUI worker and tests each create their own context; nested activation restores the previous one.

Rules of thumb: ask the context for interaction and cancellation; pass a `DeviceProfile` explicitly when you have one rather than reading `CurrentDevice`, which is only for code that cannot easily be given it.

Every `OperationResult` requires an `OperationOutcome`: `Success`, `Cancelled`, `Skipped`, `Preview` or `Failed`. Use `Succeeded` to authorize follow-up actions such as device eject, and `Cancelled` to identify cancellation. Titles and summaries are presentation data. Failed outcomes carry a nonzero exit code (normally 1, or 2 for verification/copy errors); other outcomes carry zero. `SetOutcome` updates the outcome and exit code together when a later verification or cancellation changes the result. A declined plan returns cancellation, while a read-only preview returns `Preview`.

`RestorePlanner` owns a single request's comparison, conflict choices, target actions and capacity checks. Its private plan holds the preview data and target fingerprints; execution starts only after all checks and approvals. `RestoreEngine` is stateless across runs, and stages each file in a separate helper. `ContentStoreBackup` keeps one run's state and separates source planning from approval. The GUI dispatcher delegates each operation to a focused method.

`RestoreCatalog.Load` coordinates SQLite inventory loading and recovery-record fallback in separate helpers. `M8SampleCheck.Run` coordinates reference collection, card path resolution, backup discovery and matching. Keep cancellation and per-backup error boundaries when extending these steps.

## Devices

Everything device-specific is data in a `DeviceProfile` (`Foundation\DeviceProfiles.cs`): how to detect it (USB vendor or model, volume label, MTP vendor and name), what its songs are, which folders to leave out, its picture, and optional readers. Adding a device means adding a profile, plus a reader if it needs one. A device that needs real code, like the X100VI, keeps that code in its own folder.

The GUI's gear icons (Gear Backup, M8, Tracker Mini, OP-XY, OP-1 Field) are modes of the window; `GearIconStrip` lists them and `MainForm.Navigation` swaps the tabs.

## How a backup is stored

A backup is a folder under `<BackupFolder>\Gear Backups\profiles\<history>\<date>`. A history is one device and source pair. Next to the backed-up content (or instead of it, for the content store) is the metadata folder `.gearbackup`:

| File or folder | Meaning |
|---|---|
| `status.txt` | `incomplete`, `complete` or `failed`; an interrupted backup can be resumed. |
| `device.txt` | The device profile id the backup was made from. |
| `storage-mode.txt` | `single` for the content store. |
| `objects\<sha256>` | Each distinct file content, stored once. |
| `snapshots\<time>.manifest` | One restore point: every file's path, hash, size and times (paths base64-encoded). A `.taken` and a `.kind` file sit beside it. |
| `current-snapshot.txt` | The newest restore point. |
| `catalog.sqlite` | A SQLite copy of the above for fast browsing, plus recorded integrity checks. It is rebuilt after every run and can always be rebuilt from the manifests. |
| `checksums.sha256` | The checksum list of the per-file layout. |
| `backup-name.txt`, `inventory-revision.txt` | An optional name; a token that marks the catalog stale. |

The per-file layout (backed-up files stored as they are, with `checksums.sha256` and optional snapshots) is still supported through `BackupEngine`; the single content store is the default.

## Safety rules the code relies on

These are enforced in `Foundation` and used everywhere; keep new code on the same paths.

- **Paths:** every path that comes from a backup, a ZIP or a device goes through `PathSafety.SafePath` or `CheckedPath`. They reject `..`, rooted paths, `:`, trailing dots and spaces, and Windows device names, and they refuse junctions and symbolic links, again right before writing.
- **Small files:** records are read through `Records` with size limits, and written through `AtomicFile` (a temporary file that then replaces the original).
- **Concurrency:** a history is locked while it is written (`HistoryLock.LockHistory`).
- **Copies:** `FileCopy` hashes while copying and, when verifying, reads the copy back from the disk, bypassing the cache.
- **ZIPs and SQLite:** ZIPs are untrusted (`ZipRestore` validates names, sizes and checksums before touching the target). SQLite uses bound parameters only, read-only opens for published catalogs, and limits.
- **Devices:** devices are only read. An MTP device is mirrored to a PC folder first.

## The M8 song reader

The Song tab calls `IM8SongReader` (`Devices\M8SongReader.cs`). The one implementation loads `m8_reader.dll`, built from `native\m8-reader` on the `m8-file-parser` crate, and gets a JSON summary back (schema in `native\m8-reader\src\summary.rs`). The DLL is loaded by full path from the application folder with its own dependencies limited to System32, and a parser panic comes back as an error result, never a crash. Replacing the reader means implementing the interface.

## The GUI

`GuiBackgroundWork` owns cancellation and request identity for restore and migration inventory, M8 sample/card checks, Tracker inspection and ZIP inspection. Call `Begin()` on the UI thread, give only the request's token to the worker, and dispose the request in `finally` after the worker stops. Check `CanPublish` inside the UI callback before displaying results or progress. `IsCurrent` also permits displaying a user-cancellation notice. `Invalidate()` cancels and rejects all callbacks from an obsolete request; disposing the owner does the same when its controls close. Completing an old request cannot clear a newer request's busy state.

`MainForm` is split over several files by concern: `MainForm.cs` (window, header, activity area), `.Navigation` (tabs and gear modes), `.Restore`, `.ZipRestore`, `.BackupSets`, `.Song` and `.WindowsTheme`. `GuiOperationRunner` owns the worker thread and talks to the engines; the form only displays. `GuiTheme`, `GuiPalette` and the `Gui*Theme` files give the four appearances. Several form fields are `internal` hooks (such as `SongFilePicker`) so tests can replace dialogs.

A tab can also live outside `MainForm` as a module. `IWorkflowTab` (`Build()` and `ContentHeight`) is the module's side, and `IWorkflowTabHost` is the small set of window services it may use (fonts, tooltips, the owner for dialogs, running on the UI thread, relayout, redraw freezing); `MainForm` implements the host in `MainForm.Song.cs`. The Song tab (`SongTab`) is the first module. Move another tab over by giving it the same shape, rather than adding more fields and partial files to `MainForm`.

## Building and testing

| Script | What it does |
|---|---|
| `build.cmd`, `build-gui.cmd` | Build `GearBackup.Core.dll` from `src` (recursively, without `src\Cli`), then the CLI (`src\Cli`) or the GUI (`gui`) against it, in `dist/app/` by default. The GUI build also places `m8_reader.dll`, all HTML manual variants and their images beside the app. An explicit output path overrides this directory, including for installer payloads. |
| `build\build-native.ps1` | Builds the Rust reader (`-Test` runs its tests), remaps build paths and checks the DLL's protections. |
| `build-installer.cmd` | Builds the installer and portable packages. |
| `tests\run-tests.ps1`, `tests\run-gui-tests.ps1` | The core and GUI test suites. `-Filter text` runs only the tests whose name contains it, `-List` shows them. `-BuildDirectory path` overrides the build directory (relative paths start at the repository root). Defaults are `tests\.build\core` and `tests\.build\gui`, so both suites can run concurrently. |
| `build\check-layers.ps1` | The layer check, run before the core tests and in CI. |
| `build\check-catches.ps1` | Fails an empty `catch` without a comment, run before the core tests and in CI. |
| `build\check-method-length.ps1` | Fails a method or constructor longer than 60 lines in `src` or `gui`, run before the core tests and in CI. |
| `tests\run-coverage.ps1` | Statement and branch coverage of the core by the core tests, per source file (needs OpenCover unzipped into `.tools\OpenCover`; CI does that and fails below 85% of statements). At the last run: 89% of statements, 80% of branches. The Windows device code is tested against fakes of the Portable Devices API (`tests\FakePortableDevice.cs`) and of the USB disk list; what stays uncovered is mostly ejecting through `cfgmgr32` and the interactive CLI source picker. |
| `benchmarks\run-benchmarks.ps1` | Performance scenarios; see `benchmarks\README.md`. |

A test is a method of the `Tests` class marked `[Test("name")]` (`tests\TestHarness.cs`); the runners find them by reflection, so there is no list to keep. A test takes no argument or one fixture folder, and each runs in its own operation context. GUI tests are marked `Gui = true`, live in `tests\Gui*Tests.cs` by area, and run in the GUI test program on an STA thread.

GUI screenshots are saved beside the test executable, including when a custom build directory is selected. Benchmarks use `tests\.build\benchmarks`; coverage uses `tests\.build\coverage`. For example, `powershell -File tests\run-gui-tests.ps1 -BuildDirectory tests/.build/gui-review -Filter "Restore inventory"` isolates an additional GUI run.

Each suite creates its own fixture tree below `.test-runs`. Failed runs preserve it; `GEAR_KEEP_TEST_FIXTURES=1` also preserves passing runs. Cleanup checks that its target stays below that directory, refuses linked ancestors, unlinks reparse points without walking their targets, and clears read-only attributes only on unshared regular files or real directories. A cleanup failure fails the run instead of silently leaving fixtures behind.

There are no namespaces: every type is in the global namespace, and `internal` means "inside this program". C# is limited to 7.3 because the programs run on .NET Framework.

The [Tracker compatibility corpus](../tests/fixtures/tracker/README.md) contains pinned, licensed original-Tracker device saves with offline hash checks. It supplements synthetic fixtures with independent project, tempo, instrument and pattern expectations, including the final played step. Derived and damaged files are created only in scratch storage. Mini/Plus and newer-format fixture gaps are documented with the corpus.

`TransferFailureTests.cs` injects MTP enumeration, later-transfer, cancellation and publication failures. It verifies that partial mirroring cannot publish a backup point, stale cleanup waits for successful transfers, staged files and locks are released, and reconnect resumes correctly. These deterministic tests supplement existing stream, disk-space and restore failure tests; physical USB disconnect and power-loss behavior still require hardware checks.

## Layers

The layer of a file is its top-level folder under `src`:

| Level | Folders |
|---|---|
| 0 | Foundation |
| 1 | Settings, Devices |
| 2 | Catalog |
| 3 | Backup, Restore, Zip |
| 4 | Workflow |
| 5 | Cli |

`build\check-layers.ps1` scans the sources and fails when a file mentions a type declared in a higher layer. There are no known violations; `build\layer-baseline.txt` is empty and exists so a violation that cannot be fixed at once can be recorded (run `build\check-layers.ps1 -UpdateBaseline`) while only new ones fail.

Failures that callers may want to tell apart have their own types in `Foundationrrors.cs` (`DamagedContentException`, `UnsafePathException`, `HistoryBusyException`, `UnsupportedBackupException`). They derive from `IOException`, so existing handlers still catch them, and `ErrorHints.For` gives the advice the CLI and GUI show after the message.

Errors are never swallowed silently: `build\check-catches.ps1` (run by the tests and CI) fails an empty `catch` that has no comment saying why ignoring the error is safe. Code that catches `Exception` rethrows `OperationCanceledException` first, so a cancel is never reported as a failure. A catch-all that quietly gives up (a best-effort lookup or cleanup) filters with `when (ExpectedFailure.Is(ex))`, so a bug in the code still surfaces; the check fails a bare `catch (Exception)` that neither filters nor rethrows. Catch-alls that remain are at a boundary where the error is shown to the user (the operation runner, a background task finishing, a dialog button).

The namespace commit re-indented every source file, so `git blame` would credit it for most lines. `.git-blame-ignore-revs` lists it (and any later mechanical rewrite); run `git config blame.ignoreRevsFile .git-blame-ignore-revs` once, and `git diff -w` hides the indentation in that commit.

## Where does new code go?

- A rule about paths, files or records: `Foundation`.
- A new setting: `Settings\Config.cs`, and the manual.
- A new device: a profile in `Foundation\DeviceProfiles.cs`; its own code in a folder under `Devices`.
- Something that describes backup contents: `Catalog`.
- Something that makes or restores a backup: `Backup`, `Restore` or `Zip`.
- A step both the CLI and the GUI run: `Workflow`.
- Anything that reads the console or shows a window: `Cli` or `gui`, never an engine.

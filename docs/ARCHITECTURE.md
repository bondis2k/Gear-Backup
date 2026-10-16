# Architecture

How Gear Backup is put together, and where new code goes. For how to use the program, see the [manual](../MANUAL.md).

## The shape of it

Gear Backup is a Windows program for .NET Framework 4.x, written in C# 7.3, built with the C# compiler alone (no project files, no NuGet packages). It backs up and restores music gear and cameras: a USB drive or an MTP device is read, its files are stored in a backup folder, and the backup can be checked, browsed and restored later. Nothing is ever written to a device while backing it up.

There are two programs built from the same core:

- `GearBackup.exe`, the command line (`src\Cli\Program.cs`).
- `GearBackup.GUI.exe`, the Windows Forms window (`gui\`).

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

The running operation is reached through `OperationContext.Current`, a per-thread ambient value, so helpers do not need it passed to every call. It carries the interaction, the cancellation token, preview mode, the current device and per-operation caches. Every operation starts its own context (`new OperationContext(...).Activate()`): the CLI's `Main`, the GUI's operation runner and tool windows, and each test. Nothing in it outlives the operation.

Rules of thumb: ask the context for interaction and cancellation; pass a `DeviceProfile` explicitly when you have one rather than reading `CurrentDevice`, which is only for code that cannot easily be given it.

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

`MainForm` is split over several files by concern: `MainForm.cs` (window, header, activity area), `.Navigation` (tabs and gear modes), `.Restore`, `.ZipRestore`, `.BackupSets`, `.Song` and `.WindowsTheme`. `GuiOperationRunner` owns the worker thread and talks to the engines; the form only displays. `GuiTheme`, `GuiPalette` and the `Gui*Theme` files give the four appearances. Several form fields are `internal` hooks (such as `SongFilePicker`) so tests can replace dialogs.

## Building and testing

| Script | What it does |
|---|---|
| `build.cmd`, `build-gui.cmd` | Compile the CLI and the GUI. Both compile every `.cs` file under `src` (recursively) plus `gui` for the GUI, so the core is built into each executable. The GUI build also builds `m8_reader.dll` and the HTML manual. |
| `build\build-native.ps1` | Builds the Rust reader (`-Test` runs its tests), remaps build paths and checks the DLL's protections. |
| `build-installer.cmd` | Builds the installer and portable packages. |
| `tests\run-tests.ps1`, `tests\run-gui-tests.ps1` | The core and GUI test suites. `-Filter text` runs only the tests whose name contains it, `-List` shows them. |
| `build\check-layers.ps1` | The layer check, run before the core tests and in CI. |
| `benchmarks\run-benchmarks.ps1` | Performance scenarios; see `benchmarks\README.md`. |

A test is a method of the `Tests` class marked `[Test("name")]` (`tests\TestHarness.cs`); the runners find them by reflection, so there is no list to keep. A test takes no argument or one fixture folder, and each runs in its own operation context. GUI tests are marked `Gui = true`, live in `tests\Gui*Tests.cs` by area, and run in the GUI test program on an STA thread.

There are no namespaces: every type is in the global namespace, and `internal` means "inside this program". C# is limited to 7.3 because the programs run on .NET Framework.

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

## Where does new code go?

- A rule about paths, files or records: `Foundation`.
- A new setting: `Settings\Config.cs`, and the manual.
- A new device: a profile in `Foundation\DeviceProfiles.cs`; its own code in a folder under `Devices`.
- Something that describes backup contents: `Catalog`.
- Something that makes or restores a backup: `Backup`, `Restore` or `Zip`.
- A step both the CLI and the GUI run: `Workflow`.
- Anything that reads the console or shows a window: `Cli` or `gui`, never an engine.

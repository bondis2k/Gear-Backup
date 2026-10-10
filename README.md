# M8 Backup

A Windows tool with graphical and command-line interfaces for backing up and restoring a Dirtywave M8 SD card, a card reader, or a local copy of the card.

- Full and incremental backups with dated snapshots and separate card profiles.
- Restore all files, a folder bundle, a single file, or a song with its samples.
- Preview changes, check backup integrity, and optionally keep recovery copies.

## Build and run

Requires Windows 10 or 11 and the built-in .NET Framework.

For the graphical interface:

```cmd
build-gui.cmd --no-pause
M8Backup.GUI.exe
```

For the command-line interface:

```cmd
build.cmd --no-pause
M8Backup.exe
```

You can also specify the card or folder:

```cmd
M8Backup.exe --source E:\
```

Configure backup locations and options in `M8Backup.ini`. SHA-256 comparison (`Thorough`), automatic integrity verification (`Verify`), and recovery copies (`Recovery`) default to `no`. Change previews default to `yes`; incremental backups with no file changes skip the proceed prompt.

Executable builds are ignored by Git; build from source locally. See [MANUAL.md](MANUAL.md) for all settings, restore options, and command-line arguments.

Long operations report progress. Use Cancel in the GUI or Ctrl+C in the CLI; interrupted backups can be resumed. Both interfaces use the same backup, restore, and integrity-check engines and INI settings.

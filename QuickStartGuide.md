# Gear Backup — Quick Start Guide

Start here to back up your device and restore files using the graphical app. For all settings and command-line options, see the [full manual](MANUAL.md).

## 1. Install and connect

Gear Backup runs on **Windows 10 or 11** with **.NET Framework 4.6 or newer**.

- **Setup package:** install it, then open **Gear Backup** from the Start menu.
- **Portable package:** extract it into a writable folder, then open `GearBackup.GUI.exe`.

Connect your device with a data-capable USB cable, or insert its SD card into a reader.

| Device | Mode for backing up files |
| --- | --- |
| M8 | USB disk mode |
| Tracker Mini | USB drive mode |
| OP-1 Field | **shift + COM**, then **T4** (MTP) |
| OP-XY | **COM**, then **T4** (MTP) |
| X100VI photos and videos | USB file-transfer mode (MTP) |

On the **Device** tab, click **Detect device**. Confirm the name and connection mode, then click **Go to Backup**.

For an ordinary folder or a card that is not detected, open **More options** on Backup and browse to the source. See [device selection](MANUAL.md#connecting-a-device) if you need to specify its type.

## 2. Make your first backup

1. On **Backup**, choose **Full backup**.
2. Optionally name it, for example `Before firmware update`.
3. Check the backup location shown below the controls. The default is `%USERPROFILE%\Backup`; change it under **More options** if needed.
4. Click **Back up**. If the location does not exist, accept the prompt to create it.
5. Review the planned changes and accept them. Wait for **Complete** and check the final summary before disconnecting.

You can click **Preview** first to inspect the plan without saving a backup. Leave the history/profile name blank unless you want a fixed name for a particular card or source.

For one ZIP file instead of a backup set, choose **Zip archive** in Type, optionally enter a filename, then click **Create ZIP** and choose where to save it. Blank filename uses the device name plus date and time. Extract the resulting archive with Windows; it contains the current files and folders, without restore-point history.

**With the default settings, restore files through Gear Backup.** Backups use a single content store rather than a separate browsable copy. Every successful run saves a restore point; unchanged content is shared within that full backup set.

## 3. Back up later changes

1. Connect the device and click **Detect device** again.
2. On **Backup**, choose **Incremental backup**.
3. Choose the **Full backup to update**. The latest set is selected initially.
4. Optionally name this incremental run, then click **Back up**.

Only new or changed content needs copying. The review excludes unchanged files. If nothing changed, the app saves a restore point without asking you to approve an empty action list.

Choose a new **Full backup** when you want an independent set. Separate full sets use additional space; their content is not shared with each other.

## 4. Restore what you need

Open **Restore** with or without a connected device. Without a detected device, you can choose from all available backups and restore to a PC folder, including when your device is broken. If you detect a device first, backups are filtered by that device type. The latest backup and restore point are selected initially.

**For a whole backup:**

1. Choose **Restore full backup**.
2. Select the full backup set and the full or incremental **Restore point** you want.
3. Set the **Restore destination** to a card/drive or PC folder.
4. Click **Preview** to check the changes, then **Restore** and accept the review.

Leave **Remove extra target files** unchecked to keep unrelated destination files. Checking it makes an eligible full-point restore exact and can remove files absent from that point.

**For selected files or folders:** choose **Restore individual files**, select the backup and point, then click **Choose files…**. Tick the items, click **Use selection**, choose the destination, and preview or restore. Checking a folder includes everything beneath it.

MTP backups restore to a **PC folder**, not directly to the device. Transfer OP-1 Field or OP-XY files afterward with Explorer. Restore camera photos to a PC folder for viewing or editing.

## 5. Check your backup

On **Restore**, choose **Check backups**, then click **Check** and follow the selection prompts. This reads saved content and checks its recorded SHA-256 hashes. Review any damaged or missing file reports.

Use **Dashboard** to see backup status and last checks. Use **Storage** to inspect disk usage and preview cleanup before recycling selected items.

## X100VI recipes and shooting settings

1. Set the camera to **USB RAW CONV./BACKUP RESTORE** and close other camera applications.
2. Connect one X100VI and click **Detect device**.
3. On Backup, choose **Back up X100VI settings**, then **Back up**.
4. To read the saved recipes, select the settings backup in **Dashboard** or **Storage** and click **View recipes**. You can copy a recipe or export it as text.

This saves C1–C7 recipes and current shooting settings. It does not export every camera setting or apply settings back to the camera. Switch back to USB file-transfer mode and detect again for photo/video backup.

## Where to go next

- **Help** or **F1:** open the offline manual.
- **Settings:** change verification, recovery copies, retention, or appearance. **Original** is the default theme.
- [Storage choices](MANUAL.md#choosing-how-backups-are-stored): choose between the default content store and browsable backups.
- [Troubleshooting](MANUAL.md#troubleshooting): detection, restore lists, paths, and interrupted backups.
- [Command-line reference](MANUAL.md#command-line-and-exit-codes): use `GearBackup.exe` and its options.

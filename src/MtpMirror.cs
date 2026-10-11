// Backing up an MTP device (such as the OP-1 Field): its files are first mirrored into a local folder,
// then that folder is backed up like any other source, so history, checksums, checks and zips all work
// unchanged. The mirror is kept between runs, so only new and changed files are downloaded again.
using System;
using System.Collections.Generic;
using System.IO;
using static FileCopy;
using static PathSafety;
using static GearBackup;
using static FujiSettings;
using static PortableDevices;

static class MtpMirror
{
    // What the mirror needs from a device; MtpDevice provides it, and tests use a fake.
    internal interface IMtpSource
    {
        List<MtpObject> ListAll();
        void Download(MtpObject item, string to, Action<long> progress);
    }

    internal sealed class MirrorResult
    {
        public int Downloaded, Unchanged, Removed;
        public long Bytes;
        public readonly List<string> Skipped = new List<string>();
    }

    // A connected MTP device chosen as the source, and the local folder that mirrors it.
    internal sealed class MtpSource
    {
        public DeviceProfile Device;
        public MtpDeviceInfo Info;
        public string Mirror;
        // Opens the device for reading; null means the real device (tests supply a fake).
        public Func<IMtpSource> Open = null;
        // Asks whether the X100VI is in settings mode; null means asking the real camera (tests supply an
        // answer). Asked right before every sync, not taken from detection: the camera can be switched
        // while a menu waits, and a sync in settings mode would see no photos and empty the mirror.
        public Func<bool> ReadSettingsMode = null;
    }

    internal static string MirrorRoot
    {
        get { return Path.Combine(AppIdentity.ExistingFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.DirectoryName, AppIdentity.Legacy.DirectoryName), "Devices"); }
    }

    // "<mirror root>\op1-field-L1PRC121". The folder name also becomes the backup history's profile name.
    internal static string MirrorFolder(DeviceProfile device, MtpDeviceInfo info)
    {
        var serial = new System.Text.StringBuilder();
        foreach (char c in info.Serial) if (char.IsLetterOrDigit(c) || c == '-' || c == '_') serial.Append(c);
        return Path.Combine(MirrorRoot, device.Id + "-" + (serial.Length > 0 ? serial.ToString() : "device"));
    }

    // The first connected MTP device that matches a device profile, or null.
    internal static MtpSource FindMtpSource()
    {
        foreach (DeviceProfile profile in Devices.All)
        {
            if (!profile.Mtp) continue;
            foreach (MtpDeviceInfo info in FindMtpDevices(profile.MtpVendor))
                if (profile.MtpName == null || string.Equals(info.FriendlyName, profile.MtpName, StringComparison.OrdinalIgnoreCase))
                    return new MtpSource { Device = profile, Info = info, Mirror = MirrorFolder(profile, info) };
        }
        return null;
    }

    internal static string MtpRestoreUnsupported(DeviceProfile device)
    {
        if (device.RestoreWarning != null)
            return "Restoring to the " + device.Name + " isn't offered. " + device.RestoreWarning +
                   " To do that, put a PC folder in place of the " + device.Name +
                   " (browse to it in the app, set SourceFolder, or use --source) and restore again.";
        return "Restoring to the " + device.Name + " isn't supported yet (it connects over MTP). Restore to a folder " +
               "instead (set SourceFolder or use --source), then copy the files to the " + device.Name + " with Explorer.";
    }

    // Brings the device's mirror up to date and returns its path, ready to be backed up.
    internal static string SyncMtpSource(MtpSource mtp)
    {
        if (mtp.Device == Devices.X100VI && (mtp.ReadSettingsMode != null ? mtp.ReadSettingsMode() : FujiSettingsMode(mtp.Info)))
            throw new IOException("The X100VI is in settings mode. Choose Back up X100VI settings (CLI: --camera-settings), or select USB card reader on the camera to back up photos.");
        OperationOutput.WriteLine("  Reading the " + mtp.Device.Name + " over MTP into " + mtp.Mirror);
        MirrorResult result;
        IMtpSource device = mtp.Open != null ? mtp.Open() : new MtpDevice(mtp.Info.InterfacePath);
        try { result = SyncMirror(device, mtp.Mirror); }
        finally
        {
            var disposable = device as IDisposable;
            if (disposable != null) disposable.Dispose();
        }
        OperationOutput.WriteLine("  {0:N0} file(s) downloaded ({1}), {2:N0} unchanged, {3:N0} removed from the mirror.",
            result.Downloaded, FormatSize(result.Bytes), result.Unchanged, result.Removed);
        foreach (string name in result.Skipped)
            WriteColored("  Skipped (not a valid Windows name): " + name, ConsoleColor.Yellow);
        OperationOutput.WriteLine();
        return Rooted(mtp.Mirror);
    }

    // Makes `mirror` an exact copy of the device: downloads files that are new or whose size or date
    // changed, and removes files and folders that are no longer on the device. Names from the device
    // go through SafePath, so a strange name can't write outside the mirror; such items are skipped.
    // Nothing is ever written to the device.
    internal static MirrorResult SyncMirror(IMtpSource source, string mirror)
    {
        var result = new MirrorResult();
        Directory.CreateDirectory(mirror);
        RejectRootLinks(mirror);
        // Refuse existing links before downloading or removing anything, including folders that
        // are no longer on the device. Recursive GetFiles would follow their junction targets.
        var files = new List<string>();
        var folders = new List<string>();
        ReadMirrorTree(mirror, files, folders);
        List<MtpObject> items = source.ListAll();

        var onDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloads = new List<KeyValuePair<MtpObject, string>>();
        long total = 0;
        foreach (MtpObject item in items)
        {
            string path;
            try { path = SafePath(mirror, item.RelativePath); }
            catch (IOException) { result.Skipped.Add(item.RelativePath); continue; }
            onDevice.Add(item.RelativePath);
            RejectLinksBelow(mirror, path, cleanFolders);
            if (item.IsFolder) { Directory.CreateDirectory(path); continue; }
            var existing = new FileInfo(path);
            if (existing.Exists && existing.Length == item.Size && item.Modified != DateTime.MinValue &&
                Math.Abs((existing.LastWriteTime - item.Modified).TotalSeconds) <= 2)
            {
                result.Unchanged++;
                continue;
            }
            downloads.Add(new KeyValuePair<MtpObject, string>(item, path));
            total += item.Size;
        }

        long done = 0;
        foreach (var download in downloads)
        {
            MtpObject item = download.Key;
            string path = download.Value;
            RejectRootLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = Path.Combine(Path.GetDirectoryName(path), ".geardownload-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                long before = done;
                source.Download(item, temp, bytes => OperationContext.Current.Progress(Stage.Downloading, item.RelativePath, before + bytes, total));
                long size = new FileInfo(temp).Length;
                if (item.Size > 0 && size != item.Size)
                    throw new IOException("the download of " + item.RelativePath + " was incomplete (" + size + " of " + item.Size + " bytes)");
                // Keep the device's date, however old: the next sync compares it to spot changes. (The OP-1
                // reports some factory sounds as 1970, which FAT cards can't store but the PC's disk can.)
                if (item.Modified != DateTime.MinValue)
                    try { File.SetLastWriteTime(temp, item.Modified); } catch (ArgumentException) { }
                RejectRootLinks(path);
                TryDelete(path);
                File.Move(temp, path);
                done += size;
                result.Downloaded++;
                result.Bytes += size;
            }
            finally { TryDelete(temp); }
        }

        // Remove what's no longer on the device (and any download left over from an interrupted run).
        files.Clear();
        folders.Clear();
        ReadMirrorTree(mirror, files, folders);
        foreach (string file in files)
        {
            string relative = file.Substring(Rooted(mirror).Length);
            if (onDevice.Contains(relative)) continue;
            RejectRootLinks(file);
            File.Delete(file);
            if (!Path.GetFileName(file).StartsWith(".geardownload-")) result.Removed++;
        }
        folders.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string folder in folders)
        {
            RejectRootLinks(folder);
            if (!onDevice.Contains(folder.Substring(Rooted(mirror).Length)) && Directory.GetFileSystemEntries(folder).Length == 0)
                Directory.Delete(folder);
        }
        return result;
    }

    // Rejects path, or a folder between it and the mirror (checked on its own at the start of a sync), if
    // it is a link. Folders found clean are remembered in `clean` for the rest of this sync, so listing
    // thousands of photos doesn't check the same folders, up to the drive root, again for each one.
    // Downloads still check the whole path right before writing.
    static void RejectLinksBelow(string mirror, string path, HashSet<string> clean)
    {
        string root = Rooted(mirror), current = Path.GetFullPath(path).TrimEnd('\\');
        while (current.Length >= root.Length && !clean.Contains(current))
        {
            bool isFolder = Directory.Exists(current);
            if ((isFolder || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            if (isFolder) clean.Add(current);
            current = Path.GetDirectoryName(current);
            if (current == null) break;
        }
    }

    // Enumerate one level at a time, checking each entry before descending. These checks deliberately
    // bypass the per-operation folder cache so cleanup also detects links added since the first scan.
    static void ReadMirrorTree(string mirror, List<string> files, List<string> folders)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(mirror));
        while (pending.Count > 0)
        {
            OperationContext.Current.CheckCancellation();
            string parent = pending.Pop();
            RejectRootLinks(parent);
            foreach (string entry in Directory.GetFileSystemEntries(parent))
            {
                if (!Within(entry, mirror)) throw new IOException("Path escapes its mirror: " + entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked paths are not supported: " + entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    folders.Add(entry);
                    pending.Push(entry);
                }
                else files.Add(entry);
            }
        }
    }
}

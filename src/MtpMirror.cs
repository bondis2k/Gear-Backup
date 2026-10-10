// Backing up an MTP device (such as the OP-1 Field): its files are first mirrored into a local folder,
// then that folder is backed up like any other source, so history, checksums, checks and zips all work
// unchanged. The mirror is kept between runs, so only new and changed files are downloaded again.
using System;
using System.Collections.Generic;
using System.IO;

partial class GearBackup
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
    }

    static string MirrorRoot
    {
        get { return Path.Combine(AppIdentity.ExistingFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.DirectoryName, AppIdentity.Legacy.DirectoryName), "Devices"); }
    }

    // "<mirror root>\op1-field-L1PRC121". The folder name also becomes the backup history's profile name.
    static string MirrorFolder(DeviceProfile device, MtpDeviceInfo info)
    {
        var serial = new System.Text.StringBuilder();
        foreach (char c in info.Serial) if (char.IsLetterOrDigit(c) || c == '-' || c == '_') serial.Append(c);
        return Path.Combine(MirrorRoot, device.Id + "-" + (serial.Length > 0 ? serial.ToString() : "device"));
    }

    // The first connected MTP device that matches a device profile, or null.
    static MtpSource FindMtpSource()
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

    static string MtpRestoreUnsupported(DeviceProfile device)
    {
        if (device.RestoreWarning != null)
            return "Restoring to the " + device.Name + " isn't offered. " + device.RestoreWarning +
                   " To do that, put a PC folder in place of the " + device.Name +
                   " (browse to it in the app, set SourceFolder, or use --source) and restore again.";
        return "Restoring to the " + device.Name + " isn't supported yet (it connects over MTP). Restore to a folder " +
               "instead (set SourceFolder or use --source), then copy the files to the " + device.Name + " with Explorer.";
    }

    // Brings the device's mirror up to date and returns its path, ready to be backed up.
    static string SyncMtpSource(MtpSource mtp)
    {
        OperationOutput.WriteLine("  Reading the " + mtp.Device.Name + " over MTP into " + mtp.Mirror);
        MirrorResult result;
        using (var device = new MtpDevice(mtp.Info.InterfacePath)) result = SyncMirror(device, mtp.Mirror);
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
    static MirrorResult SyncMirror(IMtpSource source, string mirror)
    {
        var result = new MirrorResult();
        Directory.CreateDirectory(mirror);
        RejectRootLinks(mirror);
        List<MtpObject> items = source.ListAll();

        var onDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloads = new List<KeyValuePair<MtpObject, string>>();
        long total = 0;
        foreach (MtpObject item in items)
        {
            string path;
            try { path = SafePath(mirror, item.RelativePath); }
            catch (IOException) { result.Skipped.Add(item.RelativePath); continue; }
            onDevice.Add(item.RelativePath);
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
                TryDelete(path);
                File.Move(temp, path);
                done += size;
                result.Downloaded++;
                result.Bytes += size;
            }
            finally { TryDelete(temp); }
        }

        // Remove what's no longer on the device (and any download left over from an interrupted run).
        foreach (string file in Directory.GetFiles(mirror, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(Rooted(mirror).Length);
            if (onDevice.Contains(relative)) continue;
            File.Delete(file);
            if (!Path.GetFileName(file).StartsWith(".geardownload-")) result.Removed++;
        }
        var folders = new List<string>(Directory.GetDirectories(mirror, "*", SearchOption.AllDirectories));
        folders.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string folder in folders)
            if (!onDevice.Contains(folder.Substring(Rooted(mirror).Length)) && Directory.GetFileSystemEntries(folder).Length == 0)
                Directory.Delete(folder);
        return result;
    }
}

// What the program knows about each kind of device it backs up. Everything device-specific lives in a
// DeviceProfile; the rest of the program asks the current device (CurrentDevice) instead of assuming
// the M8. Adding a device means adding a profile here, plus its own readers if it has any.
using System;
using System.Collections.Generic;
using System.IO;
using static PathSafety;
using static ChangeSummary;
using static SampleDependencies;

partial class GearBackup
{
    internal sealed class DeviceProfile
    {
        public string Id;                  // the Device setting's value, e.g. "m8"
        public string Name;                // shown in messages, e.g. "M8"
        // USB detection: the disk's PnP device ID contains UsbVendor, or its model starts with UsbModel.
        public string UsbVendor, UsbModel;
        // Fallback detection: a removable drive with this label. Found this way, it can't be ejected.
        public string VolumeLabel;
        // MTP devices (no drive letter): the USB vendor ID in their Windows device ID, and their name.
        public string MtpVendor, MtpName;
        public bool Mtp { get { return MtpVendor != null; } }
        // Whether to warn about file names the device may not handle (MaxNameLength, special characters).
        public bool NameWarnings = true;
        // Its picture in the GUI header once detected: assets\<IconName>-256.png, embedded in the GUI exe.
        public string IconName;
        public string[] SongExtensions = new string[0];
        public string SongsFolder;         // left out of song names in summaries, e.g. "Songs"
        // What the device calls its songs, in summaries and searches ("songs", or "projects" on the OP-XY).
        public string SongsLabel = "songs";
        // Folders whose files never count as songs, e.g. the OP-XY's automatic project backups.
        public string[] NotSongFolders = new string[0];
        // Whether each song is a folder holding the song file and everything it uses (the Tracker's
        // project.mt next to its patterns, instruments and samples). Songs are then named after their
        // folder, and a whole song folder can be restored.
        public bool SongIsFolder;
        // A file at the top of the device's card that only this device writes ("trackerConfig"), so its
        // card is recognised in a card reader too. Null when there is none.
        public string MarkerFile;
        // Why restoring onto the device (or its card) is unusual, for devices where it is (a camera). Shown
        // instead of a restore to the device, and before restoring one of its backups onto a memory card.
        public string RestoreWarning;
        public string BundlesFolder;       // null when the device has no bundles
        // Lists the sample files a song uses, for "song with samples" restore; null when not supported.
        public Func<string, List<string>> ReadSamplePaths;

        public bool IsSong(string path)
        {
            foreach (string folder in NotSongFolders)
                if (path.IndexOf(folder + "\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (string extension in SongExtensions)
                if (string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // pnp and model are upper case, as WMI reports them after ToUpperInvariant.
        public bool MatchesDisk(string pnp, string model)
        {
            return (UsbVendor != null && pnp.Contains(UsbVendor)) || (UsbModel != null && model.StartsWith(UsbModel));
        }
    }

    internal static class Devices
    {
        // The Dirtywave M8 in USB disk mode: vendor "M8" (USB\VID_16C0&PID_0630), songs in Songs\*.m8s.
        public static readonly DeviceProfile M8 = new DeviceProfile {
            Id = "m8", Name = "M8", UsbVendor = "VEN_M8", UsbModel = "M8", VolumeLabel = "M8",
            SongExtensions = new[] { ".m8s" }, SongsFolder = "Songs", BundlesFolder = "Bundles",
            ReadSamplePaths = SamplePaths, IconName = "M8" };

        // The Teenage Engineering OP-1 Field. It connects only over MTP (shift + COM, then T4), as a portable
        // device "OP-1" with USB vendor 2367, holding synth, drum, tapes and mixdown folders. It has no song
        // files or bundles, and file names are the device's own, so there are no name warnings.
        public static readonly DeviceProfile OP1Field = new DeviceProfile {
            Id = "op1-field", Name = "OP-1 Field", MtpVendor = "VID_2367", MtpName = "OP-1", NameWarnings = false,
            IconName = "OP1Field" };

        // The Teenage Engineering OP-XY. Like the OP-1 Field it connects only over MTP (COM, then T4), as a
        // portable device "OP-XY" with USB vendor 2367. Its projects are .xy files in projects\user; the
        // device keeps automatic copies of them under projects\user\backups, which are backed up but not
        // listed as projects. Samples are in samples\user; factory content isn't exposed over MTP.
        public static readonly DeviceProfile OPXY = new DeviceProfile {
            Id = "op-xy", Name = "OP-XY", MtpVendor = "VID_2367", MtpName = "OP-XY", NameWarnings = false,
            SongExtensions = new[] { ".xy" }, SongsFolder = @"projects\user", SongsLabel = "projects",
            NotSongFolders = new[] { "backups" }, IconName = "OPXY" };

        // The Polyend Tracker Mini in USB drive mode: disk "POLYEND TRACKER STORAGE" (USB\VID_16D0&PID_1118),
        // card label TRACKER_M. Each project is a folder, Projects\<group>\<name>\project.mt, with its own
        // patterns, instruments and samples; Workspace holds the project that is currently open. The name
        // rules (MaxNameLength, special characters) are the M8's, so there are no name warnings.
        public static readonly DeviceProfile TrackerMini = new DeviceProfile {
            Id = "tracker-mini", Name = "Tracker Mini", UsbVendor = "VEN_POLYEND&PROD_TRACKER", VolumeLabel = "TRACKER_M",
            NameWarnings = false, SongExtensions = new[] { ".mt" }, SongsFolder = "Projects", SongsLabel = "projects",
            SongIsFolder = true, MarkerFile = "trackerConfig", IconName = "TrackerMini" };

        // The Fujifilm X100VI camera. In its USB file-transfer mode it is a portable device "X100VI" with
        // USB vendor 04CB (Fujifilm), holding DCIM\<nnn>_FUJI with each shot as a JPG, RAF (raw) and/or
        // HIF (HEIF) file, and videos as MOV. Files report no modified date, only the time they were taken.
        // A shot's files share a name, so summaries count it once.
        public static readonly DeviceProfile X100VI = new DeviceProfile {
            Id = "x100vi", Name = "X100VI", MtpVendor = "VID_04CB", MtpName = "X100VI", NameWarnings = false,
            SongExtensions = new[] { ".jpg", ".raf", ".hif", ".mov" }, SongsFolder = "DCIM", SongsLabel = "photos and videos",
            IconName = "X100VI",
            RestoreWarning = "Photos are rarely put back on a camera: it doesn't need them to take new ones, and old " +
                "photos back on the card can clash with the camera's file numbering. To view, edit or share them, " +
                "restore to a folder on your PC instead." };

        // Detection tries these in order. The first is also the default for a folder of unknown type.
        public static readonly DeviceProfile[] All = { M8, OP1Field, OPXY, TrackerMini, X100VI };

        public static DeviceProfile Find(string id)
        {
            foreach (DeviceProfile device in All)
                if (string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)) return device;
            return null;
        }

        // "M8"; with more devices, "M8 or OP-1 Field", for "No ... detected".
        public static string Names
        {
            get
            {
                var names = new List<string>();
                foreach (DeviceProfile device in All) names.Add(device.Name);
                if (names.Count < 2) return string.Join("", names.ToArray());
                return string.Join(", ", names.GetRange(0, names.Count - 1).ToArray()) + " or " + names[names.Count - 1];
            }
        }
    }

    // The device whose history a backup belongs to, from the history's name ("x100vi-<serial>",
    // "m8-card-1A2B3C4D"); null for a named profile or a legacy history.
    internal static DeviceProfile HistoryDevice(string backup)
    {
        string history = Path.GetFileName(Path.GetDirectoryName(Rooted(backup).TrimEnd('\\')));
        foreach (DeviceProfile device in Devices.All)
            if (history.StartsWith(device.Id + "-", StringComparison.OrdinalIgnoreCase)) return device;
        return null;
    }

    // New backups remember their device even with a custom profile name. Older automatic histories
    // can still identify it through their name; unknown legacy histories use the caller's fallback.
    // A device this version doesn't know (a backup from a newer version) is treated like no record.
    internal static DeviceProfile BackupDevice(string backup)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "device.txt");
        if (!File.Exists(path)) return HistoryDevice(backup);
        RejectRootLinks(path);
        if (new FileInfo(path).Length > 128) throw new IOException("Invalid backup device record.");
        return Devices.Find(File.ReadAllText(path).Trim()) ?? HistoryDevice(backup);
    }

    // The device the current operation works with (set by the front end once the source is known).
    internal static DeviceProfile CurrentDevice { get { return OperationContext.Current.Device; } }

    // The kind of device a source is: the Device setting when it names one, otherwise the detected
    // device, otherwise the device whose marker file is at the top of the source (a card in a reader),
    // otherwise the first known device.
    static DeviceProfile DeviceFor(Config config, DeviceProfile detected, string source = null)
    {
        DeviceProfile named = Devices.Find(config.Device);
        if (named != null) return named;
        if (detected != null) return detected;
        if (!string.IsNullOrEmpty(source))
            foreach (DeviceProfile device in Devices.All)
                if (device.MarkerFile != null && File.Exists(Path.Combine(source, device.MarkerFile))) return device;
        return Devices.All[0];
    }
}

// What the program knows about each kind of device it backs up. Everything device-specific lives in a
// DeviceProfile; the rest of the program asks the current device (CurrentDevice) instead of assuming
// the M8. Adding a device means adding a profile here, plus its own readers if it has any.
using System;
using System.Collections.Generic;
using System.IO;

partial class M8Backup
{
    internal sealed class DeviceProfile
    {
        public string Id;                  // the Device setting's value, e.g. "m8"
        public string Name;                // shown in messages, e.g. "M8"
        // USB detection: the disk's PnP device ID contains UsbVendor, or its model starts with UsbModel.
        public string UsbVendor, UsbModel;
        // Fallback detection: a removable drive with this label. Found this way, it can't be ejected.
        public string VolumeLabel;
        public string[] SongExtensions = new string[0];
        public string SongsFolder;         // left out of song names in summaries, e.g. "Songs"
        public string BundlesFolder;       // null when the device has no bundles
        // Lists the sample files a song uses, for "song with samples" restore; null when not supported.
        public Func<string, List<string>> ReadSamplePaths;

        public bool IsSong(string path)
        {
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
            ReadSamplePaths = SamplePaths };

        // Detection tries these in order. The first is also the default for a folder of unknown type.
        public static readonly DeviceProfile[] All = { M8 };

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

    // The device the current operation works with (set by the front end once the source is known).
    static DeviceProfile CurrentDevice { get { return OperationContext.Current.Device; } }

    // The kind of device a source is: the Device setting when it names one, otherwise the detected
    // device, otherwise the first known device.
    static DeviceProfile DeviceFor(Config config, DeviceProfile detected)
    {
        DeviceProfile named = Devices.Find(config.Device);
        if (named != null) return named;
        return detected ?? Devices.All[0];
    }
}

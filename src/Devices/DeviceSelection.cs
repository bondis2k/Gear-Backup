// Which device an operation works with: the current one, the one a backup records, and the one a source is.
using System;
using System.Collections.Generic;
using System.IO;
using static PathSafety;
using static DeviceSelection;

static class DeviceSelection
{
    // Device identity comes only from the backup's explicit metadata, independently of its folder name.
    internal static DeviceProfile BackupDevice(string backup)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "device.txt");
        if (!File.Exists(path)) return null;
        return Devices.Find(Records.ReadText(path, Records.DeviceLimit, "Backup device record").Trim());
    }

    // The device the current operation works with (set by the front end once the source is known).
    internal static DeviceProfile CurrentDevice { get { return OperationContext.Current.Device; } }

    // The kind of device a source is: the Device setting when it names one, otherwise the detected
    // device, otherwise the device whose marker file is at the top of the source (a card in a reader),
    // otherwise the first known device.
    internal static DeviceProfile DeviceFor(Config config, DeviceProfile detected, string source = null)
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

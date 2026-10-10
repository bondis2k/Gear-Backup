// Finding the M8 drive and safely ejecting it.
using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Windows looks for a DLL in the program's own folder first. cfgmgr32.dll isn't one of the "known DLLs"
// Windows always takes from System32, so without this a cfgmgr32.dll planted next to the exe (e.g. in
// Downloads) would be loaded. This makes every DllImport in the program load from System32 only.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

partial class M8Backup
{
    // The M8 shows up as a USB disk with vendor "M8" (USB\VID_16C0&PID_0630).
    // pnpId is the disk's device instance ID, needed to eject it (null if found by label only).
    static string FindM8Drive(out string pnpId)
    {
        pnpId = null;
        var disks = new ManagementObjectSearcher(
            "SELECT DeviceID, PNPDeviceID, Model FROM Win32_DiskDrive WHERE InterfaceType='USB'");
        foreach (ManagementObject disk in disks.Get())
        {
            string pnp = (disk["PNPDeviceID"] ?? "").ToString().ToUpperInvariant();
            string model = (disk["Model"] ?? "").ToString().ToUpperInvariant();
            if (!pnp.Contains("VEN_M8") && !model.StartsWith("M8")) continue;

            string partQuery = "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + disk["DeviceID"] +
                               "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
            foreach (ManagementObject part in new ManagementObjectSearcher(partQuery).Get())
            {
                string volQuery = "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + part["DeviceID"] +
                                  "'} WHERE AssocClass=Win32_LogicalDiskToPartition";
                foreach (ManagementObject vol in new ManagementObjectSearcher(volQuery).Get())
                {
                    pnpId = disk["PNPDeviceID"].ToString();
                    return vol["DeviceID"] + "\\";
                }
            }
        }

        // Fallback: a removable drive labelled "M8"
        foreach (DriveInfo d in DriveInfo.GetDrives())
            if (d.IsReady && d.DriveType == DriveType.Removable &&
                string.Equals(d.VolumeLabel, "M8", StringComparison.OrdinalIgnoreCase))
                return d.RootDirectory.FullName;

        return null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, int flags);

    // Asks Windows to safely remove the M8 (same as "Safely Remove Hardware").
    // Returns null on success, otherwise the reason it failed.
    static string EjectDrive(string pnpId)
    {
        if (pnpId == null) return "the M8 was found by drive label only, so it can't be ejected automatically.";

        uint disk;
        if (CM_Locate_DevNodeW(out disk, pnpId, 0) != 0) return "the M8 device was not found.";

        // Eject the USB device the disk belongs to; if that's refused, try the disk itself.
        uint parent;
        uint[] candidates = CM_Get_Parent(out parent, disk, 0) == 0 ? new[] { parent, disk } : new[] { disk };
        string lastError = "Windows refused to eject it.";
        foreach (uint devInst in candidates)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int vetoType;
                var vetoName = new StringBuilder(260);
                int result = CM_Request_Device_EjectW(devInst, out vetoType, vetoName, vetoName.Capacity, 0);
                if (result == 0 && vetoType == 0) return null;
                lastError = vetoType != 0
                    ? "it is in use (close any program or Explorer window using the M8 and try again)."
                    : "Windows refused to eject it (error " + result + ").";
                Thread.Sleep(1000);
            }
        }
        return lastError;
    }
}

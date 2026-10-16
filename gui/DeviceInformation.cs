using System;
using System.IO;
using System.Text;
using static GearBackup.ConsoleUi;
using static GearBackup.Formatting;

namespace GearBackup.Gui
{
    // Small, read-only overview: no recursive scan or MTP transfer just to detect a device.
    internal static class DeviceInformation
    {
        internal static string Read(SourceChoice found)
        {
            if (found == null) return "Connect a device and click Detect device, or choose a local folder on the Backup tab.";
            if (found.Mtp != null)
            {
                var text = new StringBuilder();
                if (found.Mtp.Info != null)
                {
                    if (!string.IsNullOrEmpty(found.Mtp.Info.FriendlyName)) text.AppendLine("Device: " + found.Mtp.Info.FriendlyName);
                    if (!string.IsNullOrEmpty(found.Mtp.Info.InstanceId)) text.AppendLine("Device identifier: " + found.Mtp.Info.Serial);
                }
                text.Append(found.CameraSettingsMode ? "Backup scope: C1–C7 recipes and current shooting settings.\nStorage information is not available in camera settings mode."
                    : "Storage capacity is not reported by this connection.\nFiles are mirrored to the PC before backup; restore to a local folder.");
                return text.ToString();
            }
            string tracker = found.Detected == Devices.TrackerMini ? TrackerProjects(found.Path) : "";
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(found.Path)));
                if (!drive.IsReady) return "Storage is not ready. Click Detect device again after reconnecting.";
                long total = drive.TotalSize, free = drive.TotalFreeSpace;
                return "Storage: " + (string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel + " (" + drive.Name + ")") +
                    "\nCapacity: " + FormatSize(total) + "    Used: " + FormatSize(Math.Max(0, total - free)) + "    Free: " + FormatSize(free) +
                    (total > 0 ? " (" + (free * 100.0 / total).ToString("0") + "%)" : "") +
                    "\nFilesystem: " + drive.DriveFormat + "    Connection: " + drive.DriveType + "\nSource: " + found.Path + tracker;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)) throw;
                return "Source: " + found.Path + "\nStorage information is unavailable. Reconnect and detect again to refresh it.";
            }
        }

        // The Tracker card's projects and anything in them that needs attention; left out when the card cannot be read.
        static string TrackerProjects(string card)
        {
            try { return "\n\n" + TrackerProjectCheck.Summarize(card).Describe(); }
            catch (Exception ex) when (ExpectedFailure.Is(ex)) { return ""; }
        }
    }

}

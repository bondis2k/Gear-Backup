using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Readable identity and exact uncompressed size for a standalone archive.
internal static class ArchiveMetadata
{
    internal const string FileName = "gearbackup-info.txt";
    internal static readonly Encoding Utf8 = new UTF8Encoding(false);

    internal static string Create(string source, Config config, SourceChoice choice, DateTime takenUtc,
        int files, int folders, long contentBytes, int checksumBytes)
    {
        var header = new StringBuilder();
        header.Append("Gear Backup standalone ZIP archive\n");
        Field(header, "Application version", AppIdentity.Version);
        Field(header, "Backup taken (UTC)", takenUtc.ToString("O", CultureInfo.InvariantCulture));
        var device = OperationContext.Current.Device;
        Field(header, "Backup device profile", device.Name + " (" + device.Id + ")");
        Field(header, "Connection", choice != null && choice.Mtp != null ? "MTP" :
            choice != null && choice.Detected != null ? "USB drive / memory card" : "Local folder / memory card");
        Field(header, "Source name", new DirectoryInfo(source).Name);
        if (choice != null)
        {
            if (choice.Mtp != null && choice.Mtp.Info != null)
            {
                Field(header, "Device name", choice.Mtp.Info.FriendlyName);
                if (!string.IsNullOrEmpty(choice.Mtp.Info.InstanceId)) Field(header, "Device identifier", choice.Mtp.Info.Serial);
            }
            else if (choice.Detected != null)
            {
                Field(header, "Device name", choice.Detected.Name);
                Field(header, "Device identifier", choice.PnpId);
                // Only inspect a detected drive; a manual source may be a UNC path or a local PC folder.
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(source));
                    if (drive.IsReady)
                    {
                        Field(header, "Volume label", drive.VolumeLabel);
                        Field(header, "Filesystem", drive.DriveFormat);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            Field(header, "Camera connection mode", choice.CameraMode);
        }
        Field(header, "Profile name", config.Profile);
        Field(header, "Source files", files.ToString(CultureInfo.InvariantCulture));
        Field(header, "Source folders", folders.ToString(CultureInfo.InvariantCulture));
        Field(header, "Backup content size", Formatting.FormatSize(contentBytes));
        Field(header, "Backup content size (bytes)", contentBytes.ToString(CultureInfo.InvariantCulture));
        header.Append("Backup content size excludes generated metadata.\n");
        const string note = "Extracted size includes all files, including gearbackup-info.txt and checksums.sha256; filesystem allocation overhead is excluded.\n";
        // The report includes its own byte length. Recalculate until the decimal size's length is stable.
        long extracted = contentBytes + checksumBytes;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string text = header.ToString() + "Total extracted size (bytes): " + extracted.ToString(CultureInfo.InvariantCulture) + "\n" + note;
            long measured = contentBytes + checksumBytes + Utf8.GetByteCount(text);
            if (measured == extracted) return text;
            extracted = measured;
        }
        throw new IOException("Could not calculate archive extracted size.");
    }

    static void Field(StringBuilder text, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) text.Append(label).Append(": ").Append(value.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
    }

    internal static string Checksums(Dictionary<string, string> hashes)
    {
        var names = new List<string>(hashes.Keys);
        names.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (string name in names)
        {
            OperationContext.Current.CheckCancellation();
            text.Append(hashes[name]).Append("  ").Append(name).Append('\n');
        }
        return text.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using static PathSafety;
using static Backups;
using static MtpMirror;
using static PortableDevices;
using static ProfileSettings;
using static Session;
using static ProfileNaming;
using static FujiSettings;

static class FujiSettings
{
    // Codes describe stored recipes and their corresponding live shooting values. Raw bytes are
    // retained even when an enum isn't decoded, so a snapshot doesn't silently lose unknown values.
    internal static readonly string[] FujiSettingNames = {
        "Image size", "Image quality", "Dynamic range", "Dynamic range priority", "Film simulation",
        "Monochromatic color warm/cool", "Monochromatic color magenta/green", "Grain effect",
        "Color Chrome effect", "Color Chrome FX Blue", "Smooth skin effect", "White balance",
        "White balance shift red", "White balance shift blue", "Color temperature", "Highlight tone",
        "Shadow tone", "Color", "Sharpness", "Noise reduction", "Clarity", "Lens modulation optimizer", "Color space"
    };
    internal static readonly ushort[] FujiLiveCodes = {
        0xD1A5, 0xD018, 0xD007, 0xD02E, 0xD001, 0xD104, 0xD031, 0xD023, 0xD029, 0xD030,
        0xD189, 0x5005, 0xD00B, 0xD00C, 0xD017, 0xD320, 0xD321, 0xD008, 0x5015, 0xD01C,
        0xD032, 0xD34D, 0xD00A
    };
    internal static bool FujiSigned(int index)
    {
        return index == 5 || index == 6 || index == 12 || index == 13 ||
            index == 15 || index == 16 || index == 17 || index == 18 || index == 20;
    }

    internal static FujiInfo ParseFujiInfo(byte[] data)
    {
        if (data == null || data.Length > 1024 * 1024) throw new IOException("Invalid camera device information.");
        try
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new BinaryReader(stream))
            {
                reader.ReadUInt16(); // standard version
                reader.ReadUInt32(); reader.ReadUInt16(); // vendor extension and version
                ReadFujiString(reader);
                reader.ReadUInt16(); // functional mode
                ReadFujiArray(reader); ReadFujiArray(reader); // operations, events
                var info = new FujiInfo { Properties = new HashSet<ushort>(ReadFujiArray(reader)) };
                ReadFujiArray(reader); ReadFujiArray(reader); // capture and image formats
                info.Manufacturer = ReadFujiString(reader); info.Model = ReadFujiString(reader);
                info.Firmware = ReadFujiString(reader); info.Serial = ReadFujiString(reader);
                if (stream.Position != stream.Length || info.Manufacturer != "FUJIFILM" || info.Model != "X100VI")
                    throw new IOException("Settings capture supports the Fujifilm X100VI only.");
                return info;
            }
        }
        catch (EndOfStreamException ex) { throw new IOException("Truncated camera device information.", ex); }
    }
    static ushort[] ReadFujiArray(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        if (count > 4096 || count * 2 > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new IOException("Invalid camera capability list.");
        ushort[] items = new ushort[count];
        for (int i = 0; i < items.Length; i++) items[i] = reader.ReadUInt16();
        return items;
    }
    static string ReadFujiString(BinaryReader reader)
    {
        int characters = reader.ReadByte();
        if (characters == 0) return "";
        byte[] bytes = reader.ReadBytes(characters * 2);
        if (bytes.Length != characters * 2 || bytes[bytes.Length - 1] != 0 || bytes[bytes.Length - 2] != 0)
            throw new IOException("Invalid camera text response.");
        return Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
    }
    internal static string FujiText(byte[] data)
    {
        using (var stream = new MemoryStream(data))
        using (var reader = new BinaryReader(stream))
        {
            string text = ReadFujiString(reader);
            if (stream.Position != stream.Length) throw new IOException("Invalid recipe name response.");
            return text;
        }
    }
    internal static ushort FujiWord(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 2) throw new IOException("Invalid camera settings value.");
        return (ushort)(bytes[0] | (bytes[1] << 8));
    }

    internal static Dictionary<string, byte[]> CaptureFujiSettings(IFujiSettingsSource source)
    {
        OperationContext.Current.CheckCancellation();
        FujiInfo info = ParseFujiInfo(source.DeviceInfo());
        if (!info.Properties.Contains(0xD18C) || !info.Properties.Contains(0xD18D))
            throw new IOException("Select USB RAW CONV./BACKUP RESTORE on the X100VI, then try again.");
        for (ushort code = 0xD18E; code <= 0xD1A4; code++)
            if (!info.Properties.Contains(code)) throw new IOException("The camera does not expose the full C1–C7 recipe block.");
        ushort original = FujiWord(source.ReadProperty(0xD18C));
        if (original > 7) throw new IOException("The camera returned an unknown recipe selector; it was left unchanged.");
        var files = new Dictionary<string, byte[]>();
        var live = ReadFujiValues(source, info, true);
        files.Add("Shooting settings.xml", FujiSettingsXml(info, "Current shooting settings", null, live, true));
        Exception captureError = null;
        bool selected = false;
        try
        {
            for (ushort slot = 1; slot <= 7; slot++)
            {
                OperationContext.Current.Progress(Stage.ReadingCameraSettings, "C" + slot, 0, 0, slot - 1, 7);
                selected = true; // Even a failed selection may already have reached the camera.
                source.SelectRecipe(slot);
                if (FujiWord(source.ReadProperty(0xD18C)) != slot) throw new IOException("The camera did not select recipe C" + slot + ".");
                string name = FujiText(source.ReadProperty(0xD18D));
                var values = ReadFujiValues(source, info, false);
                files.Add("C" + slot + ".xml", FujiSettingsXml(info, "C" + slot, name, values, false));
            }
        }
        catch (Exception ex) { captureError = ex; throw; }
        finally
        {
            if (selected)
            {
                try
                {
                    source.SelectRecipe(original);
                    if (FujiWord(source.ReadProperty(0xD18C)) != original) throw new IOException("Recipe selector verification failed.");
                }
                catch (Exception ex)
                {
                    throw new IOException("Could not restore the camera's original recipe selector C" + original +
                        ". Reconnect the camera and check its selected recipe." +
                        (captureError == null ? "" : " Capture also failed: " + captureError.Message), ex);
                }
            }
        }
        // Confirm that selecting read banks did not alter the live shooting values.
        byte[][] after = ReadFujiValues(source, info, true);
        for (int i = 0; i < live.Length; i++)
            if (!SameFujiBytes(live[i], after[i])) throw new IOException("The camera's shooting settings changed during capture. Retry while leaving the camera controls untouched.");
        files.Add("About this backup.txt", Encoding.UTF8.GetBytes(
            "Gear Backup X100VI settings snapshot, format 1\r\n\r\n" +
            "Includes C1–C7 recipe names and raw property values, plus readable current shooting settings.\r\n" +
            "This is not a full-camera backup or a Fujifilm X Acquire/Tether App settings file.\r\n" +
            "Button assignments, network settings and other camera setup menus are not captured.\r\n" +
            "XML files include raw little-endian bytes and numeric values; enum numbers are camera protocol values.\r\n" +
            "Gear Backup can restore these files to a PC folder, but does not write settings back to the camera.\r\n"));
        return files;
    }
    static byte[][] ReadFujiValues(IFujiSettingsSource source, FujiInfo info, bool live)
    {
        byte[][] values = new byte[FujiSettingNames.Length][];
        for (int i = 0; i < values.Length; i++)
        {
            OperationContext.Current.CheckCancellation();
            ushort code = live ? FujiLiveCodes[i] : (ushort)(0xD18E + i);
            if (!info.Properties.Contains(code)) continue;
            values[i] = source.ReadProperty(code);
            FujiWord(values[i]); // The known block contains 16-bit settings, not arbitrary blobs.
        }
        return values;
    }
    internal static bool SameFujiBytes(byte[] left, byte[] right)
    {
        if (left == null || right == null) return left == right;
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }
    static byte[] FujiSettingsXml(FujiInfo info, string section, string name, byte[][] values, bool live)
    {
        using (var output = new MemoryStream())
        {
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
            using (XmlWriter xml = XmlWriter.Create(output, settings))
            {
                xml.WriteStartElement("GearBackupCameraSettings"); xml.WriteAttributeString("format", "1");
                xml.WriteAttributeString("scope", "recipes-and-shooting-settings");
                xml.WriteElementString("Model", info.Model); xml.WriteElementString("Firmware", info.Firmware);
                xml.WriteElementString("Serial", info.Serial); xml.WriteElementString("Section", section);
                if (name != null) xml.WriteElementString("RecipeName", name);
                for (int i = 0; i < values.Length; i++)
                {
                    xml.WriteStartElement("Setting"); xml.WriteAttributeString("name", FujiSettingNames[i]);
                    xml.WriteAttributeString("code", "0x" + (live ? FujiLiveCodes[i] : 0xD18E + i).ToString("X4"));
                    if (values[i] == null) xml.WriteAttributeString("supported", "no");
                    else
                    {
                        xml.WriteAttributeString("type", FujiSigned(i) ? "int16" : "uint16");
                        xml.WriteAttributeString("raw", BitConverter.ToString(values[i]).Replace("-", ""));
                        int number = FujiSigned(i) ? (int)(short)FujiWord(values[i]) : FujiWord(values[i]);
                        xml.WriteString(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            return output.ToArray();
        }
    }

    internal static OperationResult RunFujiSettingsBackup(Config config)
    {
        var cameras = FindMtpDevices("VID_04CB");
        cameras.RemoveAll(info => !string.Equals(info.FriendlyName, "X100VI", StringComparison.OrdinalIgnoreCase));
        if (cameras.Count != 1) throw new IOException("Connect one X100VI in USB RAW CONV./BACKUP RESTORE mode. Close other camera applications and try again.");
        OperationContext.Current.Device = Devices.X100VI;
        // Settings use a separate history (Studio-settings) with the named profile's preferences.
        string automatic = Path.GetFileName(MirrorFolder(Devices.X100VI, cameras[0])).Replace("x100vi-", "x100vi-settings-");
        string profileName = config.Profile.Length > 0 ? SettingsProfileName(config.Profile) : automatic;
        ValidateProfile(profileName);
        config = ResolveProfileSettings(config, Devices.X100VI.Id, config.Profile.Length > 0 ? config.Profile : profileName);
        string history = Path.Combine(HistoriesRoot(config), "profiles", profileName);
        if (!EnsureBackupFolder(config)) return BackupFolderCancelled();
        OperationOutput.WriteLine("  Capturing X100VI C1–C7 recipes and current shooting settings.");
        OperationOutput.WriteLine("  This is a settings snapshot, not a full-camera or X Acquire backup.");
        Dictionary<string, byte[]> files;
        // A history lock is too late to protect recipe-bank selection, and different profiles can
        // refer to the same camera. Serialize capture itself across Gear Backup processes.
        string mutexName = @"Local\GearBackup.X100VISettings." + Path.GetFileName(MirrorFolder(Devices.X100VI, cameras[0]));
        using (var mutex = new System.Threading.Mutex(false, mutexName))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (System.Threading.AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another Gear Backup instance is reading this camera's settings.");
            try { using (var camera = new FujiPtp(cameras[0].InterfacePath)) files = CaptureFujiSettings(camera); }
            finally { mutex.ReleaseMutex(); }
        }
        OperationContext.Current.CheckCancellation();
        return SaveFujiSettings(files, history, config);
    }

    internal static OperationResult SaveFujiSettings(Dictionary<string, byte[]> files, string history, Config config)
    {
        if (!EnsureBackupFolder(config)) return BackupFolderCancelled();
        string staging = Path.Combine(Path.GetTempPath(), "GearBackup-camera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            RejectRootLinks(staging);
            foreach (var file in files)
                using (var output = new FileStream(SafePath(staging, file.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    output.Write(file.Value, 0, file.Value.Length);
            using (LockUnlessPreview(history))
            {
                OperationResult result = RunBackup(staging, history, null, false, config);
                if (result.Title != null && !result.Cancelled && result.ExitCode == 0)
                {
                    result.Title = "X100VI settings backed up";
                    result.Summary = "Saved C1–C7 recipes and current shooting settings. Camera selector restored and shooting values verified.";
                }
                return result;
            }
        }
        finally
        {
            RejectRootLinks(staging);
            foreach (string name in files.Keys) File.Delete(SafePath(staging, name));
            Directory.Delete(staging); // Don't recursively delete anything unexpected in the temporary folder.
        }
    }

    // "Studio" -> "Studio-settings", shortened to fit the 64-character profile limit.
    internal static string SettingsProfileName(string profile)
    {
        const string suffix = "-settings";
        if (profile.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return profile;
        return profile.Substring(0, Math.Min(profile.Length, 64 - suffix.Length)).TrimEnd() + suffix;
    }

    internal static bool FujiSettingsMode(MtpDeviceInfo info)
    {
        using (var camera = new FujiPtp(info.InterfacePath))
            return ParseFujiInfo(camera.DeviceInfo()).Properties.Contains(0xD18C);
    }

    internal static string FujiModeName(bool settingsMode) { return settingsMode ? "settings mode" : "photo-transfer mode"; }
}

internal interface IFujiSettingsSource
{
    byte[] DeviceInfo();
    byte[] ReadProperty(ushort code);
    void SelectRecipe(ushort slot);
}

internal sealed class FujiInfo
{
    public string Manufacturer, Model, Firmware, Serial;
    public HashSet<ushort> Properties = new HashSet<ushort>();
}

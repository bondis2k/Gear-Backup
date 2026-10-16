using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using static FujiSettings;

partial class Tests
{
    [Test("X100VI: settings capture and selector safety", Folder = "helpers/fuji-settings")]
    static void TestFujiSettings(string root)
    {
        var source = new FakeFujiSettings();
        Dictionary<string, byte[]> files;
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) files = CaptureFujiSettings(source);
        Assert(files.Count == 9 && source.Slot == 3 && source.Selections == 8, "captures all seven recipes and restores the original selector");
        for (int slot = 1; slot <= 7; slot++)
        {
            var xml = new XmlDocument(); xml.LoadXml(Encoding.UTF8.GetString(files["C" + slot + ".xml"]));
            Assert(xml.SelectNodes("//Setting").Count == 23 && xml.SelectSingleNode("//RecipeName").InnerText == "Recipe & <" + slot + ">",
                "each recipe preserves all properties and escapes its name");
            Assert(xml.SelectSingleNode("//Setting[@code='0xD192']").InnerText == slot.ToString(), "each recipe is read from its own bank");
            Assert(xml.SelectSingleNode("//Setting[@code='0xD19A']").InnerText == "-2" &&
                xml.SelectSingleNode("//Setting[@code='0xD19A']").Attributes["raw"].Value == "FEFF", "signed values retain exact wire bytes");
        }
        string live = Encoding.UTF8.GetString(files["Shooting settings.xml"]);
        Assert(live.Contains("0xD001") && live.Contains("recipes-and-shooting-settings"), "live settings use separate codes and explicit scope");
        Assert(Encoding.UTF8.GetString(files["About this backup.txt"]).Contains("not a full-camera backup"), "export states its limitations");

        string history = Path.Combine(root, "history");
        var config = new Config { BackupFolder = root, Preview = false, Verify = true, Zip = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { PreviewOnly = true, Device = Devices.X100VI }.Activate())
            SaveFujiSettings(files, history, config);
        Assert(!Directory.Exists(history), "settings preview does not create a backup history");
        OperationResult saved;
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.X100VI }.Activate())
            saved = SaveFujiSettings(files, history, config);
        Assert(saved.ExitCode == 0 && saved.Counts.Copied == 9 && File.Exists(saved.OutputFolder + ".zip"),
            "settings snapshot uses the full backup engine and optional ZIP");
        Assert(SameFujiBytes(File.ReadAllBytes(Snapshots.CurrentBackup(saved.OutputFolder).Files.Find(f => f.Relative == "C3.xml").StoredPath), files["C3.xml"]),
            "saved recipe bytes match captured content");
        int ok = 0, damaged = 0, missing = 0;
        using (new OperationContext(new TestInteraction(), CancellationToken.None) { Device = Devices.X100VI }.Activate())
            Snapshots.CheckSnapshots(saved.OutputFolder, ref ok, ref damaged, ref missing);
        Assert(ok > 0 && damaged == 0 && missing == 0, "camera exports participate in normal checksum verification");

        source = new FakeFujiSettings { MissingLive = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate()) files = CaptureFujiSettings(source);
        Assert(Encoding.UTF8.GetString(files["Shooting settings.xml"]).Contains("supported=\"no\""), "unavailable live properties are recorded explicitly");

        source = new FakeFujiSettings { FailRead = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            AssertFujiFailure(() => CaptureFujiSettings(source), "simulated read failure");
        Assert(source.Slot == 3 && source.Selections == 4, "read failure restores the original selector before returning");

        source = new FakeFujiSettings { FailSelect = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            AssertFujiFailure(() => CaptureFujiSettings(source), "simulated selection failure");
        Assert(source.Slot == 3 && source.Selections == 3, "a selector command that fails after applying is still restored");

        using (var cancellation = new CancellationTokenSource())
        {
            source = new FakeFujiSettings { Cancel = cancellation };
            bool cancelled = false;
            using (new OperationContext(new TestInteraction(), cancellation.Token).Activate())
                try { CaptureFujiSettings(source); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && source.Slot == 3, "cancellation restores the selector without bypassing cancellation elsewhere");
        }
        source = new FakeFujiSettings { FailRestore = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            AssertFujiFailure(() => CaptureFujiSettings(source), "Could not restore the camera's original recipe selector");
        source = new FakeFujiSettings { ChangeLive = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            AssertFujiFailure(() => CaptureFujiSettings(source), "shooting settings changed");
        source = new FakeFujiSettings { Slot = 8 };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            AssertFujiFailure(() => CaptureFujiSettings(source), "unknown recipe selector");
        Assert(source.Selections == 0, "unknown original selector is rejected before any camera write");
        source = new FakeFujiSettings { WrongMode = true };
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
            AssertFujiFailure(() => CaptureFujiSettings(source), "USB RAW CONV./BACKUP RESTORE");
        Assert(source.Selections == 0, "photo mode is rejected without selecting recipes");

        byte[] valid = new FakeFujiSettings().DeviceInfo();
        Assert(ParseFujiInfo(valid).Firmware == "1.32", "camera information parses model and firmware");
        foreach (int length in new[] { 0, 4, valid.Length - 1 })
        {
            byte[] shortInfo = new byte[length]; Array.Copy(valid, shortInfo, length);
            AssertFujiFailure(() => ParseFujiInfo(shortInfo), null);
        }
        AssertFujiFailure(() => ParseFujiInfo(new FakeFujiSettings { Model = "X100V" }.DeviceInfo()), "X100VI only");
        AssertFujiFailure(() => FujiText(new byte[] { 2, 65, 0 }), "Invalid camera text");
        AssertFujiFailure(() => FujiWord(new byte[0]), "Invalid camera settings value");
    }

    static void AssertFujiFailure(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (IOException ex) { rejected = message == null || ex.Message.Contains(message); }
        Assert(rejected, "settings capture rejects invalid or incomplete data: " + (message ?? "truncated information"));
    }

    sealed class FakeFujiSettings : IFujiSettingsSource
    {
        public ushort Slot = 3;
        public int Selections;
        public bool MissingLive, FailRead, FailSelect, FailRestore, ChangeLive, WrongMode;
        public string Model = "X100VI";
        public CancellationTokenSource Cancel;
        public byte[] DeviceInfo()
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write((ushort)100); writer.Write((uint)14); writer.Write((ushort)100);
                WriteText(writer, ""); writer.Write((ushort)0);
                writer.Write((uint)0); writer.Write((uint)0); // operations, events
                var properties = new SortedSet<ushort>(FujiLiveCodes);
                if (!WrongMode)
                    for (ushort code = 0xD18C; code <= 0xD1A4; code++) properties.Add(code);
                if (MissingLive) properties.Remove(0xD001);
                writer.Write((uint)properties.Count); foreach (ushort code in properties) writer.Write(code);
                writer.Write((uint)0); writer.Write((uint)0); // formats
                WriteText(writer, "FUJIFILM"); WriteText(writer, Model); WriteText(writer, "1.32"); WriteText(writer, "TEST-CAMERA");
                return output.ToArray();
            }
        }
        public byte[] ReadProperty(ushort code)
        {
            if (code == 0xD18C) return BitConverter.GetBytes(Slot);
            if (FailRead && Selections == 3 && code == 0xD19D) throw new IOException("simulated read failure");
            if (Cancel != null && Selections == 2 && code == 0xD192) Cancel.Cancel();
            if (code == 0xD18D)
                using (var output = new MemoryStream())
                using (var writer = new BinaryWriter(output)) { WriteText(writer, "Recipe & <" + Slot + ">"); return output.ToArray(); }
            if (code == 0xD19A || code == 0xD00B) return new byte[] { 0xFE, 0xFF };
            if (code >= 0xD18E && code <= 0xD1A4) return BitConverter.GetBytes(Slot);
            return BitConverter.GetBytes((ushort)(ChangeLive && Selections == 8 && code == 0xD001 ? 2 : 1));
        }
        public void SelectRecipe(ushort slot)
        {
            Selections++;
            if (FailRestore && Selections == 8) throw new IOException("disconnected");
            Slot = slot;
            if (FailSelect && Selections == 2) throw new IOException("simulated selection failure");
        }
        static void WriteText(BinaryWriter writer, string value)
        {
            writer.Write((byte)(value.Length + 1)); writer.Write(Encoding.Unicode.GetBytes(value + "\0"));
        }
    }
}

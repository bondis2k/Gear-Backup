using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using GearBackup;

partial class Tests
{
    [Test("Audit: restore destinations cannot overlap backups", Folder = "audit-restore")]
    static void TestAuditRestoreIsolation(string root)
    {
        string source = Folder(root, "source"); Put(Path.Combine(source, "song.txt"), "ORIGINAL");
        var config = new Config { BackupFolder = Path.Combine(root, "store"), SettingsDirectory = root, Preview = false, Recovery = false, Verify = true };
        PreparedSource prepared = null; string backup = null;
        WithUi(() => {
            prepared = Session.PrepareSource(config, new SourceChoice { Path = source, Detected = Devices.M8 });
            backup = Session.RunPrepared(prepared, false, false).OutputFolder;
        });
        string objects = Path.Combine(AppIdentity.MetadataRoot(backup), "objects");
        string objectFile = Directory.GetFiles(objects)[0];
        byte[] original = File.ReadAllBytes(objectFile);
        var destinations = new[] { backup, Path.GetDirectoryName(backup), objects, Folder(backup, "child") };
        foreach (string target in destinations) foreach (char mode in new[] { 'm', 'e' })
        {
            var ui = new TestInteraction { OnChooseItem = (q, items) => 1 };
            ui.Keys.Enqueue('a'); ui.Keys.Enqueue(mode);
            using (new OperationContext(ui, CancellationToken.None).Activate())
                Throws(() => RestoreFlow.RunRestore(target, new List<string> { backup }, config), "CLI refuses overlapping destination before planning");
            Assert(ui.Questions.Count == 0 && ui.Plans == 0, "no restore-mode or destructive prompt for an unsafe destination");
            Assert(Convert.ToBase64String(File.ReadAllBytes(objectFile)) == Convert.ToBase64String(original), "backup object remains intact");
        }
        prepared.Path = PathSafety.Rooted(objects); prepared.Source.Path = objects;
        using (new OperationContext(new TestInteraction { OnChooseItem = (q, items) => 1 }, CancellationToken.None).Activate())
            Throws(() => Session.RunPrepared(prepared, true, false), "normal CLI workflow enforces overlap protection");
        WithUi(() => {
            var set = RestoreCatalog.Load(backup, Devices.M8); var point = set.Points[0];
            var request = new RestoreRequest { Backup = backup, DeviceId = Devices.M8.Id, Point = point.Id, Stamp = RestoreCatalog.Stamp(point.Snapshot), Exact = true };
            foreach (string target in destinations) Throws(() => RestoreCatalog.Run(target, config, request), "catalog restore enforces the same guard");
        });
        Assert(File.Exists(objectFile), "all rejected workflows preserve the object");
        foreach (string target in new[] { Path.Combine(config.BackupFolder, AppIdentity.BackupsFolderName, "sibling"), RestoreFlow.RecoveryRoot(config) })
            Throws(() => RestoreFlow.ValidateRestoreDestination(backup, target, config), "repository and recovery destinations are protected too");
    }

    [Test("Audit: MTP streaming budgets", Folder = "audit-mtp")]
    static void TestAuditMtpBudgets(string root)
    {
        WithUi(() => {
            foreach (long advertised in new long[] { 0, 1, 4 })
            {
                using (var output = new MemoryStream())
                {
                    int reads = 0;
                    Throws(() => MtpDevice.CopyDownload((b, n) => ++reads == 1 ? 5 : 0, output, new byte[8], advertised, null), "oversized chunk refused");
                    Assert(output.Length == 0, "over-budget chunk never reaches disk");
                }
            }
            foreach (int invalid in new[] { -1, 9 })
                using (var output = new MemoryStream())
                    Throws(() => MtpDevice.CopyDownload((b, n) => invalid, output, new byte[8], 100, null), "invalid read count refused");
            using (var output = new MemoryStream())
            {
                MtpDevice.CopyDownload((b, n) => 0, output, new byte[8], 0, null);
                Assert(output.Length == 0, "empty object succeeds");
                Throws(() => MtpDevice.CopyDownload((b, n) => 0, output, new byte[8], 1, null), "premature EOF refused");
            }
            using (var output = new MemoryStream())
            {
                int reads = 0;
                MtpDevice.CopyDownload((b, n) => ++reads < 3 ? 4 : 0, output, new byte[8], 8, null);
                Assert(output.Length == 8, "exact-size stream succeeds");
            }
            var fake = new FakeMtpSource(); fake.File("bad.bin", "OVERSIZED", DateTime.MinValue); fake.Items[0].Size = 0;
            string mirror = Folder(root, "mirror");
            Throws(() => MtpMirror.SyncMirror(fake, mirror), "mirror refuses zero advertised/nonempty result");
            Assert(Directory.GetFileSystemEntries(mirror).Length == 0, "failed transfer cleans its temporary file");
        });
        using (var cancel = new CancellationTokenSource())
        using (new OperationContext(new TestInteraction(), cancel.Token).Activate())
        using (var output = new MemoryStream())
        {
            int reads = 0; bool stopped = false;
            try { MtpDevice.CopyDownload((b, n) => { reads++; return 1; }, output, new byte[8], 8, n => cancel.Cancel()); }
            catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && reads == 1 && output.Length == 1, "cancellation stops before another read or write");
        }
    }

    [Test("Audit: metadata handle byte limits", Folder = "audit-records")]
    static void TestAuditRecordBudgets(string root)
    {
        string path = Path.Combine(root, "text");
        File.WriteAllText(path, "one\r\ntwo\n", Encoding.Unicode);
        Assert(Records.ReadText(path, 100, "record") == "one\r\ntwo\n", "bounded text reader preserves BOM decoding");
        Assert(Records.ReadLines(path, 100, "record").Length == 2, "bounded lines preserve newline behavior");
        Throws(() => Records.ReadBytes(path, 1, "record"), "opened-handle length limit enforced");
        using (var stream = new LimitedReadStream(new MemoryStream(new byte[10]), 3, "limit"))
        {
            var bytes = new byte[2];
            Assert(stream.Read(bytes, 0, 2) == 2, "stream reads within budget");
            Throws(() => stream.Read(bytes, 0, 2), "actual bytes, not declared length, enforce the limit");
        }
        using (var stream = new LimitedReadStream(new MemoryStream(new byte[3]), 3, "limit"))
        {
            var bytes = new byte[4];
            Assert(stream.Read(bytes, 0, 4) == 3 && stream.Read(bytes, 0, 4) == 0, "exact byte limit accepts EOF");
        }
    }

    [Test("Audit: retention revalidation", Folder = "audit-retention")]
    static void TestAuditRetention(string root)
    {
        string history = Folder(root, "history"), old = Folder(history, "2026-01-01"), current = Folder(history, "2026-01-02");
        string content = Path.Combine(old, "keep.txt"); Put(content, "ORIGINAL");
        int recycled = 0;
        var ui = new TestInteraction { OnQuestion = q => Put(content, "CHANGED CONTENT") };
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Throws(() => BackupFolders.PruneBackups(history, 1, current, p => recycled++), "changed backup refused after confirmation");
        Assert(recycled == 0 && File.Exists(content), "changed folder never recycled");
        string displaced = Path.Combine(root, "displaced-old");
        ui = new TestInteraction { OnQuestion = q => {
            Directory.Move(old, displaced); Directory.CreateDirectory(old);
            File.Copy(Path.Combine(displaced, "keep.txt"), content);
            File.SetLastWriteTimeUtc(content, File.GetLastWriteTimeUtc(Path.Combine(displaced, "keep.txt")));
        } };
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Throws(() => BackupFolders.PruneBackups(history, 1, current, p => recycled++), "replacement with identical file metadata refused by directory identity");
        Assert(recycled == 0, "a replacement directory never inherits prior approval");
        ui = new TestInteraction { OnQuestion = q => Directory.Move(current, Path.Combine(root, "moved-current")) };
        using (new OperationContext(ui, CancellationToken.None).Activate())
            Throws(() => BackupFolders.PruneBackups(history, 1, current, p => recycled++), "newly latest backup refused");
        Assert(recycled == 0, "latest backup remains protected after confirmation");
        Directory.CreateDirectory(current);
        WithUi(() => BackupFolders.PruneBackups(history, 1, current, p => recycled++));
        Assert(recycled == 1, "unchanged older backup remains eligible");
    }

    static byte[] AuditZip(int count, string name = null)
    {
        using (var file = new MemoryStream())
        {
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                for (int i = 0; i < count; i++) zip.CreateEntry(name ?? ("item-" + i));
            return file.ToArray();
        }
    }
    static void AuditSet16(byte[] bytes, int at, ushort value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, at, 2); }

    [Test("Audit: ZIP preflight resource limits", Folder = "audit-zip")]
    static void TestAuditZipPreflight(string root)
    {
        WithUi(() => {
            byte[] archive = AuditZip(20000);
            GC.Collect(); long before = GC.GetTotalMemory(true);
            using (var file = new MemoryStream(archive)) Throws(() => ZipPreflight.Validate(file, 10), "entry budget checked before allocating entries");
            Assert(GC.GetTotalMemory(false) - before < 2 * 1024 * 1024, "rejection uses bounded buffers, not one object per ZIP entry");
            byte[] normal = AuditZip(2);
            using (var file = new MemoryStream(normal)) { ZipPreflight.Validate(file, 10); Assert(file.Position == 0, "normal ZIP rewound for ZipArchive"); }
            foreach (ushort count in new ushort[] { 1, 3 })
            {
                byte[] lie = (byte[])normal.Clone(); AuditSet16(lie, lie.Length - 14, count); AuditSet16(lie, lie.Length - 12, count);
                using (var file = new MemoryStream(lie)) Throws(() => ZipPreflight.Validate(file, 10), "actual central records must match declared count");
            }
            using (var file = new MemoryStream(AuditZip(1, new string('x', ZipPreflight.MaxNameBytes + 1))))
                Throws(() => ZipPreflight.Validate(file, 10), "long entry name refused before allocation");
            byte[] extra = AuditZip(1);
            int central = (int)BitConverter.ToUInt32(extra, extra.Length - 6);
            AuditSet16(extra, central + 30, ZipPreflight.MaxExtraBytes + 1);
            using (var file = new MemoryStream(extra)) Throws(() => ZipPreflight.Validate(file, 10), "large extra data refused");

            // Small, real ZIP64 ending; the central directory itself can use ordinary small-entry fields.
            byte[] small = AuditZip(1); int end = small.Length - 22;
            using (var file = new MemoryStream())
            {
                file.Write(small, 0, end);
                using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
                {
                    writer.Write(0x06064b50u); writer.Write(44UL); writer.Write((ushort)45); writer.Write((ushort)45);
                    writer.Write(0u); writer.Write(0u); writer.Write(1UL); writer.Write(1UL);
                    writer.Write((ulong)BitConverter.ToUInt32(small, end + 12)); writer.Write((ulong)BitConverter.ToUInt32(small, end + 16));
                    writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)end); writer.Write(1u);
                    byte[] ending = new byte[22]; Array.Copy(small, end, ending, 0, 22);
                    AuditSet16(ending, 8, ushort.MaxValue); AuditSet16(ending, 10, ushort.MaxValue);
                    Array.Copy(BitConverter.GetBytes(uint.MaxValue), 0, ending, 12, 4); Array.Copy(BitConverter.GetBytes(uint.MaxValue), 0, ending, 16, 4);
                    writer.Write(ending);
                }
                file.Position = 0; ZipPreflight.Validate(file, 10);
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true)) Assert(zip.Entries.Count == 1, "ZIP64 remains readable after bounded preflight");
                byte[] forged = file.ToArray(); Array.Copy(BitConverter.GetBytes(99999999UL), 0, forged, end + 24, 8); Array.Copy(BitConverter.GetBytes(99999999UL), 0, forged, end + 32, 8);
                using (var bad = new MemoryStream(forged)) Throws(() => ZipPreflight.Validate(bad, 10), "ZIP64 excessive count refused");
            }
        });
        using (var cancel = new CancellationTokenSource())
        using (new OperationContext(new TestInteraction(), cancel.Token).Activate())
        using (var file = new MemoryStream(AuditZip(1)))
        {
            cancel.Cancel(); bool stopped = false;
            try { ZipPreflight.Validate(file, 10); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "ZIP preflight honors cancellation");
        }
    }
}

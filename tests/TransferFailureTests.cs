using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GearBackup;

partial class Tests
{
    // Faults are scheduled at a transfer boundary, after earlier files may already be complete.
    sealed class FaultingMtpSource : IMtpSource, IDisposable
    {
        internal readonly FakeMtpSource Data = new FakeMtpSource();
        internal int Downloads, FailAt, Disposed;
        internal bool OmitProgress, ListingFails;
        internal Action AfterDownload;
        public List<MtpObject> ListAll()
        {
            if (ListingFails) throw new IOException("Device disconnected while enumerating storage.");
            return Data.ListAll();
        }
        public void Download(MtpObject item, string to, Action<long> progress)
        {
            Downloads++;
            if (Downloads == FailAt) { File.WriteAllText(to, "PARTIAL"); throw new IOException("Device disconnected during transfer."); }
            Data.Download(item, to, OmitProgress ? null : progress);
            if (AfterDownload != null) AfterDownload();
        }
        public void Dispose() { Disposed++; }
    }

    [Test("MTP failure: cancellation without device progress preserves old copy")]
    static void TestMtpSilentCancellation(string root)
    {
        string mirror = Folder(root, "mirror"), old = Path.Combine(mirror, "sound.wav"), stale = Path.Combine(mirror, "stale.wav");
        Put(old, "OLD"); Put(stale, "KEEP");
        using (var cancellation = new CancellationTokenSource())
        {
            var source = new FaultingMtpSource { OmitProgress = true, AfterDownload = () => cancellation.Cancel() };
            source.Data.File("sound.wav", "NEW SOUND", DateTime.Now);
            bool stopped = false;
            using (new OperationContext(new TestInteraction(), cancellation.Token).Activate())
                try { MtpMirror.SyncMirror(source, mirror); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && File.ReadAllText(old) == "OLD" && File.ReadAllText(stale) == "KEEP",
                "cancellation is checked before publication even if the device reports no progress");
            Assert(Directory.GetFiles(mirror, ".geardownload-*", SearchOption.AllDirectories).Length == 0, "cancelled staged download is removed");
        }
    }

    [Test("MTP failure: completed transfers, backup isolation and reconnect", Folder = "mtp-reconnect")]
    static void TestMtpPartialRunReconnect(string root)
    {
        var source = new FaultingMtpSource();
        var day = new DateTime(2026, 1, 1, 10, 0, 0);
        source.Data.File("a.wav", "OLD A", day); source.Data.File("b.wav", "OLD B", day);
        string mirrors = Folder(root, "mirrors"), mirror = Folder(mirrors, "op1-field-FAULT");
        var mtp = new MtpSource { Device = Devices.OP1Field, Mirror = mirror,
            Info = new MtpDeviceInfo { InstanceId = @"USB\VID_2367&PID_0102\FAULT", FriendlyName = "OP-1" }, Open = () => source };
        var config = new Config { BackupFolder = Folder(root, "backups"), SettingsDirectory = root, Preview = false };
        var context = new OperationContext(new TestInteraction(), CancellationToken.None);
        var prepared = Session.PrepareSource(config, new SourceChoice { Path = mirror, Detected = Devices.OP1Field, Mtp = mtp }, true, context);
        var initial = Session.RunPrepared(prepared, false, false, context);
        Assert(initial.Succeeded && initial.OutputFolder != null, "initial backup completes before disconnect: " + initial.Summary);
        string point = ContentStore.Current(initial.OutputFolder).Name;
        Put(Path.Combine(mirror, "stale.wav"), "KEEP UNTIL SUCCESS");
        source.Data.File("a.wav", "NEW A", day.AddDays(1)); source.Data.File("b.wav", "NEW B", day.AddDays(1));
        source.FailAt = source.Downloads + 2;
        Throws(() => Session.RunPrepared(prepared, false, true, context), "the second transfer disconnect aborts backup");
        Assert(File.ReadAllText(Path.Combine(mirror, "a.wav")) == "NEW A" && File.ReadAllText(Path.Combine(mirror, "b.wav")) == "OLD B" &&
            File.Exists(Path.Combine(mirror, "stale.wav")), "completed mirror files remain, failed files and stale files are preserved");
        var saved = ContentStore.Current(initial.OutputFolder);
        Assert(saved.Name == point && File.ReadAllText(saved.Files.Find(f => f.Relative == "a.wav").StoredPath) == "OLD A" &&
            Directory.GetFiles(Snapshots.SnapshotRoot(initial.OutputFolder), "*.manifest").Length == 1,
            "a partial mirror never becomes a published backup point");
        Assert(source.Disposed == 2 && Directory.GetFiles(mirror, ".geardownload-*", SearchOption.AllDirectories).Length == 0,
            "failed source is disposed and its partial transfer is removed");
        using (HistoryLock.LockHistory(mirrors)) { }
        using (HistoryLock.LockHistory(prepared.History)) { }
        int before = source.Downloads; source.FailAt = 0;
        var resumed = Session.RunPrepared(prepared, false, true, context);
        Assert(resumed.Succeeded && source.Downloads == before + 1 && source.Disposed == 3 && !File.Exists(Path.Combine(mirror, "stale.wav")),
            "reconnect reacquires locks, skips completed transfers and cleans stale files only after success");
        saved = ContentStore.Current(resumed.OutputFolder);
        Assert(saved.Name != point && File.ReadAllText(saved.Files.Find(f => f.Relative == "a.wav").StoredPath) == "NEW A" &&
            File.ReadAllText(saved.Files.Find(f => f.Relative == "b.wav").StoredPath) == "NEW B", "retry commits a complete new point");
    }

    [Test("MTP failure: enumeration failure preserves mirror and closes source")]
    static void TestMtpListingFailure(string root)
    {
        var source = new FaultingMtpSource { ListingFails = true };
        string mirror = Folder(root, "mirrors/device");
        Put(Path.Combine(mirror, "old.wav"), "KEEP");
        var mtp = new MtpSource { Device = Devices.OP1Field, Mirror = mirror,
            Info = new MtpDeviceInfo { InstanceId = @"USB\VID_2367&PID_0102\LIST", FriendlyName = "OP-1" }, Open = () => source };
        var context = new OperationContext(new TestInteraction(), CancellationToken.None);
        var prepared = Session.PrepareSource(new Config { BackupFolder = Folder(root, "backups"), SettingsDirectory = root },
            new SourceChoice { Path = mirror, Detected = Devices.OP1Field, Mtp = mtp }, true, context);
        Throws(() => Session.RunPrepared(prepared, false, false, context), "enumeration disconnect is not treated as an empty device");
        Assert(source.Disposed == 1 && source.Downloads == 0 && File.ReadAllText(Path.Combine(mirror, "old.wav")) == "KEEP" &&
            BackupFolders.ListBackups(prepared.History).Count == 0, "listing failure closes the device without deleting mirror files or creating a backup");
    }

    [Test("MTP failure: locked publication preserves old file and permits retry")]
    static void TestMtpPublicationFailure(string root)
    {
        string mirror = Folder(root, "mirror"), target = Path.Combine(mirror, "sound.wav"), stale = Path.Combine(mirror, "stale.wav");
        Put(target, "OLD"); Put(stale, "KEEP");
        var source = new FaultingMtpSource();
        source.Data.File("sound.wav", "NEW SOUND", DateTime.Now);
        FileStream held = null;
        source.AfterDownload = () => held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Throws(() => MtpMirror.SyncMirror(source, mirror), "publication failure propagates");
        }
        finally { if (held != null) held.Dispose(); }
        Assert(File.ReadAllText(target) == "OLD" && File.Exists(stale) &&
            Directory.GetFiles(mirror, ".geardownload-*").Length == 0, "failed publication preserves the old file and skips stale cleanup");
        source.AfterDownload = null;
        var result = MtpMirror.SyncMirror(source, mirror);
        Assert(result.Downloaded == 1 && result.Removed == 1 && File.ReadAllText(target) == "NEW SOUND", "publication can be retried after the lock is released");
    }
}

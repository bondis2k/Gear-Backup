using System;
using System.IO;
using System.Linq;
using System.Threading;
using GearBackup;

partial class Tests
{
    [Test("M8 relative sample paths beside songs")]
    static void TestM8RelativeSamplePaths(string root)
    {
        string songPath = Path.Combine(root, "Songs", "Demos", "SNOWYDAY.m8s");
        Put(songPath, "song fixture");
        Put(Path.Combine(root, "Songs", "Demos", "Samples", "kick.wav"), "RELATIVE");
        Put(Path.Combine(root, "Samples", "root.wav"), "ROOTED");
        Put(Path.Combine(root, "Samples", "wrong.wav"), "WRONG LOCATION");
        var song = new M8SongSummary { Directory = "/Bundles/OLD_LOCATION/", Firmware = "3.2.1" };
        song.Instruments.Add(new M8InstrumentInfo { Slot = 0, Sample = "Samples/kick.wav" });
        song.Instruments.Add(new M8InstrumentInfo { Slot = 1, Sample = "/Samples/root.wav" });
        song.Instruments.Add(new M8InstrumentInfo { Slot = 2, Sample = "Samples/wrong.wav" });
        song.Instruments.Add(new M8InstrumentInfo { Slot = 3, Sample = "../Samples/root.wav" });
        var report = M8SampleCheck.Run(song, root, null, songPath);
        Assert(report.Samples[0].Relative == @"Songs\Demos\Samples\kick.wav" && report.Samples[0].CardStatus == "Present on card", "relative references resolve beside the actual song, ignoring stale embedded directories");
        Assert(report.Samples[1].Relative == @"Samples\root.wav" && report.Samples[1].CardStatus == "Present on card", "leading slash remains rooted at the card");
        Assert(report.Samples[2].CardStatus == "Missing on card", "an unrelated root-level filename cannot mask a missing song-relative sample");
        Assert(report.Samples[3].CardStatus == "Invalid reference", "relative resolution still refuses traversal");
        Assert(M8SampleCheck.Run(song, null, null, songPath).Samples[0].CardStatus == "Cannot resolve reference", "missing card context is reported rather than guessing");
        Assert(M8SampleCheck.Run(song, root, null, Path.Combine(Path.GetTempPath(), "external-song.m8s")).Samples[0].CardStatus == "Cannot resolve reference", "a song outside the selected card cannot silently resolve to the wrong folder");
        var config = new Config { BackupFolder = Folder(root, "backups"), Preview = false };
        string history = Path.Combine(Histories.HistoriesRoot(config), "profiles", "M8");
        // Keep the repository outside the source tree being captured.
        string source = Folder(root, "card-source");
        Put(Path.Combine(source, "Songs", "Demos", "Samples", "kick.wav"), "RELATIVE");
        Backups.RunBackup(source, history, null, false, config);
        report = M8SampleCheck.Run(song, root, config, songPath);
        Assert(report.Samples[0].Matches.Any(m => m.Exact && m.Relative == @"Songs\Demos\Samples\kick.wav"), "backup matching uses the resolved card-relative path");
    }

    [Test("M8 sample checker: card, history and damaged objects")]
    static void TestM8SampleCheck(string root)
    {
        string card = Folder(root, "card");
        Put(Path.Combine(card, "Samples", "kick.wav"), "KICK");
        Put(Path.Combine(card, "Samples", "lost.wav"), "OLD SAMPLE");
        Put(Path.Combine(card, "Elsewhere", "candidate.wav"), "CANDIDATE");
        Put(Path.Combine(card, "Samples", "broken.wav"), "BROKEN");
        var config = new Config { BackupFolder = Folder(root, "backups"), Preview = false };
        string history = Path.Combine(Histories.HistoriesRoot(config), "profiles", "M8");
        string backup = Backups.RunBackup(card, history, null, false, config).OutputFolder;
        File.Delete(Path.Combine(card, "Samples", "lost.wav"));
        File.Delete(Path.Combine(card, "Samples", "broken.wav"));
        Backups.RunBackup(card, history, backup, true, config);
        var set = RestoreCatalog.Load(backup, Devices.M8);
        var broken = set.Points.SelectMany(p => p.Snapshot.Files).First(f => f.Relative == @"Samples\broken.wav");
        File.WriteAllText(broken.StoredPath, "DAMAGE"); // Same length; hash verification is required.
        var song = new M8SongSummary();
        Action<int, string> sample = (slot, path) => song.Instruments.Add(new M8InstrumentInfo { Slot = slot, Kind = "Sampler", Sample = path });
        sample(0, "/Samples/kick.wav"); sample(1, "/Samples/lost.wav"); sample(2, "/Samples/candidate.wav");
        sample(3, "/Samples/broken.wav"); sample(4, "/Samples/absent.wav"); sample(5, "/SAMPLES/KICK.WAV");
        sample(6, "/../outside.wav"); sample(7, "/Samples/NUl.wav"); sample(8, "/Samples/ki\u202Eck.wav");
        var report = M8SampleCheck.Run(song, card, config);
        Assert(report.Samples.Count == 8 && report.BackupsSearched == 1, "deduplicates references case-insensitively and searches M8 sets");
        var kick = report.Samples.First(s => s.Relative == @"Samples\kick.wav");
        Assert(kick.Slots == "00, 05" && kick.CardStatus == "Present on card", "lists all referencing slots and resolves card-root paths");
        var lost = report.Samples.First(s => s.Relative == @"Samples\lost.wav");
        Assert(lost.CardStatus == "Missing on card" && lost.Matches.Any(m => m.Exact && m.Status == "Verified copy" && m.Point.StartsWith("Full backup")),
            "finds samples deleted from the card in an older full restore point");
        Assert(report.Samples.First(s => s.Relative == @"Samples\candidate.wav").Matches.All(m => !m.Exact) &&
            report.Samples.First(s => s.Relative == @"Samples\candidate.wav").Matches.Count > 0, "filename-only matches remain candidates");
        Assert(report.Samples.First(s => s.Relative == @"Samples\broken.wav").Matches.All(m => m.Status.StartsWith("Unavailable / damaged")), "damaged objects are never reported as verified copies");
        Assert(report.Samples.First(s => s.Relative == @"Samples\absent.wav").Matches.Count == 0, "missing references remain missing");
        Assert(report.Samples.Count(s => s.CardStatus == "Invalid reference") == 3, "rejects traversal, reserved names and invisible characters");
        Assert(M8SampleCheck.Run(song, null, config).Samples.First().CardStatus == "Card not checked", "backup search works without a connected card");
        Assert(M8SampleCheck.Run(song, card, null).BackupsSearched == 0, "card-only checks need no backup location");
        File.WriteAllText(BackupDatabase.CatalogPath(backup), "damaged index");
        report = M8SampleCheck.Run(song, card, config);
        Assert(report.Samples.First(s => s.Relative == @"Samples\lost.wav").Matches.Any(m => m.Exact && m.Status == "Verified copy") &&
            report.Warnings.Any(w => w.Contains("recovery records")), "damaged catalogs fall back to recovery records with a visible warning");
        string other = Path.Combine(Histories.HistoriesRoot(config), "profiles", "Other");
        using (OperationContext.Current.UseDevice(Devices.OPXY)) Backups.RunBackup(card, other, null, false, config);
        Assert(M8SampleCheck.Run(song, null, config).BackupsSearched == 1, "sample search excludes other device types");
        Throws(() => M8SampleCheck.Run(song, Path.Combine(root, "missing"), null), "unavailable card folders do not report every reference missing");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            using (new OperationContext(new TestInteraction(), cancelled.Token).Activate())
                try { M8SampleCheck.Run(song, card, config); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "sample checking honors cancellation");
        }
        var parsed = M8SongJson.Parse("{\"schema\":1,\"song\":{\"instruments\":[{\"sample\":\"/Samples/ki\\u202Eck.wav\"}]}}");
        Assert(parsed.Instruments[0].Sample == "/Samples/kick.wav" && parsed.Instruments[0].RawSample.Contains("\u202E"), "display sanitization preserves the raw reference for safe lookup");
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using GearBackup;

partial class Tests
{
    sealed class CardTestReader : IM8SongReader
    {
        public M8SongSummary Read(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name == "broken") throw new M8SongException("Damaged song fixture");
            var song = new M8SongSummary { Name = name };
            if (name == "synth") return song;
            string sample = name == "missing" ? "/Samples/lost.wav" : name == "invalid" ? "/../escape.wav" : name == "relative" ? "Samples/kick.wav" : "/Samples/kick.wav";
            song.Instruments.Add(new M8InstrumentInfo { Slot = 0, Kind = "Sampler", Sample = sample });
            song.Instruments.Add(new M8InstrumentInfo { Slot = 127, Kind = "Sampler", Sample = sample });
            return song;
        }
    }

    static void MakeCardCheckFixture(string root)
    {
        Put(Path.Combine(root, "Songs", "good.m8s"), "song");
        Put(Path.Combine(root, "Bundles", "nested", "missing.M8S"), "song");
        Put(Path.Combine(root, "Songs", "broken.m8s"), "damaged");
        Put(Path.Combine(root, "Songs", "invalid.m8s"), "song");
        Put(Path.Combine(root, "Songs", "synth.m8s"), "song");
        Put(Path.Combine(root, "Samples", "kick.wav"), "sample");
        Put(Path.Combine(root, "Songs", "notes.txt"), "not a song");
    }

    [Test("M8 card-wide sample check")]
    static void TestM8CardCheck(string root)
    {
        MakeCardCheckFixture(root);
        int progress = 0;
        var report = M8CardCheck.Run(root, new CardTestReader(), (count, path) => progress = count);
        Assert(report.Songs.Count == 5 && progress == 5, "finds all songs recursively, including bundles and upper-case extensions");
        Assert(report.Songs.Count(s => s.Missing > 0) == 1 && report.Songs.Sum(s => s.Missing) == 1, "reports missing references without double-counting instrument slots");
        var missing = report.Songs.Single(s => s.Missing > 0);
        Assert(missing.Relative == @"Bundles\nested\missing.M8S" && missing.Samples.Samples[0].Slots == "00, 7F", "locates the affected song and includes unused sampler slots");
        Assert(report.Songs.Count(s => s.NeedsAttention) == 2 && report.Songs.Single(s => s.Error != null).Error.Contains("Damaged"), "unreadable songs and invalid references cannot be reported as all present");
        Assert(report.Songs.Single(s => s.Relative.EndsWith("good.m8s")).Samples.Samples[0].CardStatus == "Present on card", "checks references from the card root rather than song folder");
        Assert(report.Songs.Single(s => s.Relative.EndsWith("synth.m8s")).Samples.Samples.Count == 0, "songs without samples remain in the results");
        Assert(report.Songs.All(s => s.Samples == null || s.Samples.BackupsSearched == 0), "whole-card checks do not search backup storage");
        Throws(() => M8CardCheck.Run(null, new CardTestReader()), "requires an explicit card root");
        using (var cancellation = new CancellationTokenSource())
        using (new OperationContext(new TestInteraction(), cancellation.Token).Activate())
        {
            bool stopped = false;
            try { M8CardCheck.Run(root, new CardTestReader(), (count, path) => cancellation.Cancel()); }
            catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "cancellation stops scanning between songs");
        }
        Assert(File.ReadAllText(Path.Combine(root, "Songs", "good.m8s")) == "song" && File.ReadAllText(Path.Combine(root, "Samples", "kick.wav")) == "sample", "checking leaves songs and samples unchanged");
    }

    [Test("M8 card-wide relative sample paths")]
    static void TestM8CardRelativePaths(string root)
    {
        Put(Path.Combine(root, "Songs", "Demos", "relative.m8s"), "song");
        Put(Path.Combine(root, "Songs", "Demos", "Samples", "kick.wav"), "sample");
        var report = M8CardCheck.Run(root, new CardTestReader());
        Assert(report.Songs.Count == 1 && report.Songs[0].Missing == 0 && !report.Songs[0].NeedsAttention,
            "whole-card scan passes each song's real location when resolving relative sample paths");
        Assert(report.Songs[0].Samples.Samples[0].Relative == @"Songs\Demos\Samples\kick.wav", "card scan records the resolved card-relative path");
    }
}

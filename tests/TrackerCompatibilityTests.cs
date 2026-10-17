using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using GearBackup;

partial class Tests
{
    static string TrackerFixture(string name) { return Path.Combine("tests", "fixtures", "tracker", name.Replace('/', Path.DirectorySeparatorChar)); }

    [Test("Tracker compatibility: pinned upstream fixture hashes")]
    static void TestTrackerFixtureHashes()
    {
        var entries = (object[])new JavaScriptSerializer().DeserializeObject(File.ReadAllText(TrackerFixture("sources.json")));
        Assert(entries.Length == 11, "the fixture manifest accounts for nine binaries and both licenses");
        foreach (Dictionary<string, object> entry in entries)
        {
            string path = TrackerFixture((string)entry["File"]);
            Assert(new FileInfo(path).Length == Convert.ToInt64(entry["Bytes"]) && FileCopy.HashFile(path) == (string)entry["Sha256"],
                "pinned fixture is unchanged: " + entry["File"]);
        }
    }

    [Test("Tracker compatibility: device-saved instruments")]
    static void TestTrackerDeviceInstruments()
    {
        // Upstream pti-test.md identifies these as Tracker firmware 1.5.0b2 saves.
        var expected = new[] {
            new { File = "default.pti", Name = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcde", Frames = 11025, Mode = "One shot", Wave = false },
            new { File = "forward-loop.pti", Name = "test", Frames = 11025, Mode = "Forward loop", Wave = false },
            new { File = "wavetable.pti", Name = "test", Frames = 11025, Mode = "Wavetable", Wave = true },
            new { File = "granular.pti", Name = "test", Frames = 11025, Mode = "Granular", Wave = false },
            new { File = "tone-10ms.pti", Name = "test-10ms", Frames = 441, Mode = "Granular", Wave = false }
        };
        foreach (var item in expected)
        {
            var instrument = TrackerInstrumentFile.Read(TrackerFixture(item.File));
            Assert(instrument.SampleName == item.Name && instrument.Frames == item.Frames && instrument.Channels == 1 &&
                instrument.AudioBytes == item.Frames * 2 && instrument.PlayMode == item.Mode && instrument.Wavetable == item.Wave && !instrument.Legacy,
                "device-saved sample metadata and mode: " + item.File);
        }
    }

    [Test("Tracker compatibility: real project and last pattern step")]
    static void TestTrackerDeviceProject(string root)
    {
        var project = TrackerProjectFile.Read(File.ReadAllBytes(TrackerFixture("chord-arp/project.mt")));
        Assert(project.Name == "1 chord arp test data" && project.Tempo == 130 && project.FirmwareVersion == "1.5.0.255" &&
            project.Playlist.SequenceEqual(new[] { 1 }) && !project.Truncated, "legacy device project name, tempo, versions and playlist");
        Assert(Math.Abs(TrackerProjectFile.Read(File.ReadAllBytes(TrackerFixture("bpm-131.mt"))).Tempo - 131) < 0.001 &&
            Math.Abs(TrackerProjectFile.Read(File.ReadAllBytes(TrackerFixture("bpm-40.1.mt"))).Tempo - 40.1) < 0.001,
            "independent upstream BPM test saves retain integer and fractional tempo");
        byte[] bytes = File.ReadAllBytes(TrackerFixture("chord-arp/patterns/pattern_01.mtp"));
        var pattern = TrackerPatternFile.Read(bytes);
        Assert(pattern.TrackCount == 8 && pattern.Tracks.All(t => t.Length == 32) && pattern.NoteSteps == 64,
            "the stored length 31 means 32 played steps on all eight tracks");
        Assert(pattern.Tracks[0].Steps[31].Note == 48 && pattern.Tracks[2].Steps[1].Fx0Type == 14 &&
            pattern.Tracks[2].Steps[1].Fx0Value == 2 && pattern.Tracks[2].Steps[1].Fx1Type == 21 &&
            pattern.Tracks[2].Steps[1].Fx1Value == 1, "last played note and both effect columns survive parsing");
        // A derived fixture exists only inside this run: put a missing instrument on the last played step.
        bytes[28 + 1 + 31 * 6 + 1] = 47;
        File.Copy(TrackerFixture("chord-arp/project.mt"), Path.Combine(root, "project.mt"));
        string patterns = Folder(root, "patterns"), instruments = Folder(root, "instruments");
        File.WriteAllBytes(Path.Combine(patterns, "pattern_01.mtp"), bytes);
        File.Copy(TrackerFixture("default.pti"), Path.Combine(instruments, "1 Tone.pti"));
        var report = TrackerProjectCheck.Inspect(root);
        Assert(report.Missing.Count == 1 && report.Missing[0].Slot == 48 && report.Missing[0].InSong,
            "missing-instrument inspection includes the last played step");
    }

    [Test("Tracker compatibility: corruption of real fixture layouts")]
    static void TestTrackerFixtureCorruption(string root)
    {
        byte[] pattern = File.ReadAllBytes(TrackerFixture("chord-arp/patterns/pattern_01.mtp"));
        foreach (int encoded in new[] { 0, 127 })
        {
            byte[] changed = (byte[])pattern.Clone(); changed[28] = (byte)encoded;
            Assert(TrackerPatternFile.Read(changed).Tracks[0].Length == encoded + 1, "one-step and 128-step boundaries use zero-based lengths");
        }
        foreach (int encoded in new[] { 128, 255 })
        {
            byte[] changed = (byte[])pattern.Clone(); changed[28] = (byte)encoded;
            Throws(() => TrackerPatternFile.Read(changed), "out-of-range stored length is refused");
        }
        Array.Resize(ref pattern, pattern.Length - 1);
        Throws(() => TrackerPatternFile.Read(pattern), "a truncated device pattern is refused");
        byte[] instrument = File.ReadAllBytes(TrackerFixture("tone-10ms.pti"));
        Array.Resize(ref instrument, 391);
        string path = Path.Combine(root, "truncated.pti"); File.WriteAllBytes(path, instrument);
        Throws(() => TrackerInstrumentFile.Read(path), "a truncated device instrument header is refused");
    }
}

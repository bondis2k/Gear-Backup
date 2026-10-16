using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GearBackup;

// The Tracker project readers and the missing-instrument check, on files built to the layouts in Polyend's tracker-lib.
partial class Tests
{
    static byte[] TrackerProject(string name = "Jam", float tempo = 120f, int[] playlist = null, byte structure = 17, string[] trackNames = null)
    {
        var data = new byte[2200];
        data[0] = (byte)'M'; data[1] = (byte)'T'; data[2] = 1; data[4] = 1; data[5] = 8; data[6] = 0; data[7] = 0; data[8] = structure;
        int p = 0; foreach (int pattern in playlist ?? new int[0]) data[16 + p++] = (byte)pattern;
        BitConverter.GetBytes(tempo).CopyTo(data, 0x1C0);
        int nameOffset = structure > 16 ? 0x810 : structure > 15 ? 0x80C : 0x600;
        Encoding.ASCII.GetBytes(name).CopyTo(data, nameOffset);
        for (int i = 0; trackNames != null && i < trackNames.Length; i++) Encoding.ASCII.GetBytes(trackNames[i]).CopyTo(data, i < 8 ? 0x428 + i * 21 : 0x603 + (i - 8) * 8);
        return data;
    }

    // A 16-track pattern; each given (track, step, note, instrument) is set, and each track plays `length` steps.
    static byte[] TrackerPattern(int tracks, int length, params int[][] steps)
    {
        var data = new byte[28 + (1 + 128 * 6) * tracks + 4];
        data[0] = (byte)'P'; data[1] = (byte)'M'; data[2] = 2;
        for (int t = 0; t < tracks; t++)
        {
            int start = 28 + t * (1 + 128 * 6);
            data[start] = (byte)length;
            for (int s = 0; s < 128; s++) data[start + 1 + s * 6] = 0xFF;      // empty step: note -1
        }
        foreach (int[] step in steps)
        {
            int at = 28 + step[0] * (1 + 128 * 6) + 1 + step[1] * 6;
            data[at] = (byte)step[2]; data[at + 1] = (byte)step[3];
            if (step.Length > 5) { data[at + 4] = (byte)step[4]; data[at + 5] = (byte)step[5]; }       // first effect
            if (step.Length > 7) { data[at + 2] = (byte)step[6]; data[at + 3] = (byte)step[7]; }       // second effect
        }
        return data;
    }

    static void TrackerInstrument(string path, string sample, int frames, int channels, bool wavetable = false, long audioBytes = -1)
    {
        var data = new byte[394 + (audioBytes >= 0 ? audioBytes : frames * 2L * channels)];
        data[0] = (byte)'T'; data[1] = (byte)'I'; data[20] = (byte)(wavetable ? 1 : 0);
        Encoding.ASCII.GetBytes(sample).CopyTo(data, 21);
        BitConverter.GetBytes((uint)frames).CopyTo(data, 60);
        data[76] = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, data);
    }

    [Test("Tracker files: project, pattern and instrument", Folder = "helpers")]
    static void TestTrackerFiles(string dir)
    {
        var names = new string[16]; for (int i = 0; i < 16; i++) names[i] = "T" + (i + 1);
        TrackerProjectFile project = TrackerProjectFile.Read(TrackerProject("My Jam", 133.5f, new[] { 3, 1, 2 }, 17, names));
        Assert(project.Name == "My Jam" && Math.Abs(project.Tempo - 133.5f) < 0.01 && string.Join(",", project.Playlist) == "3,1,2", "the project's name, tempo and song order are read");
        Assert(project.FirmwareVersion == "1.8.0.0" && project.StructureVersion.StartsWith("17."), "the versions are read: " + project.StructureVersion);
        Assert(project.TrackNames.Count == 16 && project.TrackNames[0] == "T1" && project.TrackNames[8] == "T9", "all sixteen track names are read");
        Assert(TrackerProjectFile.Read(TrackerProject("Old", 100f, null, 15)).TrackNames.Count == 8, "an original-Tracker project has eight track names");
        Assert(TrackerProjectFile.Read(TrackerProject("Odd", 5f)).Tempo == 0, "an impossible tempo is reported as unknown");
        var hostile = TrackerProject("A\u0007B\0hidden");
        Assert(TrackerProjectFile.Read(hostile).Name == "A\u0007B".Replace("\u0007", ""), "control characters are dropped from names");
        Throws(() => TrackerProjectFile.Read(new byte[100]), "a short file is not a project");
        var wrong = TrackerProject(); wrong[0] = (byte)'X';
        Throws(() => TrackerProjectFile.Read(wrong), "a wrong signature is refused");

        TrackerPatternFile pattern = TrackerPatternFile.Read(TrackerPattern(16, 16, new[] { 0, 0, 60, 4 }, new[] { 1, 15, 62, 7 }, new[] { 2, 20, 64, 9 }, new[] { 3, 0, 255, 11 }, new[] { 4, 3, 70, 50 }));
        Assert(pattern.TrackCount == 16 && pattern.NoteSteps == 3 && string.Join(",", pattern.Instruments) == "4,7,50", "only notes inside a track's length name an instrument: " + string.Join(",", pattern.Instruments));
        Assert(TrackerPatternFile.Read(TrackerPattern(12, 128)).TrackCount == 12, "a twelve-track pattern is read");
        var truncated = TrackerPattern(16, 16); Array.Resize(ref truncated, truncated.Length - 100);
        Throws(() => TrackerPatternFile.Read(truncated), "a pattern of an odd size is refused");

        string mono = Path.Combine(dir, "tracker-instruments", "mono.pti"), stereo = Path.Combine(dir, "tracker-instruments", "stereo.pti");
        TrackerInstrument(mono, "kick", 1000, 1); TrackerInstrument(stereo, "pad", 500, 2);
        TrackerInstrumentFile one = TrackerInstrumentFile.Read(mono);
        Assert(one.SampleName == "kick" && one.Frames == 1000 && one.Channels == 1 && one.PlayMode == "Forward loop", "a mono instrument is read");
        Assert(TrackerInstrumentFile.Read(stereo).Channels == 2, "a stereo instrument is read");
        string odd = Path.Combine(dir, "tracker-instruments", "odd.pti"); TrackerInstrument(odd, "odd", 1000, 1, false, 1234);
        Assert(TrackerInstrumentFile.Read(odd).Channels == 0, "audio of an unexpected size has no channel count");
        File.WriteAllBytes(Path.Combine(dir, "tracker-instruments", "short.pti"), new byte[50]);
        Throws(() => TrackerInstrumentFile.Read(Path.Combine(dir, "tracker-instruments", "short.pti")), "a short instrument file is refused");
        var wrongInstrument = new byte[394]; wrongInstrument[0] = (byte)'Q';
        File.WriteAllBytes(Path.Combine(dir, "tracker-instruments", "wrong.pti"), wrongInstrument);
        Throws(() => TrackerInstrumentFile.Read(Path.Combine(dir, "tracker-instruments", "wrong.pti")), "a wrong signature is refused");
    }

    [Test("Tracker project check", Folder = "helpers")]
    static void TestTrackerProjectCheck(string dir)
    {
        string root = Folder(dir, "tracker-check-card"), project = Path.Combine(root, "Projects", "User", "Jam");
        Directory.CreateDirectory(Path.Combine(project, "patterns"));
        File.WriteAllBytes(Path.Combine(project, "project.mt"), TrackerProject("Jam", 120f, new[] { 1, 2, 9 }));
        // pattern 1 plays instruments 1, 3 and 4 (stored 0, 2, 3); pattern 2 plays 6 and a MIDI instrument; pattern 3 is not in the song and plays 7.
        File.WriteAllBytes(Path.Combine(project, "patterns", "pattern_01.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 0 }, new[] { 0, 1, 62, 2 }, new[] { 1, 0, 64, 3 }));
        File.WriteAllBytes(Path.Combine(project, "patterns", "pattern_02.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 5 }, new[] { 2, 4, 60, 50 }));
        File.WriteAllBytes(Path.Combine(project, "patterns", "pattern_03.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 6 }));
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_01.pti"), "kick", 1000, 1);
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_03.pti"), "snare", 800, 2);
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_06.pti"), "empty", 0, 1);
        File.WriteAllBytes(Path.Combine(project, "instruments", "instrument_09.pti"), new byte[10]);

        TrackerProjectReport report = null;
        WithUi(() => report = TrackerProjectCheck.Inspect(project));
        Assert(report.Error == null && report.Project.Name == "Jam" && report.Patterns.Count == 3 && report.Instruments.Count == 4, "the project's patterns and instruments are listed");
        Assert(report.UnreadableFiles == 1 && report.Instruments.Exists(i => i.Slot == 9 && i.Error != null), "an unreadable instrument file is reported, not thrown");
        var missing = report.Missing;
        Assert(missing.Count == 3 && missing[0].Slot == 4 && missing[0].Reason.Contains("no instrument file") && missing[0].Patterns[0] == 1 && missing[0].InSong,
            "instrument 4 is played by pattern 1 but has no file: " + string.Join("; ", missing.ConvertAll(m => m.Slot + " " + m.Reason)));
        Assert(missing[1].Slot == 6 && missing[1].Reason.Contains("no usable sample") && missing[1].InSong, "an instrument file with no audio counts as missing");
        Assert(missing[2].Slot == 7 && !missing[2].InSong, "a missing instrument used only by a pattern outside the song is marked as such");
        Assert(report.MidiOrSynthInstruments == 1, "MIDI and synth instruments are counted, not looked for");
        Assert(report.MissingPatterns.Count == 1 && report.MissingPatterns[0].Contains("pattern 9"), "a song position without a pattern file is reported: " + string.Join("|", report.MissingPatterns));

        // Once the files are in place nothing is missing.
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_04.pti"), "hat", 100, 1);
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_06.pti"), "bass", 100, 1);
        TrackerInstrument(Path.Combine(project, "instruments", "instrument_07.pti"), "lead", 100, 1);
        WithUi(() => report = TrackerProjectCheck.Inspect(project));
        Assert(report.Missing.Count == 0, "with every played instrument present nothing is missing");

        // Numbering from zero: instrument_00.pti is the first instrument.
        string zero = Path.Combine(root, "Projects", "User", "Zero");
        Directory.CreateDirectory(Path.Combine(zero, "patterns"));
        File.WriteAllBytes(Path.Combine(zero, "project.mt"), TrackerProject("Zero", 120f, new[] { 1 }));
        File.WriteAllBytes(Path.Combine(zero, "patterns", "pattern_01.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 0 }));
        TrackerInstrument(Path.Combine(zero, "instruments", "instrument_00.pti"), "kick", 100, 1);
        WithUi(() => report = TrackerProjectCheck.Inspect(zero));
        Assert(report.Missing.Count == 0 && report.Instruments[0].Slot == 1, "a folder that counts from zero is understood");

        // A damaged pattern and a damaged project.
        File.WriteAllBytes(Path.Combine(project, "patterns", "pattern_02.mtp"), new byte[40]);
        WithUi(() => report = TrackerProjectCheck.Inspect(project));
        Assert(report.UnreadableFiles >= 2 && report.Patterns.Exists(p => p.Number == 2 && p.Error != null), "a damaged pattern is reported");
        File.WriteAllBytes(Path.Combine(project, "project.mt"), new byte[20]);
        WithUi(() => report = TrackerProjectCheck.Inspect(project));
        Assert(report.Error != null && report.Error.Contains("project.mt") && report.Patterns.Count == 0, "a damaged project file stops the inspection with the reason");

        // Finding projects below a card or Projects folder.
        List<string> found = null;
        WithUi(() => found = TrackerProjectCheck.FindProjects(root));
        Assert(found.Count == 2 && found[0].EndsWith("Jam") && found[1].EndsWith("Zero"), "projects are found below the card: " + string.Join("|", found));
        WithUi(() => found = TrackerProjectCheck.FindProjects(zero));
        Assert(found.Count == 1, "a project folder lists itself");
        WithUi(() => found = TrackerProjectCheck.FindProjects(Path.Combine(root, "nothing-here")));
        Assert(found.Count == 0, "a missing folder holds no projects");
    }

    [Test("Tracker pattern steps and card summary", Folder = "helpers")]
    static void TestTrackerStepsAndSummary(string dir)
    {
        // Track 1 step 1: note 60 on instrument 4, tempo 60 (shown 120) and volume 80; step 2 is a cut; track 2 step 1 a fade.
        TrackerPatternFile pattern = TrackerPatternFile.Read(TrackerPattern(16, 16, new[] { 0, 0, 60, 3, 15, 60, 18, 80 }, new[] { 0, 1, 253, 0 }, new[] { 1, 0, 254, 0 }));
        TrackerStep first = pattern.Tracks[0].Steps[0];
        Assert(first.HasNote && first.Note == 60 && first.Instrument == 3 && TrackerFx.Text(first.Fx0Type, first.Fx0Value) == "T120" && TrackerFx.Text(first.Fx1Type, first.Fx1Value) == "V80",
            "a step's note, instrument and two effects are read: " + TrackerFx.Text(first.Fx0Type, first.Fx0Value) + "|" + TrackerFx.Text(first.Fx1Type, first.Fx1Value));
        Assert(!pattern.Tracks[0].Steps[1].HasNote && pattern.Tracks[0].Steps[1].Note == -3 && pattern.Tracks[1].Steps[0].Note == -2, "cut and fade steps are not notes");
        Assert(pattern.Tracks[0].Length == 16 && pattern.Tracks[0].Steps[127].IsEmpty, "a track keeps its length and its empty steps");
        Assert(TrackerFx.Text(0, 5) == "" && TrackerFx.Text(17, 60) == "I10" && TrackerFx.Text(31, 25) == "P-25" && TrackerFx.Text(25, 0) == "S1" && TrackerFx.Text(40, 30) == "U6", "effect values are shown as the Tracker shows them");
        Assert(TrackerFx.Name(15) == "Tempo" && TrackerFx.Name(99).Contains("Unknown") && TrackerFx.Symbol(18) == "V" && TrackerFx.Symbol(99) == "?", "effect names and symbols are looked up");

        string root = Folder(dir, "tracker-summary-card");
        Func<string, int, string> make = (name, instrumentsPresent) => {
            string folder = Path.Combine(root, "Projects", name);
            Directory.CreateDirectory(Path.Combine(folder, "patterns"));
            File.WriteAllBytes(Path.Combine(folder, "project.mt"), TrackerProject(name, 120f, new[] { 1 }));
            File.WriteAllBytes(Path.Combine(folder, "patterns", "pattern_01.mtp"), TrackerPattern(16, 16, new[] { 0, 0, 60, 0 }, new[] { 0, 1, 60, 1 }));
            for (int i = 1; i <= instrumentsPresent; i++) TrackerInstrument(Path.Combine(folder, "instruments", "instrument_0" + i + ".pti"), "s" + i, 100, 1);
            return folder;
        };
        make("Good", 2);
        string bad = make("Bad", 1);
        File.SetLastWriteTime(Path.Combine(bad, "project.mt"), DateTime.Now.AddDays(1));
        TrackerCardSummary summary = null;
        WithUi(() => summary = TrackerProjectCheck.Summarize(root));
        Assert(summary.Projects == 2 && summary.Checked == 2 && summary.NewestName == "Bad", "the card's projects are counted and the newest named");
        Assert(summary.Problems.Count == 1 && summary.Problems[0].StartsWith("Bad: 1 instrument played without a usable file"), "a project with a missing instrument is listed: " + string.Join("|", summary.Problems));
        string text = summary.Describe();
        Assert(text.Contains("Tracker projects: 2") && text.Contains("Needs attention:") && text.Contains("Bad:"), "the summary reads: " + text);
        WithUi(() => summary = TrackerProjectCheck.Summarize(root, null, 1));
        Assert(summary.Checked == 1 && summary.Describe().Contains("The first 1 projects were checked"), "only the newest projects are checked when there are many");
        make("Bad", 2);
        WithUi(() => summary = TrackerProjectCheck.Summarize(root));
        Assert(summary.Problems.Count == 0 && summary.Describe().Contains("no missing instruments"), "a card with nothing wrong says so");
        string empty = Folder(dir, "tracker-summary-empty");
        WithUi(() => summary = TrackerProjectCheck.Summarize(empty));
        Assert(summary.Projects == 0 && summary.Describe().Contains("No Tracker projects"), "a card without projects says so");
    }
}

using System;
using System.IO;
using System.Linq;

partial class Tests
{
    const string SampleSongJson = "{\"schema\":1,\"song\":{\"name\":\"DEMO\",\"directory\":\"/Songs/\",\"firmware\":\"6.2.0\",\"tempo\":123.5,\"transpose\":12,\"quantize\":2," +
        "\"key\":3,\"songRows\":4,\"trackRows\":[4,2,0,0,0,0,0,1],\"chains\":5,\"phrases\":6,\"tables\":1," +
        "\"instruments\":[{\"slot\":0,\"kind\":\"Sampler\",\"name\":\"KICK\",\"sample\":\"/Samples/kick.wav\"},{\"slot\":9,\"kind\":\"FM Synth\",\"name\":\"\",\"sample\":\"\"}]," +
        "\"mixer\":{\"master\":200,\"tracks\":[1,2,3,4,5,6,7,8],\"chorus\":10,\"delay\":20,\"reverb\":30}}}";

    // Fixtures come from the m8-file-parser crate's example songs (tests/fixtures/m8).
    static string M8Fixture(string name) { return Path.Combine("tests", "fixtures", "m8", name); }

    static NativeM8SongReader NativeReaderOrNull()
    {
        var reader = new NativeM8SongReader(Path.Combine("native", "m8-reader", "target", "release", NativeM8SongReader.LibraryName));
        if (File.Exists(reader.LibraryPath)) return reader;
        Console.WriteLine("  (native M8 reader not built; skipping its checks. Install Rust and run build\\build-native.ps1.)");
        return null;
    }

    static void TestM8Song()
    {
        M8SongSummary song = M8SongJson.Parse(SampleSongJson);
        Assert(song.Name == "DEMO" && song.Firmware == "6.2.0" && song.Tempo == 123.5 && song.Transpose == 12 && song.Quantize == 2 && song.SongRows == 4,
            "the reader's JSON becomes a song summary");
        Assert(song.TrackRows.SequenceEqual(new[] { 4, 2, 0, 0, 0, 0, 0, 1 }) && song.Chains == 5 && song.Phrases == 6 && song.Tables == 1 &&
            song.MasterVolume == 200 && song.TrackVolumes[7] == 8 && song.ReverbVolume == 30, "track usage, counts and mixer values are read");
        Assert(song.Instruments.Count == 2 && song.Instruments[0].Sample == "/Samples/kick.wav" && song.Instruments[1].Slot == 9 && song.Instruments[1].Kind == "FM Synth",
            "instruments keep their slot, type, name and sample");
        M8SongSummary dirty = M8SongJson.Parse("{\"schema\":1,\"song\":{\"name\":\"A&B\\u202E\\u0007\\u200Bx\",\"instruments\":[{\"slot\":1,\"kind\":\"Sampler\",\"name\":\"K\\r\\nICK\",\"sample\":\"/S/\\u0000a.wav\"}]}}");
        Assert(dirty.Name == "A&Bx" && dirty.Instruments[0].Name == "KICK" && dirty.Instruments[0].Sample == "/S/a.wav",
            "control, zero-width and bidirectional characters are removed from song text, while & and letters stay");
        string reported = null;
        Action<string> fails = json => { try { M8SongJson.Parse(json); } catch (M8SongException ex) { reported = ex.Message; return; } reported = "no exception"; };
        fails("{\"schema\":1,\"error\":\"Not a readable M8 song\"}");
        Assert(reported == "Not a readable M8 song", "an error result from the reader is reported to the user");
        fails("not json");
        Assert(reported.Contains("unexpected"), "a malformed result is reported, not thrown as a raw parser error");
        fails("{\"schema\":2,\"song\":{}}");
        Assert(reported.Contains("unexpected"), "a different schema version is rejected");

        var missing = new NativeM8SongReader(Path.Combine(Path.GetTempPath(), "gearbackup-missing", "m8_reader.dll"));
        string missingMessage = null;
        try { missing.Read(M8Fixture("TEST-FILE.m8s")); } catch (M8SongException ex) { missingMessage = ex.Message; }
        Assert(missingMessage != null && missingMessage.Contains("m8_reader.dll"), "a missing native reader gives a clear message");
        // The load path can no longer be redirected through the environment.
        string before = Environment.GetEnvironmentVariable("GEARBACKUP_M8_READER");
        Environment.SetEnvironmentVariable("GEARBACKUP_M8_READER", Path.Combine(Path.GetTempPath(), "elsewhere", "evil.dll"));
        try { Assert(new NativeM8SongReader().LibraryPath == Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "m8_reader.dll"), "the reader's DLL location ignores environment overrides"); }
        finally { Environment.SetEnvironmentVariable("GEARBACKUP_M8_READER", before); }
        try { new NativeM8SongReader().Read(""); missingMessage = null; } catch (M8SongException ex) { missingMessage = ex.Message; }
        Assert(missingMessage != null, "an empty path is refused");

        NativeM8SongReader native = NativeReaderOrNull();
        if (native == null) return;
        M8SongSummary full = native.Read(M8Fixture("TEST-FILE.m8s"));
        Assert(full.Firmware.Length > 0 && full.Tempo > 0 && full.TrackRows.Length == 8 && full.Instruments.Count > 0, "the native reader reads a real song");
        Assert(full.Instruments.All(i => i.Kind.Length > 0 && i.Slot >= 0 && i.Slot < 128), "every listed instrument has a type and a valid slot");
        M8SongSummary empty = native.Read(M8Fixture("V4EMPTY.m8s"));
        Assert(empty.SongRows == 0 && empty.Instruments.Count == 0 && empty.TrackRows.All(rows => rows == 0), "an empty song reports no rows or instruments");

        string scratch = Path.Combine(Environment.CurrentDirectory, ".test-runs", "m8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string notSong = Path.Combine(scratch, "notes.m8s");
            File.WriteAllText(notSong, "this is not an M8 song");
            ThrowsSong(() => native.Read(notSong), "a file that is not a song is refused");
            ThrowsSong(() => native.Read(Path.Combine(scratch, "gone.m8s")), "a missing song is refused");
            string cut = Path.Combine(scratch, "cut.m8s");
            File.WriteAllBytes(cut, File.ReadAllBytes(M8Fixture("TEST-FILE.m8s")).Take(50000).ToArray());
            ThrowsSong(() => native.Read(cut), "a song cut short is refused, not a crash");
            ThrowsSong(() => native.Read(scratch), "a folder is refused");
            // Non-ASCII folder names must reach the DLL intact, so the path is passed as UTF-16.
            string unicode = Path.Combine(scratch, "sång æøå");
            Directory.CreateDirectory(unicode);
            string copy = Path.Combine(unicode, "TEST.m8s");
            File.Copy(M8Fixture("TEST-FILE.m8s"), copy);
            Assert(native.Read(copy).Instruments.Count == full.Instruments.Count, "songs in folders with non-ASCII names can be read");
            // Song-sample dependency restore parses firmware-6 songs separately; both readers must agree on the samples.
            if (full.Firmware.StartsWith("6"))
            {
                var expected = SampleDependencies.SamplePaths(copy);
                var actual = full.Instruments.Where(i => i.Sample.Length > 0).Select(i => i.Sample.Replace('/', '\\').TrimStart('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Assert(expected.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                    "the native reader finds the same samples as the existing dependency reader");
            }
        }
        finally { RemoveFixtures(scratch); }
    }

    static void ThrowsSong(Action action, string message)
    {
        bool threw = false;
        try { action(); } catch (M8SongException) { threw = true; }
        Assert(threw, message);
    }
}

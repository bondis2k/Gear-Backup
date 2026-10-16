using System.Collections.Generic;

// What Gear Backup knows about a Dirtywave M8 song. The values come from the native reader's JSON
// summary (native/m8-reader), never from the parser crate's own types, so the two can change separately.
internal sealed class M8SongSummary
{
    public string Name = "", Directory = "", Firmware = "";
    public double Tempo;
    public int Transpose, Quantize, Key;
    // Rows of the song grid that hold anything, and how many rows each of the 8 tracks uses.
    public int SongRows;
    public int[] TrackRows = new int[8];
    public int Chains, Phrases, Tables;
    public List<M8InstrumentInfo> Instruments = new List<M8InstrumentInfo>();
    public int MasterVolume, ChorusVolume, DelayVolume, ReverbVolume;
    public int[] TrackVolumes = new int[8];
}

internal sealed class M8InstrumentInfo
{
    public int Slot;
    public string Kind = "", Name = "", Sample = "";
}

// Raised when a song can't be read: the native reader is missing, or the file isn't a readable M8 song.
internal sealed class M8SongException : System.Exception
{
    public M8SongException(string message) : base(message) { }
}

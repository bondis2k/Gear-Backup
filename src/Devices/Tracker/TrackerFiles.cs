// Read-only readers for the Polyend Tracker's project (.mt), pattern (.mtp) and instrument (.pti) files. The layouts follow
// Polyend's open-source tracker-lib (https://github.com/polyend/tracker-lib, MIT): what is read here is the signature, a few
// fields of the project, which instrument each step plays, and the sample an instrument holds. Nothing is written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GearBackup
{
    // A file that is not a readable Tracker file of the expected kind.
    internal sealed class TrackerFileException : IOException
    {
        public TrackerFileException(string message) : base(message) { }
    }

    static class TrackerFiles
    {
        internal const int ProjectLimit = 1 << 20, PatternLimit = 1 << 20, InstrumentHeaderSize = 392;

        internal static ushort U16(byte[] b, int i) { return (ushort)(b[i] | b[i + 1] << 8); }
        internal static uint U32(byte[] b, int i) { return (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24); }

        // Text up to the first NUL, with control and format characters removed so a hostile name cannot disturb the display.
        internal static string Text(byte[] data, int offset, int length)
        {
            if (offset < 0 || offset >= data.Length) return "";
            length = Math.Min(length, data.Length - offset);
            int end = 0;
            while (end < length && data[offset + end] != 0) end++;
            var text = new StringBuilder(end);
            foreach (char c in Encoding.ASCII.GetString(data, offset, end))
                if (c >= ' ' && c < 127) text.Append(c);
            return text.ToString().Trim();
        }

        internal static string Version(byte[] data, int offset)
        {
            return data[offset] + "." + data[offset + 1] + "." + data[offset + 2] + "." + data[offset + 3];
        }
    }

    // project.mt: the song's playlist, the tempo and the names.
    internal sealed class TrackerProjectFile
    {
        public string Name = "", FirmwareVersion = "", StructureVersion = "";
        public float Tempo;
        // Pattern numbers in song order, up to the first empty entry.
        public List<int> Playlist = new List<int>();
        public List<string> TrackNames = new List<string>();
        // Names or tempo the file was too short to hold.
        public bool Truncated;

        const int PlaylistOffset = 16, PlaylistSize = 255, TempoOffset = 0x1C0, TrackNameOffset = 0x428, ShortTrackNameOffset = 0x603;

        internal static TrackerProjectFile Read(byte[] data)
        {
            if (data.Length < PlaylistOffset + PlaylistSize + 1 || data[0] != 'M' || data[1] != 'T')
                throw new TrackerFileException("Not a Tracker project file (missing the MT signature).");
            var project = new TrackerProjectFile { FirmwareVersion = TrackerFiles.Version(data, 4), StructureVersion = TrackerFiles.Version(data, 8) };
            for (int i = 0; i < PlaylistSize && data[PlaylistOffset + i] != 0; i++) project.Playlist.Add(data[PlaylistOffset + i]);
            if (data.Length >= TempoOffset + 4)
            {
                float tempo = BitConverter.ToSingle(data, TempoOffset);
                project.Tempo = tempo >= 10 && tempo <= 1000 ? tempo : 0;
            }
            else project.Truncated = true;
            int version = data[8];
            // The project name moved when the Tracker+ and Mini structure arrived; an older (original Tracker) file keeps it lower.
            int nameOffset = version > 16 ? 0x810 : version > 15 ? 0x80C : 0x600;
            bool legacy = version <= 15;
            if (data.Length >= nameOffset + 32) project.Name = TrackerFiles.Text(data, nameOffset, 32);
            else project.Truncated = true;
            for (int i = 0; i < 8; i++) project.TrackNames.Add(TrackerFiles.Text(data, TrackNameOffset + i * 21, 21));
            if (!legacy) for (int i = 0; i < 8; i++) project.TrackNames.Add(TrackerFiles.Text(data, ShortTrackNameOffset + i * 8, 8));
            return project;
        }
    }

    // One step of a pattern: a note (0-127; negative means no new note), the instrument it plays, and up to two effects.
    internal struct TrackerStep
    {
        public sbyte Note;
        public byte Instrument;
        public byte Fx0Type, Fx0Value, Fx1Type, Fx1Value;
        public bool HasNote { get { return Note >= 0; } }
        public bool HasFx { get { return Fx0Type != 0 || Fx1Type != 0; } }
        public bool IsEmpty { get { return Note == -1 && !HasFx; } }
    }

    internal sealed class TrackerTrack
    {
        // How many of the 128 steps the track plays before it starts over.
        public int Length;
        public TrackerStep[] Steps = new TrackerStep[128];
    }

    // The effects a step can carry, by the number stored in the pattern (from tracker-lib).
    static class TrackerFx
    {
        static readonly string[] Symbols = { "-", "!", "m", "R", "C", "n", "i", "v", "a", "b", "c", "d", "e", "x", "0", "T", "x", "I", "V", "G", "q", "A", "p", "g", "h", "S", "r", "L", "H", "B", "s", "P", "t", "l", "M", "j", "k", "f", "D", "E", "U", "F", "J" };
        static readonly string[] Names = { "None", "Off", "Micro-move", "Roll", "Chance", "Random note", "Random instrument", "Random volume", "MIDI CC A", "MIDI CC B", "MIDI CC C", "MIDI CC D", "MIDI CC E", "Break pattern", "MIDI chord", "Tempo", "Random FX value", "Swing", "Volume/velocity", "Glide", "Gate length", "Arp", "Position", "Volume LFO", "Panning LFO", "Slice", "Reverse playback", "Low-pass", "High-pass", "Band-pass", "Delay send", "Panning", "Reverb send", "Finetune LFO", "Micro-tune/pitchbend", "Filter LFO", "Position LFO", "MIDI CC F", "Overdrive", "Bit depth", "Tune", "Slide up", "Slide down" };

        internal static string Symbol(int type) { return type >= 0 && type < Symbols.Length ? Symbols[type] : "?"; }
        internal static string Name(int type) { return type >= 0 && type < Names.Length ? Names[type] : "Unknown effect " + type; }

        // The value as the Tracker shows it: some effects are stored offset or doubled (tempo, swing, slice, panning, tune, pitchbend).
        internal static int Shown(int type, int stored)
        {
            switch (type)
            {
                case 15: return stored * 2;
                case 17: return stored - 50;
                case 25: return stored + 1;
                case 31: return stored - 50;
                case 34: return stored - 99;
                case 40: return stored - 24;
                default: return stored;
            }
        }

        // "T120" for a tempo of 120, or an empty text for no effect.
        internal static string Text(int type, int stored)
        {
            if (type == 0) return "";
            return Symbol(type) + Shown(type, stored).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // One pattern: every track's steps, with a summary of which instruments the notes play.
    internal sealed class TrackerPatternFile
    {
        public int TrackCount;
        public int NoteSteps;
        public List<TrackerTrack> Tracks = new List<TrackerTrack>();
        // Instrument numbers used by note steps, zero based as stored (0-47 sample instruments, 48-63 MIDI, 64-66 synth engines).
        public SortedSet<int> Instruments = new SortedSet<int>();

        const int Header = 28, TrackSize = 1 + 128 * 6, StepsPerTrack = 128, CrcSize = 4;

        internal static TrackerPatternFile Read(byte[] data)
        {
            if (data.Length < Header + TrackSize + CrcSize || !((data[0] == 'P' && data[1] == 'M') || (data[0] == 'K' && data[1] == 'S')))
                throw new TrackerFileException("Not a Tracker pattern file (missing the PM signature).");
            int body = data.Length - Header - CrcSize;
            if (body % TrackSize != 0 || body / TrackSize > 32) throw new TrackerFileException("The pattern file has an unexpected size.");
            var pattern = new TrackerPatternFile { TrackCount = body / TrackSize };
            for (int track = 0; track < pattern.TrackCount; track++)
            {
                int start = Header + track * TrackSize;
                var read = new TrackerTrack { Length = Math.Min(Math.Max((int)data[start], 1), StepsPerTrack) };
                for (int step = 0; step < StepsPerTrack; step++)
                {
                    int at = start + 1 + step * 6;
                    // The file stores the second effect first: type, value of effect 2, then of effect 1.
                    read.Steps[step] = new TrackerStep { Note = (sbyte)data[at], Instrument = data[at + 1],
                        Fx1Type = data[at + 2], Fx1Value = data[at + 3], Fx0Type = data[at + 4], Fx0Value = data[at + 5] };
                    if (step < read.Length && read.Steps[step].HasNote)    // empty, fade, cut and off steps play nothing new
                    {
                        pattern.NoteSteps++;
                        pattern.Instruments.Add(read.Steps[step].Instrument);
                    }
                }
                pattern.Tracks.Add(read);
            }
            return pattern;
        }
    }

    // One instrument's file: the sample it holds. A .pti keeps the audio inside the file after its 392-byte header (measured on
    // real Tracker Mini cards); the original Tracker's .mti holds only the settings and the name of a wav file beside it.
    internal sealed class TrackerInstrumentFile
    {
        public string SampleName = "";
        public bool Wavetable;
        public long Frames, AudioBytes;
        // 1 (mono), 2 (stereo), or 0 when the audio size matches neither.
        public int Channels;
        public string PlayMode = "";
        // True for the original Tracker's .mti instrument, whose audio is a wav file in the project's samples folder.
        public bool Legacy;

        static readonly string[] PlayModes = { "One shot", "Forward loop", "Backward loop", "Ping-pong loop", "Slice", "Beat slice", "Wavetable", "Granular" };

        // Only the header is read; the audio itself is not loaded. The original Tracker's .mti files vary in size by firmware
        // (388 and 392 bytes seen), so a legacy file needs only the part with the sample name.
        internal static TrackerInstrumentFile Read(string path)
        {
            PathSafety.RejectRootLinks(path);
            bool legacy = path.EndsWith(".mti", StringComparison.OrdinalIgnoreCase);
            int need = legacy ? 64 : TrackerFiles.InstrumentHeaderSize;
            var header = new byte[TrackerFiles.InstrumentHeaderSize];
            long fileSize;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PathSafety.RejectRootLinks(path);
                fileSize = stream.Length;
                if (fileSize < need) throw new TrackerFileException("The instrument file is too short.");
                int done = 0, read;
                while (done < header.Length && (read = stream.Read(header, done, header.Length - done)) > 0) done += read;
                if (done < need) throw new TrackerFileException("The instrument file is too short.");
            }
            TrackerInstrumentFile instrument = Parse(header, fileSize);
            instrument.Legacy = legacy;
            if (legacy) { instrument.Channels = 0; instrument.Frames = 0; instrument.AudioBytes = 0; }
            return instrument;
        }

        internal static TrackerInstrumentFile Parse(byte[] header, long fileSize)
        {
            if (header.Length < TrackerFiles.InstrumentHeaderSize || header[0] != 'T' || header[1] != 'I')
                throw new TrackerFileException("Not a Tracker instrument file (missing the TI signature).");
            var instrument = new TrackerInstrumentFile { Wavetable = header[20] == 1, SampleName = TrackerFiles.Text(header, 21, 32) };
            instrument.Frames = TrackerFiles.U32(header, 60);
            instrument.AudioBytes = fileSize - TrackerFiles.InstrumentHeaderSize;
            // Audio is 16-bit: 2 bytes a frame for mono, 4 for stereo.
            if (instrument.Frames > 0 && instrument.AudioBytes == instrument.Frames * 2) instrument.Channels = 1;
            else if (instrument.Frames > 0 && instrument.AudioBytes == instrument.Frames * 4) instrument.Channels = 2;
            byte mode = header[76];
            instrument.PlayMode = mode < PlayModes.Length ? PlayModes[mode] : "";
            return instrument;
        }
    }
}

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
        internal const int ProjectLimit = 1 << 20, PatternLimit = 1 << 20, InstrumentHeaderSize = 394;

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

    // One pattern: for each track, how many steps it plays and which instrument each note step uses.
    internal sealed class TrackerPatternFile
    {
        public int TrackCount;
        public int NoteSteps;
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
                int length = Math.Min(Math.Max((int)data[start], 1), StepsPerTrack);
                for (int step = 0; step < length; step++)
                {
                    int at = start + 1 + step * 6;
                    sbyte note = (sbyte)data[at];
                    if (note < 0) continue;     // empty, fade, cut and off steps play nothing new
                    pattern.NoteSteps++;
                    pattern.Instruments.Add(data[at + 1]);
                }
            }
            return pattern;
        }
    }

    // One instrument's file: the sample it holds (the audio is stored inside the file, after its 394-byte header).
    internal sealed class TrackerInstrumentFile
    {
        public string SampleName = "";
        public bool Wavetable;
        public long Frames, AudioBytes;
        // 1 (mono), 2 (stereo), or 0 when the audio size matches neither.
        public int Channels;
        public string PlayMode = "";

        static readonly string[] PlayModes = { "One shot", "Forward loop", "Backward loop", "Ping-pong loop", "Slice", "Beat slice", "Wavetable", "Granular" };

        // Only the header is read; the audio itself is not loaded.
        internal static TrackerInstrumentFile Read(string path)
        {
            PathSafety.RejectRootLinks(path);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Instrument file not found.", path);
            if (info.Length < TrackerFiles.InstrumentHeaderSize) throw new TrackerFileException("The instrument file is too short.");
            var header = new byte[TrackerFiles.InstrumentHeaderSize];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int done = 0, read;
                while (done < header.Length && (read = stream.Read(header, done, header.Length - done)) > 0) done += read;
                if (done < header.Length) throw new TrackerFileException("The instrument file is too short.");
            }
            return Parse(header, info.Length);
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

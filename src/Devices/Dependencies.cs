using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static PathSafety;
using static DeviceSelection;

static class SampleDependencies
{
    // Read-only dependency extraction, bounded to the known firmware-6 song layout.
    // Layout references: github.com/jhw/pym8 m8/api/{instrument.py,instruments/sampler.py}
    // and github.com/Twinside/m8-file-parser src/version.rs. No project bytes are rewritten.
    // Real firmware-6 songs are about 100 KB; anything far larger isn't a song and isn't read into memory.
    internal const long MaxSongSize = 16L << 20;

    internal static List<string> SamplePaths(string song)
    {
        if (new FileInfo(song).Length > MaxSongSize)
            throw new IOException("This file is too large to be an " + CurrentDevice.Name + " song. Use bundle or full restore.");
        byte[] data = Records.ReadBytes(song, MaxSongSize, "Song");
        const int start = 80446, stride = 215, count = 128, pathOffset = 87, pathSize = 128;
        if (data.Length < start + stride * count || Encoding.ASCII.GetString(data, 0, 9) != "M8VERSION" || (data[11] & 15) != 6)
            throw new IOException("Dependency restore supports firmware-6 .m8s files only. Use bundle or full restore for this song.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            int offset = start + i * stride;
            byte type = data[offset];
            if (type != 255 && type > 6) throw new IOException("Unknown instrument layout; use bundle or full restore.");
            if (type != 2) continue;
            int length = 0;
            while (length < pathSize && data[offset + pathOffset + length] != 0) length++;
            if (length == pathSize) throw new IOException("Sample path has no terminator; use bundle restore.");
            if (length == 0) continue;
            for (int j = 0; j < length; j++)
                if (data[offset + pathOffset + j] < 32 || data[offset + pathOffset + j] > 126)
                    throw new IOException("Non-ASCII sample path; use bundle restore.");
            string path = Encoding.ASCII.GetString(data, offset + pathOffset, length).Replace('/', '\\').TrimStart('\\');
            // M8 paths are rooted at the card, including bundle paths.
            SafePath(Path.GetTempPath(), path);
            paths.Add(path);
        }
        var result = new List<string>(paths);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class M8Backup
{
    // Read-only dependency extraction, bounded to the known firmware-6 song layout.
    // Layout references: github.com/jhw/pym8 m8/api/{instrument.py,instruments/sampler.py}
    // and github.com/Twinside/m8-file-parser src/version.rs. No project bytes are rewritten.
    static List<string> SamplePaths(string song)
    {
        byte[] data = File.ReadAllBytes(song);
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

    static List<RestoreEntry> SongDependencies(RestoreEntry song, Snapshot snapshot, string target, bool verify = true)
    {
        ValidateEntry(song, verify);
        var result = new List<RestoreEntry> { song };
        var available = new Dictionary<string, RestoreEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (RestoreEntry item in snapshot.Files) available[item.Relative] = item;
        var missing = new List<string>();
        foreach (string path in SamplePaths(song.StoredPath))
        {
            RestoreEntry item;
            if (available.TryGetValue(path, out item)) result.Add(item);
            else missing.Add(path);
        }
        if (missing.Count > 0)
        {
            Console.WriteLine("  Referenced samples missing from this backup:");
            foreach (string path in missing) Console.WriteLine("    " + path);
            throw new IOException("Cannot restore a complete song. Use a backup containing its samples, or restore the song file separately.");
        }
        Console.WriteLine("  Song plus {0} referenced sample(s), including any unused sampler slots.", result.Count - 1);
        return result;
    }
}

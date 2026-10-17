// The sample files a song needs, for "song with samples" restore.
using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup.Snapshots;
using static GearBackup.DeviceSelection;
using static GearBackup.SampleDependencies;

namespace GearBackup
{
    static class SongRestoreDependencies
    {
        internal static List<RestoreEntry> SongDependencies(RestoreEntry song, Snapshot snapshot, string target, bool verify = true, OperationContext context = null)
        {
            context = context ?? OperationContext.Current;
            ValidateEntry(song, verify, context);
            var result = new List<RestoreEntry> { song };
            var available = new Dictionary<string, RestoreEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (RestoreEntry item in snapshot.Files) available[item.Relative] = item;
            var missing = new List<string>();
            if (!context.Device.ReadsSamplePaths)
                throw new IOException("Restoring a song with its samples isn't supported for the " + context.Device.Name + ".");
            foreach (string path in SamplePaths(song.StoredPath))
            {
                RestoreEntry item;
                if (available.TryGetValue(path, out item)) result.Add(item);
                else missing.Add(path);
            }
            if (missing.Count > 0)
            {
                context.WriteLine("  Referenced samples missing from this backup:");
                foreach (string path in missing) context.WriteLine("    " + path);
                throw new IOException("Cannot restore a complete song. Use a backup containing its samples, or restore the song file separately.");
            }
            context.WriteLine("  Song plus {0} referenced sample(s), including any unused sampler slots.", result.Count - 1);
            return result;
        }
    }

}

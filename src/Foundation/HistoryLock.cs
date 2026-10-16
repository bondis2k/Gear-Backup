// One writer at a time per history, and per settings file.
using System.IO;
using static GearBackup.PathSafety;
using static GearBackup.HistoryLock;

namespace GearBackup
{
    static class HistoryLock
    {
        // Holds .history.lock open without sharing, so a second program or window can't write to the same
        // history at the same time. Windows releases it when the stream is disposed or the process ends.
        internal static FileStream LockHistory(string root)
        {
            RejectRootLinks(root);
            Directory.CreateDirectory(root);
            try { return new FileStream(Path.Combine(root, ".history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new HistoryBusyException("This backup history is already in use by another process."); }
        }
    }

}

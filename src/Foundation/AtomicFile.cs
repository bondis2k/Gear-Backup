using System;
using System.IO;
using System.Text;
using static GearBackup.FileCopy;
using static GearBackup.PathSafety;

namespace GearBackup
{
    // Writing files so readers see either the old or the new contents, never half a file.
    static class AtomicFile
    {
        // Writes a text file so readers see either the old or the new contents, never half a file:
        // the text goes to a temporary file that then replaces the original.
        internal static void AtomicText(string path, string contents, OperationContext context = null)
        {
            // Also used outside operations (settings saved from a window), where there is nothing to cancel.
            OperationContext active = context ?? OperationContext.Active;
            if (active != null) active.CheckCancellation();
            // Never write through a link: it could redirect the file anywhere.
            RejectRootLinks(path);
            string folder = Path.GetDirectoryName(path);
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, ".geartext-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            try
            {
                // CreateNew refuses an existing file or link; never truncate a planted temporary file.
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(contents);
                }
                if (active != null) active.CheckCancellation();
                if (File.Exists(path)) ReplaceSafely(temp, path);
                else File.Move(temp, path);
            }
            finally { if (created) TryDelete(temp); }
        }

        internal static void ReplaceSafely(string staged, string target)
        {
            // ReplaceFile can lose the old name on certain failures without a backup argument.
            // Keep a transient on-volume rollback file even when optional PC recovery is off.
            string rollback = Path.Combine(Path.GetDirectoryName(target), ".gearrollback-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.Replace(staged, target, rollback); }
            catch (Exception ex)
            {
                try
                {
                    if (!File.Exists(target) && File.Exists(rollback)) File.Move(rollback, target);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException("Replacement failed. Original copy is at " + rollback + "; rollback failed: " + rollbackError.Message, ex);
                }
                if (File.Exists(rollback)) throw new IOException("Replacement failed. Retained original copy: " + rollback, ex);
                throw;
            }
            TryDelete(rollback);
        }
    }

}

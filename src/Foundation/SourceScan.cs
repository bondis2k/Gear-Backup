// Walking a source folder: which files count, and what is skipped.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using static SourceScan;

static class SourceScan
{
    // Linux disk-check (fsck.fat) recovered fragments: FSCK0000.REC, FSCK0001.REC, ...
    static readonly Regex FsckFile = new Regex(@"^FSCK\d{4}\.REC$", RegexOptions.IgnoreCase);

    // Leftovers other computers write to memory cards, which the M8 doesn't use:
    // - names starting with "." — macOS (.DS_Store, ._*, .Spotlight-V100, .fseventsd, .Trashes) and
    //   Linux (.Trash-1000, .directory, .hidden, .xdg-volume-info, .goutputstream-*, .fuse_hidden*)
    // - Linux: the lost+found folder, FSCK*.REC files, and editor backup files ending in "~"
    // - Windows: Thumbs.db, ehthumbs.db (thumbnail caches) and desktop.ini (folder view settings)
    internal static bool IsLeftover(string name, bool isFolder)
    {
        if (name.StartsWith(".")) return true;
        if (isFolder) return name == "lost+found";
        return name.EndsWith("~") || FsckFile.IsMatch(name) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ehthumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    // Walks the tree, skipping Windows system folders and leftovers from other computers (see IsLeftover).
    internal static void CollectFiles(string dir, ScanResult scan)
    {
        OperationContext.Current.Progress(Stage.Scanning, dir, 0, 0, scan.Files.Count);
        try
        {
            foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles())
            {
                OperationContext.Current.CheckCancellation();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked file is not supported: " + file.FullName);
                if (IsLeftover(file.Name, false))
                {
                    scan.SkippedFiles++;
                    scan.SkippedFileBytes += file.Length;
                    if (!file.Name.StartsWith(".")) scan.SkippedNamed.Add(file.FullName);
                }
                else
                {
                    scan.Files.Add(file);
                }
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                OperationContext.Current.CheckCancellation();
                string name = Path.GetFileName(sub);
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folder is not supported: " + sub);
                if (name == "System Volume Information" || name == "$RECYCLE.BIN") continue;
                if (IsLeftover(name, true))
                {
                    scan.SkippedFolders++;
                    if (!name.StartsWith(".")) scan.SkippedNamed.Add(sub + "\\");
                    try
                    {
                        foreach (FileInfo f in new DirectoryInfo(sub).GetFiles("*", SearchOption.AllDirectories))
                        {
                            OperationContext.Current.CheckCancellation();
                            scan.SkippedFolderFiles++;
                            scan.SkippedFolderBytes += f.Length;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { }  // only used for the statistics
                    continue;
                }
                scan.Folders.Add(sub);
                CollectFiles(sub, scan);
            }
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("Cannot scan " + dir + ": " + ex.Message, ex); }
    }
}

internal class ScanResult
{
    public List<FileInfo> Files = new List<FileInfo>();
    public List<string> Folders = new List<string>();
    public int SkippedFolders, SkippedFolderFiles, SkippedFiles;
    public long SkippedFolderBytes, SkippedFileBytes;
    // Skipped items without a leading "." (Linux and Windows leftovers), listed by name in the statistics
    public List<string> SkippedNamed = new List<string>();
}

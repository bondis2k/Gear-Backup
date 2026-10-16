using System;
using System.Collections.Generic;
using System.IO;
using static FileCopy;
using static Backups;
using static ChangeSummary;
using static ConsoleUi;
using static Formatting;
using static DeviceSelection;
using static PathSafety;
using static AtomicFile;

// Formats and saves statistics. BackupRun retains control of execution and completion status.
internal sealed class BackupReport
{
    public string Source, Destination, Previous, ChecksumStatus;
    public bool Incremental;
    public int Copied, Updated, Unchanged, PrunedVersions, Failed;
    public long CopiedBytes;
    public DateTime Started;
    public TimeSpan Elapsed;
    public Config Config;
    public ScanResult Scan;
    public FileInfo LargestFile;
    public Changes Changes;
    public SortedDictionary<string, long[]> PerFolder;
    public List<string> NameWarnings, KeptVersions, Failures;

    public string StatusText { get { return Failed == 0 ? "Backup complete." : "Backup finished with " + Failed + " failed file(s)."; } }

    public void Write()
    {
        double seconds = Math.Max(Elapsed.TotalSeconds, 0.001);
        var report = new StringWriter();
        report.WriteLine("  ----------------------------------------");
        report.WriteLine("  Date:          {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now);
        report.WriteLine("  Type:          {0}", Incremental ? "Incremental" : "Full");
        report.WriteLine("  History:       {0}", Config.PointInTimeHistory ? "point-in-time snapshots" : "off (backup files and checksums only)");
        report.WriteLine("  Source:        {0}", Source);
        report.WriteLine("  Destination:   {0}", Destination);
        report.WriteLine("  Files copied:  {0:N0}", Copied);
        if (Incremental)
        {
            report.WriteLine("    New:         {0:N0}", Copied - Updated);
            report.WriteLine("    Updated:     {0:N0}", Updated);
            report.WriteLine("  Unchanged:     {0:N0}", Unchanged);
        }
        report.WriteLine("  Folders:       {0:N0}", Scan.Folders.Count);
        report.WriteLine("  Data copied:   {0}", FormatSize(CopiedBytes));
        report.WriteLine("  Time:          {0}", FormatTime(Elapsed));
        report.WriteLine("  Average speed: {0}/s", FormatSize((long)(CopiedBytes / seconds)));
        report.WriteLine("  Verified:      {0}", Config.Verify
            ? "yes (each copy read back and its SHA-256 checksum compared)"
            : "no (Verify=no in " + AppIdentity.ConfigFileName + ")");
        report.WriteLine("  Checksums:     {0}", ChecksumStatus);
        if (LargestFile != null)
            report.WriteLine("  Largest file:  {0} ({1})", LargestFile.FullName.Substring(Source.Length), FormatSize(LargestFile.Length));
        report.WriteLine();

        WriteChanges(report, Changes, Previous);

        report.WriteLine("  {0,-16}{1,10}{2,14}", "Folder", "Files", "Size");
        foreach (var pair in PerFolder)
            report.WriteLine("  {0,-16}{1,10:N0}{2,14}", pair.Key, pair.Value[0], FormatSize(pair.Value[1]));
        report.WriteLine();

        report.WriteLine("  Skipped (leftovers from macOS, Linux and Windows, e.g. .DS_Store, .Trash-1000, Thumbs.db):");
        report.WriteLine("    {0:N0} folder(s) containing {1:N0} files, {2}", Scan.SkippedFolders, Scan.SkippedFolderFiles, FormatSize(Scan.SkippedFolderBytes));
        report.WriteLine("    {0:N0} file(s), {1}", Scan.SkippedFiles, FormatSize(Scan.SkippedFileBytes));
        foreach (string path in Scan.SkippedNamed)
            report.WriteLine("    skipped: " + path.Substring(Source.Length));

        string spaceLabel = "Free space on " + CurrentDevice.Name + ":";
        // Same column as before for the M8; longer device names still get a gap before the value.
        report.WriteLine("  {0}{1}", spaceLabel.PadRight(Math.Max(24, spaceLabel.Length + 2)),
            CurrentDevice.Mtp ? "not available over MTP" : DriveSpaceText(Path.GetPathRoot(Source)));
        string destRoot = Path.GetPathRoot(Destination);
        report.WriteLine("  Free space left on {0}  {1}", destRoot, FreeSpaceText(destRoot));
        report.WriteLine();

        if (NameWarnings.Count > 0)
        {
            report.WriteLine("  Names the {0} may not handle well ({1}):", CurrentDevice.Name, NameWarnings.Count);
            foreach (string w in NameWarnings) report.WriteLine("    " + w);
            report.WriteLine();
        }

        if (KeptVersions.Count > 0)
        {
            report.WriteLine("  Old versions of updated files, kept as ({0}):", KeptVersions.Count);
            foreach (string f in KeptVersions) report.WriteLine("    " + f);
            report.WriteLine();
        }
        if (PrunedVersions > 0)
        {
            report.WriteLine("  Older versions moved to the Recycle Bin (KeepOldVersions={0}): {1:N0}", Config.KeepOldVersions, PrunedVersions);
            report.WriteLine();
        }

        if (Failures.Count > 0)
        {
            report.WriteLine("  Failed files ({0}):", Failures.Count);
            foreach (string f in Failures) report.WriteLine("    " + f);
            report.WriteLine();
        }
        string status = StatusText;

        WriteColored("  Statistics", ConsoleColor.Cyan);
        OperationOutput.Write(report.ToString());

        // An incremental run gets its own dated file so the earlier statistics are kept
        string statsFile = Path.Combine(Destination, Incremental
            ? "backup-statistics_" + Started.ToString("yyyy-MM-dd_HHmmss_fffffff") + ".txt"
            : "backup-statistics.txt");
        try
        {
            AtomicText(statsFile, "  " + AppIdentity.Name + " " + (Incremental ? "incremental " : "") + "statistics\r\n" + report + "  " + status + "\r\n");
            OperationOutput.WriteLine("  Statistics saved to " + statsFile);
        }
        catch (Exception ex)
        {
            WriteColored("  Could not save statistics file: " + ex.Message, ConsoleColor.Yellow);
        }
        OperationOutput.WriteLine();
    }
}


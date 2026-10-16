using System;
using System.IO;

// Display names are metadata, never paths. An optional blank name displays the dated folder label.
internal static class BackupSetNames
{
    internal static string Validate(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length > 80) throw new ArgumentException("Backup names may contain at most 80 characters.");
        foreach (char c in name) if (char.IsControl(c)) throw new ArgumentException("Backup names cannot contain control characters.");
        return name;
    }
    internal static string Read(string backup)
    {
        string path = Path.Combine(AppIdentity.MetadataRoot(backup), "backup-name.txt");
        try
        {
            PathSafety.RejectRootLinks(path);
            if (File.Exists(path) && new FileInfo(path).Length <= 1024)
            {
                string name = Validate(File.ReadAllText(path));
                if (name.Length != 0) return name;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return Path.GetFileName(backup.TrimEnd('\\', '/'));
    }
    internal static void Write(string backup, string name)
    {
        name = Validate(name);
        if (name.Length != 0) AtomicFile.AtomicText(Path.Combine(AppIdentity.MetadataRoot(backup), "backup-name.txt"), name);
    }
    internal static string Label(string backup)
    {
        string folder = Path.GetFileName(backup.TrimEnd('\\', '/')), name = Read(backup);
        return name == folder ? folder : name + "  ·  " + folder;
    }
}

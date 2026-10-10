using System;
using System.IO;

partial class M8Backup
{
    static string ExpandPath(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    static string Rooted(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
    }

    static bool Within(string path, string parent)
    {
        return Rooted(path).StartsWith(Rooted(parent), StringComparison.OrdinalIgnoreCase);
    }

    // Never allow manifest/checksum entries to escape the selected backup or target.
    static string SafePath(string root, string relative)
    {
        RejectRootLinks(root);
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
            throw new IOException("Invalid relative path: " + relative);
        foreach (string part in relative.Replace('/', '\\').Split('\\'))
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" "))
                throw new IOException("Non-canonical relative path: " + relative);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root) || Rooted(full) == Rooted(root))
            throw new IOException("Path escapes its folder: " + relative);
        RejectLinks(root, full);
        return full;
    }

    static void CheckParents(string root, string path)
    {
        string parent = Path.GetDirectoryName(path);
        while (parent != null && Within(parent, root))
        {
            if (File.Exists(parent)) throw new IOException("A file blocks the destination folder: " + parent);
            if (Rooted(parent) == Rooted(root)) break;
            parent = Path.GetDirectoryName(parent);
        }
    }

    static void RejectLinks(string root, string path)
    {
        string current = path;
        while (Within(current, root))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            if (Rooted(current) == Rooted(root)) break;
            current = Path.GetDirectoryName(current.TrimEnd('\\'));
            if (current == null) break;
        }
    }

    static void RejectRootLinks(string path)
    {
        string current = Path.GetFullPath(path).TrimEnd('\\', '/');
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            current = Path.GetDirectoryName(current);
        }
    }

}

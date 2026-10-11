using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup;

static class PathSafety
{
    internal static string ExpandPath(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    // The full path with exactly one trailing backslash, so paths compare reliably as folder prefixes.
    internal static string Rooted(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
    }

    // True if path is parent itself or inside it.
    internal static bool Within(string path, string parent)
    {
        return Rooted(path).StartsWith(Rooted(parent), StringComparison.OrdinalIgnoreCase);
    }

    // Never allow manifest/checksum entries to escape the selected backup or target.
    internal static string SafePath(string root, string relative)
    {
        RejectRootLinksOnce(root);
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

    // Fails when a file sits where one of path's parent folders must be created.
    internal static void CheckParents(string root, string path)
    {
        string parent = Path.GetDirectoryName(path);
        while (parent != null && Within(parent, root))
        {
            if (File.Exists(parent)) throw new IOException("A file blocks the destination folder: " + parent);
            if (Rooted(parent) == Rooted(root)) break;
            parent = Path.GetDirectoryName(parent);
        }
    }

    // Rejects path if it, or a folder between it and root, is a link (junction/symbolic link), which could
    // redirect a write outside the intended folder.
    static void RejectLinks(string root, string path)
    {
        HashSet<string> verified = OperationContext.Current.VerifiedFolders;
        string current = path;
        while (Within(current, root))
        {
            bool isFolder = Directory.Exists(current);
            if (isFolder && verified.Contains(Rooted(current))) break;
            if ((isFolder || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            if (isFolder) verified.Add(Rooted(current));
            if (Rooted(current) == Rooted(root)) break;
            current = Path.GetDirectoryName(current.TrimEnd('\\'));
            if (current == null) break;
        }
    }

    // RejectRootLinks, skipped when this operation already checked the same folder.
    static void RejectRootLinksOnce(string path)
    {
        HashSet<string> verified = OperationContext.Current.VerifiedFolders;
        string key = Rooted(path);
        if (verified.Contains(key)) return;
        RejectRootLinks(path);
        verified.Add(key);
    }

    // Rejects path if it or any folder above it, up to the drive root, is a link.
    internal static void RejectRootLinks(string path)
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

    // Windows' classic path limits, which the program's file calls are bound by: at most 259 characters
    // for a file and 247 for a folder. Collects the paths an operation will create, so it can stop with
    // a clear message before copying anything, instead of failing part-way through.
    internal sealed class PathLengthCheck
    {
        public const int MaxFile = 259, MaxFolder = 247;
        string longest;
        int longestLength, longestLimit;

        // `extra`: characters a name may grow by (a dated old version, a temporary file beside it).
        public void File(string path, int extra = 0) { Note(path, path.Length + extra, MaxFile); }
        public void Folder(string path) { Note(path, path.TrimEnd('\\').Length, MaxFolder); }

        void Note(string path, int length, int limit)
        {
            if (length - limit > longestLength - longestLimit || longest == null) { longest = path; longestLength = length; longestLimit = limit; }
        }

        // Null when everything fits; otherwise what's too long and what to do about it.
        public string Problem(string advice)
        {
            if (longest == null || longestLength <= longestLimit) return null;
            return "A path would be " + longestLength + " characters, over Windows' limit of " + (longestLimit + 1) + ": " + longest + ". " + advice;
        }
    }

}

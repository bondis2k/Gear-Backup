using System;
using System.Collections.Generic;
using System.IO;
using static PathSafety;

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
        RejectRootLinks(root);
        string full = CheckedPath(root, relative);
        RejectLinks(root, full);
        return full;
    }

    // The same checks without touching the disk: where `relative` lands under `root`, refusing anything
    // that is not a plain, canonical path inside it. For many names under one root, check the root once.
    internal static string CheckedPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
            throw new IOException("Invalid relative path: " + relative);
        foreach (string part in relative.Replace('/', '\\').Split('\\'))
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" "))
                throw new IOException("Non-canonical relative path: " + relative);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root) || Rooted(full) == Rooted(root))
            throw new IOException("Path escapes its folder: " + relative);
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
    static void RejectLinks(string root, string path) { RejectLinks(root, path, null); }

    // With `clean`, folders already found to be plain are not inspected again, so many paths under the same
    // folders cost one look at each folder instead of one per path. Only for one read-only scope.
    internal static void RejectLinks(string root, string path, HashSet<string> clean)
    {
        // Folder names are mutable: a previously checked directory can now be a junction.
        // Always inspect current attributes, including after a preview or a long copy.
        string current = path;
        while (Within(current, root))
        {
            bool item = object.ReferenceEquals(current, path);
            if (clean != null && !item && clean.Contains(current)) break;
            bool isFolder = Directory.Exists(current);
            if ((isFolder || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported: " + current);
            if (clean != null && !item) clean.Add(current);
            if (Rooted(current) == Rooted(root)) break;
            current = Path.GetDirectoryName(current.TrimEnd('\\'));
            if (current == null) break;
        }
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
}

// Read-only inventory scope: saved runs repeat the same paths many times. Validate each
// unique path once while materializing one catalog; never reuse this cache for filesystem
// writes or across operations. Restore execution checks the source again before opening it.
internal sealed class InventoryPathCache
{
    readonly string root;
    readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.Ordinal);
    readonly HashSet<string> cleanFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal InventoryPathCache(string root) { this.root = root; PathSafety.RejectRootLinks(root); }
    internal string Resolve(string relative)
    {
        string path;
        if (!paths.TryGetValue(relative ?? "", out path))
        {
            path = PathSafety.CheckedPath(root, relative);
            PathSafety.RejectLinks(root, path, cleanFolders); // each folder is inspected once per scope
            paths.Add(relative, path);
        }
        return path;
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

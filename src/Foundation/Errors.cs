using System.IO;

// Kinds of failure the program tells apart. All derive from IOException, so code that catches IOException still
// sees them; callers that care catch the kind, and ErrorHints turns a kind into advice.

// Stored content or a backup file is missing, truncated or does not match its checksum.
internal sealed class DamagedContentException : IOException
{
    internal DamagedContentException(string message) : base(message) { }
}

// A path is a link, escapes its folder or is otherwise refused for safety.
internal sealed class UnsafePathException : IOException
{
    internal UnsafePathException(string message) : base(message) { }
}

// Another process or window holds the backup history.
internal sealed class HistoryBusyException : IOException
{
    internal HistoryBusyException(string message) : base(message) { }
}

// A backup made by an older version in a layout this version no longer reads.
internal sealed class UnsupportedBackupException : IOException
{
    internal UnsupportedBackupException(string message) : base(message) { }
}

static class ErrorHints
{
    // What to try next for a kind of failure, or null when there is nothing specific to suggest.
    internal static string For(System.Exception error)
    {
        if (error is DamagedContentException) return "Run Check backups to see which files are damaged, and restore from another copy of the backup if you have one.";
        if (error is HistoryBusyException) return "Close other Gear Backup windows or wait for the running operation to finish, then try again.";
        if (error is UnsupportedBackupException) return "Make a new full backup. The old folder can stay for reference.";
        if (error is UnsafePathException) return "Links and junctions are not followed. Remove the link or choose another folder.";
        return null;
    }
}

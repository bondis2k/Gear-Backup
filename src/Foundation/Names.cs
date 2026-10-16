namespace GearBackup
{
    // Named values with their own types, so a stage, status, restore mode or storage kind can't be mixed up
    // with arbitrary text. Each value prints as the text it always had (ToString), so files on disk, the
    // console, reports and windows show exactly what they did when these were plain strings and characters.

    // Progress stages the engines report to the front ends.
    internal sealed class Stage
    {
        public static readonly Stage Scanning = new Stage("Scanning"), PlanningBackup = new Stage("Planning backup"),
            BackupCopy = new Stage("Backup copy"), Copying = new Stage("Copying"), Hashing = new Stage("Hashing"),
            Verifying = new Stage("Verifying"), Comparing = new Stage("Comparing"), SavingHistory = new Stage("Saving history"),
            SavingChecksums = new Stage("Saving checksums"), Zipping = new Stage("Zipping"), ScanningBackup = new Stage("Scanning backup"),
            CheckingBackup = new Stage("Checking backup"), CheckingHistory = new Stage("Checking history"),
            PlanningRestore = new Stage("Planning restore"), Restoring = new Stage("Restoring"), SavingRecovery = new Stage("Saving recovery"),
            Downloading = new Stage("Downloading"), ReadingCameraSettings = new Stage("Reading camera settings");

        readonly string text;
        Stage(string text) { this.text = text; }
        public override string ToString() { return text; }

        // Per-file detail. During a full backup only overall progress is shown, so front ends skip these.
        public bool IsFileDetail { get { return this == Copying || this == Hashing || this == Verifying || this == Comparing || this == ScanningBackup; } }
    }

    // A backup folder's status, stored in .gearbackup\status.txt as its text.
    internal sealed class Status
    {
        public static readonly Status Incomplete = new Status("incomplete"), Complete = new Status("complete"), Failed = new Status("failed");
        public static readonly Status Unknown = new Status("unknown (missing or invalid status)");

        readonly string text;
        Status(string text) { this.text = text; }
        public override string ToString() { return text; }

        // Missing or unrecognized status metadata does not count as a successful backup.
        public static Status FromText(string text)
        {
            foreach (Status known in new[] { Incomplete, Complete, Failed })
                if (known.text == text) return known;
            return Unknown;
        }

        // An interrupted or failed backup, which can be resumed.
        public bool Unfinished { get { return this == Incomplete || this == Failed; } }
    }

    // What a restore brings back. Key is also what's typed in the console menu.
    internal sealed class RestoreMode
    {
        public static readonly RestoreMode All = new RestoreMode('a'), Bundle = new RestoreMode('b'), ProjectFolder = new RestoreMode('p'),
            SingleFile = new RestoreMode('s'), SongWithSamples = new RestoreMode('d');

        public readonly char Key;
        RestoreMode(char key) { Key = key; }
        public override string ToString() { return Key.ToString(); }

        // The mode chosen with this menu key; null for any other key.
        public static RestoreMode FromKey(char key)
        {
            foreach (RestoreMode mode in new[] { All, Bundle, ProjectFolder, SingleFile, SongWithSamples })
                if (mode.Key == key) return mode;
            return null;
        }
    }

    // The kinds of item Storage offers for cleanup; the text is shown in its table.
    internal sealed class StorageKind
    {
        public static readonly StorageKind OlderBackup = new StorageKind("Older backup"), Zip = new StorageKind("ZIP"), Mirror = new StorageKind("MTP mirror");

        readonly string text;
        StorageKind(string text) { this.text = text; }
        public override string ToString() { return text; }
    }

    // Mode setting: what to do when a backup already exists.
    internal enum BackupMode { Ask, Full, Incremental }

    // Eject setting: what to do with the M8 when finished.
    internal enum EjectMode { No, Ask, Yes }

}

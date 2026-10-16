using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static GearBackup.PathSafety;

namespace GearBackup
{
    internal sealed class ZipRestoreInfo
    {
        internal DeviceProfile Device;
        internal DateTime TakenUtc;
        internal long ContentBytes, ExtractedBytes;
        internal int Files, Folders;
        internal string Profile;
    }

    // ZIPs are untrusted input. Validate the inventory, metadata and all hashes before changing the target.
    internal static class ZipRestore
    {
        sealed class ZipInventory
        {
            internal ZipRestoreInfo Info;
            internal readonly Dictionary<string, ZipArchiveEntry> Files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Folders = new List<string>();
        }

        // A ZIP's central directory is read into memory, so a ZIP listing millions of entries is refused; real device
        // backups hold far fewer.
        internal static int MaxEntries = 1000000;

        internal static ZipRestoreInfo Inspect(string path)
        {
            RejectRootLinks(path);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read)) return Read(zip).Info;
        }

        internal static OperationResult Run(string path, SourceChoice detected, string target, Config config,
            Action validateDevice = null, Func<string, long, string> checkSpace = null, string stagingParent = null)
        {
            if (detected == null || detected.Detected == null) throw new IOException("Detect the destination device before restoring a ZIP archive.");
            target = Rooted(target); path = Path.GetFullPath(path);
            RejectRootLinks(path); RejectRootLinks(target);
            if (!Directory.Exists(target)) throw new IOException("Restore destination does not exist: " + target);
            if (detected.Mtp != null && Within(target, detected.Mtp.Mirror))
                throw new IOException("Choose a PC folder outside the MTP mirror. Transfer the restored files to the device manually.");
            if (validateDevice != null) validateDevice();
            var context = OperationContext.Current;
            var space = checkSpace ?? CheckSpace;
            string parent = Rooted(stagingParent ?? Path.GetTempPath());
            RejectRootLinks(parent);
            if (!Directory.Exists(parent)) throw new IOException("Temporary extraction location does not exist.");
            string stage = Path.Combine(parent, "GearBackup-ZipRestore-" + Guid.NewGuid().ToString("N"));
            bool created = false;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var inventory = Read(zip);
                    if (inventory.Info.Device.Id != detected.Detected.Id)
                        throw new IOException("This ZIP contains a " + inventory.Info.Device.Name + " backup; the detected device is " + detected.Detected.Name + ". Restore is blocked.");
                    using (context.UseDevice(inventory.Info.Device))
                    {
                        Snapshot snapshot = BuildSnapshot(inventory, path, target, stage);
                        long reserve = RestoreFlow.RestoreSpaceNeeded(target, ChangedFiles(snapshot, target, config));
                        bool sameVolume = string.Equals(Path.GetPathRoot(parent), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
                        RequireSpace(space, target, checked(reserve + (sameVolume ? inventory.Info.ContentBytes : 0)));
                        RequireSpace(space, parent, checked(inventory.Info.ContentBytes + (sameVolume ? reserve : 0)));
                        OperationOutput.WriteLine("  ZIP backup: " + inventory.Info.Device.Name + " · taken " + inventory.Info.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                        OperationOutput.WriteLine("  Backup content: " + Formatting.FormatSize(inventory.Info.ContentBytes) + "; target working space: " + Formatting.FormatSize(reserve) + ".");
                        OperationOutput.WriteLine("  Checking ZIP SHA-256 checksums before restore.");
                        context.CheckCancellation();
                        Directory.CreateDirectory(stage); created = true;
                        ExtractAndVerify(inventory, snapshot, stage, context);
                        // Recheck space and device after verification and again after the user approves the plan.
                        Action guard = delegate {
                            context.CheckCancellation(); RejectRootLinks(target);
                            if (!Directory.Exists(target)) throw new IOException("Restore destination is no longer available.");
                            if (validateDevice != null) validateDevice();
                            RequireSpace(space, target, reserve);
                        };
                        guard();
                        return new RestoreEngine().RunSelected(target, snapshot, snapshot.Files, RestoreMode.All, null, false, config, context, guard);
                    }
                }
            }
            finally
            {
                if (created)
                {
                    try { RejectRootLinks(stage); Directory.Delete(stage, true); }
                    catch (Exception ex) { OperationOutput.WriteLine("  Could not remove temporary ZIP files: " + ex.Message); }
                }
            }
        }

        // Lists the ZIP's files and folders as a restore point, checking every name and length before anything is written.
        static Snapshot BuildSnapshot(ZipInventory inventory, string path, string target, string stage)
        {
            var snapshot = new Snapshot { Name = Path.GetFileName(path), TakenUtc = inventory.Info.TakenUtc };
            var lengths = new PathLengthCheck();
            foreach (var item in inventory.Files)
            {
                if (IsMetadata(item.Key)) continue;
                string relative = item.Key.Replace('/', '\\');
                string destination = SafePath(target, relative), stored = CheckedPath(stage, relative);
                if (string.Equals(destination, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("The ZIP file would overwrite itself during restore.");
                PathSafety.CheckParents(target, destination);
                if (Directory.Exists(destination)) throw new IOException("A folder blocks the destination file: " + relative);
                lengths.File(destination); lengths.File(stored);
                snapshot.Files.Add(new RestoreEntry { Relative = relative, StoredPath = stored, Size = item.Value.Length,
                    Hash = inventory.Hashes[item.Key], ModifiedTicks = item.Value.LastWriteTime.UtcDateTime.Ticks,
                    CreatedTicks = item.Value.LastWriteTime.UtcDateTime.Ticks });
            }
            foreach (string folder in inventory.Folders)
            {
                string relative = folder.Replace('/', '\\');
                lengths.Folder(CheckedPath(stage, relative)); lengths.Folder(CheckedPath(target, relative));
                snapshot.Folders.Add(relative);
            }
            string problem = lengths.Problem("Choose a shorter restore destination.");
            if (problem != null) throw new IOException(problem);
            return snapshot;
        }

        // The files the restore would replace or add: existing identical files need no replacement space. Undated files stay in the conservative estimate.
        static List<RestoreEntry> ChangedFiles(Snapshot snapshot, string target, Config config)
        {
            return snapshot.Files.FindAll(item => {
                string destination = SafePath(target, item.Relative);
                var actual = new FileInfo(destination);
                if (!actual.Exists || actual.Length != item.Size) return true;
                if (config.Thorough) return FileCopy.HashFile(destination) != item.Hash;
                return !FileCopy.UsableFileTime(actual.LastWriteTimeUtc) || !FileCopy.UsableFileTime(new DateTime(item.ModifiedTicks, DateTimeKind.Utc)) ||
                    Math.Abs(actual.LastWriteTimeUtc.Ticks - item.ModifiedTicks) > TimeSpan.FromSeconds(2).Ticks;
            });
        }

        // Copies every entry into the staging folder, checking size and SHA-256 as it goes.
        static void ExtractAndVerify(ZipInventory inventory, Snapshot snapshot, string stage, OperationContext context)
        {
            var buffer = new byte[65536]; long done = 0; int count = 0;
            foreach (var item in snapshot.Files)
            {
                context.CheckCancellation();
                RejectRootLinks(stage);
                Directory.CreateDirectory(Path.GetDirectoryName(SafePath(stage, item.Relative)));
                var entry = inventory.Files[item.Relative.Replace('\\', '/')];
                using (var from = entry.Open())
                using (var to = new FileStream(SafePath(stage, item.Relative), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hash = SHA256.Create())
                {
                    long copied = 0; int read;
                    while ((read = from.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        context.CheckCancellation();
                        copied = checked(copied + read);
                        if (copied > item.Size) throw new IOException("ZIP entry exceeds its declared size: " + item.Relative);
                        to.Write(buffer, 0, read); hash.TransformBlock(buffer, 0, read, buffer, 0); done += read;
                        context.Progress(Stage.Verifying, item.Relative, done, inventory.Info.ContentBytes, count, snapshot.Files.Count);
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    if (copied != item.Size || FileCopy.ToHex(hash.Hash) != item.Hash)
                        throw new IOException("ZIP checksum verification failed: " + item.Relative);
                }
                File.SetLastWriteTimeUtc(item.StoredPath, new DateTime(item.ModifiedTicks, DateTimeKind.Utc));
                context.Progress(Stage.Verifying, item.Relative, done, inventory.Info.ContentBytes, ++count, snapshot.Files.Count);
            }
        }

        internal static string CheckSpace(string path, long needed)
        {
            return DiskSpace.Shortage(path, needed, "ZIP restore");
        }

        static void RequireSpace(Func<string, long, string> check, string path, long needed)
        { string error = check(path, needed); if (error != null) throw new IOException(error); }
        static bool IsMetadata(string name)
        { return name.Equals(ArchiveMetadata.FileName, StringComparison.OrdinalIgnoreCase) || name.Equals(ArchiveMetadata.ChecksumFileName, StringComparison.OrdinalIgnoreCase); }

        static ZipInventory Read(ZipArchive zip)
        {
            var result = new ZipInventory();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string validationRoot = Rooted(Path.Combine(Path.GetTempPath(), "GearBackup-ZipValidation"));
            long content = 0, total = 0;
            if (zip.Entries.Count > MaxEntries) throw new IOException("This ZIP lists " + zip.Entries.Count.ToString("N0") + " entries, more than the " + MaxEntries.ToString("N0") + " Gear Backup will restore.");
            foreach (var entry in zip.Entries)
            {
                OperationContext.Current.CheckCancellation();
                bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                string name = directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName;
                ValidateName(validationRoot, name);
                if (!names.Add(name)) throw new IOException("Duplicate ZIP path: " + name);
                int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new UnsafePathException("Linked ZIP entries are not supported: " + name);
                total = checked(total + entry.Length);
                if (directory)
                {
                    if (entry.Length != 0 || IsMetadata(name)) throw new IOException("Invalid ZIP folder: " + name);
                    result.Folders.Add(name);
                }
                else { result.Files.Add(name, entry); if (!IsMetadata(name)) content = checked(content + entry.Length); }
            }
            foreach (string name in names)
            {
                string parent = name;
                while (parent.LastIndexOf('/') >= 0)
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (result.Files.ContainsKey(parent)) throw new IOException("A ZIP file blocks a folder: " + parent);
                    if (!names.Contains(parent)) throw new IOException("ZIP is missing a source folder entry: " + parent);
                }
            }
            ZipArchiveEntry info, sums;
            if (!result.Files.TryGetValue(ArchiveMetadata.FileName, out info) || !result.Files.TryGetValue(ArchiveMetadata.ChecksumFileName, out sums))
                throw new IOException("Select a Gear Backup standalone ZIP containing gearbackup-info.txt and checksums.sha256.");
            byte[] infoBytes = ReadBytes(info, 64 * 1024);
            string manifest = new UTF8Encoding(false, true).GetString(ReadBytes(sums, Records.ListLimit));
            foreach (string raw in manifest.Split('\n'))
            {
                string line = raw.TrimEnd('\r'); if (line.Length == 0) continue;
                if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new IOException("Invalid ZIP checksum manifest.");
                string hash = line.Substring(0, 64).ToLowerInvariant(), name = line.Substring(66);
                ValidateName(validationRoot, name);
                if (!Snapshots.ValidHash(hash) || name.Equals(ArchiveMetadata.ChecksumFileName, StringComparison.OrdinalIgnoreCase) ||
                    !result.Files.ContainsKey(name) || result.Hashes.ContainsKey(name)) throw new IOException("Invalid ZIP checksum entry: " + name);
                result.Hashes.Add(name, hash);
            }
            if (result.Hashes.Count != result.Files.Count - 1) throw new IOException("ZIP checksum manifest does not cover all files.");
            using (var hash = SHA256.Create())
                if (FileCopy.ToHex(hash.ComputeHash(infoBytes)) != result.Hashes[ArchiveMetadata.FileName]) throw new IOException("ZIP device information checksum verification failed.");
            result.Info = ReadInfo(new UTF8Encoding(false, true).GetString(infoBytes));
            if (result.Info.ContentBytes != content || result.Info.ExtractedBytes != total || result.Info.Files != result.Files.Count - 2 || result.Info.Folders != result.Folders.Count)
                throw new IOException("ZIP contents do not match the sizes/counts in gearbackup-info.txt.");
            return result;
        }

        static void ValidateName(string root, string name)
        {
            if (name.IndexOf('\\') >= 0) throw new IOException("ZIP paths must use forward slashes: " + name);
            foreach (string part in name.Split('/'))
            {
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("Invalid ZIP filename: " + name);
                if (IsReservedName(part)) throw new IOException("Reserved ZIP filename: " + name);
            }
            CheckedPath(root, name);
        }

        static byte[] ReadBytes(ZipArchiveEntry entry, long limit)
        {
            if (entry.Length > limit) throw new IOException("ZIP metadata is too large: " + entry.FullName);
            using (var input = entry.Open())
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    OperationContext.Current.CheckCancellation();
                    if (output.Length + read > limit || output.Length + read > entry.Length) throw new IOException("ZIP metadata exceeds its declared size: " + entry.FullName);
                    output.Write(buffer, 0, read);
                }
                if (output.Length != entry.Length) throw new IOException("Truncated ZIP metadata: " + entry.FullName);
                return output.ToArray();
            }
        }

        static ZipRestoreInfo ReadInfo(string text)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                int separator = raw.IndexOf(": "); if (separator < 0) continue;
                string key = raw.Substring(0, separator);
                if (fields.ContainsKey(key)) throw new IOException("Duplicate ZIP information field: " + key);
                fields.Add(key, raw.Substring(separator + 2).TrimEnd('\r'));
            }
            string deviceText;
            if (!fields.TryGetValue("Backup device profile", out deviceText)) throw new IOException("ZIP device type is missing.");
            DeviceProfile device = Array.Find(Devices.All, d => deviceText == d.Name + " (" + d.Id + ")");
            if (device == null) throw new IOException("ZIP device type is unknown: " + deviceText);
            string taken; DateTime time;
            if (!fields.TryGetValue("Backup taken (UTC)", out taken) || !DateTime.TryParseExact(taken, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) || time.Kind != DateTimeKind.Utc)
                throw new IOException("Invalid ZIP backup capture time.");
            var result = new ZipRestoreInfo { Device = device, TakenUtc = time, ContentBytes = Number(fields, "Backup content size (bytes)"),
                ExtractedBytes = Number(fields, "Total extracted size (bytes)"), Files = checked((int)Number(fields, "Source files")), Folders = checked((int)Number(fields, "Source folders")) };
            fields.TryGetValue("Profile name", out result.Profile);
            return result;
        }
        static long Number(Dictionary<string, string> fields, string key)
        {
            string text; long value;
            if (!fields.TryGetValue(key, out text) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw new IOException("Invalid ZIP information field: " + key);
            return value;
        }
    }

}

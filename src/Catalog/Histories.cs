using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup.PathSafety;
using static GearBackup.PlanReview;
using static GearBackup.ConsoleUi;
using static GearBackup.ProfileSettings;
using static GearBackup.BackupFolders;
using static GearBackup.ProfileNaming;
using static GearBackup.HistoryLock;

namespace GearBackup
{
    // Backup histories (the folders under Gear Backups\profiles): finding, choosing, naming new folders and locking one.
    static class Histories
    {
        // Lets the user pick a profile history containing backups. Null when cancelled.
        internal static string ChooseHistory(string root, string preferred)
        {
            var roots = new List<string>();
            CollectHistories(root, roots);
            return ChooseHistoryList(roots, preferred, preferred ?? root);
        }

        // The histories with backups in every configured location. Looked up once per operation; callers
        // get their own copy of the list.
        internal static List<string> AvailableHistories(Config config)
        {
            List<string> locations = ConfiguredBackupLocations(config);
            OperationContext context = OperationContext.Current;
            Dictionary<string, List<string>> cache = context.HistoryCache;
            string key = string.Join("|", locations.ConvertAll(location => Rooted(location)).ToArray());
            List<string> roots;
            if (cache == null || !cache.TryGetValue(key, out roots))
            {
                roots = new List<string>();
                foreach (string location in locations)
                    CollectHistories(HistoriesRoot(new Config { BackupFolder = location }), roots);
                roots.Sort(StringComparer.OrdinalIgnoreCase);
                if (cache != null) cache[key] = roots;
            }
            return new List<string>(roots);
        }

        static void CollectHistories(string root, List<string> roots)
        {
            RejectRootLinks(root);
            string profiles = Path.Combine(root, "profiles");
            if (Directory.Exists(profiles))
            {
                RejectRootLinks(profiles);
                foreach (string dir in Directory.GetDirectories(profiles))
                    if (ListBackups(dir).Count > 0 && !roots.Exists(p => Rooted(p) == Rooted(dir))) roots.Add(dir);
            }
        }

        internal static string ChooseHistory(Config config, string preferred)
        {
            var roots = AvailableHistories(config);
            if (preferred != null && ListBackups(preferred).Count > 0 && !roots.Exists(p => Rooted(p) == Rooted(preferred)))
                roots.Add(preferred);
            return ChooseHistoryList(roots, preferred, preferred ?? HistoriesRoot(config));
        }

        static string ChooseHistoryList(List<string> roots, string preferred, string fallback)
        {
            roots.Sort(StringComparer.OrdinalIgnoreCase);
            if (roots.Count == 0) return fallback;
            var labels = new List<string>();
            foreach (string history in roots)
                labels.Add(HistoryNames.DisplayName(history) + " — " + history +
                    (string.Equals(history, preferred, StringComparison.OrdinalIgnoreCase) ? " (current source)" : ""));
            int pick = AskItem("Choose a backup history.", labels);
            return pick == 0 ? null : roots[pick - 1];
        }

        internal static bool HasHistory(string root)
        {
            string profiles = Path.Combine(root, "profiles");
            if (Directory.Exists(profiles))
                foreach (string dir in Directory.GetDirectories(profiles)) if (ListBackups(dir).Count > 0) return true;
            return false;
        }

        internal static bool HasHistory(Config config) { return AvailableHistories(config).Count > 0; }

        // root\name, or root\name_2, name_3, ... if that folder already exists.
        internal static string UniqueFolder(string root, string name)
        {
            string path = Path.Combine(root, name);
            for (int n = 2; Directory.Exists(path); n++) path = Path.Combine(root, name + "_" + n);
            return path;
        }



        internal static string HistoriesRoot(Config config)
        {
            return Path.Combine(config.BackupFolder, AppIdentity.BackupsFolderName);
        }

        // Checks the source against the backup location and returns this source's history folder.
        internal static string HistoryFor(Config config, string source)
        {
            string linked = ProfileLinks.Find(config, source);
            if (linked != null)
            {
                RejectRootLinks(linked);
                string location = Path.GetDirectoryName(Path.GetDirectoryName(linked));
                if (Within(location, source) || Within(source, location))
                    throw new IOException("The backup folder and source must not contain one another.");
                if (!Directory.Exists(linked)) throw new IOException("The linked backup history is unavailable: " + linked);
                return linked;
            }
            string histories = HistoriesRoot(config);
            if (Within(histories, source) || Within(source, histories))
                throw new IOException("The backup folder and source must not contain one another.");
            return ProfileFolder(Path.Combine(histories, "profiles"), source, config.Profile);
        }

        // The history to check: the named profile, or one the user picks. Null when cancelled.
        internal static string HistoryToCheck(Config config)
        {
            string histories = HistoriesRoot(config);
            if (config.Profile.Length == 0) return ChooseHistory(config, null);
            var found = AvailableHistories(config).FindAll(path =>
                Path.GetFileName(Path.GetDirectoryName(path)).Equals("profiles", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(path).Equals(config.Profile, StringComparison.OrdinalIgnoreCase));
            if (found.Count == 1) return found[0];
            if (found.Count > 1) { int pick = AskItem("Choose the location for this profile.", found); return pick == 0 ? null : found[pick - 1]; }
            return Path.Combine(histories, "profiles", config.Profile);
        }

        // Locks a history against a second writer; a preview changes nothing, so it takes no lock.
        internal static IDisposable LockUnlessPreview(string history)
        {
            return PreviewOnly ? null : LockHistory(history);
        }
    }

}

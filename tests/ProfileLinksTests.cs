using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GearBackup;

partial class Tests
{
    [Test("Replacement card history links: persistence, routing and overrides")]
    static void TestProfileLinks(string root)
    {
        var config = new Config { SettingsDirectory = Folder(root, "settings"), BackupFolder = Folder(root, "default-backups") };
        string source = Folder(root, "source");
        string originalLocation = Folder(root, "original-backups");
        string history = Folder(originalLocation, Path.Combine(AppIdentity.BackupsFolderName, "profiles", "OriginalM8"));
        // A folder key exercises routing without needing to write to a physical SD card.
        string key = "m8-" + ProfileNaming.SourceKey(PathSafety.Rooted(source));
        using (new OperationContext(new TestInteraction(), CancellationToken.None).Activate())
        using (OperationContext.Current.UseDevice(Devices.M8))
        {
            Assert(ProfileLinks.Find(config, source) == null, "unlinked sources retain normal profile naming");
            ProfileSettings.SaveProfileSettings(config, "profile:OriginalM8", new Dictionary<string, string> { { "Verify", "yes" } });
            ProfileLinks.Save(config, key, history);
            ProfileLinks.Save(config, "m8-card-12345678", history);
            Assert(ProfileLinks.Read(config).Count == 2 && ProfileLinks.Find(config.Clone(), source) == history, "saved associations survive config reload and preserve other cards");
            var effective = ProfileSettings.ResolveSourceSettings(config, source);
            Assert(effective.Verify && effective.BackupFolder == originalLocation && !config.Verify, "original history preferences and backup location apply without changing global settings");
            Assert(Histories.HistoryFor(effective, source) == history, "future backups select the exact original history");
            Assert(ProfileSettings.ConfiguredBackupLocations(config).Contains(originalLocation), "restore discovery includes linked backup locations");
            var explicitProfile = config.Clone(); explicitProfile.Profile = "Independent";
            Assert(ProfileLinks.Find(explicitProfile, source) == null && Histories.HistoryFor(explicitProfile, source).EndsWith("Independent"), "explicit profile selection takes precedence");
            string other = Folder(originalLocation, Path.Combine(AppIdentity.BackupsFolderName, "profiles", "Other"));
            Throws(() => ProfileLinks.Save(config, key, other), "a conflicting link cannot silently replace an association");
            Assert(ProfileLinks.Find(config, source) == history, "conflict leaves the original association intact");
            Throws(() => ProfileLinks.Key(source), "migration linking refuses rehearsal folders");
            Throws(() => ProfileLinks.Save(config, key, Path.Combine(root, "outside")), "unsafe history structure is refused");
            Directory.Delete(history);
            Throws(() => Histories.HistoryFor(effective, source), "unavailable history fails visibly instead of starting an independent history");
            using (OperationContext.Current.UseDevice(Devices.OPXY))
                Assert(ProfileLinks.Find(config, source) == null, "card links do not change other device histories");
        }
    }
}

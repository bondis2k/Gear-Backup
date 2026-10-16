// Backing up the X100VI's recipes and shooting settings as an ordinary backup.
using System;
using System.Collections.Generic;
using System.IO;
using static GearBackup.PathSafety;
using static GearBackup.Backups;
using static GearBackup.MtpMirror;
using static GearBackup.PortableDevices;
using static GearBackup.ProfileSettings;
using static GearBackup.Session;
using static GearBackup.ProfileNaming;
using static GearBackup.FujiSettings;
using static GearBackup.Histories;

namespace GearBackup
{
    static class FujiSettingsBackup
    {
        internal static OperationResult RunFujiSettingsBackup(Config config)
        {
            var cameras = FindMtpDevices("VID_04CB");
            cameras.RemoveAll(info => !string.Equals(info.FriendlyName, "X100VI", StringComparison.OrdinalIgnoreCase));
            if (cameras.Count != 1) throw new IOException("Connect one X100VI in USB RAW CONV./BACKUP RESTORE mode. Close other camera applications and try again.");
            OperationContext.Current.Device = Devices.X100VI;
            // Settings use a separate history (Studio-settings) with the named profile's preferences.
            string automatic = Path.GetFileName(MirrorFolder(Devices.X100VI, cameras[0])).Replace("x100vi-", "x100vi-settings-");
            string profileName = config.Profile.Length > 0 ? SettingsProfileName(config.Profile) : automatic;
            ValidateProfile(profileName);
            config = ResolveProfileSettings(config, Devices.X100VI.Id, config.Profile.Length > 0 ? config.Profile : profileName);
            string history = Path.Combine(HistoriesRoot(config), "profiles", profileName);
            if (!EnsureBackupFolder(config)) return BackupFolderCancelled();
            OperationOutput.WriteLine("  Capturing X100VI C1–C7 recipes and current shooting settings.");
            OperationOutput.WriteLine("  This is a settings snapshot, not a full-camera or X Acquire backup.");
            Dictionary<string, byte[]> files;
            // A history lock is too late to protect recipe-bank selection, and different profiles can
            // refer to the same camera. Serialize capture itself across Gear Backup processes.
            string mutexName = @"Local\GearBackup.X100VISettings." + Path.GetFileName(MirrorFolder(Devices.X100VI, cameras[0]));
            using (var mutex = new System.Threading.Mutex(false, mutexName))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (System.Threading.AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Another Gear Backup instance is reading this camera's settings.");
                try { using (var camera = new FujiPtp(cameras[0].InterfacePath)) files = CaptureFujiSettings(camera); }
                finally { mutex.ReleaseMutex(); }
            }
            OperationContext.Current.CheckCancellation();
            return SaveFujiSettings(files, history, config);
        }

        internal static OperationResult SaveFujiSettings(Dictionary<string, byte[]> files, string history, Config config)
        {
            if (!EnsureBackupFolder(config)) return BackupFolderCancelled();
            string staging = Path.Combine(Path.GetTempPath(), "GearBackup-camera-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                RejectRootLinks(staging);
                foreach (var file in files)
                    using (var output = new FileStream(SafePath(staging, file.Key), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        output.Write(file.Value, 0, file.Value.Length);
                using (LockUnlessPreview(history))
                {
                    OperationResult result = RunBackup(staging, history, null, false, config);
                    if (result.Title != null && !result.Cancelled && result.ExitCode == 0)
                    {
                        result.Title = "X100VI settings backed up";
                        result.Summary = "Saved C1–C7 recipes and current shooting settings. Camera selector restored and shooting values verified.";
                    }
                    return result;
                }
            }
            finally
            {
                RejectRootLinks(staging);
                foreach (string name in files.Keys) File.Delete(SafePath(staging, name));
                Directory.Delete(staging); // Don't recursively delete anything unexpected in the temporary folder.
            }
        }
    }

}

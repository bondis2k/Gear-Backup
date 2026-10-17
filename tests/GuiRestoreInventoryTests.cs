using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using GearBackup;
using GearBackup.Gui;

partial class Tests
{
    static RestoreSection RestoreInventory(MainForm form)
    { return (RestoreSection)typeof(MainForm).GetField("restoreSection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }

    static GuiBackgroundWork.Request RestoreRead(RestoreSection section, string name)
    { return ((GuiBackgroundWork)typeof(RestoreSection).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(section)).Current; }

    static bool RestoreReadDisposed(GuiBackgroundWork.Request request) { return request.IsDisposed; }

    static RestoreSet InventoryPoint(string backup, DeviceProfile device, OperationContext context)
    {
        var set = new RestoreSet { Backup = backup, Device = device ?? Devices.M8, Notice = "Test inventory" };
        set.Points.Add(new RestorePoint { Id = "current", Label = Path.GetFileName(backup), Snapshot = new Snapshot() });
        return set;
    }

    [Test("Restore inventory: location changes cancel and discard old reads", Gui = true)]
    static void TestRestoreInventoryLocations(string root)
    {
        string first = Folder(root, "first"), second = Folder(root, "second");
        string stale = Folder(first, "history\\2026-01-01"), latest = Folder(second, "history\\2026-01-02");
        using (var entered = new ManualResetEventSlim())
        using (var cancelled = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var form = new MainForm(true, new Config { BackupFolder = first, SettingsDirectory = root }))
        {
            var section = RestoreInventory(form);
            section.BackupReader = (options, device, context) => {
                if (options.BackupFolder == first)
                {
                    var token = context.Cancellation;
                    entered.Set();
                    if (!token.WaitHandle.WaitOne(5000)) throw new Exception("Backup discovery was not cancelled.");
                    cancelled.Set();
                    if (!release.Wait(5000)) throw new Exception("Old discovery was not released.");
                    return new List<string> { stale }; // Even a reader that returns after cancellation must be ignored.
                }
                return new List<string> { latest };
            };
            section.PointReader = InventoryPoint;
            form.ShowDevice(Devices.M8); form.Show(); form.SelectOperation(GuiOperation.Restore);
            try
            {
                WaitForGui(() => entered.IsSet, "initial backup discovery starts");
                var oldRead = RestoreRead(section, "backupRead");
                ((TextBox)form.Controls.Find("BackupFolder", true)[0]).Text = second;
                WaitForGui(() => cancelled.IsSet && section.Ready, "replacement location loads while old reader is cancelled");
                Assert(section.CaptureSelection().Backup == latest, "the replacement location supplies the selected backup");
                release.Set();
                WaitForGui(() => RestoreReadDisposed(oldRead), "superseded discovery is disposed after returning");
                Assert(section.CaptureSelection().Backup == latest && !form.Controls.Find("RestoreNotice", true)[0].Text.Contains("cancel"),
                    "late backup results and cancellations cannot replace the latest selection or notice");
            }
            finally { release.Set(); form.Close(); }
        }
    }

    [Test("Restore inventory: changing backup cancels point loading and close stops readers", Gui = true)]
    static void TestRestoreInventoryPoints(string root)
    {
        string a = Folder(root, "history\\2026-01-01"), b = Folder(root, "history\\2026-01-02");
        using (var entered = new ManualResetEventSlim())
        using (var cancelled = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var closeEntered = new ManualResetEventSlim())
        using (var closeStopped = new ManualResetEventSlim())
        using (var form = new MainForm(true, new Config { BackupFolder = root, SettingsDirectory = root }))
        {
            var section = RestoreInventory(form);
            section.BackupReader = (options, device, context) => new List<string> { a, b };
            section.PointReader = (backup, device, context) => {
                if (backup == a)
                {
                    var token = context.Cancellation; entered.Set();
                    if (!token.WaitHandle.WaitOne(5000)) throw new Exception("Point loading was not cancelled.");
                    cancelled.Set();
                    if (!release.Wait(5000)) throw new Exception("Old point reader was not released.");
                }
                return InventoryPoint(backup, device, context);
            };
            form.ShowDevice(Devices.M8); form.Show(); form.SelectOperation(GuiOperation.Restore);
            try
            {
                WaitForGui(() => entered.IsSet, "first backup's points start loading");
                var oldRead = RestoreRead(section, "pointRead");
                ((ComboBox)form.Controls.Find("RestoreBackup", true)[0]).SelectedIndex = 1;
                WaitForGui(() => cancelled.IsSet && section.Ready, "second backup's points load");
                release.Set(); WaitForGui(() => RestoreReadDisposed(oldRead), "obsolete point reader is disposed");
                Assert(section.CaptureSelection().Backup == b && form.Controls.Find("RestorePoint", true)[0].Text == "2026-01-02",
                    "the selected backup and its points remain consistent after the old reader returns");
                section.PointReader = (backup, device, context) => {
                    if (device == Devices.TrackerMini) return InventoryPoint(backup, device, context);
                    var token = context.Cancellation; closeEntered.Set();
                    try
                    {
                        if (!token.WaitHandle.WaitOne(5000)) throw new Exception("Closing did not cancel point loading.");
                        token.ThrowIfCancellationRequested(); return null;
                    }
                    finally { closeStopped.Set(); }
                };
                section.RefreshBackups(true);
                WaitForGui(() => closeEntered.IsSet, "point read starts before device changes");
                var deviceRead = RestoreRead(section, "pointRead");
                form.ShowDevice(Devices.TrackerMini);
                WaitForGui(() => closeStopped.IsSet && section.Ready && RestoreReadDisposed(deviceRead), "device change cancels the previous point read");
                Assert(section.CaptureSelection().DeviceId == Devices.TrackerMini.Id, "old device results cannot replace the new device's inventory");
                closeEntered.Reset(); closeStopped.Reset();
                form.ShowDevice(Devices.M8);
                WaitForGui(() => closeEntered.IsSet, "point read starts before window closes");
                form.Close(); WaitForGui(() => closeStopped.IsSet, "window close cancels point read");
                Assert(!section.Ready, "closed inventory cannot enable a restore");
            }
            finally { release.Set(); form.Close(); }
        }
    }

    [Test("Restore inventory: invalid options and disposal cancel discovery", Gui = true)]
    static void TestRestoreInventoryInvalidAndDisposed(string root)
    {
        using (var entered = new ManualResetEventSlim())
        using (var stopped = new ManualResetEventSlim())
        using (var form = new MainForm(true, new Config { BackupFolder = Folder(root, "first"), SettingsDirectory = root }))
        {
            var section = RestoreInventory(form);
            section.BackupReader = (options, device, context) => {
                var token = context.Cancellation; entered.Set();
                try
                {
                    if (!token.WaitHandle.WaitOne(5000)) throw new Exception("Discovery was not cancelled.");
                    token.ThrowIfCancellationRequested(); return null;
                }
                finally { stopped.Set(); }
            };
            form.ShowDevice(Devices.M8); form.Show(); form.SelectOperation(GuiOperation.Restore);
            WaitForGui(() => entered.IsSet, "backup discovery starts");
            ((TextBox)form.Controls.Find("Profile", true)[0]).Text = "invalid/name";
            ((TextBox)form.Controls.Find("BackupFolder", true)[0]).Text = Folder(root, "second");
            WaitForGui(() => stopped.IsSet, "invalid options cancel the preceding discovery");
            Assert(!section.Ready && ((ComboBox)form.Controls.Find("RestorePoint", true)[0]).Items.Count == 0 &&
                form.Controls.Find("RestoreNotice", true)[0].Text.StartsWith("Cannot list backups"), "invalid options clear stale restore choices and explain the error");
            ((TextBox)form.Controls.Find("Profile", true)[0]).Text = "";
            entered.Reset(); stopped.Reset(); section.RefreshBackups(true);
            WaitForGui(() => entered.IsSet, "new discovery starts before disposal");
            form.Dispose(); WaitForGui(() => stopped.IsSet, "disposal cancels backup discovery");
            Assert(!section.Ready, "disposed inventory remains unavailable");
        }
    }
}

using System;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The window's side of the Restore area: it owns the sections and answers what they ask of the window.
    internal sealed partial class MainForm
    {
        readonly RestoreSection restoreSection;
        // 0 restore everything, 1 restore chosen files: what the Restore operation last used (the mode box also offers ZIP and Check).
        int rememberedRestoreScope;

        MainForm IRestoreSectionHost.Window { get { return this; } }
        bool IRestoreSectionHost.WindowReady { get { return !IsDisposed && IsHandleCreated; } }
        int IRestoreSectionHost.RestoreScope { get { return rememberedRestoreScope; } }
        void IRestoreSectionHost.SetDetail(string text) { detail.Text = text; }

        void PrepareSampleRestore(M8SampleMatch match, string cardRoot, Config searchConfig)
        {
            if (runner.IsBusy) return;
            backupFolder.Text = searchConfig.BackupFolder;
            restoreDestination.Text = cardRoot ?? "";
            gearIcons.SetActive(GearIconStrip.Gears[0]);
            rememberedRestoreScope = 1;
            restoreMode.SelectedIndex = 1;
            SelectOperation(GuiOperation.Restore);
            restoreSection.SelectSample(match);
        }

        void RefreshRestoreControls()
        {
            zipRestore.SetVisible(Selected == GuiOperation.RestoreZip);
            restoreSection.RefreshControls();
        }

        void RefreshRestoreActionState()
        {
            if (Selected == GuiOperation.RestoreZip) { start.Enabled = preview.Enabled = zipRestore.Ready; return; }
            // Restore holds its action buttons back until a point is chosen; the other operations take them back.
            if (Selected != GuiOperation.Restore)
            {
                start.Enabled = !runner.IsBusy;
                preview.Enabled = !runner.IsBusy && Selected != GuiOperation.Check;
                return;
            }
            start.Enabled = preview.Enabled = restoreSection.Ready;
        }

        void UpdateRestoreSelectionNotice() { restoreSection.UpdateNotice(); }
        void RefreshRestoreBackups(bool force = false) { restoreSection.RefreshBackups(force); }
        RestoreRequest CaptureRestoreSelection() { return restoreSection.CaptureSelection(); }

        void RestoreUi(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (!InvokeRequired) { action(); return; }
            try { BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing) action(); }); }
            catch (InvalidOperationException) { } // The window closed while the read-only scan finished.
        }
    }
}

using System;
using System.Threading;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The window's side of the ZIP restore section: it owns the section and answers what the section asks of the window.
    internal sealed partial class MainForm : IRestoreSectionHost
    {
        readonly ZipRestoreSection zipRestore;

        // Tests replace the file dialog, the reader and the device check.
        internal Func<string> ArchiveSourcePicker { get { return zipRestore.SourcePicker; } set { zipRestore.SourcePicker = value; } }
        internal Func<string, CancellationToken, ZipRestoreInfo> ArchiveInspector { get { return zipRestore.Inspector; } set { zipRestore.Inspector = value; } }
        internal Action<SourceChoice> ArchiveRestoreDeviceValidator { get { return zipRestore.DeviceValidator; } set { zipRestore.DeviceValidator = value; } }
        internal void ValidateArchiveRestoreDevice(SourceChoice known) { zipRestore.ValidateDevice(known); }
        internal bool ArchiveRestoreReady { get { return zipRestore.Ready; } }
        internal bool ArchiveLoading { get { return zipRestore.Loading; } }
        internal ZipRestoreInfo ArchiveInfo { get { return zipRestore.Info; } }

        DeviceProfile IRestoreSectionHost.DisplayedDevice { get { return displayedDevice; } }
        string IRestoreSectionHost.RestoreDestinationText { get { return restoreDestination.Text; } }
        void IRestoreSectionHost.RefreshActionState() { RefreshRestoreActionState(); }
        IOperationInteraction IRestoreSectionHost.QuietInteraction() { return new GuiInteraction(this, p => { }); }
    }
}

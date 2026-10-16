using System;
using System.Drawing;
using System.Windows.Forms;

namespace GearBackup.Gui
{
    // The window services a tab module needs; MainForm implements them. A tab keeps its own controls and logic and
    // knows nothing else about the window.
    internal interface IWorkflowTabHost
    {
        Font Font { get; }
        ToolTip Hints { get; }
        IWin32Window Owner { get; }
        bool Busy { get; }
        // Runs the action on the window's thread, unless the window has closed.
        void RunOnUi(Action action);
        // Asks the window to resize its tab area after the tab's content changed.
        void RefreshLayout();
        // While held the window keeps its picture and paints once when released.
        IDisposable FreezeRedraw();
    }

    // What a section of the Restore area (it shares the window's inputs with other operations) asks of the window.
    internal interface IRestoreSectionHost : IWorkflowTabHost
    {
        // The device that is currently identified, or null.
        DeviceProfile DisplayedDevice { get; }
        // The folder the user chose to restore into.
        string RestoreDestinationText { get; }
        // Enables or disables Start and Preview from the current state.
        void RefreshActionState();
        // An interaction that shows nothing, for background reads.
        IOperationInteraction QuietInteraction();
    }

    // One tab of the workflow area, built once and then driven by its own events.
    internal interface IWorkflowTab
    {
        Control Build();
        // The height the tab's content needs, so the window can size the tab area.
        int ContentHeight { get; }
    }

}

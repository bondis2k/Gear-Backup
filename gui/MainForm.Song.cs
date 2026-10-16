using System;

using System.Windows.Forms;



namespace GearBackup.Gui
{
    // The window's side of the tab modules: the services they ask for, and the Song tab itself.

    internal sealed partial class MainForm : IWorkflowTabHost

    {

        const int SongTabIndex = 3;

        readonly SongTab songTab;



        // Tests replace the song file dialog.

        internal Func<string> SongFilePicker { get { return songTab.FilePicker; } set { songTab.FilePicker = value; } }



        ToolTip IWorkflowTabHost.Hints { get { return hints; } }

        IWin32Window IWorkflowTabHost.Owner { get { return this; } }

        bool IWorkflowTabHost.Busy { get { return runner.IsBusy; } }

        void IWorkflowTabHost.RunOnUi(Action action) { RestoreUi(action); }

        void IWorkflowTabHost.RefreshLayout() { RefreshWorkflowLayout(); }

        IDisposable IWorkflowTabHost.FreezeRedraw() { return new RedrawFreeze(this); }

    }


}

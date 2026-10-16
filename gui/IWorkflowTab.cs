using System;
using System.Drawing;
using System.Windows.Forms;

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

// One tab of the workflow area, built once and then driven by its own events.
internal interface IWorkflowTab
{
    Control Build();
    // The height the tab's content needs, so the window can size the tab area.
    int ContentHeight { get; }
}

using System;
using static PathSafety;
using static ConsoleUi;
using static Session;

// The CLI picks its source here; the GUI has its own source box and detection button.
static class SourceSelection
{
    // The CLI's source: SourceFolder if set, otherwise a detected device, otherwise a path typed by the
    // user. Null if cancelled. An MTP device's mirror isn't read yet: RunPrepared syncs it.
    internal static SourceChoice SelectSource(Config config)
    {
        if (config.SourceFolder.Length > 0) return new SourceChoice { Path = config.SourceFolder };
        SourceChoice found = null;
        try { found = DetectSource(); }
        catch (Exception ex) { OperationOutput.WriteLine("  Automatic detection failed: " + ex.Message); }
        if (found != null)
        {
            if (found.Mtp != null)
                OperationOutput.WriteLine("  " + found.Mtp.Device.Name + " found over MTP (" + found.Mtp.Info.FriendlyName + ", serial " + found.Mtp.Info.Serial + ").");
            if (found.CameraMode != null) OperationOutput.WriteLine("  Camera connection: " + found.CameraMode + ".");
            return found;
        }
        OperationOutput.WriteLine("  No " + Devices.Names + " detected. Select an SD card in a reader or a folder instead.");
        string typed = ExpandPath(AskText("Source path (Enter = cancel):"));
        return typed.Length == 0 ? null : new SourceChoice { Path = typed };
    }
}

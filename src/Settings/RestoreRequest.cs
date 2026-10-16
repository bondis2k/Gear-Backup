namespace GearBackup
{
    // A restore chosen in the GUI, carried in the settings it runs with.
    internal sealed class RestoreRequest { internal string Backup, DeviceId, Point, Stamp; internal string[] Paths; internal bool Exact; }

}

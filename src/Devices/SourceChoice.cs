namespace GearBackup
{
    // Where an operation's files come from (or, for a restore, go to).
    internal sealed class SourceChoice
    {
        public string Path;             // the drive or folder; for an MTP device, its mirror
        public string PnpId;            // set only for a detected drive, which can then be ejected
        public DeviceProfile Detected;  // the detected device; null for a folder chosen by hand
        public MtpSource Mtp;           // set for an MTP device, whose mirror is synced before a backup
        public string CameraMode;       // the X100VI's connection mode, read once at detection
        public bool CameraSettingsMode;
    }

}

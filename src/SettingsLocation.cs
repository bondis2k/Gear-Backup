using System;
using System.IO;
using static PathSafety;

partial class GearBackup
{
    static string DefaultSettingsDirectory()
    {
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        return File.Exists(Path.Combine(executableDirectory, AppIdentity.InstalledMarker))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppIdentity.DirectoryName)
            : executableDirectory;
    }

    static Config LoadApplicationConfig(string executableDirectory, string roamingDirectory)
    {
        if (!File.Exists(Path.Combine(executableDirectory, AppIdentity.InstalledMarker)))
            return LoadConfig(executableDirectory);

        string settingsDirectory = Path.Combine(roamingDirectory, AppIdentity.DirectoryName);
        if (!PreviewOnly)
        {
            Directory.CreateDirectory(settingsDirectory);
            foreach (string name in new[] { AppIdentity.ConfigFileName, AppIdentity.ProfileConfigFileName, AppIdentity.Legacy.ConfigFileName })
            {
                string source = Path.Combine(executableDirectory, name), destination = Path.Combine(settingsDirectory, name);
                if (File.Exists(source) && !File.Exists(destination))
                {
                    RejectRootLinks(source);
                    File.Copy(source, destination, false);
                }
            }
        }
        // Preview can read previous settings without creating directories or migrating files.
        return LoadConfig(settingsDirectory, executableDirectory);
    }

    static string SettingsReadPath(Config config, string name)
    {
        string path = Path.Combine(config.SettingsDirectory ?? DefaultSettingsDirectory(), name);
        if (!File.Exists(path) && config.PreviousSettingsDirectory != null)
        {
            string previous = Path.Combine(config.PreviousSettingsDirectory, name);
            if (File.Exists(previous)) return previous;
        }
        return path;
    }
}

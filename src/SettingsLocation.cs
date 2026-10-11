using System;
using System.IO;
using static AppSettings;
using static Workflow;

static class SettingsLocation
{
    internal static string DefaultSettingsDirectory()
    {
        string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        return File.Exists(Path.Combine(executableDirectory, AppIdentity.InstalledMarker))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppIdentity.DirectoryName)
            : executableDirectory;
    }

    internal static Config LoadApplicationConfig(string executableDirectory, string roamingDirectory)
    {
        if (!File.Exists(Path.Combine(executableDirectory, AppIdentity.InstalledMarker)))
            return LoadConfig(executableDirectory);

        string settingsDirectory = Path.Combine(roamingDirectory, AppIdentity.DirectoryName);
        if (!PreviewOnly) Directory.CreateDirectory(settingsDirectory);
        return LoadConfig(settingsDirectory);
    }

    internal static string SettingsReadPath(Config config, string name)
    {
        return Path.Combine(config.SettingsDirectory ?? DefaultSettingsDirectory(), name);
    }
}

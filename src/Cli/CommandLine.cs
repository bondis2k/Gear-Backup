using System;
using System.Collections.Generic;
using static AppSettings;

// What was typed on the command line, and which combinations of options are refused.
sealed class CommandLine
{
    internal bool Restore, Check, Archive, CameraSettings, Preview, NoPause, Zip, SourceGiven, ProfileGiven;
    internal string RebuildCatalog, Output, Name;

    static readonly string[] Switches = { "--restore", "--check", "--camera-settings", "--zip-archive", "--zip", "--preview", "--no-pause" };

    // Reads the options. --source, --profile and --zip also change the settings they stand for.
    internal static CommandLine Parse(string[] args, Config config)
    {
        var line = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--rebuild-catalog")
            {
                if (++i >= args.Length) throw new ArgumentException("Missing backup folder for --rebuild-catalog.");
                line.RebuildCatalog = args[i];
            }
            else if (arg == "--source" || arg == "--profile")
            {
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + arg);
                ApplySetting(config, arg == "--source" ? "SourceFolder" : "Profile", args[i]);
                if (arg == "--source") line.SourceGiven = true; else line.ProfileGiven = true;
            }
            else if (arg == "--output" || arg == "--name")
            {
                if (++i >= args.Length || args[i].StartsWith("--") || string.IsNullOrWhiteSpace(args[i]))
                    throw new ArgumentException("Missing value for " + arg);
                if (arg == "--output") line.Output = args[i]; else line.Name = BackupSetNames.Validate(args[i]);
            }
            else if (Array.IndexOf(Switches, arg) < 0) throw new ArgumentException("Unknown argument: " + arg);
            else if (arg == "--restore") line.Restore = true;
            else if (arg == "--check") line.Check = true;
            else if (arg == "--camera-settings") line.CameraSettings = true;
            else if (arg == "--zip-archive") line.Archive = true;
            else if (arg == "--preview") line.Preview = true;
            else if (arg == "--no-pause") line.NoPause = true;
            else { line.Zip = true; config.Zip = true; }
        }
        return line;
    }

    // Refuses options that don't go together. Call after the Restore and Check flags are final.
    internal void Validate(Config config)
    {
        if (Restore && Check) throw new ArgumentException("Choose --restore or --check, not both.");
        if (!Archive && (Output != null || Name != null))
            throw new ArgumentException("--output and --name apply only to --zip-archive.");
        if (Archive && (Restore || Check || RebuildCatalog != null || config.Zip || CameraSettings))
            throw new ArgumentException("--zip-archive cannot be combined with --restore, --check, --camera-settings, --rebuild-catalog or --zip.");
        if (RebuildCatalog != null && (Restore || Check || CameraSettings || SourceGiven || ProfileGiven || Zip))
            throw new ArgumentException("--rebuild-catalog accepts only a backup folder, --preview and --no-pause.");
        if (CameraSettings && (Restore || Check || SourceGiven))
            throw new ArgumentException("--camera-settings cannot be combined with --restore, --check or --source.");
        if (config.Zip && (Restore || Check)) throw new ArgumentException("--zip applies to backups, not to --restore or --check.");
    }
}

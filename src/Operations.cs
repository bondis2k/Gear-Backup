using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

partial class M8Backup
{
    // Frontends supply decisions/output; engines never read the console themselves.
    // How the engines talk to a front end. The CLI implements it with the console (ConsoleInteraction),
    // the GUI with dialogs (GuiInteraction), and the tests with a scripted fake (TestInteraction).
    internal interface IOperationInteraction
    {
        void Write(string text);
        void WriteLine(string text);
        void WriteColored(string text, ConsoleColor color);
        string ReadLine();
        // One of the characters in `allowed`, e.g. "rkc" for Replace / Keep / Cancel.
        char ChooseKey(string question, string allowed);
        // 1..max from a numbered list printed just before; 0 means "none" / Enter.
        int ChooseNumber(string question, int max);
        // Shows the planned actions; true to go ahead.
        bool ReviewPlan(OperationPlan plan);
        void ReportProgress(OperationProgress progress);
    }

    // The list of actions a backup or restore is about to take, for the preview.
    internal sealed class OperationPlan
    {
        public string Label;
        public List<string> Actions;
        public long Bytes;
        // ShowDetails: Preview=yes, list every action and ask. PreviewOnly: --preview, show and stop.
        public bool ShowDetails, PreviewOnly;
    }

    internal sealed class OperationProgress
    {
        public string Stage, Path;
        // True during full backups: show overall progress only, not every file's copy progress.
        public bool OverallOnly;
        public long Bytes, TotalBytes;
        public int Files, TotalFiles;
    }

    internal sealed class OperationResult
    {
        public int ExitCode;
        public string Title, Summary;
        public bool Cancelled;
        public TimeSpan Elapsed;
    }

    // Everything an operation needs from its caller: the front end, the cancellation token and per-run
    // flags. Engines reach it through OperationContext.Current instead of passing it to every helper.
    internal sealed class OperationContext
    {
        // Per thread, because the GUI runs operations on a worker thread. When nothing has been activated
        // (simple CLI paths), a console context is created on first use.
        [ThreadStatic] static OperationContext current;
        [ThreadStatic] static OperationContext fallback;
        public static OperationContext Current
        {
            get { return current ?? (fallback ?? (fallback = new OperationContext(new ConsoleInteraction(), CancellationToken.None))); }
        }
        public readonly IOperationInteraction Interaction;
        public readonly CancellationToken Cancellation;
        public bool PreviewOnly;
        public bool OverallProgressOnly;
        // Folders already found not to be links during this operation, so path checks
        // don't repeat the same file-system lookups for every file.
        public readonly HashSet<string> VerifiedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public OperationContext(IOperationInteraction interaction, CancellationToken cancellation)
        {
            if (interaction == null) throw new ArgumentNullException("interaction");
            Interaction = interaction;
            Cancellation = cancellation;
        }
        public void CheckCancellation() { Cancellation.ThrowIfCancellationRequested(); }
        // Reports progress, and checks for cancellation before and after, so a Cancel pressed while the
        // front end handles the update stops the operation right there.
        public void Progress(string stage, string path, long bytes, long totalBytes, int files = 0, int totalFiles = 0)
        {
            CheckCancellation();
            Interaction.ReportProgress(new OperationProgress { Stage = stage, Path = path,
                OverallOnly = OverallProgressOnly,
                Bytes = bytes, TotalBytes = totalBytes, Files = files, TotalFiles = totalFiles });
            CheckCancellation();
        }
        // Makes this the current context until the returned scope is disposed (scopes nest).
        public IDisposable Activate() { return new ContextScope(this); }
        sealed class ContextScope : IDisposable
        {
            readonly OperationContext previous;
            public ContextScope(OperationContext next) { previous = current; current = next; }
            public void Dispose() { current = previous; }
        }
    }

    // Console-like output that goes to whichever front end is current.
    static class OperationOutput
    {
        public static void Write(string text) { OperationContext.Current.Interaction.Write(text); }
        public static void Write(string format, params object[] values) { Write(string.Format(format, values)); }
        public static void WriteLine() { WriteLine(""); }
        public static void WriteLine(object value) { WriteLine(value == null ? "" : value.ToString()); }
        public static void WriteLine(string text) { OperationContext.Current.Interaction.WriteLine(text); }
        public static void WriteLine(string format, params object[] values) { WriteLine(string.Format(format, values)); }
        public static string ReadLine() { OperationContext.Current.CheckCancellation(); return OperationContext.Current.Interaction.ReadLine(); }
    }


}

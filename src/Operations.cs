using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

partial class M8Backup
{
    // Frontends supply decisions/output; engines never read the console themselves.
    internal interface IOperationInteraction
    {
        void Write(string text);
        void WriteLine(string text);
        void WriteColored(string text, ConsoleColor color);
        string ReadLine();
        char ChooseKey(string question, string allowed);
        int ChooseNumber(string question, int max);
        bool ReviewPlan(OperationPlan plan);
        void ReportProgress(OperationProgress progress);
    }

    internal sealed class OperationPlan
    {
        public string Label;
        public List<string> Actions;
        public long Bytes;
        public bool ShowDetails, PreviewOnly;
    }

    internal sealed class OperationProgress
    {
        public string Stage, Path;
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

    internal sealed class OperationContext
    {
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
        public OperationContext(IOperationInteraction interaction, CancellationToken cancellation)
        {
            if (interaction == null) throw new ArgumentNullException("interaction");
            Interaction = interaction;
            Cancellation = cancellation;
        }
        public void CheckCancellation() { Cancellation.ThrowIfCancellationRequested(); }
        public void Progress(string stage, string path, long bytes, long totalBytes, int files = 0, int totalFiles = 0)
        {
            CheckCancellation();
            Interaction.ReportProgress(new OperationProgress { Stage = stage, Path = path,
                OverallOnly = OverallProgressOnly,
                Bytes = bytes, TotalBytes = totalBytes, Files = files, TotalFiles = totalFiles });
            CheckCancellation();
        }
        public IDisposable Activate() { return new ContextScope(this); }
        sealed class ContextScope : IDisposable
        {
            readonly OperationContext previous;
            public ContextScope(OperationContext next) { previous = current; current = next; }
            public void Dispose() { current = previous; }
        }
    }

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static ConsoleUi;
using static PlanReview;

// Frontends supply decisions/output; engines never read the console themselves.
// How the engines talk to a front end. The CLI implements it with the console (ConsoleInteraction),
// the GUI with dialogs (GuiInteraction), and the tests with a scripted fake (TestInteraction).
internal interface IOperationInteraction
{
    void Write(string text);
    void WriteLine(string text);
    void WriteColored(string text, ConsoleColor color);
    // A question with a fixed set of answers; returns the chosen option's Key.
    char Choose(string question, IList<Option> options);
    // Picks one of `items`: 1..items.Count. Returns 0 for `noneLabel` (e.g. "Search again"), or, when
    // noneLabel is null, for Enter in the console. Cancelling a GUI dialog cancels the operation.
    int ChooseItem(string question, IList<string> items, string noneLabel);
    // Free text such as a search or a path; null when cancelled.
    string AskText(string question);
    // Shows the planned actions; true to go ahead.
    bool ReviewPlan(OperationPlan plan);
    void ReportProgress(OperationProgress progress);
}

// The list of actions a backup or restore is about to take, for the preview.
// One answer to a question: the key the console accepts, and the label both front ends show.
// The console brackets the key's letter in the label ("[R]eplace them"); the GUI makes a button.
internal sealed class Option
{
    public readonly char Key;
    public readonly string Label;
    public Option(char key, string label) { Key = key; Label = label; }
}

// The list of actions a backup or restore is about to take, for the preview.
internal sealed class OperationPlan
{
    public string Label;
    public List<string> Actions;
    public Dictionary<string, long> FileSizes;
    public long Bytes;
    // ShowDetails: Preview=yes, list every action and ask. PreviewOnly: --preview, show and stop.
    public bool ShowDetails, PreviewOnly;
}

internal sealed class OperationProgress
{
    public Stage Stage;
    public string Path;
    // True during full backups: show overall progress only, not every file's copy progress.
    public bool OverallOnly;
    public long Bytes, TotalBytes;
    public int Files, TotalFiles;
}

internal sealed class OperationResult
{
    public int ExitCode;
    public string Title, Summary;
    public string OutputFolder;
    public OperationCounts Counts;
    public bool Cancelled;
    public TimeSpan Elapsed;
}

internal sealed class OperationCounts
{
    public int Copied, Unchanged, Removed, Failed;
}

// Everything an operation needs from its caller: the front end, the cancellation token and per-run
// flags. Engines reach it through OperationContext.Current instead of passing it to every helper.
internal sealed class OperationContext
{
    // Per thread, because the GUI runs operations on a worker thread. Every operation starts its own
    // context (the CLI's Main, the GUI's operation runner and tool windows, each test), so state such as
    // the device, preview mode and caches never outlives the operation it belongs to.
    [ThreadStatic] static OperationContext current;
    public static OperationContext Current
    {
        get
        {
            if (current == null) throw new InvalidOperationException("No operation is running: start one with new OperationContext(...).Activate().");
            return current;
        }
    }
    // The running operation, or null outside one; only for work that is also done outside operations,
    // such as saving settings from a window.
    public static OperationContext Active { get { return current; } }
    public readonly IOperationInteraction Interaction;
    public readonly CancellationToken Cancellation;
    public bool PreviewOnly;
    public bool OverallProgressOnly;
    // The kind of device the source is (see Devices.cs); the M8 unless a front end sets another.
    public DeviceProfile Device = Devices.M8;
    // Buffers belong to this operation, never to another worker or thread. Rent separately so a
    // nested operation (for example from a progress callback) cannot overwrite an active copy. Copy workers share
    // the pools, so access is locked.
    readonly Stack<byte[]> smallBuffers = new Stack<byte[]>(), largeBuffers = new Stack<byte[]>();
    internal byte[] RentBuffer(bool small)
    {
        var pool = small ? smallBuffers : largeBuffers;
        lock (pool) if (pool.Count != 0) return pool.Pop();
        return new byte[small ? 65536 : FileCopy.ChunkSize];
    }
    internal void ReturnBuffer(byte[] buffer)
    {
        if (buffer == null) return;
        var pool = buffer.Length == 65536 ? smallBuffers : largeBuffers;
        lock (pool) pool.Push(buffer);
    }
    // Histories found in each set of backup locations, so an unreachable location (a disconnected
    // network drive) delays an operation once.
    internal readonly Dictionary<string, List<string>> HistoryCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    public OperationContext(IOperationInteraction interaction, CancellationToken cancellation)
    {
        if (interaction == null) throw new ArgumentNullException("interaction");
        Interaction = interaction;
        Cancellation = cancellation;
    }
    public void CheckCancellation() { Cancellation.ThrowIfCancellationRequested(); }
    // Reports progress, and checks for cancellation before and after, so a Cancel pressed while the
    // front end handles the update stops the operation right there.
    public void Progress(Stage stage, string path, long bytes, long totalBytes, int files = 0, int totalFiles = 0)
    {
        CheckCancellation();
        Interaction.ReportProgress(new OperationProgress { Stage = stage, Path = path,
            OverallOnly = OverallProgressOnly,
            Bytes = bytes, TotalBytes = totalBytes, Files = files, TotalFiles = totalFiles });
        CheckCancellation();
    }
    // Makes this the current context until the returned scope is disposed (scopes nest).
    public IDisposable Activate() { return new ContextScope(this); }
    // Works with another device until the returned scope is disposed, then switches back (restoring a
    // backup made from another device than the source's).
    public IDisposable UseDevice(DeviceProfile device) { return new DeviceScope(this, device); }
    sealed class DeviceScope : IDisposable
    {
        readonly OperationContext context;
        readonly DeviceProfile previous;
        public DeviceScope(OperationContext context, DeviceProfile device) { this.context = context; previous = context.Device; context.Device = device; }
        public void Dispose() { context.Device = previous; }
    }
    sealed class ContextScope : IDisposable
    {
        readonly OperationContext previous;
        public ContextScope(OperationContext next) { previous = current; current = next; }
        public void Dispose() { current = previous; }
    }
}

// Console-like output that goes to whichever front end is current.
internal static class OperationOutput
{
    public static void Write(string text) { OperationContext.Current.Interaction.Write(text); }
    public static void Write(string format, params object[] values) { Write(string.Format(format, values)); }
    public static void WriteLine() { WriteLine(""); }
    public static void WriteLine(object value) { WriteLine(value == null ? "" : value.ToString()); }
    public static void WriteLine(string text) { OperationContext.Current.Interaction.WriteLine(text); }
    public static void WriteLine(string format, params object[] values) { WriteLine(string.Format(format, values)); }
}

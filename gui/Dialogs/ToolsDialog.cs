using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class GearBackup
{
    // The base of the tool windows (Dashboard, Storage, Profiles, X100VI recipes): the same layout and
    // style, a row of action buttons, a notice line, and slow work run on a background thread that closing
    // the window cancels.
    abstract class ToolsDialog : Form
    {
        protected readonly TableLayoutPanel ToolLayout;
        protected readonly FlowLayoutPanel Actions;
        protected readonly Label Notice;
        readonly MainForm owner;
        CancellationTokenSource cancellation;
        bool closing;
        // Progress of the current work: the worker thread stores the latest update, and a timer shows it
        // in the notice line, so many quick updates don't flood the window.
        readonly object progressLock = new object();
        OperationProgress latestProgress;
        readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer { Interval = 200 };

        protected ToolsDialog(MainForm main, string title, string description)
        {
            owner = main;
            Text = title + " · " + AppIdentity.Name;
            Font = new Font("Segoe UI", 10);
            BackColor = GuiTheme.Background;
            ForeColor = GuiTheme.Ink;
            ClientSize = new Size(1000, 650);
            MinimumSize = new Size(680, 440);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            if (GuiHeadless) Opacity = 0;

            // Rows: description, content (added by each window), notice, actions.
            ToolLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 4, ColumnCount = 1 };
            ToolLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ToolLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            ToolLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ToolLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            ToolLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading = GuiTheme.Label(description, 11, GuiTheme.Ink);
            heading.MaximumSize = new Size(940, 0);
            ToolLayout.Controls.Add(heading, 0, 0);
            Notice = GuiTheme.Label("", 9, GuiTheme.Muted);
            Notice.MaximumSize = new Size(940, 130);
            Notice.Name = "ToolNotice";
            ToolLayout.Controls.Add(Notice, 0, 2);
            Actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 12, 0, 0) };
            var close = GuiTheme.Button("Close", false);
            close.Click += delegate { Close(); };
            close.DialogResult = DialogResult.Cancel;
            Actions.Controls.Add(close);
            CancelButton = close;
            ToolLayout.Controls.Add(Actions, 0, 3);
            Controls.Add(ToolLayout);

            Shown += delegate { GuiTheme.FitToScreen(this); };
            progressTimer.Tick += delegate { ShowProgress(); };
            FormClosed += delegate { progressTimer.Dispose(); };
            // Wrap the description and notice to the window's width.
            ToolLayout.SizeChanged += delegate {
                int width = Math.Max(100, ToolLayout.ClientSize.Width - ToolLayout.Padding.Horizontal);
                heading.MaximumSize = new Size(width, 0);
                Notice.MaximumSize = new Size(width, 180);
            };
            // Closing while work runs cancels it; the window closes once the work has stopped.
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (cancellation == null) return;
                closing = true;
                cancellation.Cancel();
                e.Cancel = true;
                Notice.Text = "Stopping safely…";
            };
        }

        protected bool Working { get { return cancellation != null; } }

        // Adds an action button. It does nothing while work runs, and shows any error it throws.
        protected Button ToolButton(string text, string name, Action action)
        {
            var button = GuiTheme.Button(text, false);
            button.Name = name;
            button.Click += delegate {
                if (Working) return;
                try { action(); }
                catch (Exception ex) { ShowToolError(ex); }
            };
            Actions.Controls.Add(button);
            return button;
        }

        protected void ShowToolError(Exception ex)
        {
            Notice.Text = ex.Message;
            if (!GuiHeadless) MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Runs `action` on a background thread with its own operation context, then `complete` with its
        // result on the UI thread. One piece of work at a time.
        protected void Work<T>(Func<T> action, Action<T> complete)
        {
            if (Working) return;
            cancellation = new CancellationTokenSource();
            lock (progressLock) latestProgress = null;
            var context = new OperationContext(new GuiInteraction(owner, value => { lock (progressLock) latestProgress = value; }), cancellation.Token);
            Notice.Text = "Working… Close to cancel.";
            progressTimer.Start();
            Task.Factory.StartNew(delegate {
                T value = default(T);
                Exception error = null;
                try { using (context.Activate()) value = action(); }
                catch (Exception ex) { error = ex; }
                BeginInvoke((Action)delegate {
                    progressTimer.Stop();
                    cancellation.Dispose();
                    cancellation = null;
                    if (closing) { Close(); return; }
                    if (error != null) { ShowToolError(error); return; }
                    complete(value);
                });
            });
        }

        // "Scanning backup · 1,234 files · C:\...6-10-10" while work runs.
        void ShowProgress()
        {
            OperationProgress value;
            lock (progressLock) value = latestProgress;
            if (value == null || closing || cancellation == null) return;
            string text = "Working… " + value.Stage;
            if (value.TotalFiles > 0) text += " · " + value.Files.ToString("N0") + " / " + value.TotalFiles.ToString("N0");
            if (!string.IsNullOrEmpty(value.Path)) text += " · " + value.Path;
            Notice.Text = text + ". Close to cancel.";
        }

        // A read-only, full-row-select table in the app's style.
        internal static DataGridView ToolGrid(string name)
        {
            return new DataGridView { Name = name, AccessibleName = name, Dock = DockStyle.Fill, BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly = true, MultiSelect = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(5), ForeColor = GuiTheme.Ink,
                    SelectionBackColor = GuiTheme.Accent, SelectionForeColor = Color.White } };
        }
    }
}

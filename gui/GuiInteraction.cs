using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;

partial class M8Backup
{
    sealed class GuiInteraction : IOperationInteraction
    {
        readonly MainForm form;
        readonly Dictionary<int, string> numbered = new Dictionary<int, string>();
        public GuiInteraction(MainForm owner) { form = owner; }
        public void Write(string text) { form.AppendActivity(text); }
        public void WriteLine(string text)
        {
            var match = Regex.Match(text, @"^\s*(\d+)\)\s*(.*)$");
            if (match.Success) numbered[int.Parse(match.Groups[1].Value)] = match.Groups[2].Value;
            form.AppendActivity(text + Environment.NewLine);
        }
        public void WriteColored(string text, ConsoleColor color) { WriteLine(text); }
        public void ReportProgress(OperationProgress progress) { form.UpdateProgress(progress); }
        public string ReadLine()
        {
            return form.OnUi(delegate {
                using (var dialog = new SearchDialog("Find a file", "Search by name or path. Leave blank to list songs."))
                    return dialog.ShowDialog(form) == DialogResult.OK ? dialog.Input.Text : "q";
            });
        }
        public char ChooseKey(string question, string allowed)
        {
            return form.OnUi(delegate {
                using (var dialog = new ChoiceDialog(question, allowed))
                {
                    if (dialog.ShowDialog(form) != DialogResult.OK) throw new OperationCanceledException();
                    return dialog.Key;
                }
            });
        }
        public int ChooseNumber(string question, int max)
        {
            var choices = new List<string>();
            for (int i = 1; i <= max; i++) choices.Add(numbered.ContainsKey(i) ? numbered[i] : i.ToString());
            bool allowCurrentFiles = question.IndexOf("browse current backup files", StringComparison.OrdinalIgnoreCase) >= 0;
            if (allowCurrentFiles) choices.Add("Current backup files (browse without a snapshot)");
            numbered.Clear();
            return form.OnUi(delegate {
                using (var dialog = new SelectionDialog(question, choices))
                {
                    if (dialog.ShowDialog(form) != DialogResult.OK) throw new OperationCanceledException();
                    int selected = dialog.List.SelectedIndex + 1;
                    return allowCurrentFiles && selected > max ? 0 : selected;
                }
            });
        }
        public bool ReviewPlan(OperationPlan plan)
        {
            if (!plan.ShowDetails && !plan.PreviewOnly) return true;
            return form.OnUi(delegate {
                using (var dialog = new PreviewDialog(plan))
                    return dialog.ShowDialog(form) == DialogResult.OK && !plan.PreviewOnly;
            });
        }
    }

}

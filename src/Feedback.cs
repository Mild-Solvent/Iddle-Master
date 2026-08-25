// IDLE MASTER - the bug report door. Something looked wrong; say so without
// leaving the app or hunting for the repo.
//
// The rule of the door: nothing leaves this machine by itself. The button
// opens github.com in the browser with the report pre-typed, and the Submit
// press over there is the sending. The preview shows the report byte for
// byte before that, so there is never a surprise about what travels.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace IdleMaster
{
    internal static class Feedback
    {
        // Where reports land - the same repo the updater watches.
        public static string NewIssueUrl
        {
            get { return "https://github.com/" + Updater.Repo + "/issues/new"; }
        }

        // What a report needs to be debuggable: which build, which Windows,
        // and what the app was saying just before things looked wrong.
        public static string Diagnostics(int logLines)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("app      : IdleMaster v" + App.Version);
            sb.AppendLine("windows  : " + Environment.OSVersion.VersionString
                + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)"));
            sb.AppendLine("elevated : " + (IsElevated() ? "yes" : "no"));
            string tail = LogTail(logLines);
            if (tail.Length > 0)
            {
                sb.AppendLine("--- last " + logLines + " log lines ---");
                sb.Append(tail);
            }
            return sb.ToString();
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        private static string LogTail(int lines)
        {
            try
            {
                if (!File.Exists(App.LogFile)) return "";
                string[] all = File.ReadAllLines(App.LogFile);
                int from = all.Length > lines ? all.Length - lines : 0;
                StringBuilder sb = new StringBuilder();
                for (int i = from; i < all.Length; i++) sb.AppendLine(all[i]);
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        // The whole issue body, as it will appear on GitHub.
        public static string Report(string description, bool attachDiagnostics, int logLines)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(description.Trim().Length > 0 ? description.Trim() : "(no description)");
            if (attachDiagnostics)
            {
                sb.AppendLine();
                sb.AppendLine("```");
                sb.Append(Diagnostics(logLines));
                sb.AppendLine("```");
            }
            return sb.ToString();
        }

        // The first line of what they typed becomes the issue title.
        public static string Title(string description)
        {
            string first = description.Trim();
            int nl = first.IndexOf('\n');
            if (nl >= 0) first = first.Substring(0, nl).Trim();
            if (first.Length == 0) first = "something looked wrong";
            if (first.Length > 80) first = first.Substring(0, 77) + "...";
            return "[bug] " + first;
        }

        // Browsers and GitHub both cap URLs around 8K, so the report shrinks
        // until it fits: fewer log lines first, then a hard cut with a mark.
        public static string IssueUrl(string description, bool attachDiagnostics)
        {
            string title = Title(description);
            int[] tries = { 30, 10, 0 };
            string body = "";
            for (int i = 0; i < tries.Length; i++)
            {
                body = Report(description, attachDiagnostics && tries[i] > 0, tries[i]);
                if (Encode(title, body).Length <= 7500) break;
            }
            string url = Encode(title, body);
            if (url.Length > 7500)
            {
                body = body.Substring(0, 2000) + "\n... (trimmed to fit the URL)";
                url = Encode(title, body);
            }
            return url;
        }

        private static string Encode(string title, string body)
        {
            return NewIssueUrl + "?title=" + Uri.EscapeDataString(title)
                + "&body=" + Uri.EscapeDataString(body);
        }
    }

    // The report page: what looked wrong, what rides along, and the exact
    // bytes that will be sent - then the browser takes over.
    internal sealed class FeedbackForm : Form
    {
        private readonly TextBox what;
        private readonly CheckBox attach;
        private readonly TextBox preview;

        public bool Opened;    // the browser has the report
        public bool Copied;    // the clipboard has it instead

        public FeedbackForm()
        {
            Theme.Form(this);
            Text = "IDLE MASTER - report a bug";
            Size = new Size(600, 640);
            MinimumSize = new Size(480, 480);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("REPORT A BUG");
            cap.SetBounds(16, 12, 300, 18);
            Controls.Add(cap);

            Label hint = Theme.Hint("What looked wrong, and what were you doing when it did?");
            hint.Font = Theme.Small();
            hint.SetBounds(16, 32, 552, 16);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(hint);

            what = new TextBox();
            what.Multiline = true;
            what.ScrollBars = ScrollBars.Vertical;
            what.Font = Theme.Base();
            Theme.Input_(what);
            what.SetBounds(16, 52, 552, 96);
            what.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            what.TextChanged += delegate { Refresh_(); };
            Controls.Add(what);

            attach = new CheckBox();
            attach.Text = "Attach diagnostics - version, Windows, and the last 30 log lines";
            attach.Checked = true;
            attach.SetBounds(18, 156, 552, 22);
            attach.ForeColor = Theme.Fg;
            attach.FlatStyle = FlatStyle.Flat;
            attach.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            attach.Click += delegate { Refresh_(); };
            Controls.Add(attach);

            Label cap2 = Theme.Caption("Exactly what will be sent");
            cap2.SetBounds(16, 186, 300, 18);
            Controls.Add(cap2);

            preview = new TextBox();
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Vertical;
            preview.BackColor = Theme.LogBg;
            preview.ForeColor = Theme.LogFg;
            preview.Font = Theme.Mono();
            preview.BorderStyle = BorderStyle.FixedSingle;
            preview.SetBounds(16, 206, 552, 320);
            preview.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(preview);

            Label foot = Theme.Hint("Opens github.com in your browser with this pre-typed."
                + " Nothing is sent until you press Submit there.");
            foot.Font = Theme.Small();
            foot.SetBounds(16, 534, 552, 16);
            foot.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(foot);

            Button open = Theme.Action("Open GitHub report");
            open.SetBounds(16, 558, 180, 32);
            open.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            open.Click += delegate { OpenReport(); };
            Controls.Add(open);

            Button copy = Theme.Quiet("Copy report");
            copy.SetBounds(204, 558, 120, 32);
            copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            copy.Click += delegate { CopyReport(); };
            Controls.Add(copy);

            Button cancel = Theme.Quiet("Cancel");
            cancel.SetBounds(448, 558, 120, 32);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);

            CancelButton = cancel;
            Refresh_();
        }

        private void Refresh_()
        {
            preview.Text = Feedback.Report(what.Text, attach.Checked, 30)
                .Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        private void OpenReport()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Feedback.IssueUrl(what.Text, attach.Checked))
                { UseShellExecute = true });
                Opened = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the browser: " + ex.Message.Split('\n')[0]
                    + "\n\nUse 'Copy report' and paste it at\n" + Feedback.NewIssueUrl,
                    "Report a bug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyReport()
        {
            try
            {
                Clipboard.SetText("Title: " + Feedback.Title(what.Text) + Environment.NewLine
                    + Environment.NewLine + preview.Text);
                Copied = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not reach the clipboard: " + ex.Message.Split('\n')[0],
                    "Report a bug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}

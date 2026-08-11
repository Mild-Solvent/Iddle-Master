// IDLE MASTER - every window in the app.
//
//   AskForm           : the countdown toast the sentry raises for newcomers.
//   MemGauge          : the RAM bar, custom-painted so it neither flickers nor lies.
//   PickForm          : pick processes/services off the machine for a list.
//   ListPane          : one editable ini section inside the advanced window.
//   QuickSettingsForm : the handful of switches most people actually touch.
//   ConfigForm        : the whole config - reached via "Advanced settings".
//   EatersForm        : the live task manager behind "What's eating RAM?".
//   MainForm          : gauge, mode buttons, and the log console front and centre.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace IdleMaster
{
    // ------------------------------------------------------------------ dialog

    // The toast that appears when something you started lands on a kill list.
    // Bottom-right, always on top, counts down, and defaults to leaving it alone -
    // the tool should never be the reason you lost work you were in the middle of.
    internal sealed class AskForm : Form
    {
        private static readonly List<AskForm> Open = new List<AskForm>();

        private readonly System.Windows.Forms.Timer countdown;
        private readonly Label ticker;
        private int left;

        public Verdict Choice = Verdict.NoAnswer;

        public AskForm(Question q, int seconds)
        {
            left = seconds;

            Theme.Form(this);
            Text = "Idle Master";
            BackColor = Theme.Panel;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(430, 176);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

            Label head = new Label();
            head.Text = q.What.Name;
            head.Font = Theme.Big();
            head.ForeColor = Theme.Accent;
            head.SetBounds(16, 14, 400, 26);
            Controls.Add(head);

            string size = Engine.Size(q.What.Bytes);
            string many = q.What.Pids.Count > 1 ? q.What.Pids.Count + " processes, " : "";
            Label body = new Label();
            body.Text = q.OnKillList
                ? "just started - " + many + size + ".\n\nIt is on your "
                  + q.Mode.ToUpperInvariant() + " kill list, so the sentry is about to close it."
                : "just started - " + many + size + ".\n\nIt is on no list, but it is big enough "
                  + "to be worth asking about.";
            body.SetBounds(16, 44, 400, 56);
            Controls.Add(body);

            ticker = new Label();
            ticker.SetBounds(16, 104, 400, 18);
            ticker.ForeColor = Theme.Dim;
            Controls.Add(ticker);
            Tick();

            Button keep = Btn(Theme.Quiet("Keep it"), 16);
            keep.Click += delegate { Answer(Verdict.Keep); };

            Button always = Btn(Theme.Action("Always keep"), 122);
            always.Click += delegate { Answer(Verdict.KeepAlways); };

            Button kill = Btn(Theme.Dangerous("Trash it"), 300);
            kill.Click += delegate { Answer(Verdict.Kill); };

            AcceptButton = keep;
            CancelButton = keep;

            countdown = new System.Windows.Forms.Timer();
            countdown.Interval = 1000;
            countdown.Tick += delegate
            {
                left--;
                if (left <= 0) Answer(Verdict.NoAnswer);
                else Tick();
            };
            countdown.Start();

            lock (Open) Open.Add(this);
        }

        private void Tick()
        {
            ticker.Text = "no answer in " + left + "s = left alone, and asked again later";
        }

        private Button Btn(Button b, int x)
        {
            b.SetBounds(x, 132, 100, 30);
            Controls.Add(b);
            return b;
        }

        private void Answer(Verdict v)
        {
            Choice = v;
            countdown.Stop();
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            lock (Open) Open.Remove(this);
            base.OnFormClosed(e);
        }

        // Called when the sentry is told to stand down while a dialog is still up.
        public static void CloseAll()
        {
            List<AskForm> copy;
            lock (Open) copy = new List<AskForm>(Open);
            foreach (AskForm f in copy)
            {
                try { if (!f.IsDisposed) f.Answer(Verdict.NoAnswer); }
                catch (Exception) { }
            }
        }

        public static bool AnyOpen { get { lock (Open) return Open.Count > 0; } }
    }

    // ------------------------------------------------------------------- gauge

    // The RAM bar. One control, painted in one pass, so resizing does not
    // flicker the way the old pair of nested panels did.
    internal sealed class MemGauge : Control
    {
        private ulong total, free;

        public MemGauge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Set(ulong totalMb, ulong freeMb)
        {
            total = totalMb;
            free = freeMb;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            Rectangle bar = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            if (bar.Width < 8 || bar.Height < 8) return;

            ulong used = total > free ? total - free : 0;
            double pct = total == 0 ? 0 : (double)used / total;
            Color fill = pct > 0.85 ? Theme.GaugeBad : pct > 0.6 ? Theme.GaugeWarn : Theme.GaugeOk;

            using (GraphicsPath track = Rounded(bar, 6))
            using (SolidBrush b = new SolidBrush(Theme.Track))
                g.FillPath(b, track);

            int w = (int)(bar.Width * pct);
            if (w > 2)
            {
                Rectangle fillRect = new Rectangle(bar.X, bar.Y, w, bar.Height);
                using (GraphicsPath path = Rounded(fillRect, 6))
                using (SolidBrush b = new SolidBrush(fill))
                    g.FillPath(b, path);
            }

            string leftText = string.Format(CultureInfo.InvariantCulture,
                "RAM  {0:0.0} GB used / {1:0.0} GB", used / 1024.0, total / 1024.0);
            string rightText = string.Format(CultureInfo.InvariantCulture,
                "{0:0.0} GB free   {1:0}%", free / 1024.0, pct * 100);

            TextRenderer.DrawText(g, leftText, Font, new Rectangle(10, 0, bar.Width - 20, bar.Height),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, rightText, Font, new Rectangle(10, 0, bar.Width - 20, bar.Height),
                Color.White, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ------------------------------------------------------------ config window

    // Pick names off the machine instead of typing them: running processes by how
    // much RAM they are actually costing, or installed services by display name.
    internal sealed class PickForm : Form
    {
        private readonly CheckedListBox box = new CheckedListBox();
        private readonly List<string> values = new List<string>();
        public readonly List<string> Picked = new List<string>();

        public PickForm(string title, bool services)
        {
            Theme.Form(this);
            Text = title;
            BackColor = Theme.Panel;
            Size = new Size(560, 520);
            StartPosition = FormStartPosition.CenterParent;

            box.SetBounds(12, 12, 520, 420);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            box.Font = Theme.Mono();
            box.CheckOnClick = true;
            Theme.Input_(box);
            Controls.Add(box);

            if (services) FillServices(); else FillProcesses();

            Button ok = Theme.Action("Add selected");
            ok.SetBounds(316, 444, 105, 30);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.Click += delegate
            {
                foreach (int i in box.CheckedIndices) Picked.Add(values[i]);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = Theme.Quiet("Cancel");
            cancel.SetBounds(427, 444, 105, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private void FillProcesses()
        {
            foreach (ProcRow r in Engine.Snapshot(null))
            {
                values.Add(r.Name);
                box.Items.Add(string.Format(CultureInfo.InvariantCulture, "{0,-34} {1,8}  {2}",
                    r.Name, Engine.Size(r.Bytes), r.Count > 1 ? "x" + r.Count : ""));
            }
        }

        private void FillServices()
        {
            List<ServiceController> all = new List<ServiceController>(ServiceController.GetServices());
            all.Sort(delegate(ServiceController a, ServiceController b)
            {
                int byState = (b.Status == ServiceControllerStatus.Running ? 1 : 0)
                            - (a.Status == ServiceControllerStatus.Running ? 1 : 0);
                if (byState != 0) return byState;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            foreach (ServiceController sc in all)
            {
                try
                {
                    values.Add(sc.ServiceName);
                    box.Items.Add((sc.Status == ServiceControllerStatus.Running ? "* " : "  ")
                        + sc.ServiceName + "   -   " + sc.DisplayName);
                }
                catch (Exception) { }
            }
        }
    }

    // One editable list: everything in a section, commented-out entries included
    // as unchecked rows so you can see what the config ships and turn it on.
    internal sealed class ListPane : Panel
    {
        private readonly string section;
        private readonly bool services;
        private readonly CheckedListBox box = new CheckedListBox();
        private readonly List<IniFile.Entry> before;

        public ListPane(IniFile ini, string sectionName, string caption, bool isServices)
        {
            section = sectionName;
            services = isServices;
            before = ini.Section(section);

            BackColor = Theme.Panel;
            ForeColor = Theme.Fg;

            Label head = Theme.Caption(caption);
            head.SetBounds(6, 6, 340, 18);
            Controls.Add(head);

            box.SetBounds(6, 28, 320, 300);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            box.Font = Theme.Mono();
            box.CheckOnClick = true;
            Theme.Input_(box);
            Controls.Add(box);

            foreach (IniFile.Entry e in before) box.Items.Add(e.Text, e.Enabled);

            Button add = Btn("Add from machine", 6, 130);
            add.Click += delegate { Pick(); };

            Button typed = Btn("Type one", 142, 88);
            typed.Click += delegate { Typed(); };

            Button del = Btn("Remove", 236, 90);
            del.Click += delegate
            {
                for (int i = box.Items.Count - 1; i >= 0; i--)
                    if (box.SelectedIndices.Contains(i)) box.Items.RemoveAt(i);
            };
        }

        private Button Btn(string text, int x, int w)
        {
            Button b = Theme.Quiet(text);
            b.SetBounds(x, 334, w, 28);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(b);
            return b;
        }

        private void Pick()
        {
            using (PickForm f = new PickForm(services ? "Running services" : "Running processes", services))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (string v in f.Picked)
                {
                    if (Has(v)) continue;
                    box.Items.Add(v, true);
                }
            }
        }

        private void Typed()
        {
            using (Form f = new Form())
            {
                Theme.Form(f);
                f.Text = "Add entry";
                f.BackColor = Theme.Panel;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(340, 96);
                f.MinimizeBox = f.MaximizeBox = false;

                Label l = new Label();
                l.Text = services ? "Service name:" : "Process name ('*' allowed):";
                l.SetBounds(12, 10, 300, 18);
                f.Controls.Add(l);

                TextBox t = new TextBox();
                t.SetBounds(12, 32, 316, 22);
                Theme.Input_(t);
                f.Controls.Add(t);

                Button ok = Theme.Action("Add");
                ok.SetBounds(228, 60, 100, 28);
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                if (f.ShowDialog(this) != DialogResult.OK) return;
                string v = t.Text.Trim();
                if (v.Length == 0 || Has(v)) return;
                box.Items.Add(v, true);
            }
        }

        private bool Has(string v)
        {
            foreach (object o in box.Items)
                if (string.Equals(o.ToString(), v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Save(IniFile ini)
        {
            List<string> now = new List<string>();
            for (int i = 0; i < box.Items.Count; i++) now.Add(box.Items[i].ToString());

            foreach (IniFile.Entry e in before)
            {
                int at = -1;
                for (int i = 0; i < now.Count; i++)
                    if (now[i].Equals(e.Text, StringComparison.OrdinalIgnoreCase)) { at = i; break; }

                if (at < 0) { ini.Remove(section, e.Text); continue; }
                bool on = box.GetItemChecked(at);
                if (on != e.Enabled) ini.SetEnabled(section, e.Text, on);
            }

            for (int i = 0; i < now.Count; i++)
            {
                bool old = false;
                foreach (IniFile.Entry e in before)
                    if (now[i].Equals(e.Text, StringComparison.OrdinalIgnoreCase)) { old = true; break; }
                if (old) continue;
                ini.Add(section, now[i]);
                if (!box.GetItemChecked(i)) ini.SetEnabled(section, now[i], false);
            }
        }
    }

    // What each settings key means, shared by the quick dialog and the advanced
    // window so a switch cannot mean two different things in two places.
    internal static class SettingSpec
    {
        // key, label
        public static readonly string[][] Flags = new string[][]
        {
            new string[] { "Sentry",               "Keep hunting after a mode has run" },
            new string[] { "AskBeforeKill",        "Ask before killing anything that started after the boost" },
            new string[] { "SentrySkipForeground", "Never kill the window you are using (boost only)" },
            new string[] { "Tray",                 "Tray icon - closing the window hides to it" },
            new string[] { "KillExplorer",         "Absolute idle also closes the shell (taskbar, desktop)" },
            new string[] { "NetworkGuard",         "Check Sunshine + Tailscale, restart them if they die" },
            new string[] { "TrimWorkingSets",      "Squeeze the working set of every surviving process" },
            new string[] { "ClearStandbyList",     "Purge the standby (cached) list" },
            new string[] { "CloseBrowsersInBoost", "Boost closes browsers too" },
        };

        // key, label, min, max, default
        public static readonly string[][] Numbers = new string[][]
        {
            new string[] { "SentrySeconds",        "Sweep for new junk every (seconds)",            "5", "3600", "20" },
            new string[] { "SentryServiceMinutes", "Re-stop restarted services every (minutes)",    "1", "1440", "5" },
            new string[] { "SentryTrimMinutes",    "Re-trim RAM every (minutes)",                   "1", "1440", "10" },
            new string[] { "SentryGuardMinutes",   "Check the stream stack every (minutes)",        "1", "1440", "5" },
            new string[] { "SentryRespawnLimit",   "Give up on a process after this many respawns", "1", "100",  "6" },
            new string[] { "SentryBackoffMinutes", "...and leave it alone for (minutes)",           "1", "1440", "30" },
            new string[] { "AskTimeoutSeconds",    "Dialog answers itself after (seconds)",         "5", "600",  "25" },
            new string[] { "AskAboveMb",           "Ask about unlisted newcomers bigger than (MB, 0 = off)", "0", "99999", "250" },
            new string[] { "TrimWhenFreeBelowMb",  "Emergency trim when free RAM drops below (MB, 0 = off)", "0", "99999", "0" },
        };

        public static string[] Number(string key)
        {
            foreach (string[] n in Numbers)
                if (n[0] == key) return n;
            throw new ArgumentException("unknown numeric setting: " + key);
        }

        public static decimal Clamp(NumericUpDown u, string raw, string fallback)
        {
            decimal d;
            if (raw == null || !decimal.TryParse(raw.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out d))
                d = decimal.Parse(fallback, CultureInfo.InvariantCulture);
            if (d < u.Minimum) d = u.Minimum;
            if (d > u.Maximum) d = u.Maximum;
            return d;
        }

        public static bool Truthy(string v, bool fallback)
        {
            if (v == null) return fallback;
            v = v.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    // The switches most people actually touch, in plain words, with the whole
    // config one click away behind "Advanced settings".
    internal sealed class QuickSettingsForm : Form
    {
        private readonly IniFile ini = new IniFile();
        private readonly Dictionary<string, CheckBox> flags = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, NumericUpDown> numbers = new Dictionary<string, NumericUpDown>();

        public bool Saved;

        public QuickSettingsForm()
        {
            Theme.Form(this);
            Text = "Idle Master - settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(460, 360);

            int y = 16;
            y = Flag("Sentry", "Keep hunting after a boost",
                "Re-applies your kill lists on a timer until you hit Restore.", y);
            y = Flag("AskBeforeKill", "Ask before killing anything new",
                "A small countdown toast appears; no answer means it is left alone.", y);
            y = Flag("Tray", "Keep running in the tray",
                "Closing the window hides Idle Master instead of quitting it.", y);

            y += 8;
            y = Number("SentrySeconds", y);
            y = Number("TrimWhenFreeBelowMb", y);

            Button advanced = Theme.Quiet("Advanced settings...");
            advanced.SetBounds(20, 312, 150, 30);
            advanced.Click += delegate { OpenAdvanced(); };
            Controls.Add(advanced);

            Button save = Theme.Action("Save");
            save.SetBounds(252, 312, 90, 30);
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Cancel");
            cancel.SetBounds(350, 312, 90, 30);
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private int Flag(string key, string label, string hint, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label;
            c.Checked = SettingSpec.Truthy(ini.GetSetting(key), true);
            c.SetBounds(20, y, 420, 22);
            Controls.Add(c);
            flags[key] = c;

            Label h = Theme.Hint(hint);
            h.Font = Theme.Small();
            h.SetBounds(38, y + 22, 402, 16);
            Controls.Add(h);
            return y + 48;
        }

        private int Number(string key, int y)
        {
            string[] spec = SettingSpec.Number(key);

            Label l = new Label();
            l.Text = spec[1];
            l.SetBounds(20, y + 3, 330, 20);
            Controls.Add(l);

            NumericUpDown u = new NumericUpDown();
            u.Minimum = decimal.Parse(spec[2], CultureInfo.InvariantCulture);
            u.Maximum = decimal.Parse(spec[3], CultureInfo.InvariantCulture);
            u.Value = SettingSpec.Clamp(u, ini.GetSetting(key), spec[4]);
            u.SetBounds(350, y, 90, 22);
            Theme.Input_(u);
            Controls.Add(u);
            numbers[key] = u;
            return y + 32;
        }

        // The advanced window persists on its own; if it saved, this dialog has
        // nothing newer to add, so it closes as saved too.
        private void OpenAdvanced()
        {
            using (ConfigForm f = new ConfigForm())
            {
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            Saved = true;
            Close();
        }

        private void Persist()
        {
            try
            {
                foreach (KeyValuePair<string, CheckBox> kv in flags)
                    ini.SetSetting(kv.Key, kv.Value.Checked ? "1" : "0");
                foreach (KeyValuePair<string, NumericUpDown> kv in numbers)
                    ini.SetSetting(kv.Key, ((int)kv.Value.Value).ToString(CultureInfo.InvariantCulture));
                ini.Save();
                Saved = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the config:\n\n" + ex.Message,
                    "Idle Master", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // The whole config, with no text editor in sight.
    internal sealed class ConfigForm : Form
    {
        private readonly IniFile ini = new IniFile();
        private readonly Dictionary<string, CheckBox> flags = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, NumericUpDown> numbers = new Dictionary<string, NumericUpDown>();
        private readonly List<ListPane> panes = new List<ListPane>();

        public bool Saved;

        public ConfigForm()
        {
            Theme.Form(this);
            Text = "Idle Master - advanced settings";
            Size = new Size(780, 680);
            MinimumSize = new Size(680, 580);
            StartPosition = FormStartPosition.CenterParent;

            TabControl tabs = new TabControl();
            tabs.SetBounds(10, 10, 754, 580);
            tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(112, 28);
            tabs.DrawItem += DrawTab;
            Controls.Add(tabs);

            tabs.TabPages.Add(SettingsTab());
            tabs.TabPages.Add(Pair("Never touch", "protect", "Processes that survive everything",
                                                  "protect.services", "Services that survive everything"));
            tabs.TabPages.Add(Pair("Boost now", "boost.kill", "Processes closed by Boost",
                                                "boost.services", "Services stopped by Boost"));
            tabs.TabPages.Add(Pair("Absolute idle", "idle.kill", "Also closed by Absolute Idle",
                                                    "idle.services", "Also stopped by Absolute Idle"));
            tabs.TabPages.Add(Single("Restore", "restore.launch",
                "Relaunched by Restore desktop  (full path, optional |arguments)"));

            Label hint = Theme.Hint("Unchecked entries stay in the file, commented out. Nothing here can "
                + "override 'Never touch'.");
            hint.SetBounds(14, 600, 520, 32);
            hint.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(hint);

            Button save = Theme.Action("Save");
            save.SetBounds(556, 600, 100, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Cancel");
            cancel.SetBounds(664, 600, 100, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        // The strip repainted dark. The pale border the control draws around the
        // tab body is native and stays - this removes the worst of the clash.
        private void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabControl tc = (TabControl)sender;
            bool sel = e.Index == tc.SelectedIndex;
            Rectangle r = tc.GetTabRect(e.Index);
            r.Inflate(2, 2);
            using (SolidBrush b = new SolidBrush(sel ? Theme.Panel : Theme.Bg))
                e.Graphics.FillRectangle(b, r);
            TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text, tc.Font, r,
                sel ? Theme.Accent : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private TabPage SettingsTab()
        {
            TabPage page = new TabPage("Settings");
            page.BackColor = Theme.Panel;
            page.ForeColor = Theme.Fg;
            page.AutoScroll = true;

            int y = 12;
            y = Header(page, "Behaviour", y);
            foreach (string[] f in SettingSpec.Flags)
            {
                CheckBox c = new CheckBox();
                c.Text = f[1];
                c.Checked = SettingSpec.Truthy(ini.GetSetting(f[0]), true);
                c.SetBounds(16, y, 700, 24);
                c.ForeColor = Theme.Fg;
                page.Controls.Add(c);
                flags[f[0]] = c;
                y += 26;
            }

            y += 8;
            y = Header(page, "Sentry timing", y);
            for (int i = 0; i < 6; i++) y = NumberRow(page, SettingSpec.Numbers[i], y);

            y += 8;
            y = Header(page, "Asking && safety", y);
            for (int i = 6; i < SettingSpec.Numbers.Length; i++)
                y = NumberRow(page, SettingSpec.Numbers[i], y);

            return page;
        }

        private static int Header(TabPage page, string text, int y)
        {
            Label l = Theme.Caption(text);
            l.SetBounds(16, y, 300, 20);
            page.Controls.Add(l);
            return y + 26;
        }

        private int NumberRow(TabPage page, string[] n, int y)
        {
            Label l = new Label();
            l.Text = n[1];
            l.SetBounds(16, y + 4, 480, 20);
            page.Controls.Add(l);

            NumericUpDown u = new NumericUpDown();
            u.Minimum = decimal.Parse(n[2], CultureInfo.InvariantCulture);
            u.Maximum = decimal.Parse(n[3], CultureInfo.InvariantCulture);
            u.Value = SettingSpec.Clamp(u, ini.GetSetting(n[0]), n[4]);
            u.SetBounds(504, y, 90, 22);
            Theme.Input_(u);
            page.Controls.Add(u);
            numbers[n[0]] = u;
            return y + 28;
        }

        private TabPage Pair(string title, string leftSection, string leftCaption,
                             string rightSection, string rightCaption)
        {
            TabPage page = new TabPage(title);
            page.BackColor = Theme.Panel;

            ListPane left = new ListPane(ini, leftSection, leftCaption, false);
            left.SetBounds(4, 4, 366, 510);
            left.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            page.Controls.Add(left);

            ListPane right = new ListPane(ini, rightSection, rightCaption, true);
            right.SetBounds(376, 4, 366, 510);
            right.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(right);

            panes.Add(left);
            panes.Add(right);
            return page;
        }

        private TabPage Single(string title, string section, string caption)
        {
            TabPage page = new TabPage(title);
            page.BackColor = Theme.Panel;

            ListPane pane = new ListPane(ini, section, caption, false);
            pane.SetBounds(4, 4, 738, 510);
            pane.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(pane);

            panes.Add(pane);
            return page;
        }

        private void Persist()
        {
            try
            {
                foreach (KeyValuePair<string, CheckBox> kv in flags)
                    ini.SetSetting(kv.Key, kv.Value.Checked ? "1" : "0");
                foreach (KeyValuePair<string, NumericUpDown> kv in numbers)
                    ini.SetSetting(kv.Key, ((int)kv.Value.Value).ToString(CultureInfo.InvariantCulture));
                foreach (ListPane p in panes) p.Save(ini);
                ini.Save();
                Saved = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the config:\n\n" + ex.Message,
                    "Idle Master", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ------------------------------------------------------------ task manager

    // ListView is the only stock control that can show the eaters table, and the
    // only way to reach its protected DoubleBuffered is to inherit it.
    internal sealed class EaterListView : ListView
    {
        public EaterListView() { DoubleBuffered = true; }
    }

    // The Master's task manager: what's eating RAM, live, with the verdicts one
    // right-click away. Opened by the main window; non-modal so the log stays
    // visible while you work through the list.
    internal sealed class EatersForm : Form
    {
        private const int MaxRows = 30;

        private readonly Config cfg;
        private readonly Engine engine;
        private readonly Action<string> log;
        private readonly Func<bool> sentryAlive;
        private readonly EaterListView eaters;
        private readonly ColumnHeader colName, colCount, colMem, colTag;
        private readonly ToolStripMenuItem miKill, miBoost, miIdle, miProtect;
        private readonly System.Windows.Forms.Timer timer;
        private bool rowMenuOpen;

        public EatersForm(Config c, Engine e, Action<string> logger, Func<bool> sentryUp)
        {
            cfg = c;
            engine = e;
            log = logger;
            sentryAlive = sentryUp;

            Theme.Form(this);
            Text = "IDLE MASTER - task manager";
            Size = new Size(640, 560);
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("WHAT'S EATING RAM");
            cap.SetBounds(16, 12, 240, 18);
            Controls.Add(cap);

            Label hint = Theme.Hint("updates every 2 s - right-click a row to act on it");
            hint.Font = Theme.Small();
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.SetBounds(264, 12, 344, 18);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(hint);

            eaters = new EaterListView();
            eaters.View = View.Details;
            eaters.FullRowSelect = true;
            eaters.MultiSelect = false;
            eaters.HideSelection = false;
            eaters.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            eaters.BorderStyle = BorderStyle.FixedSingle;
            eaters.BackColor = Theme.Input;
            eaters.ForeColor = Theme.Fg;
            eaters.OwnerDraw = true;
            eaters.DrawColumnHeader += DrawHeader;
            eaters.DrawItem += delegate(object s, DrawListViewItemEventArgs a) { a.DrawDefault = true; };
            eaters.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs a) { a.DrawDefault = true; };
            colName = eaters.Columns.Add("Process", 320);
            colCount = eaters.Columns.Add("Instances", 74, HorizontalAlignment.Right);
            colMem = eaters.Columns.Add("Memory", 96, HorizontalAlignment.Right);
            colTag = eaters.Columns.Add("Tag", 80);
            eaters.SetBounds(16, 36, 592, 476);
            eaters.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            eaters.Resize += delegate { SizeColumns(); };
            Controls.Add(eaters);
            SizeColumns();

            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.Menu(menu);
            miKill = new ToolStripMenuItem("End it now");
            miKill.Click += delegate { KillSelected(); };
            miBoost = new ToolStripMenuItem("Close on every boost");
            miBoost.Click += delegate { AddSelected("boost.kill", "boost list"); };
            miIdle = new ToolStripMenuItem("Also close on absolute idle");
            miIdle.Click += delegate { AddSelected("idle.kill", "idle list"); };
            miProtect = new ToolStripMenuItem("Never touch (protect)");
            miProtect.Click += delegate { AddSelected("protect", "protected list"); };
            menu.Items.Add(miKill);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miBoost);
            menu.Items.Add(miIdle);
            menu.Items.Add(miProtect);
            menu.Opening += MenuOpening;
            menu.Closed += delegate { rowMenuOpen = false; };
            eaters.ContextMenuStrip = menu;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { RefreshNow(); };
            timer.Start();
            RefreshNow();
        }

        private void SizeColumns()
        {
            int rest = colCount.Width + colMem.Width + colTag.Width;
            int w = eaters.ClientSize.Width - rest - 4;
            if (w > 80) colName.Width = w;
        }

        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                e.Graphics.FillRectangle(b, e.Bounds);
            TextFormatFlags align = e.ColumnIndex == 1 || e.ColumnIndex == 2
                ? TextFormatFlags.Right : TextFormatFlags.Left;
            Rectangle r = e.Bounds;
            r.Inflate(-6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, eaters.Font, r, Theme.Dim,
                align | TextFormatFlags.VerticalCenter);
        }

        // Rows are updated in place, keyed by name, so scroll position and the
        // selection survive every 2-second refresh.
        public void RefreshNow()
        {
            if (!Visible || WindowState == FormWindowState.Minimized || rowMenuOpen) return;

            List<ProcRow> rows = Engine.Snapshot(engine);
            int n = Math.Min(MaxRows, rows.Count);

            string selected = eaters.SelectedItems.Count > 0 ? eaters.SelectedItems[0].Name : null;
            eaters.BeginUpdate();
            try
            {
                Dictionary<string, bool> want = new Dictionary<string, bool>();
                for (int i = 0; i < n; i++) want[rows[i].Key] = true;
                for (int i = eaters.Items.Count - 1; i >= 0; i--)
                    if (!want.ContainsKey(eaters.Items[i].Name)) eaters.Items.RemoveAt(i);

                for (int i = 0; i < n; i++)
                {
                    ProcRow r = rows[i];
                    int at = eaters.Items.IndexOfKey(r.Key);
                    ListViewItem it;
                    if (at < 0)
                    {
                        it = new ListViewItem(r.Name);
                        it.Name = r.Key;
                        it.UseItemStyleForSubItems = false;
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        eaters.Items.Insert(Math.Min(i, eaters.Items.Count), it);
                    }
                    else
                    {
                        it = eaters.Items[at];
                        if (at != i)
                        {
                            eaters.Items.RemoveAt(at);
                            eaters.Items.Insert(Math.Min(i, eaters.Items.Count), it);
                        }
                    }
                    SetRow(it, r);
                }

                if (selected != null)
                {
                    int back = eaters.Items.IndexOfKey(selected);
                    if (back >= 0) eaters.Items[back].Selected = true;
                }
            }
            finally { eaters.EndUpdate(); }
        }

        private void SetRow(ListViewItem it, ProcRow r)
        {
            it.Tag = r;
            Put(it.SubItems[1], r.Count.ToString(CultureInfo.InvariantCulture));
            Put(it.SubItems[2], Engine.Size(r.Bytes));

            if (it.SubItems[3].Text != r.Tag)
            {
                it.SubItems[3].Text = r.Tag;
                it.SubItems[3].ForeColor =
                    r.Tag == "BOOST" ? Theme.Accent :
                    r.Tag == "IDLE" ? Theme.Warn :
                    r.Tag == "KEEP" ? Theme.Dim : Theme.Fg;
                Color row = r.Tag == "KEEP" ? Theme.Dim : Theme.Fg;
                it.ForeColor = row;
                it.SubItems[1].ForeColor = row;
                it.SubItems[2].ForeColor = row;
            }
        }

        private static void Put(ListViewItem.ListViewSubItem s, string text)
        {
            if (s.Text != text) s.Text = text;
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (eaters.SelectedItems.Count == 0) { e.Cancel = true; return; }
            rowMenuOpen = true;
            ProcRow r = (ProcRow)eaters.SelectedItems[0].Tag;
            miKill.Text = "End " + r.Name + " now  (" + Engine.Size(r.Bytes) + ")";
            miKill.Enabled = r.Tag != "KEEP";
            miBoost.Enabled = r.Tag == "" || r.Tag == "IDLE";
            miIdle.Enabled = r.Tag == "";
            miProtect.Enabled = r.Tag != "KEEP";
        }

        private void KillSelected()
        {
            if (eaters.SelectedItems.Count == 0) return;
            ProcRow r = (ProcRow)eaters.SelectedItems[0].Tag;

            if (r.Tag.Length == 0 && MessageBox.Show(this,
                "End all " + r.Count + " process(es) named '" + r.Name + "'?\n\n"
                + "It is on no list - unsaved work in it is gone.",
                "Idle Master", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            Candidate c = new Candidate(r.Name);
            c.Bytes = r.Bytes;
            c.Pids.AddRange(r.Pids);

            // Reap waits up to 3 s per pid - keep that off the UI thread.
            Thread t = new Thread(delegate()
            {
                List<KillHit> hits = engine.Reap(c);
                string line = hits.Count > 0
                    ? "Ended " + r.Name + " - " + Engine.Size(Engine.TotalOf(hits)) + " back."
                    : "Nothing died - " + r.Name + " was gone already or refused.";
                log(line);
                try { BeginInvoke((Action)RefreshNow); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AddSelected(string section, string label)
        {
            if (eaters.SelectedItems.Count == 0) return;
            ProcRow r = (ProcRow)eaters.SelectedItems[0].Tag;

            if (!Config.Append(section, r.Name.ToLowerInvariant()))
            {
                log("! could not write " + r.Name + " into the " + label + ".");
                return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                log(r.Name + " added to the " + label + ".");
                if (sentryAlive())
                    log("The sentry is using the new lists from its next sweep.");
            }
            catch (Exception ex) { log("! could not reload the config: " + ex.Message); }
            RefreshNow();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------- gui

    internal sealed class MainForm : Form
    {
        private readonly Config cfg;
        private readonly Engine engine;
        private readonly TextBox logBox;
        private readonly MemGauge gauge;
        private readonly Button btnBoost, btnIdle, btnRestore, btnEaters, btnTrim, btnConfig, btnUpdate;
        private readonly CheckBox chkSentry;
        private readonly Label sentryLabel;
        private readonly Label updateLabel;
        private readonly System.Windows.Forms.Timer timer;
        private Sentry sentry;
        private NotifyIcon tray;
        private EatersForm eatersWin;
        private bool reallyExit;
        private bool startHidden;
        private bool watchMode;

        public MainForm(Config c)
        {
            cfg = c;
            engine = new Engine(cfg, AppendLog);

            Theme.Form(this);
            Text = "IDLE MASTER";
            Size = new Size(700, 706);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;

            Label title = new Label();
            title.Text = "IDLE MASTER";
            title.Font = Theme.Title();
            title.ForeColor = Theme.Accent;
            title.SetBounds(20, 14, 400, 36);
            Controls.Add(title);

            Label sub = Theme.Hint("Sunshine + Tailscale stay up. Everything else is negotiable.   v"
                + App.Version);
            sub.SetBounds(22, 50, 500, 20);
            Controls.Add(sub);

            gauge = new MemGauge();
            gauge.SetBounds(22, 78, 640, 36);
            gauge.Font = Theme.Bold();
            gauge.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gauge);

            btnBoost = BigButton("BOOST NOW",
                "Kill the background junk. Desktop stays usable.", Theme.Good, 130);
            btnBoost.Click += delegate { Run("boost"); };

            btnIdle = BigButton("ABSOLUTE IDLE",
                "Strip to Windows vitals + Sunshine + Tailscale. For sleep.", Theme.Danger, 218);
            btnIdle.Click += delegate { ConfirmIdle(); };

            btnRestore = SmallButton("Restore desktop", 22);
            btnRestore.Click += delegate { Run("restore"); };
            btnEaters = SmallButton("What's eating RAM?", 182);
            btnEaters.Click += delegate { OpenEaters(); };
            btnTrim = SmallButton("Trim RAM now", 342);
            btnTrim.Click += delegate { Run("trim"); };
            btnConfig = SmallButton("Settings", 502);
            btnConfig.Click += delegate { EditConfig(); };

            btnUpdate = Theme.Quiet("Check for updates");
            btnUpdate.SetBounds(22, 342, 152, 30);
            btnUpdate.Click += delegate { CheckUpdates(); };
            Controls.Add(btnUpdate);

            updateLabel = Theme.Hint("running v" + App.Version + " - " + Updater.Repo);
            updateLabel.SetBounds(182, 348, 480, 20);
            updateLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(updateLabel);

            chkSentry = new CheckBox();
            chkSentry.Text = "Keep hunting after boost";
            chkSentry.Checked = cfg.Sentry;
            chkSentry.SetBounds(24, 382, 190, 22);
            chkSentry.ForeColor = Theme.Fg;
            chkSentry.FlatStyle = FlatStyle.Flat;
            chkSentry.Click += delegate { ToggleSentry(); };
            Controls.Add(chkSentry);

            sentryLabel = Theme.Hint("");
            sentryLabel.SetBounds(220, 384, 442, 20);
            sentryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(sentryLabel);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Theme.LogBg;
            logBox.ForeColor = Theme.LogFg;
            logBox.Font = Theme.Mono();
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.SetBounds(22, 412, 640, 212);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(logBox);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { UpdateMemory(); UpdateSentry(); };
            timer.Start();
            UpdateMemory();
            UpdateSentry();

            AppendLog("Ready. Config: " + Config.Path_);
            StateFile st = StateFile.Load();
            if (st.Mode.Length > 0)
                AppendLog("Note: last run was '" + st.Mode + "' and has not been restored yet ("
                    + st.StoppedServices.Count + " services still stopped).");

            if (cfg.Tray) BuildTray();

            // A mode was run earlier and never restored - pick the watch back up.
            if (st.Mode.Length > 0 && st.SentryArmed && cfg.Sentry && !Sentry.IsRunningSomewhere())
                StartSentry(st.Mode);

            FormClosing += OnClosing;
        }

        // One live task-manager window, reused if it is already open.
        private void OpenEaters()
        {
            if (eatersWin != null && !eatersWin.IsDisposed)
            {
                eatersWin.Activate();
                return;
            }
            eatersWin = new EatersForm(cfg, engine, AppendLog,
                delegate { return sentry != null && sentry.Alive; });
            eatersWin.Location = new Point(Location.X + Width - 40, Location.Y + 40);
            eatersWin.Show(this);
        }

        // Started by --watch: no window, just the tray icon and the sentry.
        public void HideOnStart(string enforce)
        {
            startHidden = true;
            watchMode = true;
            WindowState = FormWindowState.Minimized;
            // Touching Handle builds the window now, on this thread, so the sentry
            // has something to marshal its dialogs onto before anything is shown.
            IntPtr forced = Handle;
            GC.KeepAlive(forced);
            if (enforce.Length > 0) StartSentry(enforce);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Shield;
            tray.Text = "Idle Master";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.Menu(menu);
            menu.Items.Add("Open Idle Master", null, delegate { ShowWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Boost now", null, delegate { ShowWindow(); Run("boost"); });
            menu.Items.Add("Absolute idle", null, delegate { ShowWindow(); ConfirmIdle(); });
            menu.Items.Add("Restore desktop", null, delegate { ShowWindow(); Run("restore"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Stop hunting", null, delegate
            {
                chkSentry.Checked = false;
                StopSentry();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings...", null, delegate { ShowWindow(); EditConfig(); });
            menu.Items.Add("Check for updates", null, delegate { ShowWindow(); CheckUpdates(); });
            menu.Items.Add("Exit", null, delegate { reallyExit = true; Close(); });
            tray.ContextMenuStrip = menu;
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // Closing the window while the sentry is up would let RAM drift back, so
        // it goes to the tray instead. Exit from the tray menu when you mean it.
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            bool hunting = sentry != null && sentry.Alive;
            if (!reallyExit && cfg.Tray && hunting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    tray.ShowBalloonTip(4000, "Still hunting",
                        "Idle Master is holding " + sentry.Mode.ToUpperInvariant()
                        + " in the tray. Right-click the icon to stop it.", ToolTipIcon.Info);
                }
                catch (Exception) { }
                return;
            }
            StopSentry(false);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        }

        private void StartSentry(string mode)
        {
            if (sentry != null && sentry.Alive) return;
            sentry = new Sentry(cfg, engine, AppendLog, mode);
            sentry.Ask = AskOnUiThread;
            if (!sentry.Start()) sentry = null;
            UpdateSentry();
        }

        // Called from the sentry's thread; blocks it until you answer or it times
        // out. That is fine - the next sweep is 20 seconds away anyway.
        private Verdict AskOnUiThread(Question q)
        {
            if (InvokeRequired)
            {
                try { return (Verdict)Invoke((Func<Question, Verdict>)AskOnUiThread, q); }
                catch (Exception) { return Verdict.Keep; }
            }
            using (AskForm f = new AskForm(q, cfg.AskTimeoutSeconds))
            {
                f.ShowDialog();
                return f.Choice;
            }
        }

        // disarm=false is "the window is closing" - the watch should resume next launch.
        private void StopSentry() { StopSentry(true); }

        private void StopSentry(bool disarm)
        {
            if (sentry != null)
            {
                sentry.Stop();
                sentry = null;
            }
            if (disarm)
            {
                StateFile st = StateFile.Load();
                if (st.Mode.Length > 0 && st.SentryArmed) { st.SentryArmed = false; st.Save(); }
            }
            UpdateSentry();
        }

        private void ToggleSentry()
        {
            if (chkSentry.Checked)
            {
                StateFile st = StateFile.Load();
                if (st.Mode.Length == 0)
                {
                    AppendLog("Sentry armed. It starts hunting as soon as you run a mode.");
                    UpdateSentry();
                    return;
                }
                StartSentry(st.Mode);
            }
            else StopSentry();
        }

        private void UpdateSentry()
        {
            bool on = sentry != null && sentry.Alive;
            if (!on && sentry != null)
            {
                sentry = null;
                // --unwatch (or Restore from elsewhere) killed the watch, and a tray
                // app with nothing to do is just a stray icon.
                if (watchMode) { reallyExit = true; Close(); return; }
            }

            if (on)
            {
                string txt = "hunting " + sentry.Mode.ToUpperInvariant() + " - "
                    + sentry.Reaped + " reaped, " + Engine.Size(sentry.Reclaimed) + " held off";
                if (sentry.Restopped > 0) txt += ", " + sentry.Restopped + " services re-stopped";
                sentryLabel.Text = txt;
                sentryLabel.ForeColor = Theme.Accent;
            }
            else if (chkSentry.Checked)
            {
                sentryLabel.Text = "armed - starts with the next boost or idle";
                sentryLabel.ForeColor = Theme.Dim;
            }
            else
            {
                sentryLabel.Text = "off - RAM will drift back up on its own";
                sentryLabel.ForeColor = Theme.Dim;
            }
        }

        // Asks GitHub, then hands the decision to you. Nothing downloads until you
        // say so, and the installer that arrives is the one you publish.
        private void CheckUpdates()
        {
            btnUpdate.Enabled = false;
            Status("asking GitHub...", Theme.Dim);
            AppendLog("Checking " + Updater.Repo + " for releases newer than " + App.Version + "...");

            Thread t = new Thread(delegate()
            {
                Updater.Release r = null;
                string failure = null;
                try { r = Updater.Latest(); }
                catch (Exception ex) { failure = ex.Message.Split('\n')[0]; }

                try { BeginInvoke((Action)delegate { Finish(r, failure); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Finish(Updater.Release r, string failure)
        {
            btnUpdate.Enabled = true;

            if (failure != null)
            {
                Status("update check failed", Theme.Warn);
                AppendLog("! update check failed: " + failure);
                return;
            }
            if (r.Tag.Length == 0)
            {
                Status("no releases published yet", Theme.Dim);
                return;
            }
            if (!r.Newer)
            {
                Status("v" + App.Version + " is the newest (" + r.Tag + " published)", Theme.Dim);
                AppendLog("Already on the newest release.");
                return;
            }
            if (r.Url.Length == 0)
            {
                Status(r.Tag + " is out, but has no installer attached", Theme.Warn);
                AppendLog("! " + r.Tag + " has no IdleMasterSetup.exe asset - update it by hand.");
                return;
            }

            Status(r.Tag + " is available", Theme.Accent);
            if (MessageBox.Show(this,
                r.Tag + " is out - you are on " + App.Version + "."
                + "\n\nDownload it and update this copy in " + App.Dir + "?"
                + "\n\nYour idlemaster.ini is kept exactly as it is. Idle Master will close "
                + "while the installer replaces it.",
                "Idle Master", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
            {
                AppendLog("Update available (" + r.Tag + ") - not installed, your call.");
                return;
            }

            try
            {
                AppendLog("Downloading " + r.Tag + "...");
                Status("downloading " + r.Tag + "...", Theme.Accent);
                string setup = Updater.Fetch(r);
                AppendLog("Handing over to " + setup);

                // Point the installer at THIS copy, so updating a portable exe
                // updates it where it stands instead of installing a second one.
                Process.Start(new ProcessStartInfo(setup, "--dir \"" + App.Dir + "\"")
                { UseShellExecute = true });

                reallyExit = true;
                Close();
            }
            catch (Exception ex)
            {
                Status("update failed", Theme.Warn);
                AppendLog("! update failed: " + ex.Message.Split('\n')[0]);
            }
        }

        private void Status(string text, Color color)
        {
            updateLabel.Text = "v" + App.Version + "  -  " + text;
            updateLabel.ForeColor = color;
        }

        // The switches, edited in place. The running sentry picks up the new
        // config immediately - no restart, no text editor.
        private void EditConfig()
        {
            using (QuickSettingsForm f = new QuickSettingsForm())
            {
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                chkSentry.Checked = cfg.Sentry;
                AppendLog("Config saved. " + cfg.Protect.Count + " protected names, "
                    + cfg.BoostKill.Count + " on the boost list, "
                    + cfg.IdleKill.Count + " more on the idle list.");
                if (sentry != null && sentry.Alive)
                    AppendLog("The sentry is using the new lists from its next sweep.");
                if (cfg.Tray && tray == null) BuildTray();
                if (eatersWin != null && !eatersWin.IsDisposed) eatersWin.RefreshNow();
            }
            catch (Exception ex) { AppendLog("! could not reload the config: " + ex.Message); }
        }

        private Button BigButton(string text, string sub, Color color, int y)
        {
            Button b = Theme.Button(text + "\n" + sub, color, Color.White);
            b.SetBounds(22, y, 640, 76);
            b.Font = Theme.Big();
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(b);
            return b;
        }

        private Button SmallButton(string text, int x)
        {
            Button b = Theme.Quiet(text);
            b.SetBounds(x, 306, 152, 30);
            Controls.Add(b);
            return b;
        }

        private void ConfirmIdle()
        {
            string msg = "This closes your browsers, Claude, and everything else you have open."
                + (cfg.KillExplorer ? "\n\nIt also closes the Windows shell (taskbar and desktop disappear)." : "")
                + "\n\nSunshine and Tailscale stay up, so you can still reach this machine."
                + "\n\nTo get the desktop back: press Ctrl+Shift+Esc, then File > Run new task,"
                + " and run this exe again -> Restore desktop."
                + "\n\nGo idle now?";
            if (MessageBox.Show(this, msg, "Absolute Idle",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Run("idle");
        }

        private void Run(string what)
        {
            SetBusy(true);
            // The sentry has to stand down before restore starts putting things back,
            // otherwise it shoots them again on the next sweep.
            if (what == "restore") StopSentry();

            Thread t = new Thread(delegate()
            {
                try
                {
                    if (what == "boost") engine.RunBoost();
                    else if (what == "idle") engine.RunIdle();
                    else if (what == "restore") engine.RunRestore();
                    else if (what == "trim") engine.TrimAll();
                }
                catch (Exception ex) { AppendLog("!! " + ex.ToString()); }
                finally
                {
                    BeginInvoke((Action)delegate
                    {
                        SetBusy(false);
                        if (eatersWin != null && !eatersWin.IsDisposed) eatersWin.RefreshNow();
                        if ((what == "boost" || what == "idle") && chkSentry.Checked)
                            StartSentry(what);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetBusy(bool busy)
        {
            btnBoost.Enabled = btnIdle.Enabled = btnRestore.Enabled = btnTrim.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        // Everything the engine and the sentry say arrives here, from any thread.
        // The file write happens on the UI hop only - doing it before the hop as
        // well is what put every line in the log twice.
        private void AppendLog(string line)
        {
            if (logBox.InvokeRequired)
            {
                try { logBox.BeginInvoke((Action<string>)AppendLog, line); }
                catch (Exception) { App.FileLog(line); }
                return;
            }
            App.FileLog(line);
            logBox.AppendText(line + Environment.NewLine);
        }

        private void UpdateMemory()
        {
            ulong total, free;
            Engine.ReadMemory(out total, out free);
            gauge.Set(total, free);
        }
    }
}

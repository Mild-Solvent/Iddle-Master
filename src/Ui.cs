// IDLE MASTER - every window in the app.
//
//   AskForm           : the countdown toast the sentry raises for newcomers.
//   MemGauge          : the RAM bar, custom-painted so it neither flickers nor lies.
//   RepeatBadge       : the repeat-boost arrow that rides on the BOOST NOW button.
//   PickForm          : pick processes/services off the machine for a list.
//   ListPane          : one editable ini section inside the advanced window.
//   QuickSettingsForm : the handful of switches most people actually touch.
//   ConfigForm        : the whole config - reached via "Advanced settings".
//   EatersForm        : the full task manager behind the "Task manager" button.
//   CleanupForm       : the disk-map tree behind "Disk cleanup".
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
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IdleMaster
{
    // ------------------------------------------------------------------ dialog

    // The toast that appears when something you started lands on a kill list.
    // Bottom-right, always on top, shows the app's own icon and what it is, and
    // counts down. Four answers, two of them "trash": once, or every time.
    // No answer means whatever AskTimeoutAction says (trash once, by default).
    //
    // Deliberately mute: it never takes keyboard focus, so it cannot interrupt
    // typing or knock a fullscreen game out of exclusive mode. Even clicking a
    // button leaves the foreground window alone (WS_EX_NOACTIVATE); the only
    // way to answer is with the mouse, and silence is already an answer.
    internal sealed class AskForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private static readonly List<AskForm> Open = new List<AskForm>();

        private readonly System.Windows.Forms.Timer countdown;
        private readonly Label ticker;
        private readonly string onTimeout;
        private int left;

        public Verdict Choice = Verdict.NoAnswer;

        public AskForm(Question q, int seconds, string timeoutAction)
        {
            left = seconds;
            onTimeout = timeoutAction == "always" ? "trashed for good"
                      : timeoutAction == "keep" ? "left alone" : "trashed once";

            Theme.Form(this);
            Text = "Idle Master";
            BackColor = Theme.Panel;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(480, 240);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

            // Who is this, really: the exe's own icon and the description its
            // maker put in it. "Update.exe, 300 MB" says nothing; "Discord Inc."
            // does.
            string exe = ExePath(q.What);
            string desc = null, company = null;
            if (exe != null)
            {
                try
                {
                    FileVersionInfo fv = FileVersionInfo.GetVersionInfo(exe);
                    desc = Clean(fv.FileDescription);
                    company = Clean(fv.CompanyName);
                }
                catch (Exception) { }
            }

            PictureBox pic = new PictureBox();
            pic.SetBounds(16, 16, 32, 32);
            pic.SizeMode = PictureBoxSizeMode.CenterImage;
            try
            {
                Icon ic = exe != null ? Icon.ExtractAssociatedIcon(exe) : null;
                pic.Image = (ic != null ? ic : App.Icon).ToBitmap();
            }
            catch (Exception) { try { pic.Image = App.Icon.ToBitmap(); } catch (Exception) { } }
            Controls.Add(pic);

            Label head = new Label();
            head.Text = q.What.Name;
            head.Font = Theme.Big();
            head.ForeColor = Theme.Accent;
            head.AutoEllipsis = true;
            head.SetBounds(60, 10, 404, 26);
            Controls.Add(head);

            string who = desc != null && company != null && !desc.Equals(company, StringComparison.OrdinalIgnoreCase)
                ? desc + "  -  " + company
                : (desc ?? company ?? "no description in the exe");
            Label whoL = new Label();
            whoL.Text = who;
            whoL.ForeColor = Theme.Fg;
            whoL.AutoEllipsis = true;
            whoL.SetBounds(60, 36, 404, 18);
            Controls.Add(whoL);

            Label where = Theme.Hint(exe ?? "(path not readable)");
            where.Font = Theme.Small();
            where.AutoEllipsis = true;
            where.SetBounds(60, 54, 404, 16);
            Controls.Add(where);

            string size = Engine.Size(q.What.Bytes);
            string many = q.What.Pids.Count > 1 ? q.What.Pids.Count + " processes, " : "";
            Label body = new Label();
            body.Text = q.OnKillList
                ? "Just started - " + many + size + ". It is on your "
                  + q.Mode.ToUpperInvariant() + " kill list, so the sentry is about to close it."
                : "Just started - " + many + size + ". It is on no list, but it is big enough "
                  + "to be worth asking about.";
            body.SetBounds(16, 80, 448, 36);
            Controls.Add(body);

            Label legend = Theme.Hint(
                "Keep it = leave it, ask again later        Always keep = protect it forever\n"
                + "Trash once = close it, ask if it returns        Always trash = close it every time"
                + (q.OnKillList ? "" : ", via the BOOST list"));
            legend.Font = Theme.Small();
            legend.SetBounds(16, 122, 448, 32);
            Controls.Add(legend);

            ticker = new Label();
            ticker.SetBounds(16, 160, 448, 18);
            ticker.ForeColor = Theme.Warn;
            Controls.Add(ticker);
            Tick();

            Button keep = Btn(Theme.Quiet("Keep it"), 16);
            keep.Click += delegate { Answer(Verdict.Keep); };

            Button always = Btn(Theme.Action("Always keep"), 130);
            always.Click += delegate { Answer(Verdict.KeepAlways); };

            Button once = Btn(Theme.Button("Trash once", Theme.Lift(Theme.Danger, -30), Theme.OnAccent), 244);
            once.Click += delegate { Answer(Verdict.KillOnce); };

            Button kill = Btn(Theme.Dangerous("Always trash"), 358);
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

        private static string Clean(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        // The first pid whose image path we are allowed to read.
        private static string ExePath(Candidate c)
        {
            foreach (int pid in c.Pids)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                        return p.MainModule.FileName;
                }
                catch (Exception) { }
            }
            return null;
        }

        private void Tick()
        {
            ticker.Text = "no answer in " + left + " s = " + onTimeout + "  (Settings changes this)";
        }

        private Button Btn(Button b, int x)
        {
            b.SetBounds(x, 194, 106, 30);
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
        // Standing down means nothing gets killed, so this is a Keep, not a timeout.
        public static void CloseAll()
        {
            List<AskForm> copy;
            lock (Open) copy = new List<AskForm>(Open);
            foreach (AskForm f in copy)
            {
                try { if (!f.IsDisposed) f.Answer(Verdict.Keep); }
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
                Theme.OnAccent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, rightText, Font, new Rectangle(10, 0, bar.Width - 20, bar.Height),
                Theme.OnAccent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
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


    // ------------------------------------------------------------ update badge

    // "Check for updates" as a corner icon rather than a button in the grid: an
    // arrow pointing up, top right of the window, level with the title.
    //
    // It has exactly two things to say and it says them with colour. White while
    // there is nothing new - an outlined arrow, bright enough that you can still
    // find it in the corner and click it whenever you want to ask - and a filled
    // green disc the moment a newer release is known, because "there is an
    // update" is worth a colour of its own. Green means: click me, it installs.
    internal sealed class UpdateBadge : Control
    {
        private bool ready;             // a newer release is waiting for a click
        private bool busy;              // asking GitHub, or downloading
        private bool hot;

        public UpdateBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(34, 34);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        // Busy is not Enabled=false: the icon stays lit and simply goes dim and
        // unclickable while GitHub is answering, so the corner never blinks out.
        public bool Busy { get { return busy; } }

        public void Set(bool isReady, bool isBusy)
        {
            if (isReady == ready && isBusy == busy) return;
            ready = isReady;
            busy = isBusy;
            Cursor = isBusy ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        { hot = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);

            float s = Math.Min(Width, Height) / 34f;         // scale off the 34 px design
            Rectangle disc = new Rectangle((int)(2 * s), (int)(2 * s),
                (int)(Width - 4 * s) - 1, (int)(Height - 4 * s) - 1);

            Color skin = busy ? Theme.Dim : (ready ? Theme.Ready : Color.White);
            if (hot && !busy) skin = Theme.Lift(skin, ready ? 20 : 0);

            // Waiting: an outline, so the corner stays quiet. Ready: the disc
            // fills green and the arrow flips to white - visible across the room.
            Color ink;
            if (ready)
            {
                using (SolidBrush b = new SolidBrush(skin)) g.FillEllipse(b, disc);
                ink = Color.White;
            }
            else
            {
                if (hot)
                    using (SolidBrush b = new SolidBrush(Theme.Neutral)) g.FillEllipse(b, disc);
                using (Pen pen = new Pen(skin, 2f * s)) g.DrawEllipse(pen, disc);
                ink = skin;
            }

            // The arrow: a head and a shaft, pointing up, centred on the disc.
            float cx = disc.X + disc.Width / 2f, cy = disc.Y + disc.Height / 2f;
            using (SolidBrush b = new SolidBrush(ink))
            {
                g.FillPolygon(b, new PointF[]
                {
                    new PointF(cx,             cy - 8.0f * s),
                    new PointF(cx - 6.4f * s,  cy - 0.6f * s),
                    new PointF(cx + 6.4f * s,  cy - 0.6f * s)
                });
                g.FillRectangle(b, cx - 2.2f * s, cy - 1.6f * s, 4.4f * s, 9.2f * s);
            }
        }
    }


    // ------------------------------------------------------------ repeat badge

    // The repeat loop, folded into the BOOST NOW button: a refresh arrow on the
    // left of the blue slab with the interval in its middle. The ring doubles as
    // the countdown - it fills as the next automatic boost comes round - and a
    // click opens the little menu that sets the interval instead of boosting.
    //
    // It is a sibling of the button sitting on top of it, not a child, so the
    // click never reaches the button underneath.
    internal sealed class RepeatBadge : Control
    {
        private int minutes = 30;
        private bool armed;
        private double progress;        // 0..1 through the current interval
        private bool hot;

        public RepeatBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(46, 46);
            BackColor = Theme.Good;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public void Set(bool on, int mins, double through)
        {
            if (on == armed && mins == minutes && Math.Abs(through - progress) < 0.005) return;
            armed = on;
            minutes = mins;
            progress = through < 0 ? 0 : (through > 1 ? 1 : through);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        { hot = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        { hot = false; Invalidate(); base.OnMouseLeave(e); }

        // Minutes while they fit, hours after that: "30", "90" is "1.5h" only in
        // theory - the presets are whole, so "2h", "24h".
        private string Digits()
        {
            if (minutes < 100) return minutes.ToString(CultureInfo.InvariantCulture);
            if (minutes % 60 == 0) return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h";
            return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h+";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // The slab behind us is the button; only the hover shade is ours.
            using (SolidBrush b = new SolidBrush(hot ? Theme.Lift(Theme.Good, 22) : Theme.Good))
                g.FillRectangle(b, ClientRectangle);
            if (hot)
                using (GraphicsPath p = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 8))
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
                    g.DrawPath(pen, p);

            Rectangle ring = new Rectangle(6, 6, Width - 13, Height - 13);
            Color live = armed ? Theme.OnAccent : Color.FromArgb(150, Theme.OnAccent);

            // The dim circle first - three quarters of one, the gap at the top
            // right where the arrow head goes - then the bright arc that has
            // run so far.
            const float Start = -40f, Sweep = 290f;
            using (Pen dim = new Pen(Color.FromArgb(armed ? 70 : 45, 255, 255, 255), 2f))
                g.DrawArc(dim, ring, Start, Sweep);
            if (armed && progress > 0.01)
                using (Pen p = new Pen(Color.FromArgb(210, 255, 255, 255), 2f))
                    g.DrawArc(p, ring, Start, Sweep * (float)progress);

            // The head that makes the circle a refresh: a triangle at the end of
            // the arc, laid along the tangent so it points the way round.
            using (SolidBrush b = new SolidBrush(live))
            {
                double a = (Start + Sweep) * Math.PI / 180.0;
                float cx = ring.X + ring.Width / 2f, cy = ring.Y + ring.Height / 2f;
                float r = ring.Width / 2f;
                float px = cx + (float)Math.Cos(a) * r, py = cy + (float)Math.Sin(a) * r;
                float dx = -(float)Math.Sin(a), dy = (float)Math.Cos(a);   // the way round
                float nx = -dy, ny = dx;                                   // across it
                PointF[] head = new PointF[]
                {
                    new PointF(px + dx * 6f, py + dy * 6f),
                    new PointF(px - dx * 2f + nx * 4.2f, py - dy * 2f + ny * 4.2f),
                    new PointF(px - dx * 2f - nx * 4.2f, py - dy * 2f - ny * 4.2f)
                };
                g.FillPolygon(b, head);
            }

            using (Font f = new Font("Segoe UI", armed ? 8.5f : 8f, FontStyle.Bold))
                TextRenderer.DrawText(g, armed ? Digits() : "off", f, ClientRectangle, live,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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

    // -------------------------------------------------------------- list badge

    // The lists, folded into the button that uses them: three bars on the LEFT
    // of the slab, mirroring the repeat badge on the right. A click opens
    // exactly the two lists that button acts on - what it closes, and what it
    // stops - and nothing else.
    //
    // The lists ARE the button. Having to go Settings -> tab -> pane to change
    // what BOOST NOW does made them read as somebody else's configuration,
    // which is how an entry like 'claude' sits on a kill list for weeks without
    // anyone meeting it.
    //
    // A sibling of the button underneath, not a child, so the click opens the
    // lists instead of boosting the machine.
    internal sealed class ListBadge : Control
    {
        private readonly Color slab;
        private bool hot;

        public ListBadge(Color buttonColor)
        {
            slab = buttonColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(46, 46);
            BackColor = slab;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        { hot = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // The slab behind us is the button; only the hover shade is ours.
            using (SolidBrush b = new SolidBrush(hot ? Theme.Lift(slab, 22) : slab))
                g.FillRectangle(b, ClientRectangle);
            if (hot)
                using (GraphicsPath p = RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), 8))
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
                    g.DrawPath(pen, p);

            // Three bars. Bright enough to read as a control, not so bright it
            // competes with the word next to it.
            Color ink = Color.FromArgb(hot ? 235 : 190, 255, 255, 255);
            const int W = 22, H = 3, Gap = 6;
            int x = (Width - W) / 2;
            int y = (Height - (H * 3 + Gap * 2)) / 2;
            using (SolidBrush b = new SolidBrush(ink))
                for (int i = 0; i < 3; i++)
                {
                    using (GraphicsPath p = RoundRect(new Rectangle(x, y + i * (H + Gap), W, H), 1))
                        g.FillPath(b, p);
                }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // --------------------------------------------------------------- band rule

    // The divider under the two big buttons. The dozen small buttons used to
    // be one undifferentiated grid of gray slabs - "Trim RAM now" sat next to
    // "Settings", and the two that launch somebody else's installer sat
    // wherever there was a gap. They are bands now, and this is what heads a
    // band: its name in the middle of a hairline that runs out to both sides
    // and fades as it goes.
    //
    // Deliberately short. Four of these had to be found inside the window
    // without taking any height from the console, so a rule is one line of
    // 7.5pt and nothing else - no box, no padding, no second colour behind it.
    // (The 816px ceiling this was originally cut to no longer holds - see the
    // note where the bands are built.)
    //
    // The name carries the band's colour, and the line starts in the same
    // colour before it dies away: steel for the boost's own row, soft red for
    // the row that takes things away for good, gray for the program's own.
    internal sealed class BandRule : Control
    {
        private readonly Color ink;

        public BandRule(string name, Color color)
        {
            ink = color;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = name;
            Font = Theme.Tag();
            BackColor = Theme.Bg;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine;
            Size sz = TextRenderer.MeasureText(g, Text, Font, new Size(Width, Height), flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ink, flags);

            // The hairline, in a quieter version of the name's colour: out of
            // the name in both directions, fading as it goes, so the band is
            // announced from the middle and the line ends nowhere in
            // particular rather than stopping against something.
            int y = Height / 2;
            int gap = 10;
            int left = (Width - sz.Width) / 2 - gap;
            int right = (Width + sz.Width) / 2 + gap;
            Color lit = Theme.Mix(ink, Theme.Bg, 0.45);
            if (left > 2)
            {
                Rectangle r = new Rectangle(0, y, left, 1);
                using (LinearGradientBrush lg = new LinearGradientBrush(r, Theme.Bg, lit,
                           LinearGradientMode.Horizontal))
                    g.FillRectangle(lg, r);
            }
            if (right < Width - 2)
            {
                Rectangle r = new Rectangle(right, y, Width - right, 1);
                using (LinearGradientBrush lg = new LinearGradientBrush(r, lit, Theme.Bg,
                           LinearGradientMode.Horizontal))
                    g.FillRectangle(lg, r);
            }
        }
    }

    // What one button closes and what it stops, side by side, with Save. The
    // panes are the very same ListPane the settings window builds, reading and
    // writing the very same idlemaster.ini - this is a shorter way in, not a
    // second copy of the truth.
    internal sealed class ListsPopup : Form
    {
        private readonly IniFile ini = new IniFile();
        private readonly List<ListPane> panes = new List<ListPane>();
        public bool Saved;

        public ListsPopup(string title, string note,
                          string killSection, string killCaption,
                          string svcSection, string svcCaption)
        {
            Theme.Form(this);
            Text = title;
            BackColor = Theme.Panel;
            Size = new Size(788, 500);
            MinimumSize = new Size(620, 420);
            StartPosition = FormStartPosition.CenterParent;

            Label hint = Theme.Caption(note);
            hint.SetBounds(12, 10, 748, 18);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            hint.AutoEllipsis = true;
            Controls.Add(hint);

            ListPane left = new ListPane(ini, killSection, killCaption, false);
            ListPane right = new ListPane(ini, svcSection, svcCaption, true);
            Controls.Add(left);
            Controls.Add(right);
            panes.Add(left);
            panes.Add(right);

            Button save = Theme.Action("Save");
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Cancel");
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);

            // Laid out by hand on every resize, for the reason the settings
            // window lays its pair out by hand: anchoring two panes to the same
            // bottom edge sends their buttons off the form the first time
            // somebody drags a corner.
            Resize += delegate
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                int top = 34, bottom = h - 46;
                int half = (w - 30) / 2;
                left.SetBounds(12, top, half, bottom - top);
                right.SetBounds(18 + half, top, half, bottom - top);
                save.SetBounds(w - 116, h - 40, 104, 30);
                cancel.SetBounds(w - 228, h - 40, 104, 30);
            };
            OnResize(EventArgs.Empty);

            AcceptButton = save;
            CancelButton = cancel;
        }

        private void Persist()
        {
            try
            {
                foreach (ListPane p in panes) p.Save(ini);
                ini.Save();
                Saved = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not write idlemaster.ini:\n\n" + ex.Message,
                    "Idle Master", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

        public PickForm(string title, bool services) : this(title, services ? "svc" : "proc") { }

        public PickForm(string title, string kind)
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

            if (kind == "svc") FillServices();
            else if (kind == "wifi") FillWifi();
            else if (kind == "remote") FillRemote();
            else FillProcesses();

            Button ok = Theme.Action("Add selected");
            ok.SetBounds(316, 444, 105, 30);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.Click += delegate
            {
                // Heading rows carry an empty value and are not picks.
                foreach (int i in box.CheckedIndices)
                    if (values[i].Length > 0) Picked.Add(values[i]);
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

        // The common remote-desktop stacks first, detected ones marked - and
        // then everything running, because literally any app can be watched.
        private void FillRemote()
        {
            values.Add("");
            box.Items.Add("--- common remote desktop services ('*' = found on this machine) ---");
            foreach (string[] c in RemoteApps.Common)
            {
                values.Add(c[0]);
                box.Items.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1,-16} {2}",
                    RemoteApps.Detected(c[0], c[2]) ? "*" : " ", c[0],
                    c[1] + (c[2].Length > 0 ? "   (service " + c[2] + ")" : "")));
            }
            values.Add("");
            box.Items.Add("--- everything running right now ---");
            foreach (ProcRow r in Engine.Snapshot(null))
            {
                values.Add(r.Name);
                box.Items.Add(string.Format(CultureInfo.InvariantCulture, "  {0,-34} {1,8}  {2}",
                    r.Name, Engine.Size(r.Bytes), r.Count > 1 ? "x" + r.Count : ""));
            }
        }

        // Saved Wi-Fi profiles; the ones in the air right now first, by signal.
        private void FillWifi()
        {
            Wlan.Interface w = Wlan.First();
            if (w == null)
            {
                box.Items.Add("(no Wi-Fi adapter found - or the WLAN service is not running)");
                box.Enabled = false;
                return;
            }
            List<string> profiles = Wlan.Profiles(w.Guid);
            Dictionary<string, int> air = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Looking at what is in range makes Windows ask for location. Only
            // when the guard itself has been allowed to (NetworkGuardScan).
            bool scan = false;
            try { scan = Config.Load().NetworkGuardScan; } catch (Exception) { }
            foreach (Wlan.Network n in scan ? Wlan.Visible(w.Guid, false) : new List<Wlan.Network>())
            {
                string name = n.Profile.Length > 0 ? n.Profile : n.Ssid;
                if (name.Length == 0) continue;
                if (!air.ContainsKey(name) || air[name] < n.Signal) air[name] = n.Signal;
            }
            profiles.Sort(delegate(string a, string b)
            {
                int sa = air.ContainsKey(a) ? air[a] : -1, sb = air.ContainsKey(b) ? air[b] : -1;
                if (sa != sb) return sb.CompareTo(sa);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            foreach (string p in profiles)
            {
                values.Add(p);
                box.Items.Add(air.ContainsKey(p)
                    ? string.Format(CultureInfo.InvariantCulture, "* {0,-40} in range, signal {1}%", p, air[p])
                    : "  " + p);
            }
            if (profiles.Count == 0) box.Items.Add("(no saved Wi-Fi profiles - connect to a network once by hand first)");
            else if (!scan) box.Items.Add("  (in Windows' own order; NetworkGuardScan=1 would mark the ones in range - Windows asks for location then)");
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
    // Every row says where it came from - the shipped base kit, a toast answer
    // ("Always trash" written by the ask dialog), or added by you - and wears
    // that origin's colour.
    internal sealed class ListPane : Panel
    {
        private readonly string section;
        private readonly string kind;       // "proc" | "svc" | "wifi" | "remote" - what "Add from machine" lists
        private readonly BufferedListView box = new BufferedListView();
        private readonly ColumnHeader colName, colFrom;
        private readonly List<IniFile.Entry> before;

        public ListPane(IniFile ini, string sectionName, string caption, bool isServices)
            : this(ini, sectionName, caption, isServices ? "svc" : "proc") { }

        public ListPane(IniFile ini, string sectionName, string caption, string listKind)
        {
            section = sectionName;
            kind = listKind;
            before = ini.Section(section);

            BackColor = Theme.Panel;
            ForeColor = Theme.Fg;
            // The children are laid out for this size; whoever hosts the pane
            // resizes it afterwards, and the anchors take it from there.
            Size = new Size(366, 372);

            Label head = Theme.Caption(caption);
            head.SetBounds(6, 6, 354, 18);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            head.AutoEllipsis = true;
            Controls.Add(head);

            box.View = View.Details;
            box.CheckBoxes = true;
            box.FullRowSelect = true;
            box.HideSelection = false;
            box.HeaderStyle = ColumnHeaderStyle.None;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Theme.Input;
            box.ForeColor = Theme.ListFg;
            box.Font = Theme.Mono();
            colName = box.Columns.Add("Entry", 280);
            colFrom = box.Columns.Add("From", 66, HorizontalAlignment.Right);
            box.SetBounds(6, 28, 354, 300);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            box.Resize += delegate { SizeColumns(); };
            Controls.Add(box);
            SizeColumns();

            foreach (IniFile.Entry e in before)
                AddRow(e.Text, e.Enabled,
                    e.Chosen ? "toast" : Config.IsKitEntry(section, e.Text) ? "kit" : "added");

            Button add = Btn(kind == "wifi" ? "Pick a saved network" : "Add from machine", 6, 130);
            add.Click += delegate { Pick(); };

            Button typed = Btn("Type one", 142, 88);
            typed.Click += delegate { Typed(); };

            Button del = Btn("Remove", 236, 90);
            del.Click += delegate
            {
                for (int i = box.Items.Count - 1; i >= 0; i--)
                    if (box.Items[i].Selected) box.Items.RemoveAt(i);
            };
        }

        private void SizeColumns()
        {
            int w = box.ClientSize.Width - colFrom.Width - 4;
            if (w > 60) colName.Width = w;
        }

        // The origin decides the colour: base-kit rows in the list's usual pale
        // blue, toast answers in the accent, your own additions in white.
        private void AddRow(string text, bool check, string origin)
        {
            ListViewItem it = new ListViewItem(text);
            it.UseItemStyleForSubItems = false;
            it.ForeColor = origin == "kit" ? Theme.ListFg
                         : origin == "toast" ? Theme.Accent : Theme.Fg;
            ListViewItem.ListViewSubItem from = it.SubItems.Add(origin);
            from.ForeColor = origin == "toast" ? Theme.Accent : Theme.Dim;
            box.Items.Add(it);
            it.Checked = check;
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
            string title = kind == "svc" ? "Running services" : kind == "wifi" ? "Saved Wi-Fi networks"
                : kind == "remote" ? "Remote desktop apps" : "Running processes";
            using (PickForm f = new PickForm(title, kind))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (string v in f.Picked)
                {
                    if (Has(v)) continue;
                    AddRow(v, true, "added");
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
                l.Text = kind == "svc" ? "Service name:" : kind == "wifi" ? "Wi-Fi profile name ('*' allowed):" : "Process name ('*' allowed):";
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
                AddRow(v, true, "added");
            }
        }

        private bool Has(string v)
        {
            foreach (ListViewItem it in box.Items)
                if (string.Equals(it.Text, v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The ticked names, for callers that want the list as it stands on
        // screen (the remote page calibrates against it before saving).
        public List<string> CheckedNames()
        {
            List<string> names = new List<string>();
            foreach (ListViewItem it in box.Items)
                if (it.Checked) names.Add(it.Text);
            return names;
        }

        public void Save(IniFile ini)
        {
            List<string> now = new List<string>();
            for (int i = 0; i < box.Items.Count; i++) now.Add(box.Items[i].Text);

            foreach (IniFile.Entry e in before)
            {
                int at = -1;
                for (int i = 0; i < now.Count; i++)
                    if (now[i].Equals(e.Text, StringComparison.OrdinalIgnoreCase)) { at = i; break; }

                if (at < 0) { ini.Remove(section, e.Text); continue; }
                bool on = box.Items[at].Checked;
                if (on != e.Enabled) ini.SetEnabled(section, e.Text, on);
            }

            for (int i = 0; i < now.Count; i++)
            {
                bool old = false;
                foreach (IniFile.Entry e in before)
                    if (now[i].Equals(e.Text, StringComparison.OrdinalIgnoreCase)) { old = true; break; }
                if (old) continue;
                ini.Add(section, now[i]);
                if (!box.Items[i].Checked) ini.SetEnabled(section, now[i], false);
            }

            // A pane can be saved twice (Calibrate saves, then Save saves again);
            // 'before' has to move with the file or the second pass double-adds.
            before.Clear();
            for (int i = 0; i < box.Items.Count; i++)
                before.Add(new IniFile.Entry(box.Items[i].Text, box.Items[i].Checked));
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
            new string[] { "SkipOpenApps",         "Never kill an app with a window open (boost only)" },
            new string[] { "OverclockedSentry",    "Overclocked sentry - while hunting, kill EVERYTHING not protected (no asking, no sparing)" },
            new string[] { "Tray",                 "Tray icon - closing the window hides to it" },
            new string[] { "StartWithWindows",     "Start Idle Master as you log in (saving here makes/removes the logon task)" },
            new string[] { "KillExplorer",         "Absolute idle recycles the shell - desktop, taskbar and Start come back fresh" },
            new string[] { "NetworkGuard",         "Network guard - keep the link, Tailscale and Sunshine up; fix and reconnect when they drop" },
            new string[] { "TrimWorkingSets",      "Squeeze the working set of every surviving process" },
            new string[] { "ClearStandbyList",     "Purge the standby (cached) list" },
            new string[] { "CloseBrowsersInBoost", "Boost closes browsers too" },
            new string[] { "NetworkGuardWifi",     "...including reconnecting Wi-Fi to a known network on its own" },
            new string[] { "NetworkGuardKeepWifiAwake", "...and stop Windows powering the Wi-Fi adapter down to save energy" },
            new string[] { "NetworkGuardScan",     "...and scan for which saved networks are in range (Windows asks for location permission once)" },
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
            new string[] { "SentryFullPassMinutes","Repeat a whole boost pass (services, trim, guard) every (minutes, 0 = off)", "0", "1440", "0" },
            new string[] { "BoostWhenFreeBelowMb", "...and do one now when free RAM drops below (MB, 0 = off)", "0", "99999", "0" },
            new string[] { "RepeatBoostMinutes",   "Repeat loop: click BOOST NOW again every (minutes, 0 = off)", "0", "1440", "0" },
            new string[] { "AskTimeoutSeconds",    "Dialog answers itself after (seconds)",         "5", "600",  "47" },
            new string[] { "AskAboveMb",           "Ask about unlisted newcomers bigger than (MB, 0 = off)", "0", "99999", "250" },
            new string[] { "TrimWhenFreeBelowMb",  "Emergency trim when free RAM drops below (MB, 0 = off)", "0", "99999", "0" },
            new string[] { "UpdateCheckHours",     "Look for a newer release every (hours, 0 = only by hand)", "0", "720", "6" },
            new string[] { "CleanupInstallerDays", "Suggest Downloads installers older than (days)",  "7",  "3650",   "90" },
            new string[] { "CleanupBigDirMinMb",   "Big-folder suggestions start at (MB)",            "50", "999999", "500" },
            new string[] { "NetworkGuardSeconds",  "Network guard checks the connection every (seconds)", "15", "3600", "60" },
        };

        // How the Numbers table splits into headings in the advanced window.
        public const int SentryCount = 9;
        public const int AskCount = 4;
        public const int CleanupCount = 2;

        // Which flags ship OFF; everything else defaults on when the key is
        // missing from the ini. Has to match the Config field initialisers.
        public static bool FlagDefault(string key)
        {
            return key != "OverclockedSentry" && key != "StartWithWindows"
                && key != "NetworkGuardScan" && key != "CloseBrowsersInBoost";
        }

        // key, label, choices (value|label)
        public static readonly string[] TimeoutAction = new string[]
        {
            "trash|trash it once", "keep|leave it alone", "always|trash it every time",
        };

        // What the StartWithWindows logon start runs on its own.
        public static readonly string[] StartupActions = new string[]
        {
            "none|nothing", "boost|BOOST NOW", "idle|ABSOLUTE IDLE",
        };

        public static ComboBox Choice(string current) { return ChoiceOf(TimeoutAction, current); }

        public static ComboBox ChoiceOf(string[] pairs, string current)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Input;
            c.ForeColor = Theme.Fg;
            int sel = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] kv = pairs[i].Split('|');
                c.Items.Add(kv[1]);
                if (current != null && kv[0].Equals(current.Trim(), StringComparison.OrdinalIgnoreCase)) sel = i;
            }
            c.SelectedIndex = sel;
            return c;
        }

        public static string ChoiceValue(ComboBox c) { return ChoiceValueOf(TimeoutAction, c); }

        public static string ChoiceValueOf(string[] pairs, ComboBox c)
        {
            int i = c.SelectedIndex < 0 ? 0 : c.SelectedIndex;
            return pairs[i].Split('|')[0];
        }

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
        private ComboBox timeoutAction;
        private ComboBox startupAction;

        public bool Saved;
        public bool WantsTheme;         // closed by the Theme... button, not by Save or Cancel

        public QuickSettingsForm()
        {
            Theme.Form(this);
            Text = "Idle Master - settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(460, 552);

            int y = 16;
            y = Flag("Sentry", "Keep hunting after a boost",
                "Re-applies your kill lists on a timer until you hit Restore.", y);
            y = Flag("AskBeforeKill", "Ask before killing anything new",
                "A toast with the app's icon and four answers; no answer = the choice below.", y);
            y = Flag("Tray", "Keep running in the tray",
                "Closing the window hides Idle Master instead of quitting it.", y);
            y = Flag("NetworkGuard", "Network guard - never lose the way back in",
                "Checks Wi-Fi, internet, Tailscale and Sunshine; reconnects what drops.", y);
            y = Flag("StartWithWindows", "Start with Windows as I log in",
                "A logon task opens Idle Master; the choice below says what it runs.", y);

            Label sl = new Label();
            sl.Text = "On that logon start, run";
            sl.SetBounds(20, y + 3, 330, 20);
            Controls.Add(sl);
            startupAction = SettingSpec.ChoiceOf(SettingSpec.StartupActions, ini.GetSetting("StartupAction"));
            startupAction.SetBounds(290, y, 150, 22);
            Controls.Add(startupAction);
            y += 32;

            y += 8;
            y = Number("AskTimeoutSeconds", y);
            Label tl = new Label();
            tl.Text = "No answer means";
            tl.SetBounds(20, y + 3, 330, 20);
            Controls.Add(tl);
            timeoutAction = SettingSpec.Choice(ini.GetSetting("AskTimeoutAction"));
            timeoutAction.SetBounds(290, y, 150, 22);
            Controls.Add(timeoutAction);
            y += 32;
            y = Number("SentrySeconds", y);
            y = Number("TrimWhenFreeBelowMb", y);

            Button advanced = Theme.Quiet("Advanced settings...");
            advanced.SetBounds(20, 504, 150, 30);
            advanced.Click += delegate { OpenAdvanced(); };
            Controls.Add(advanced);

            // The look. Not a dropdown here: the picker is a pane over the main
            // window with previews on it, and it cannot be shown underneath a
            // modal dialog - so this closes and the window opens it.
            Button theme = Theme.Quiet("Theme...");
            theme.SetBounds(176, 504, 70, 30);
            theme.Click += delegate { WantsTheme = true; Close(); };
            Controls.Add(theme);

            Button save = Theme.Action("Save");
            save.SetBounds(252, 504, 90, 30);
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Cancel");
            cancel.SetBounds(350, 504, 90, 30);
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private int Flag(string key, string label, string hint, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label;
            c.Checked = SettingSpec.Truthy(ini.GetSetting(key), SettingSpec.FlagDefault(key));
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
                ini.SetSetting("AskTimeoutAction", SettingSpec.ChoiceValue(timeoutAction));
                ini.SetSetting("StartupAction", SettingSpec.ChoiceValueOf(SettingSpec.StartupActions, startupAction));
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
        private ComboBox timeoutAction;

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
            tabs.ItemSize = new Size(83, 28);      // nine tabs have to fit in 754 px
            tabs.DrawItem += DrawTab;
            Controls.Add(tabs);

            tabs.TabPages.Add(SettingsTab());
            tabs.TabPages.Add(NeverTouch());
            tabs.TabPages.Add(Pair("Boost now", "boost.kill", "Processes closed by Boost",
                                                "boost.services", "Services stopped by Boost"));
            tabs.TabPages.Add(Pair("Absolute idle", "idle.kill", "Also closed by Absolute Idle",
                                                    "idle.services", "Also stopped by Absolute Idle"));
            tabs.TabPages.Add(Single("Restore", "restore.launch",
                "Relaunched by Restore desktop  (full path, optional |arguments)"));
            tabs.TabPages.Add(Single("Cleanup", "cleanup.protect",
                "Paths disk cleanup must never touch  (full path, '*' works)"));
            tabs.TabPages.Add(Single("Debloat", "debloat.protect",
                "Store apps debloat must never suggest  (package names, '*' works)"));
            tabs.TabPages.Add(Single("Answered", "ask.never",
                "Answered with 'Always trash' - closed on sight, never asked about again"));
            tabs.TabPages.Add(Single("Network", "network.wifi",
                "Wi-Fi networks the network guard reconnects to, best first  (saved profiles; empty = every one it knows)", "wifi"));

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
                c.Checked = SettingSpec.Truthy(ini.GetSetting(f[0]), SettingSpec.FlagDefault(f[0]));
                c.SetBounds(16, y, 700, 24);
                c.ForeColor = Theme.Fg;
                page.Controls.Add(c);
                flags[f[0]] = c;
                y += 26;
            }

            y += 8;
            y = Header(page, "Sentry timing", y);
            for (int i = 0; i < SettingSpec.SentryCount; i++) y = NumberRow(page, SettingSpec.Numbers[i], y);

            y += 8;
            y = Header(page, "Asking && safety", y);
            for (int i = SettingSpec.SentryCount; i < SettingSpec.SentryCount + SettingSpec.AskCount; i++)
                y = NumberRow(page, SettingSpec.Numbers[i], y);

            Label tl = new Label();
            tl.Text = "No answer to the dialog means";
            tl.SetBounds(16, y + 4, 480, 20);
            page.Controls.Add(tl);
            timeoutAction = SettingSpec.Choice(ini.GetSetting("AskTimeoutAction"));
            timeoutAction.SetBounds(504, y, 150, 22);
            page.Controls.Add(timeoutAction);
            y += 28;

            y += 8;
            y = Header(page, "Disk cleanup", y);
            int cleanupFrom = SettingSpec.SentryCount + SettingSpec.AskCount;
            for (int i = cleanupFrom; i < cleanupFrom + SettingSpec.CleanupCount; i++)
                y = NumberRow(page, SettingSpec.Numbers[i], y);

            y += 8;
            y = Header(page, "Network guard", y);
            for (int i = cleanupFrom + SettingSpec.CleanupCount; i < SettingSpec.Numbers.Length; i++)
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

            // A TabPage is 200x100 until the TabControl sizes it, so anchoring
            // to its bottom here would send the panes' buttons off the page.
            // Lay them out by hand on every resize instead.
            ListPane left = new ListPane(ini, leftSection, leftCaption, false);
            page.Controls.Add(left);
            ListPane right = new ListPane(ini, rightSection, rightCaption, true);
            page.Controls.Add(right);
            page.Resize += delegate
            {
                int h = page.ClientSize.Height - 8;
                int half = (page.ClientSize.Width - 12) / 2;
                left.SetBounds(4, 4, half, h);
                right.SetBounds(8 + half, 4, half, h);
            };

            panes.Add(left);
            panes.Add(right);
            return page;
        }

        // Three lists, not two: processes on the left, and the right column
        // split between services and whole process trees. "Whole tree" is its
        // own list because it is a different promise - it spares the helpers
        // an app spawned under a name of their own, which is the only way to
        // keep a WebView2 app (WhatsApp, Discord) alive while its msedgewebview2
        // workers stay on the kill list.
        private TabPage NeverTouch()
        {
            TabPage page = new TabPage("Never touch");
            page.BackColor = Theme.Panel;

            ListPane procs = new ListPane(ini, "protect",
                "Processes that survive everything", false);
            ListPane svcs = new ListPane(ini, "protect.services",
                "Services that survive everything", true);
            ListPane trees = new ListPane(ini, "protect.tree",
                "...and these, helper processes included", false);
            page.Controls.Add(procs);
            page.Controls.Add(svcs);
            page.Controls.Add(trees);

            page.Resize += delegate
            {
                int h = page.ClientSize.Height - 8;
                int half = (page.ClientSize.Width - 12) / 2;
                procs.SetBounds(4, 4, half, h);
                // The tree list is short by nature - a handful of apps - so it
                // takes the smaller share of the right column.
                int top = (h - 4) * 3 / 5;
                svcs.SetBounds(8 + half, 4, half, top);
                trees.SetBounds(8 + half, 12 + top, half, h - top - 8);
            };

            panes.Add(procs);
            panes.Add(svcs);
            panes.Add(trees);
            return page;
        }

        private TabPage Single(string title, string section, string caption)
        {
            return Single(title, section, caption, "proc");
        }

        private TabPage Single(string title, string section, string caption, string kind)
        {
            TabPage page = new TabPage(title);
            page.BackColor = Theme.Panel;

            ListPane pane = new ListPane(ini, section, caption, kind);
            page.Controls.Add(pane);
            page.Resize += delegate
            {
                pane.SetBounds(4, 4, page.ClientSize.Width - 8, page.ClientSize.Height - 8);
            };

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
                ini.SetSetting("AskTimeoutAction", SettingSpec.ChoiceValue(timeoutAction));
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

    // ListView is the only stock control that can show these tables, and the
    // only way to reach its protected DoubleBuffered is to inherit it. Shared
    // by the task manager and the disk cleanup window.
    internal sealed class BufferedListView : ListView
    {
        [System.Runtime.InteropServices.DllImport("uxtheme.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr h, string app, string idList);

        public BufferedListView() { DoubleBuffered = true; }

        // A dark list with a bright white scrollbar down the side is the one
        // thing that gives the theme away. Same trick as CleanTree, and it has
        // to be the Explorer class: DarkMode_ItemsView leaves the scrollbar
        // white. It also rules a hairline down every column boundary, which on
        // a ten-column table is worth having.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        }
    }

    // The Master's task manager. It started as a top-30 list of who was holding
    // the most RAM; it is now the whole machine - every process, with CPU, disk,
    // threads, handles, uptime and the exe's real name, sortable on any column
    // and filterable by typing. The census behind it is ProcSampler, which asks
    // the kernel for one table rather than opening a handle per process, so a
    // 400-process list costs about as much as the old 30-name one did.
    //
    // Three things make it feel like a tool rather than a report:
    //   - the list is virtual, so sorting 400 rows is instant and never flickers
    //   - a row is one app by default and one process when you ask, because the
    //     kill lists are written per name but a runaway is usually one pid
    //   - double-click ends it, with no dialog in the way. That is deliberate.
    //     The engine already refuses to touch anything protected (Reap re-checks
    //     every pid), so the dialog was only ever asking permission for things
    //     that were already safe to do - and asking it every single time.
    //
    // Non-modal, so the log stays visible while you work through the list.
    internal sealed class EatersForm : Form
    {
        // Columns, in the order they are added. Sorting, painting and the
        // header arrow all index off this.
        private const int ColProcess = 0;
        private const int ColDesc = 1;
        private const int ColWho = 2;      // "Instances" grouped, "PID" expanded
        private const int ColCpu = 3;
        private const int ColMem = 4;
        private const int ColDisk = 5;
        private const int ColThreads = 6;
        private const int ColUp = 7;
        private const int ColWindow = 8;
        private const int ColTag = 9;
        private const int ColCount = 10;

        private static readonly int[] Rates = new int[] { 1000, 2000, 5000, 0 };

        private readonly Config cfg;
        private readonly Engine engine;
        private readonly Action<string> log;
        private readonly Func<bool> sentryAlive;
        private readonly ProcSampler sampler = new ProcSampler();

        private readonly BufferedListView eaters;
        private readonly ColumnHeader[] cols = new ColumnHeader[ColCount];
        private readonly TextBox filter;
        private readonly CheckBox chkPerProcess;
        private readonly Button btnRate;
        private readonly Label status, meter;
        private readonly ToolStripMenuItem miKill, miKillAndList, miBoost, miIdle, miProtect,
            miWhere, miCopyName, miCopyPath, miPerProcess;
        private readonly System.Windows.Forms.Timer timer;

        // "all" is the last census; "rows" is what survived the filter and the
        // sort, and is what the virtual list indexes into. They have to stay
        // separate: filtering "rows" in place would narrow the list with every
        // keystroke and never widen it again when you backspace.
        private List<ProcRow> all = new List<ProcRow>();
        private List<ProcRow> rows = new List<ProcRow>();
        private ListViewItem[] cache = new ListViewItem[0];

        private int sortCol = ColMem;
        private bool sortDown = true;       // biggest first
        private bool perProcess;
        private int rateIndex = 1;          // 2 s
        private bool sampling;
        private bool menuOpen;

        public EatersForm(Config c, Engine e, Action<string> logger, Func<bool> sentryUp)
        {
            cfg = c;
            engine = e;
            log = logger;
            sentryAlive = sentryUp;

            Theme.Form(this);
            Text = "IDLE MASTER - task manager";
            Size = new Size(980, 660);
            MinimumSize = new Size(680, 380);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;

            Label cap = Theme.Caption("TASK MANAGER");
            cap.SetBounds(16, 14, 130, 18);
            Controls.Add(cap);

            filter = new TextBox();
            Theme.Input_(filter);
            filter.SetBounds(152, 11, 230, 22);
            filter.TextChanged += delegate { Rebuild(); };
            Controls.Add(filter);
            Theme.Cue(filter, "filter by name or publisher");

            chkPerProcess = new CheckBox();
            chkPerProcess.Text = "every process separately";
            chkPerProcess.ForeColor = Theme.Fg;
            chkPerProcess.FlatStyle = FlatStyle.Flat;
            chkPerProcess.SetBounds(394, 11, 176, 22);
            chkPerProcess.CheckedChanged += delegate { SetPerProcess(chkPerProcess.Checked); };
            Controls.Add(chkPerProcess);

            btnRate = Theme.Quiet("every 2 s");
            btnRate.SetBounds(580, 10, 88, 24);
            btnRate.Font = Theme.Small();
            btnRate.Click += delegate { CycleRate(); };
            Controls.Add(btnRate);

            Label hint = Theme.Hint("double-click ends it - Del ends everything selected");
            hint.Font = Theme.Small();
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.SetBounds(676, 13, 288, 18);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(hint);

            eaters = new BufferedListView();
            eaters.View = View.Details;
            eaters.VirtualMode = true;
            eaters.FullRowSelect = true;
            eaters.MultiSelect = true;
            eaters.HideSelection = false;
            eaters.HeaderStyle = ColumnHeaderStyle.Clickable;
            eaters.BorderStyle = BorderStyle.FixedSingle;
            eaters.BackColor = Theme.Input;
            eaters.ForeColor = Theme.Fg;
            eaters.OwnerDraw = true;
            eaters.DrawColumnHeader += DrawHeader;
            eaters.DrawItem += delegate(object s, DrawListViewItemEventArgs a) { a.DrawDefault = true; };
            eaters.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs a) { a.DrawDefault = true; };
            eaters.RetrieveVirtualItem += Retrieve;
            eaters.ColumnClick += HeaderClicked;
            eaters.DoubleClick += delegate { EndSelected(); };
            eaters.KeyDown += ListKey;

            Add(ColProcess, "Process", 148, HorizontalAlignment.Left);
            Add(ColDesc, "Description", 214, HorizontalAlignment.Left);
            Add(ColWho, "Instances", 76, HorizontalAlignment.Right);
            // Widths allow for the sort arrow, which takes a 12-pixel strip out
            // of whichever column is sorted - too tight and the caption itself
            // ellipsises the moment you sort by it.
            Add(ColCpu, "CPU %", 70, HorizontalAlignment.Right);
            Add(ColMem, "Memory", 88, HorizontalAlignment.Right);
            Add(ColDisk, "Disk", 80, HorizontalAlignment.Right);
            Add(ColThreads, "Threads", 70, HorizontalAlignment.Right);
            Add(ColUp, "Running", 72, HorizontalAlignment.Right);
            Add(ColWindow, "Open", 48, HorizontalAlignment.Left);
            Add(ColTag, "Tag", 58, HorizontalAlignment.Left);

            eaters.SetBounds(16, 42, 932, 542);
            eaters.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            eaters.Resize += delegate { SizeColumns(); };
            Controls.Add(eaters);
            SizeColumns();

            status = Theme.Hint("reading the process table...");
            status.SetBounds(16, 594, 520, 20);
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(status);

            meter = Theme.Hint("");
            meter.TextAlign = ContentAlignment.MiddleRight;
            meter.ForeColor = Theme.ListFg;
            meter.SetBounds(540, 594, 408, 20);
            meter.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(meter);

            eaters.ContextMenuStrip = BuildMenu(out miKill, out miKillAndList, out miBoost,
                out miIdle, out miProtect, out miWhere, out miCopyName, out miCopyPath,
                out miPerProcess);

            sampler.Prime();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = Rates[rateIndex];
            timer.Tick += delegate { RefreshNow(); };
            timer.Start();
            RefreshNow();
        }

        private void Add(int i, string text, int width, HorizontalAlignment align)
        {
            cols[i] = eaters.Columns.Add(text, width, align);
        }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem kill,
            out ToolStripMenuItem killAndList, out ToolStripMenuItem boost,
            out ToolStripMenuItem idle, out ToolStripMenuItem protect,
            out ToolStripMenuItem where, out ToolStripMenuItem copyName,
            out ToolStripMenuItem copyPath, out ToolStripMenuItem perProc)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            Theme.Menu(m);

            kill = Item("End it now", delegate { EndSelected(); });
            kill.ShortcutKeyDisplayString = "Del";
            killAndList = Item("End it and close it on every boost from now on",
                delegate { EndSelected(); AddSelected("boost.kill", "boost list"); });

            boost = Item("Close on every boost", delegate { AddSelected("boost.kill", "boost list"); });
            idle = Item("Also close on absolute idle", delegate { AddSelected("idle.kill", "idle list"); });
            protect = Item("Never touch (protect)", delegate { AddSelected("protect", "protected list"); });

            where = Item("Open the folder it lives in", delegate { OpenFolder(); });
            copyName = Item("Copy name", delegate { Copy(false); });
            copyPath = Item("Copy full path", delegate { Copy(true); });

            perProc = Item("Show every process separately",
                delegate { SetPerProcess(!perProcess); });
            perProc.CheckOnClick = false;

            m.Items.Add(kill);
            m.Items.Add(killAndList);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(boost);
            m.Items.Add(idle);
            m.Items.Add(protect);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(where);
            m.Items.Add(copyName);
            m.Items.Add(copyPath);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(perProc);
            m.Opening += MenuOpening;
            m.Closed += delegate { menuOpen = false; };
            return m;
        }

        private static ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            i.Click += onClick;
            return i;
        }

        // Process and description share whatever is left over; everything else
        // is a number and keeps the width it was given.
        //
        // Only ever called when the width actually available has changed, so a
        // column the user has dragged to their own width stays dragged. That
        // includes the vertical scrollbar appearing: it takes its 17 pixels out
        // of ClientSize the moment the list overflows, and columns laid out
        // before that happened are what put a horizontal scrollbar underneath.
        private int lastWidth = -1;

        private void SizeColumns()
        {
            // ClientSize only stops counting the vertical scrollbar once the
            // scrollbar is actually up, and a list of every process on the
            // machine always ends up with one. Laid out before it appears, the
            // columns come out ~17px too wide and the list flashes a horizontal
            // scrollbar on its first frame. So reserve it either way: the gap
            // between Width and ClientSize says whether it is already counted.
            int avail = eaters.ClientSize.Width;
            int bar = SystemInformation.VerticalScrollBarWidth;
            if (eaters.Width - avail < bar) avail -= bar;
            lastWidth = eaters.ClientSize.Width;

            int fixedWidth = 0;
            for (int i = 0; i < ColCount; i++)
                if (i != ColProcess && i != ColDesc) fixedWidth += cols[i].Width;

            int spare = avail - fixedWidth - 4;
            if (spare < 200) return;
            cols[ColProcess].Width = (int)(spare * 0.42);
            cols[ColDesc].Width = spare - cols[ColProcess].Width;
        }

        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                e.Graphics.FillRectangle(b, e.Bounds);

            bool sorted = e.ColumnIndex == sortCol;
            bool rightAligned = e.Header.TextAlign == HorizontalAlignment.Right;

            Rectangle r = e.Bounds;
            r.Inflate(-6, 0);

            // The arrow gets a strip of its own rather than being glued onto
            // the caption: "CPU %" in a 58-pixel column has no room to spare,
            // and an appended arrow just pushed the caption into an ellipsis.
            // It sits on the side the text is running away from.
            if (sorted)
            {
                Rectangle a = r;
                a.Width = 12;
                if (!rightAligned) a.X = r.Right - 12;
                else r.X += 12;
                r.Width -= 12;
                TextRenderer.DrawText(e.Graphics, sortDown ? "▾" : "▴", eaters.Font, a,
                    Theme.Accent, TextFormatFlags.HorizontalCenter
                        | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            TextRenderer.DrawText(e.Graphics, e.Header.Text, eaters.Font, r,
                sorted ? Theme.Accent : Theme.Dim,
                (rightAligned ? TextFormatFlags.Right : TextFormatFlags.Left)
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void HeaderClicked(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == sortCol) sortDown = !sortDown;
            else
            {
                sortCol = e.Column;
                // Numbers want their biggest first; names want A first. "Open"
                // counts as a number here - the point of clicking it is to see
                // what you have on screen, not what you do not.
                sortDown = e.Column != ColProcess && e.Column != ColDesc
                    && e.Column != ColTag;
            }
            eaters.Invalidate();
            Rebuild();
        }

        private void CycleRate()
        {
            rateIndex = (rateIndex + 1) % Rates.Length;
            int ms = Rates[rateIndex];
            timer.Stop();
            if (ms > 0)
            {
                timer.Interval = ms;
                timer.Start();
                btnRate.Text = "every " + (ms / 1000) + " s";
                RefreshNow();
            }
            else btnRate.Text = "paused";
        }

        private void SetPerProcess(bool on)
        {
            if (perProcess == on) return;
            perProcess = on;
            if (chkPerProcess.Checked != on) chkPerProcess.Checked = on;
            cols[ColWho].Text = on ? "PID" : "Instances";
            eaters.Invalidate();
            RefreshNow();
        }

        private void ListKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { EndSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { RefreshNow(); e.Handled = true; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.F)) { filter.Focus(); filter.SelectAll(); return true; }
            if (keys == Keys.Escape && filter.Text.Length > 0) { filter.Clear(); return true; }
            return base.ProcessCmdKey(ref msg, keys);
        }

        // ---- the census

        // The sample itself is quick, but resolving an exe path the first time
        // it is seen reads a file, and there is no reason for the UI thread to
        // wait on a disk. One in flight at a time; a tick that lands while the
        // last one is still out is simply dropped.
        public void RefreshNow()
        {
            if (!Visible || WindowState == FormWindowState.Minimized || menuOpen) return;
            if (sampling) return;
            sampling = true;

            Thread t = new Thread(delegate()
            {
                List<ProcRow> got = null;
                try { got = sampler.Sample(engine, perProcess); }
                catch (Exception) { }

                List<ProcRow> result = got;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        sampling = false;
                        if (result == null || IsDisposed) return;
                        all = result;
                        Rebuild();
                    });
                }
                catch (Exception) { sampling = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        // Filter, sort, hand the list to the virtual view - and put the
        // selection back on the same processes it was on before, which after a
        // sort by CPU are almost never at the same row numbers.
        private void Rebuild()
        {
            HashSet<string> selected = new HashSet<string>();
            foreach (int i in eaters.SelectedIndices)
                if (i >= 0 && i < rows.Count) selected.Add(rows[i].ListKey);

            List<ProcRow> shown = Filtered();
            shown.Sort(Compare);

            eaters.BeginUpdate();
            try
            {
                // Shrinking a virtual list under a live selection throws, so the
                // selection is dropped first and restored by key afterwards.
                eaters.SelectedIndices.Clear();
                rows = shown;
                cache = new ListViewItem[shown.Count];
                eaters.VirtualListSize = shown.Count;

                // Selection follows the process, not the row number - sorting
                // by CPU moves things every tick. Deliberately no EnsureVisible:
                // a list that scrolls itself every two seconds is unusable.
                if (selected.Count > 0)
                    for (int i = 0; i < shown.Count; i++)
                        if (selected.Contains(shown[i].ListKey)) eaters.SelectedIndices.Add(i);
            }
            catch (Exception) { }
            finally { eaters.EndUpdate(); }

            if (eaters.ClientSize.Width != lastWidth) SizeColumns();
            eaters.Invalidate();
            Status();
        }

        private List<ProcRow> Filtered()
        {
            string q = filter.Text.Trim();
            if (q.Length == 0) return new List<ProcRow>(all);

            List<ProcRow> keep = new List<ProcRow>();
            foreach (ProcRow r in all)
            {
                if (r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Desc != null && r.Desc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (r.Tag.Length > 0 && r.Tag.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (perProcess && r.Pid.ToString(CultureInfo.InvariantCulture) == q))
                    keep.Add(r);
            }
            return keep;
        }

        private int Compare(ProcRow a, ProcRow b)
        {
            int n;
            switch (sortCol)
            {
                case ColProcess: n = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case ColDesc: n = string.Compare(a.Desc, b.Desc, StringComparison.OrdinalIgnoreCase); break;
                case ColWho: n = perProcess ? a.Pid.CompareTo(b.Pid) : a.Count.CompareTo(b.Count); break;
                case ColCpu: n = a.Cpu.CompareTo(b.Cpu); break;
                case ColDisk: n = a.Disk.CompareTo(b.Disk); break;
                case ColThreads: n = a.Threads.CompareTo(b.Threads); break;
                // The column shows an uptime, not a start time, so it sorts
                // as one: descending must put the longest-running first, and
                // that is the EARLIEST start.
                case ColUp: n = b.Started.CompareTo(a.Started); break;
                case ColWindow: n = a.HasWindow.CompareTo(b.HasWindow); break;
                case ColTag: n = string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase); break;
                default: n = a.Bytes.CompareTo(b.Bytes); break;
            }
            // Ties fall back to size, so the order never jitters between ticks.
            if (n == 0) n = a.Bytes.CompareTo(b.Bytes);
            if (n == 0) n = string.Compare(a.ListKey, b.ListKey, StringComparison.OrdinalIgnoreCase);
            return sortDown ? -n : n;
        }

        // ---- painting rows

        private void Retrieve(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= rows.Count)
            {
                e.Item = new ListViewItem("");
                return;
            }
            if (cache.Length == rows.Count && cache[e.ItemIndex] != null)
            {
                e.Item = cache[e.ItemIndex];
                return;
            }

            ProcRow r = rows[e.ItemIndex];
            ListViewItem it = new ListViewItem(r.Name);
            it.UseItemStyleForSubItems = false;
            it.Tag = r;
            for (int i = 1; i < ColCount; i++) it.SubItems.Add("");

            it.SubItems[ColDesc].Text = r.Desc;
            it.SubItems[ColWho].Text = perProcess
                ? r.Pid.ToString(CultureInfo.InvariantCulture)
                : r.Count.ToString(CultureInfo.InvariantCulture);
            it.SubItems[ColCpu].Text = ProcSampler.Percent(r.Cpu);
            it.SubItems[ColMem].Text = Engine.Size(r.Bytes);
            it.SubItems[ColDisk].Text = ProcSampler.Rate(r.Disk);
            it.SubItems[ColThreads].Text = r.Threads.ToString(CultureInfo.InvariantCulture);
            it.SubItems[ColUp].Text = ProcSampler.Age(r.Started);
            it.SubItems[ColWindow].Text = r.HasWindow ? "open" : "";
            it.SubItems[ColTag].Text = r.Tag;

            Color body = r.Tag == "KEEP" ? Theme.Dim : Theme.Fg;
            for (int i = 0; i < ColCount; i++) it.SubItems[i].ForeColor = body;
            it.SubItems[ColDesc].ForeColor = Theme.Dim;

            // The two numbers worth catching out of the corner of an eye.
            if (r.Cpu >= 15) it.SubItems[ColCpu].ForeColor = Theme.Warn;
            else if (r.Cpu >= 3) it.SubItems[ColCpu].ForeColor = Theme.Accent;
            if (r.Disk >= 4 * 1024 * 1024) it.SubItems[ColDisk].ForeColor = Theme.Accent;

            it.SubItems[ColWindow].ForeColor = r.HasWindow ? Theme.Accent : Theme.Dim;
            it.SubItems[ColTag].ForeColor =
                r.Tag == "BOOST" ? Theme.Accent :
                r.Tag == "IDLE" ? Theme.Warn :
                r.Tag == "KEEP" ? Theme.Dim : Theme.Fg;

            if (cache.Length == rows.Count) cache[e.ItemIndex] = it;
            e.Item = it;
        }

        private void Status()
        {
            string noun = perProcess ? " processes" : " apps";
            string what = rows.Count < all.Count
                ? rows.Count + " of " + all.Count + noun + " shown"
                : rows.Count + noun;
            // In per-process mode the row count already IS the process count,
            // and "196 processes - 196 processes running" says nothing twice.
            if (!perProcess) what += "   ·   " + sampler.ProcessCount + " processes running";
            status.Text = what
                + (ProcQuery.Detailed ? "" : "   ·   no CPU or disk figures on this machine");

            ulong totalMb, freeMb;
            Engine.ReadMemory(out totalMb, out freeMb);
            ulong usedMb = totalMb > freeMb ? totalMb - freeMb : 0;

            meter.Text = string.Format(CultureInfo.InvariantCulture,
                "CPU {0}%   ·   disk {1}   ·   RAM {2:0.0} / {3:0.0} GB",
                sampler.HaveRates ? ProcSampler.Percent(sampler.TotalCpu) : "-",
                ProcSampler.Rate(sampler.TotalIo),
                usedMb / 1024.0, totalMb / 1024.0);
        }

        // ---- acting on a row

        private List<ProcRow> Selection()
        {
            List<ProcRow> picked = new List<ProcRow>();
            foreach (int i in eaters.SelectedIndices)
                if (i >= 0 && i < rows.Count) picked.Add(rows[i]);
            return picked;
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            List<ProcRow> picked = Selection();
            if (picked.Count == 0) { e.Cancel = true; return; }
            menuOpen = true;

            bool one = picked.Count == 1;
            ProcRow r = picked[0];
            string what = one
                ? r.Name + (perProcess ? " (" + r.Pid + ")" : "")
                : picked.Count + " selected";

            miKill.Text = "End " + what + " now  (" + Engine.Size(Total(picked)) + ")";
            miKill.Enabled = AnyKillable(picked);
            miKillAndList.Text = one
                ? "End it, and close it on every boost from now on"
                : "End them, and close them on every boost from now on";
            miKillAndList.Enabled = miKill.Enabled && AnyUnlisted(picked);

            miBoost.Enabled = AnyUnlisted(picked);
            miIdle.Enabled = AnyTagged(picked, "");
            miProtect.Enabled = AnyKillable(picked);

            miWhere.Enabled = one && r.Path.Length > 0;
            miCopyName.Enabled = true;
            miCopyPath.Enabled = one && r.Path.Length > 0;
            miPerProcess.Checked = perProcess;
        }

        private static long Total(List<ProcRow> picked)
        {
            long n = 0;
            foreach (ProcRow r in picked) n += r.Bytes;
            return n;
        }

        private static bool AnyKillable(List<ProcRow> picked)
        {
            foreach (ProcRow r in picked) if (r.Tag != "KEEP") return true;
            return false;
        }

        private static bool AnyUnlisted(List<ProcRow> picked)
        {
            foreach (ProcRow r in picked) if (r.Tag == "" || r.Tag == "IDLE") return true;
            return false;
        }

        private static bool AnyTagged(List<ProcRow> picked, string tag)
        {
            foreach (ProcRow r in picked) if (r.Tag == tag) return true;
            return false;
        }

        // No confirmation. The engine's Reap re-checks every pid against the
        // protected list, the protected trees and the protected paths before it
        // touches anything, so the only processes that can actually die here are
        // ones the user picked and the Master was already willing to close.
        // Reap waits up to 3 s per pid, so it never runs on the UI thread.
        private void EndSelected()
        {
            List<ProcRow> picked = Selection();
            if (picked.Count == 0) return;

            List<Candidate> jobs = new List<Candidate>();
            foreach (ProcRow r in picked)
            {
                if (r.Tag == "KEEP") continue;
                Candidate c = new Candidate(r.Name);
                c.Bytes = r.Bytes;
                c.Pids.AddRange(r.Pids);
                jobs.Add(c);
            }
            if (jobs.Count == 0)
            {
                log("Nothing ended - everything selected is protected.");
                return;
            }

            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int died = 0;
                List<string> names = new List<string>();
                foreach (Candidate c in jobs)
                {
                    List<KillHit> hits = engine.Reap(c);
                    if (hits.Count == 0) continue;
                    died += hits.Count;
                    freed += Engine.TotalOf(hits);
                    names.Add(c.Name);
                }

                string line;
                if (died == 0)
                    line = "Nothing died - already gone, or it refused.";
                else if (names.Count == 1)
                    line = "Ended " + names[0] + " - " + Engine.Size(freed) + " back.";
                else
                    line = "Ended " + died + " processes across " + names.Count
                         + " apps - " + Engine.Size(freed) + " back.";
                log(line);

                try { BeginInvoke((Action)RefreshNow); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AddSelected(string section, string label)
        {
            List<ProcRow> picked = Selection();
            if (picked.Count == 0) return;

            List<string> written = new List<string>();
            bool trouble = false;
            foreach (ProcRow r in picked)
            {
                if (r.Tag == "KEEP" && section != "protect") continue;
                if (Config.Append(section, r.Name.ToLowerInvariant())) written.Add(r.Name);
                else trouble = true;
            }

            if (trouble) log("! could not write every name into the " + label + ".");
            if (written.Count == 0) return;

            try
            {
                cfg.CopyFrom(Config.Load());
                log((written.Count == 1 ? written[0] : written.Count + " apps")
                    + " added to the " + label + ".");
                if (sentryAlive())
                    log("The sentry is using the new lists from its next sweep.");
            }
            catch (Exception ex) { log("! could not reload the config: " + ex.Message); }
            RefreshNow();
        }

        private void OpenFolder()
        {
            List<ProcRow> picked = Selection();
            if (picked.Count != 1 || picked[0].Path.Length == 0) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe",
                    "/select,\"" + picked[0].Path + "\"");
            }
            catch (Exception ex) { log("! could not open the folder: " + ex.Message); }
        }

        private void Copy(bool fullPath)
        {
            List<ProcRow> picked = Selection();
            if (picked.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            foreach (ProcRow r in picked)
            {
                string s = fullPath ? r.Path : r.Name;
                if (s.Length > 0) sb.AppendLine(s);
            }
            try { if (sb.Length > 0) Clipboard.SetText(sb.ToString().TrimEnd()); }
            catch (Exception) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }
    }

    // ----------------------------------------------------------- disk cleanup

    // One row that lives directly in the disk map: a (tree, node) pair. The
    // path and size are read from the map on demand, never copied.
    internal sealed class FsRef
    {
        public readonly DiskTree Tree;
        public readonly int Node;
        public FsRef(DiskTree t, int n) { Tree = t; Node = n; }
        public string Path { get { return Tree.PathOf(Node); } }
        public long Bytes { get { return Tree.Bytes[Node]; } }
    }

    // A TreeView that repaints without flicker and never grows a horizontal
    // scrollbar - the columns painted on the right need a stable width. The
    // dark Explorer theme gives it dark scrollbars and visible glyphs.
    internal sealed class CleanTree : TreeView
    {
        private const int TVS_NOHSCROLL = 0x8000;
        private const int TVM_SETEXTENDEDSTYLE = 0x112C;
        private const int TVS_EX_DOUBLEBUFFER = 0x0004;

        [System.Runtime.InteropServices.DllImport("uxtheme.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr h, string app, string idList);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);

        public CleanTree() { DoubleBuffered = true; }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.Style |= TVS_NOHSCROLL; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch (Exception) { }
            try
            {
                SendMessage(Handle, TVM_SETEXTENDEDSTYLE,
                    (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
            }
            catch (Exception) { }
        }
    }

    // The window behind "Disk cleanup". Scan reads each drive's file table in
    // seconds, then everything is a tree: categories hold findings, findings
    // open into what is actually inside them, and the disk map at the bottom
    // holds the whole drive. Tick what goes - known junk arrives pre-ticked -
    // and Clean sends it to the Recycle Bin. Nothing is deleted on its own.
    internal sealed class CleanupForm : Form
    {
        // Fixed order so the tree reads top-down from "obviously junk" to
        // "you decide" - the same journey the scanner itself takes.
        private static readonly string[] CategoryOrder = new string[]
        {
            "Temp files", "Caches", "Crash dumps", "Windows update",
            "Old installers", "Recycle bin", "Possible leftovers", "Big folders",
        };
        private const string DiskMapCat = "Disk map";

        // Names that read as junk wherever they appear - the class column
        // marks them "junk?" inside the map so the eye lands on them first.
        private static readonly string[] JunkNames = new string[]
        {
            "temp", "tmp", "cache", "caches", "cache2", "code cache", "gpucache",
            "shadercache", "dxcache", "d3dscache", "crashdumps", "crash reports",
            "minidump", "dumps", "logs", "log",
        };

        private const int MaxKids = 400;    // rows shown per expanded level

        private readonly Config cfg;
        private readonly Action<string> log;
        private readonly CleanTree tree;
        // Three views of the same scan, one at a time. The findings tree is
        // the default and the only one that can tick and clean; the other two
        // are the drive itself, for the question a curated list cannot answer.
        private readonly WizTreeView wiz;       // folder tree with columns
        private readonly Panel mapPanel;        // the treemap
        private readonly TreeMapView map;
        private readonly Label crumb, mapInfo;
        private readonly Button btnUp;
        private readonly ComboBox cmbView;      // one switch, three views
        private readonly ComboBox cmbSize, cmbClass;
        private readonly TextBox txtName;
        private readonly CheckBox chkTicked;
        private readonly Button btnScan, btnStop, btnClean;
        private readonly Label progress;
        private readonly System.Windows.Forms.Timer timer;
        private readonly ToolStripMenuItem miOpen, miCleanOne, miProtect, miCopy;
        private readonly Font mono, small;

        // The worker drops findings here; a 200 ms timer drains them onto the
        // UI thread in batches, so a fast scan cannot flood BeginInvoke.
        private readonly List<CleanupItem> arrived = new List<CleanupItem>();
        private readonly object gate = new object();
        private string phase = "";
        private CleanupScanner scanner;
        private bool working;
        private bool syncing;       // programmatic check changes, ignore events
        private volatile bool cleaning;     // a clean is running, not a scan
        private volatile bool stopClean;    // ...and Stop has been pressed
        private bool syncingView;   // SetView driving the switch, not the user

        // The model the tree is rebuilt from: every finding the scan produced
        // (filters only hide, never forget), and every tick the user made,
        // keyed by lowercased path. Ticks survive filter changes and rebuilds.
        private readonly List<CleanupItem> model = new List<CleanupItem>();
        private readonly Dictionary<string, object> picked
            = new Dictionary<string, object>();

        public CleanupForm(Config c, Action<string> logger)
        {
            cfg = c;
            log = logger;
            mono = Theme.Mono();
            small = Theme.Small();

            Theme.Form(this);
            Text = "IDLE MASTER - disk cleanup";
            Size = new Size(860, 640);
            MinimumSize = new Size(680, 440);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("DISK CLEANUP");
            cap.SetBounds(16, 12, 180, 18);
            Controls.Add(cap);

            Label hint = Theme.Hint("scan, open anything to see what is inside, tick what goes - Clean sends it to the Recycle Bin");
            hint.Font = small;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.SetBounds(200, 12, 628, 18);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(hint);

            // ---- the filter bar

            Label lf = Theme.Hint("show");
            lf.SetBounds(16, 40, 36, 22);   // 34 clipped it to "sho"
            lf.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lf);

            cmbSize = new ComboBox();
            cmbSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSize.FlatStyle = FlatStyle.Flat;
            Theme.Input_(cmbSize);
            cmbSize.Items.AddRange(new object[]
                { "any size", "over 10 MB", "over 100 MB", "over 1 GB" });
            cmbSize.SelectedIndex = 0;
            cmbSize.SetBounds(52, 39, 104, 24);
            cmbSize.SelectedIndexChanged += delegate { RebuildTree(); };
            Controls.Add(cmbSize);

            cmbClass = new ComboBox();
            cmbClass.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbClass.FlatStyle = FlatStyle.Flat;
            Theme.Input_(cmbClass);
            cmbClass.Items.AddRange(new object[]
                { "safe + review", "safe only", "review only" });
            cmbClass.SelectedIndex = 0;
            cmbClass.SetBounds(164, 39, 110, 24);
            cmbClass.SelectedIndexChanged += delegate { RebuildTree(); };
            Controls.Add(cmbClass);

            txtName = new TextBox();
            Theme.Input_(txtName);
            txtName.SetBounds(282, 40, 160, 22);
            txtName.TextChanged += delegate { RebuildTree(); };
            Controls.Add(txtName);

            // The prompt lives inside the box rather than on a label beside
            // it: the label used to hold 448..588 of this row, and the two view
            // switches below need that strip. A label there also sat ON TOP of
            // them - controls added later paint behind, so "drive tree" was
            // invisible until this moved.
            Theme.Cue(txtName, "type to filter by name");

            // The review switch: a flat list of exactly what Clean will take,
            // paths and all, so nothing hides in a collapsed branch.
            chkTicked = new CheckBox();
            chkTicked.Text = "ticked only";
            chkTicked.ForeColor = Theme.Fg;
            chkTicked.FlatStyle = FlatStyle.Flat;
            chkTicked.SetBounds(Width - 142, 40, 110, 22);
            chkTicked.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkTicked.CheckedChanged += delegate { RebuildTree(); };
            Controls.Add(chkTicked);

            // One switch, not two checkboxes. The three views are one-or-the-
            // other by nature, and a pair of tickboxes both says otherwise and
            // leaves "neither ticked" needing a meaning. A dropdown makes the
            // choice exclusive by construction, and matches the two beside it.
            //
            // The findings tree stays first, and stays the default: it is the
            // only view that can tick things and clean them. The other two are
            // the drive itself, for the question a curated list is bad at.
            cmbView = new ComboBox();
            cmbView.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbView.FlatStyle = FlatStyle.Flat;
            Theme.Input_(cmbView);
            cmbView.Items.AddRange(new object[]
                { "findings", "drive tree", "drive map" });
            cmbView.SelectedIndex = 0;
            cmbView.SetBounds(452, 39, 122, 24);
            cmbView.SelectedIndexChanged += delegate
            { if (!syncingView) SetView(cmbView.SelectedIndex); };
            Controls.Add(cmbView);

            // ---- the tree

            tree = new CleanTree();
            tree.CheckBoxes = true;
            tree.ShowLines = false;
            tree.ShowPlusMinus = true;
            tree.ShowRootLines = false;
            tree.FullRowSelect = true;
            tree.HideSelection = false;
            tree.ShowNodeToolTips = true;
            tree.BorderStyle = BorderStyle.FixedSingle;
            tree.BackColor = Theme.Input;
            tree.ForeColor = Theme.Fg;
            tree.ItemHeight = 20;
            tree.Indent = 18;
            tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tree.DrawNode += DrawNode;
            tree.BeforeExpand += BeforeExpand;
            tree.AfterCheck += AfterCheck;
            tree.AfterSelect += delegate { tree.Invalidate(); };
            tree.SetBounds(16, 70, 812, 454);
            tree.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(tree);

            wiz = new WizTreeView();
            wiz.SetBounds(16, 70, 812, 454);
            wiz.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                | AnchorStyles.Left | AnchorStyles.Right;
            wiz.Visible = false;
            wiz.SelectionChanged += delegate { DescribeWiz(); };
            Controls.Add(wiz);

            // Same rectangle as the tree, same anchors, shown instead of it.
            mapPanel = new Panel();
            mapPanel.SetBounds(16, 70, 812, 454);
            mapPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                | AnchorStyles.Left | AnchorStyles.Right;
            mapPanel.BackColor = Theme.Bg;
            mapPanel.Visible = false;
            Controls.Add(mapPanel);

            btnUp = Theme.Quiet("^ up");
            btnUp.SetBounds(0, 0, 56, 22);
            btnUp.Font = small;
            btnUp.Click += delegate { map.Up(); };
            mapPanel.Controls.Add(btnUp);

            crumb = Theme.Hint("");
            crumb.SetBounds(62, 2, 750, 18);
            crumb.AutoEllipsis = true;
            crumb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            mapPanel.Controls.Add(crumb);

            map = new TreeMapView();
            map.SetBounds(0, 26, 812, 404);
            map.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                | AnchorStyles.Left | AnchorStyles.Right;
            map.RootChanged += delegate { UpdateCrumb(); };
            map.CellChosen += delegate(object o, MapCellEventArgs a) { DescribeCell(a.Cell); };
            map.CellActivated += delegate(object o, MapCellEventArgs a)
            {
                if (a.Cell.IsDir) map.Down(a.Cell.Node);
                else DescribeCell(a.Cell);
            };
            mapPanel.Controls.Add(map);

            mapInfo = Theme.Hint("click a block to see what it is - double-click a folder to go in");
            mapInfo.Font = small;
            mapInfo.SetBounds(0, 434, 812, 18);
            mapInfo.AutoEllipsis = true;
            mapInfo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            mapPanel.Controls.Add(mapInfo);

            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.Menu(menu);
            miOpen = new ToolStripMenuItem("Open in Explorer");
            miOpen.Click += delegate { OpenSelected(); };
            miCleanOne = new ToolStripMenuItem("Clean just this one");
            miCleanOne.Click += delegate { CleanSelectedOnly(); };
            miProtect = new ToolStripMenuItem("Never touch this path (protect)");
            miProtect.Click += delegate { ProtectSelected(); };
            miCopy = new ToolStripMenuItem("Copy path");
            miCopy.Click += delegate { CopySelected(); };
            menu.Items.Add(miOpen);
            menu.Items.Add(miCleanOne);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miProtect);
            menu.Items.Add(miCopy);
            menu.Opening += MenuOpening;
            tree.ContextMenuStrip = menu;
            tree.NodeMouseClick += delegate(object s, TreeNodeMouseClickEventArgs a)
            { if (a.Button == MouseButtons.Right) tree.SelectedNode = a.Node; };
            // Identical menu on the map: SelectedTag() answers for whichever
            // view is showing, so every verdict below works unchanged.
            map.ContextMenuStrip = menu;
            wiz.ContextMenuStrip = menu;

            // ---- the buttons

            btnScan = Theme.Action("Scan");
            btnScan.SetBounds(16, 556, 100, 30);
            btnScan.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnScan.Click += delegate { StartScan(); };
            Controls.Add(btnScan);

            btnStop = Theme.Quiet("Stop scan");
            btnStop.SetBounds(124, 556, 100, 30);
            btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnStop.Visible = false;
            btnStop.Click += delegate
            {
                if (cleaning)
                {
                    // Between items, not inside one: a single SHFileOperation
                    // cannot be called off once the shell has it, so the most
                    // this can promise is that nothing NEW is started.
                    stopClean = true;
                    btnStop.Enabled = false;
                    progress.Text = "stopping after this item...";
                    log("   . stop pressed - finishing the current item, then stopping");
                }
                else if (scanner != null) scanner.Cancel();
            };
            Controls.Add(btnStop);

            progress = Theme.Hint("no scan yet");
            progress.Font = small;
            progress.SetBounds(232, 561, 320, 20);
            progress.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(progress);

            btnClean = Theme.Dangerous("Clean checked");
            btnClean.SetBounds(560, 556, 268, 30);
            btnClean.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnClean.Enabled = false;
            btnClean.Click += delegate { Clean(PickedItems()); };
            Controls.Add(btnClean);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate { Drain(); };
            timer.Start();
        }

        // ---- filters

        private long MinBytes()
        {
            switch (cmbSize.SelectedIndex)
            {
                case 1: return 10L * 1024 * 1024;
                case 2: return 100L * 1024 * 1024;
                case 3: return 1024L * 1024 * 1024;
                default: return 0;
            }
        }

        private bool PassesClass(bool safe)
        {
            if (cmbClass.SelectedIndex == 1) return safe;
            if (cmbClass.SelectedIndex == 2) return !safe;
            return true;
        }

        private bool PassesName(string name, string path)
        {
            string f = txtName.Text.Trim();
            if (f.Length == 0) return true;
            if (name != null && name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return path != null && path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool PassesFilters(CleanupItem it)
        {
            return it.Bytes >= MinBytes() && PassesClass(it.Safe)
                && PassesName(it.Name, it.Path);
        }

        private static bool IsJunkName(string name)
        {
            foreach (string j in JunkNames)
                if (name.Equals(j, StringComparison.OrdinalIgnoreCase)) return true;
            return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
        }

        // ---- scanning

        public void StartScan()
        {
            if (working) return;
            working = true;
            scanner = new CleanupScanner(cfg);

            model.Clear();
            picked.Clear();
            syncing = true;
            tree.Nodes.Clear();
            syncing = false;
            lock (gate) { arrived.Clear(); phase = "starting..."; }

            btnScan.Enabled = false;
            btnStop.Visible = true;
            UpdateCleanButton();
            log("-- disk cleanup scan");

            CleanupScanner mine = scanner;
            Thread t = new Thread(delegate()
            {
                List<CleanupItem> all = null;
                try
                {
                    all = mine.Scan(
                        delegate(string where) { lock (gate) { phase = where; } },
                        delegate(CleanupItem it) { lock (gate) { arrived.Add(it); } });
                }
                catch (Exception ex) { log("   ! cleanup scan failed: " + ex.Message); }
                try { BeginInvoke((Action)delegate { ScanDone(all); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Drain()
        {
            List<CleanupItem> take = null;
            string where;
            lock (gate)
            {
                where = phase;
                if (arrived.Count > 0)
                {
                    take = new List<CleanupItem>(arrived);
                    arrived.Clear();
                }
            }
            // Not during a clean: "working" covers both a scan and a clean,
            // so this line was repainting the last SCAN phase over the clean's
            // own progress every 200 ms - which is why a clean grinding through
            // 313,000 files sat there reading "scan finished".
            if (working && !cleaning) progress.Text = where;
            if (take == null) return;

            tree.BeginUpdate();
            syncing = true;
            try
            {
                foreach (CleanupItem it in take)
                {
                    if (HasFinding(it.Key)) continue;
                    model.Add(it);
                    if (it.Safe) picked[it.Key] = it;       // auto-marked - known junk
                    if (PassesFilters(it) && !chkTicked.Checked) InsertFinding(it);
                }
            }
            finally { syncing = false; tree.EndUpdate(); }
            UpdateCleanButton();
        }

        private bool HasFinding(string key)
        {
            foreach (CleanupItem m in model)
                if (m.Key == key) return true;
            return false;
        }

        private void ScanDone(List<CleanupItem> all)
        {
            Drain();
            working = false;
            btnScan.Enabled = true;
            btnStop.Visible = false;

            long junk = 0;
            foreach (CleanupItem it in model) if (it.Safe) junk += it.Bytes;
            bool cancelled = scanner != null && scanner.Cancelled;

            if (chkTicked.Checked) RebuildTree();
            else AddDiskMap();
            progress.Text = (cancelled ? "cancelled - " : "")
                + model.Count + " findings, " + CleanupScanner.Nice(junk) + " known junk";
            log("   = scan " + (cancelled ? "cancelled" : "finished") + ": "
                + model.Count + " findings, " + CleanupScanner.Nice(junk)
                + " of known junk pre-ticked.");
            UpdateCleanButton();
        }

        // ---- building the tree

        private TreeNode CategoryNode(string cat)
        {
            foreach (TreeNode n in tree.Nodes)
                if ((n.Tag as string) == cat) return n;

            int mine = Rank(cat);
            int at = tree.Nodes.Count;
            for (int i = 0; i < tree.Nodes.Count; i++)
                if (Rank((string)tree.Nodes[i].Tag) > mine) { at = i; break; }

            TreeNode node = new TreeNode(cat);
            node.Tag = cat;
            tree.Nodes.Insert(at, node);
            node.Expand();
            return node;
        }

        private static int Rank(string category)
        {
            if (category == DiskMapCat) return CategoryOrder.Length + 1;
            for (int i = 0; i < CategoryOrder.Length; i++)
                if (CategoryOrder[i] == category) return i;
            return CategoryOrder.Length;
        }

        private void InsertFinding(CleanupItem it)
        {
            TreeNode cat = CategoryNode(it.Category);
            TreeNode node = new TreeNode(it.Name);
            node.Tag = it;
            node.ToolTipText = (it.Note.Length > 0 ? it.Note + "\r\n" : "")
                + (it.IsRecycleBin ? "(all drives)" : it.Path);
            if (it.Parts != null || CanOpen(it)) node.Nodes.Add(MakeDummy());

            int at = cat.Nodes.Count;
            for (int i = 0; i < cat.Nodes.Count; i++)
            {
                CleanupItem other = cat.Nodes[i].Tag as CleanupItem;
                if (other != null && other.Bytes < it.Bytes) { at = i; break; }
            }
            cat.Nodes.Insert(at, node);
            node.Checked = picked.ContainsKey(it.Key);
            if (!cat.IsExpanded) cat.Expand();      // a childless Expand() is
                                                    // a no-op, so re-assert it
        }

        private bool CanOpen(CleanupItem it)
        {
            return it.Tree != null && it.Node >= 0 && it.Tree.IsDir(it.Node)
                && it.Tree.FirstChild[it.Node] >= 0;
        }

        private static TreeNode MakeDummy()
        {
            TreeNode d = new TreeNode("...");
            d.Tag = "::dummy";
            return d;
        }

        private static bool IsDummy(TreeNode n)
        {
            return (n.Tag as string) == "::dummy";
        }

        private void AddDiskMap()
        {
            if (scanner == null || scanner.Trees.Count == 0) return;
            syncing = true;
            try
            {
                TreeNode cat = CategoryNode(DiskMapCat);
                cat.Nodes.Clear();
                foreach (DiskTree t in scanner.Trees)
                {
                    TreeNode d = new TreeNode(t.Root);
                    d.Tag = new FsRef(t, t.RootNode);
                    d.ToolTipText = t.Items[t.RootNode].ToString("N0") + " entries"
                        + (t.FromMft ? ", read from the file table" : ", walked");
                    d.Nodes.Add(MakeDummy());
                    cat.Nodes.Add(d);
                }
                cat.Expand();
            }
            finally { syncing = false; }
        }

        // Rebuilt from the model on any filter change. Expanded map branches
        // collapse back - the ticks survive, they live in 'picked'.
        private void RebuildTree()
        {
            if (chkTicked.Checked) { BuildReview(); UpdateCleanButton(); return; }
            tree.BeginUpdate();
            syncing = true;
            try
            {
                tree.Nodes.Clear();
                foreach (CleanupItem it in model)
                    if (PassesFilters(it)) InsertFinding(it);
            }
            finally { syncing = false; tree.EndUpdate(); }
            AddDiskMap();
            UpdateCleanButton();
        }

        // "ticked only": the flat review list of what Clean will take, after
        // nested ticks have collapsed into their parents. Untick a row here
        // and it leaves the plan on the spot - no scrolling the whole tree.
        private void BuildReview()
        {
            tree.BeginUpdate();
            syncing = true;
            try
            {
                tree.Nodes.Clear();
                List<CleanupItem> plan = PickedItems();
                TreeNode head = new TreeNode("ticked - what Clean will take");
                head.Tag = "Ticked";
                tree.Nodes.Add(head);
                foreach (CleanupItem it in plan)
                {
                    TreeNode node = new TreeNode(it.Name);
                    node.Tag = it;
                    node.ToolTipText = (it.Note.Length > 0 ? it.Note + "\r\n" : "")
                        + (it.IsRecycleBin ? "(all drives)" : it.Path);
                    head.Nodes.Add(node);
                    node.Checked = true;
                }
                head.Expand();
            }
            finally { syncing = false; tree.EndUpdate(); }
        }

        // ---- opening a row: the Revo moment

        private void BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode node = e.Node;
            if (node.Nodes.Count != 1 || !IsDummy(node.Nodes[0])) return;

            tree.BeginUpdate();
            syncing = true;
            try
            {
                node.Nodes.Clear();
                CleanupItem it = node.Tag as CleanupItem;
                FsRef f = node.Tag as FsRef;
                if (it != null && it.Parts != null) PopulateParts(node, it);
                else if (it != null && it.Tree != null && it.Node >= 0)
                    PopulateFs(node, it.Tree, it.Node);
                else if (f != null) PopulateFs(node, f.Tree, f.Node);
            }
            finally { syncing = false; tree.EndUpdate(); }
        }

        private void PopulateParts(TreeNode into, CleanupItem it)
        {
            foreach (string part in it.Parts)
            {
                DiskTree t; int n;
                if (scanner != null && scanner.Resolve(part, out t, out n))
                    into.Nodes.Add(FsNode(t, n));
                else
                {
                    TreeNode plain = new TreeNode(part);
                    plain.Tag = null;
                    into.Nodes.Add(plain);
                }
            }
        }

        // The children of one mapped folder, biggest first. The size filter
        // trims the noise but never the headline: the top rows always show,
        // or opening a temp folder full of small files would show nothing.
        private void PopulateFs(TreeNode into, DiskTree t, int n)
        {
            List<int> kids = t.ChildrenBySize(n);
            long min = MinBytes();
            string f = txtName.Text.Trim();
            int shown = 0;
            long hiddenBytes = 0;
            int hidden = 0;

            foreach (int c in kids)
            {
                bool pass = (t.Bytes[c] >= min || shown < 20)
                    && (f.Length == 0
                        || t.Name[c].IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!pass || shown >= MaxKids)
                {
                    hidden++;
                    hiddenBytes += t.Bytes[c];
                    continue;
                }
                into.Nodes.Add(FsNode(t, c));
                shown++;
            }

            if (hidden > 0)
            {
                TreeNode more = new TreeNode("... " + hidden.ToString("N0")
                    + " more, " + CleanupScanner.Nice(hiddenBytes)
                    + "  (hidden by the filters)");
                more.Tag = null;
                into.Nodes.Add(more);
            }
        }

        private TreeNode FsNode(DiskTree t, int c)
        {
            FsRef r = new FsRef(t, c);
            TreeNode node = new TreeNode(t.Name[c]);
            node.Tag = r;
            if (t.IsDir(c))
                node.ToolTipText = t.Items[c].ToString("N0") + " entries";
            if (t.IsDir(c) && !t.IsReparse(c) && t.FirstChild[c] >= 0)
                node.Nodes.Add(MakeDummy());
            node.Checked = picked.ContainsKey(r.Path.ToLowerInvariant());
            return node;
        }

        // ---- ticking

        private void AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (syncing) return;
            TreeNode node = e.Node;

            string cat = node.Tag as string;
            if (cat != null)
            {
                if (IsDummy(node)) return;
                syncing = true;
                try
                {
                    if (cat == DiskMapCat) { node.Checked = false; return; }
                    // a category header ticks or unticks every finding under it
                    foreach (TreeNode child in node.Nodes)
                    {
                        CleanupItem it = child.Tag as CleanupItem;
                        if (it == null) { child.Checked = false; continue; }
                        child.Checked = node.Checked;
                        if (node.Checked) picked[it.Key] = it;
                        else picked.Remove(it.Key);
                    }
                }
                finally { syncing = false; }
                UpdateCleanButton();
                if (chkTicked.Checked)
                    BeginInvoke((Action)delegate { RebuildTree(); });
                return;
            }

            CleanupItem item = node.Tag as CleanupItem;
            FsRef f = node.Tag as FsRef;
            if (item == null && f == null)
            {
                syncing = true;
                try { node.Checked = false; } finally { syncing = false; }
                return;
            }

            string path = item != null ? item.Path : f.Path;
            string key = item != null ? item.Key : path.ToLowerInvariant();

            if (node.Checked)
            {
                string why = ForbidCheck(item, f, path, node);
                if (why != null)
                {
                    syncing = true;
                    try { node.Checked = false; } finally { syncing = false; }
                    progress.Text = why;
                    log("   . " + why);
                    return;
                }
                picked[key] = (object)item ?? (object)f;
            }
            else picked.Remove(key);
            UpdateCleanButton();
            if (chkTicked.Checked && !node.Checked)
                BeginInvoke((Action)delegate { RebuildTree(); });   // the row
                                                    // leaves the review list
        }

        // The reasons a tick is refused. Findings are curated upstream; this
        // guards the disk map, where the whole drive is on display - in code,
        // so no amount of clicking sends the OS itself to the bin.
        private string ForbidCheck(CleanupItem item, FsRef f, string path, TreeNode node)
        {
            if (item != null && item.IsRecycleBin) return null;

            CleanupScanner guard = scanner != null ? scanner : new CleanupScanner(cfg);
            if (guard.IsProtectedPath(path))
                return "protected by [cleanup.protect]: " + path;
            if (item != null) return null;      // a curated finding

            // an ancestor already ticked takes this whole folder with it
            // The map has no TreeNode behind the selection. The ticked-ancestor
            // test is about the tree's checkboxes, so with no node there is
            // nothing it could find - the path rules below still apply.
            for (TreeNode p = node == null ? null : node.Parent; p != null; p = p.Parent)
            {
                CleanupItem pi = p.Tag as CleanupItem;
                FsRef pf = p.Tag as FsRef;
                string pp = pi != null ? pi.Key
                    : (pf != null ? pf.Path.ToLowerInvariant() : null);
                if (pp != null && picked.ContainsKey(pp))
                    return "already covered - the ticked parent takes this too";
            }

            string root = null;
            try { root = System.IO.Path.GetPathRoot(path); } catch (Exception) { }
            if (root == null || path.Length <= root.Length)
                return "not the whole drive - open it and pick what goes";
            string rest = path.Substring(root.Length);
            string top = rest.Split('\\')[0];
            bool topOnly = rest.IndexOf('\\') < 0;

            if (top.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                return "\\Windows is off limits here - its junk is curated in the categories above";
            if (top.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || top.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || top.Equals("Recovery", StringComparison.OrdinalIgnoreCase))
                return top + " belongs to Windows - not for the bin";
            if (topOnly && (top.StartsWith("pagefile", StringComparison.OrdinalIgnoreCase)
                || top.StartsWith("hiberfil", StringComparison.OrdinalIgnoreCase)
                || top.StartsWith("swapfile", StringComparison.OrdinalIgnoreCase)))
                return top + " is Windows memory - it cannot go";
            if (topOnly && (top.Equals("Users", StringComparison.OrdinalIgnoreCase)
                || top.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                || top.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || top.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)))
                return top + " as a whole is too big a bite - open it and pick";
            if (top.Equals("Users", StringComparison.OrdinalIgnoreCase)
                && rest.Split('\\').Length == 2)
                return "a whole profile is too big a bite - open it and pick";
            return null;
        }

        // ---- what is ticked, deduplicated

        private List<CleanupItem> PickedItems()
        {
            // nested ticks collapse into the outermost - the parent's delete
            // already takes the child, and one shell call beats two
            List<string> keys = new List<string>(picked.Keys);
            keys.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
            List<CleanupItem> outp = new List<CleanupItem>();
            List<string> covering = new List<string>();

            foreach (string key in keys)
            {
                bool covered = false;
                foreach (string c in covering)
                    if (key.StartsWith(c, StringComparison.Ordinal)) { covered = true; break; }
                if (covered) continue;

                object tag = picked[key];
                CleanupItem it = tag as CleanupItem;
                if (it == null)
                {
                    FsRef f = (FsRef)tag;
                    it = new CleanupItem();
                    it.Name = f.Tree.Name[f.Node];
                    it.Path = f.Path;
                    it.Category = DiskMapCat;
                    it.Bytes = f.Bytes;
                    it.Tree = f.Tree;
                    it.Node = f.Node;
                }
                outp.Add(it);
                if (!it.IsRecycleBin && it.Parts == null)
                    covering.Add(key.TrimEnd('\\') + "\\");
            }
            return outp;
        }

        private void UpdateCleanButton()
        {
            long bytes = 0;
            int n = 0;
            foreach (CleanupItem it in PickedItems()) { bytes += it.Bytes; n++; }
            btnClean.Enabled = n > 0 && !working;
            btnClean.Text = n == 0
                ? "Clean checked"
                : "Clean checked  (" + n + " items, " + CleanupScanner.Nice(bytes) + ")";
        }

        // ---- cleaning

        private void Clean(List<CleanupItem> pickedNow)
        {
            if (working || pickedNow.Count == 0) return;

            long bytes = 0;
            bool bin = false;
            foreach (CleanupItem it in pickedNow)
            {
                bytes += it.Bytes;
                if (it.IsRecycleBin) bin = true;
            }

            // Split the list by where each item is actually going, and say so.
            // The dialog used to promise "everything can be restored from the
            // bin", which was about to stop being true for the update payloads.
            List<string> forGood = new List<string>();
            long forGoodBytes = 0;
            foreach (CleanupItem it in pickedNow)
                if (CleanupActions.IsPermanent(it) && !it.IsRecycleBin)
                {
                    forGood.Add(it.Name);
                    forGoodBytes += it.Bytes;
                }

            string msg = "Clean " + pickedNow.Count + " item(s) - "
                + CleanupScanner.Nice(bytes) + "?\n\n"
                + "Everything goes to the Recycle Bin and can be restored"
                + (forGood.Count == 0 ? "." : ", except:");

            if (forGood.Count > 0)
                msg += "\n\nDELETED FOR GOOD (" + CleanupScanner.Nice(forGoodBytes) + "):\n  "
                    + string.Join("\n  ", forGood.ToArray())
                    + "\n\nThese are payloads for updates already installed. Windows' own"
                    + " Disk Cleanup deletes them outright too - recycling them would move"
                    + " hundreds of thousands of files one at a time and free nothing until"
                    + " you emptied the bin afterwards.";

            if (bin)
                msg += "\n\nThe Recycle Bin row itself is ticked - emptying it is permanent,"
                    + " and it is done last, after everything else has arrived.";
            if (MessageBox.Show(this, msg, "Disk cleanup", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            working = true;
            cleaning = true;
            stopClean = false;
            btnScan.Enabled = false;
            btnClean.Enabled = false;
            btnStop.Text = "Stop cleaning";
            btnStop.Enabled = true;
            btnStop.Visible = true;
            progress.Text = "cleaning...";
            log("-- disk cleanup");

            // The bin is emptied LAST: it is where everything else is headed,
            // and emptying it first would burn the undo for this very batch.
            pickedNow.Sort(delegate(CleanupItem a, CleanupItem b)
                { return (a.IsRecycleBin ? 1 : 0) - (b.IsRecycleBin ? 1 : 0); });

            CleanupScanner guard = scanner != null ? scanner : new CleanupScanner(cfg);
            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int at = 0;
                List<CleanupItem> gone = new List<CleanupItem>();
                foreach (CleanupItem it in pickedNow)
                {
                    if (stopClean)
                    {
                        log("   . stopped - " + (pickedNow.Count - at)
                            + " item(s) left untouched");
                        break;
                    }

                    at++;
                    // Name what is being worked on BEFORE it starts: the item
                    // that takes an hour is the one you most need named.
                    CleanupItem cur = it;
                    int n = at;
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            progress.Text = "cleaning " + n + " of " + pickedNow.Count
                                + " - " + cur.Name + " (" + CleanupScanner.Nice(cur.Bytes) + ")"
                                + (CleanupActions.IsPermanent(cur) ? " - for good" : "");
                        });
                    }
                    catch (Exception) { }

                    if (!CleanupActions.Recycle(it, guard, log)) continue;
                    freed += it.Bytes;
                    gone.Add(it);
                }
                log("   = " + CleanupScanner.Nice(freed) + " reclaimed ("
                    + gone.Count + " of " + pickedNow.Count + " items).");
                try { BeginInvoke((Action)delegate { CleanDone(gone, pickedNow.Count); }); }
                catch (Exception) { }
            });
            t.SetApartmentState(ApartmentState.STA);    // the shell is happier there
            t.IsBackground = true;
            t.Start();
        }

        private void CleanDone(List<CleanupItem> gone, int asked)
        {
            cleaning = false;
            btnStop.Visible = false;
            btnStop.Text = "Stop scan";
            btnStop.Enabled = true;
            foreach (CleanupItem it in gone)
            {
                picked.Remove(it.Key);
                for (int i = model.Count - 1; i >= 0; i--)
                    if (model[i].Key == it.Key) model.RemoveAt(i);

                // keep the map honest without a rescan
                if (it.Tree != null && it.Node >= 0 && !it.IsRecycleBin)
                {
                    if (it.Parts != null)
                    {
                        foreach (string part in it.Parts)
                        {
                            DiskTree pt; int pn;
                            if (scanner != null && scanner.Resolve(part, out pt, out pn))
                                pt.Deduct(pn);
                        }
                    }
                    else it.Tree.Deduct(it.Node);
                }
            }
            working = false;
            btnScan.Enabled = true;
            RebuildTree();
            progress.Text = gone.Count == asked
                ? "cleaned - check the Recycle Bin"
                : "partly cleaned - what refused is still listed";
        }

        // ---- painting: name, owner, size, share bar, class

        private void DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            e.DrawDefault = false;
            if (e.Bounds.Height <= 0) return;
            Graphics g = e.Graphics;
            int right = tree.ClientSize.Width - 4;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;

            Rectangle row = new Rectangle(e.Bounds.X, e.Bounds.Y,
                right - e.Bounds.X, e.Bounds.Height);
            if (row.Width <= 0) return;
            using (SolidBrush b = new SolidBrush(selected ? Theme.Neutral : Theme.Input))
                g.FillRectangle(b, row);

            // column layout, right to left; the review list trades the owner
            // and the bar for the full path - that is what it is FOR
            bool review = chkTicked.Checked;
            int classW = 52;
            int barW = !review && tree.ClientSize.Width > 620 ? 64 : 0;
            int sizeW = 84;
            int ownerW = review
                ? Math.Max(220, tree.ClientSize.Width - 440)
                : (tree.ClientSize.Width > 720 ? 170 : 0);
            int xClass = right - classW;
            int xBar = xClass - (barW > 0 ? barW + 8 : 0);
            int xSize = xBar - sizeW - 8;
            int xOwner = xSize - (ownerW > 0 ? ownerW + 10 : 0);
            int nameRight = (ownerW > 0 ? xOwner : xSize) - 8;

            string cat = e.Node.Tag as string;
            CleanupItem it = e.Node.Tag as CleanupItem;
            FsRef f = e.Node.Tag as FsRef;

            if (cat != null && !IsDummy(e.Node))
            {
                long total = 0;
                int count = 0;
                foreach (TreeNode child in e.Node.Nodes)
                {
                    CleanupItem ci = child.Tag as CleanupItem;
                    FsRef cf = child.Tag as FsRef;
                    if (ci != null) { total += ci.Bytes; count++; }
                    else if (cf != null) { total += cf.Bytes; count++; }
                }
                TextRenderer.DrawText(g, cat.ToUpperInvariant(), tree.Font,
                    new Rectangle(e.Bounds.X, row.Y, nameRight - e.Bounds.X, row.Height),
                    Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, CleanupScanner.Nice(total), mono,
                    new Rectangle(xSize, row.Y, sizeW, row.Height),
                    Theme.Dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                return;
            }

            string name, owner = "", size = "", cls = "";
            Color nameCol = Theme.ListFg, clsCol = Theme.Dim;
            double share = -1;

            if (it != null)
            {
                name = it.Name;
                size = CleanupScanner.Nice(it.Bytes);
                cls = it.Safe ? "safe" : "review";
                clsCol = it.Safe ? Theme.Accent : Theme.Warn;
                nameCol = Theme.Fg;
                if (review)
                    owner = it.IsRecycleBin ? "(all drives)" : it.Path;
                else if (!it.IsRecycleBin && scanner != null && ownerW > 0)
                    owner = scanner.OwnerOf(it.Path);
                if (it.Tree != null && it.Node >= 0)
                    share = Share(it.Tree, it.Node);
            }
            else if (f != null)
            {
                name = f.Tree.Name[f.Node];
                size = CleanupScanner.Nice(f.Bytes);
                if (f.Tree.IsDir(f.Node)) nameCol = Theme.Fg;
                if (scanner != null && ownerW > 0) owner = scanner.OwnerOf(f.Path);
                if (IsJunkName(name)) { cls = "junk?"; clsCol = Theme.Accent; }
                share = Share(f.Tree, f.Node);
            }
            else
            {
                // a dummy or a "... more" row
                TextRenderer.DrawText(g, e.Node.Text, tree.Font,
                    new Rectangle(e.Bounds.X, row.Y, right - e.Bounds.X, row.Height),
                    Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            TextRenderer.DrawText(g, name, tree.Font,
                new Rectangle(e.Bounds.X, row.Y, Math.Max(nameRight - e.Bounds.X, 20), row.Height),
                nameCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (ownerW > 0 && owner.Length > 0)
                TextRenderer.DrawText(g, owner, small,
                    new Rectangle(xOwner, row.Y, ownerW, row.Height),
                    Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | (review ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis)
                    | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, size, mono,
                new Rectangle(xSize, row.Y, sizeW, row.Height),
                Theme.Fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            if (barW > 0 && share >= 0)
            {
                Rectangle track = new Rectangle(xBar, row.Y + row.Height / 2 - 3, barW, 6);
                using (SolidBrush b = new SolidBrush(Theme.Track)) g.FillRectangle(b, track);
                int w = (int)(track.Width * Math.Min(share, 1.0));
                if (w > 0)
                    using (SolidBrush b = new SolidBrush(Theme.GaugeOk))
                        g.FillRectangle(b, new Rectangle(track.X, track.Y, w, track.Height));
            }

            if (cls.Length > 0)
                TextRenderer.DrawText(g, cls, small,
                    new Rectangle(xClass, row.Y, classW, row.Height),
                    clsCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // How much of its parent this node is - the little bar. The root is
        // measured against the drive's own capacity.
        private static double Share(DiskTree t, int n)
        {
            try
            {
                if (n == t.RootNode)
                {
                    System.IO.DriveInfo d = new System.IO.DriveInfo(t.Root.Substring(0, 1));
                    return d.TotalSize > 0 ? (double)t.Bytes[n] / d.TotalSize : 0;
                }
                long parent = t.Bytes[t.Parent[n]];
                return parent > 0 ? (double)t.Bytes[n] / parent : 0;
            }
            catch (Exception) { return 0; }
        }

        // ---- the map view

        // Swaps the views and seeds the map the first time it is asked for.
        // The filter row is disabled while the map is up rather than left
        // looking live: it filters the tree's findings, and the map draws the
        // drive, so nothing it did would show.
        // 0 = the findings tree, 1 = the drive as a columned tree, 2 = the
        // drive as a treemap.
        private void SetView(int mode)
        {
            if (mode < 0 || mode > 2) mode = 0;
            syncingView = true;
            try { cmbView.SelectedIndex = mode; }
            finally { syncingView = false; }

            tree.Visible = mode == 0;
            wiz.Visible = mode == 1;
            mapPanel.Visible = mode == 2;

            // The filter row filters the scan's FINDINGS. The other two views
            // draw the drive, so nothing it did would show - disabled rather
            // than left looking live.
            bool findings = mode == 0;
            cmbSize.Enabled = findings;
            cmbClass.Enabled = findings;
            txtName.Enabled = findings;
            chkTicked.Enabled = findings;

            if (mode == 1) { SeedWiz(); wiz.Focus(); }
            else if (mode == 2) { SeedMap(); map.Focus(); }
            UpdateCrumb();
        }

        // Both drive views open on the drive the user is already looking at.
        private DiskTree PickTree(out int node)
        {
            node = -1;
            FsRef f = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as FsRef;
            if (f != null && f.Tree != null)
            {
                node = f.Node;
                try { if (!f.Tree.IsDir(node)) node = f.Tree.Parent[node]; }
                catch (Exception) { }
                if (node >= 0) return f.Tree;
            }
            if (scanner != null && scanner.Trees.Count > 0)
            {
                DiskTree t = scanner.Trees[0];
                node = t.RootNode;
                return t;
            }
            return null;
        }

        // The columned tree always opens at the drive root: it expands, so
        // there is nothing to gain by starting mid-drive, and starting there
        // would hide the branch you were trying to compare against.
        private void SeedWiz()
        {
            int node;
            DiskTree t = PickTree(out node);
            if (t != null) wiz.Show(t, t.RootNode);
        }

        private void DescribeWiz()
        {
            FsRef f = wiz.Selected;
            if (f == null) return;
            progress.Text = CleanupScanner.Nice(f.Bytes) + "   " + f.Path;
        }

        // Start where the user already is: if a folder in the tree is selected,
        // open the map on that folder, so the switch keeps their place instead
        // of throwing them back to the drive root.
        private void SeedMap()
        {
            int node;
            DiskTree t = PickTree(out node);
            if (t != null) map.Show(t, node, false);
            else map.Show(null, -1, false);
        }

        private void UpdateCrumb()
        {
            if (!mapPanel.Visible) return;
            string path = map.RootPath;
            crumb.Text = path.Length == 0
                ? "nothing scanned yet - press Scan"
                : path + "     " + CleanupScanner.Nice(map.RootBytes);
            btnUp.Enabled = map.CanGoUp;
        }

        private void DescribeCell(MapCell c)
        {
            if (c == null) return;
            string path;
            try { path = c.Tree.PathOf(c.Node); }
            catch (Exception) { return; }
            long bytes = 0;
            try { bytes = c.Tree.Bytes[c.Node]; } catch (Exception) { }
            mapInfo.Text = CleanupScanner.Nice(bytes) + "   "
                + (c.IsDir ? "folder" : "file") + "   " + path;
        }

        // ---- the right-click verdicts

        // The one place the two views meet. Everything downstream - the menu,
        // Open, Copy, Protect, Clean just this one - asks this and never has to
        // know which view the user is looking at.
        private object SelectedTag()
        {
            if (wiz.Visible) return wiz.Selected;
            if (mapPanel.Visible)
            {
                MapCell c = map.Chosen;
                return c == null ? null : new FsRef(c.Tree, c.Node);
            }
            return tree.SelectedNode == null ? null : tree.SelectedNode.Tag;
        }

        private string SelectedPath()
        {
            CleanupItem it = SelectedTag() as CleanupItem;
            if (it != null) return it.IsRecycleBin ? null : it.Path;
            FsRef f = SelectedTag() as FsRef;
            return f != null ? f.Path : null;
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CleanupItem it = SelectedTag() as CleanupItem;
            FsRef f = SelectedTag() as FsRef;
            if (it == null && f == null) { e.Cancel = true; return; }

            long bytes = it != null ? it.Bytes : f.Bytes;
            bool bin = it != null && it.IsRecycleBin;
            bool driveRoot = f != null && f.Node == f.Tree.RootNode;
            miOpen.Enabled = true;
            miCleanOne.Enabled = !working && !driveRoot;
            miCleanOne.Text = bin
                ? "Empty the Recycle Bin (permanent)"
                : "Clean just this one  (" + CleanupScanner.Nice(bytes) + ")";
            miProtect.Enabled = !bin && !driveRoot;
            miCopy.Enabled = !bin;
        }

        private void OpenSelected()
        {
            CleanupItem it = SelectedTag() as CleanupItem;
            try
            {
                if (it != null && it.IsRecycleBin)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
                        { UseShellExecute = true });
                    return;
                }
                string path = SelectedPath();
                if (path == null) return;
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                    { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        private void CleanSelectedOnly()
        {
            CleanupItem it = SelectedTag() as CleanupItem;
            FsRef f = SelectedTag() as FsRef;
            if (it == null && f == null) return;
            if (it == null)
            {
                if (f.Node == f.Tree.RootNode) return;
                string why = ForbidCheck(null, f, f.Path, tree.SelectedNode);
                if (why != null) { progress.Text = why; log("   . " + why); return; }
                it = new CleanupItem();
                it.Name = f.Tree.Name[f.Node];
                it.Path = f.Path;
                it.Category = DiskMapCat;
                it.Bytes = f.Bytes;
                it.Tree = f.Tree;
                it.Node = f.Node;
            }
            List<CleanupItem> one = new List<CleanupItem>();
            one.Add(it);
            Clean(one);
        }

        // The same recipe the task manager uses for "Never touch": write the
        // decision into the ini, reload the running config, drop the row.
        private void ProtectSelected()
        {
            string path = SelectedPath();
            if (path == null) return;

            if (!Config.Append("cleanup.protect", path))
            {
                log("   ! could not write " + path + " into [cleanup.protect].");
                return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                log(path + " added to [cleanup.protect] - cleanup will never touch it.");
            }
            catch (Exception ex) { log("   ! could not reload the config: " + ex.Message); }

            string key = path.ToLowerInvariant();
            picked.Remove(key);
            for (int i = model.Count - 1; i >= 0; i--)
                if (model[i].Key == key) model.RemoveAt(i);
            RebuildTree();
        }

        private void CopySelected()
        {
            string path = SelectedPath();
            if (path == null) return;
            try { Clipboard.SetText(path); }
            catch (Exception) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (scanner != null) scanner.Cancel();
            base.OnFormClosed(e);
        }
    }

    // ----------------------------------------------------------------- debloat

    // The review table behind "Debloat". Scan fills it on a worker thread, you
    // tick what goes, Remove uninstalls the ticked apps - and unlike cleanup
    // this is NOT the Recycle Bin: gone means reinstall-from-the-Store gone.
    // The dialog says so, the known-junk table pre-ticks only the shameless.
    internal sealed class DebloatForm : Form
    {
        private readonly Config cfg;
        private readonly Action<string> log;
        private readonly BufferedListView list;
        private readonly ColumnHeader colName, colCat, colPkg, colSize, colClass;
        private readonly Button btnScan, btnStop, btnRemove;
        private readonly CheckBox chkDeprovision;
        private readonly Label progress;
        private readonly System.Windows.Forms.Timer timer;
        private readonly ToolStripMenuItem miRemoveOne, miProtect, miCopy;

        // The worker drops findings here; a 200 ms timer drains them onto the
        // UI thread in batches - the same plumbing the cleanup window uses.
        private readonly List<DebloatItem> arrived = new List<DebloatItem>();
        private readonly object gate = new object();
        private string phase = "";
        private DebloatScanner scanner;
        private bool working;
        private bool filling;

        public DebloatForm(Config c, Action<string> logger)
        {
            cfg = c;
            log = logger;

            Theme.Form(this);
            Text = "IDLE MASTER - debloat";
            Size = new Size(760, 656);
            MinimumSize = new Size(620, 456);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("DEBLOAT");
            cap.SetBounds(16, 12, 180, 18);
            Controls.Add(cap);

            Label hint = Theme.Hint("scan, tick what goes - Remove UNINSTALLS it (the Store can bring it back)");
            hint.Font = Theme.Small();
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.SetBounds(200, 12, 528, 18);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(hint);

            list = new BufferedListView();
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.ShowItemToolTips = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = Theme.Input;
            list.ForeColor = Theme.Fg;
            list.OwnerDraw = true;
            list.DrawColumnHeader += DrawHeader;
            list.DrawItem += delegate(object s, DrawListViewItemEventArgs a) { a.DrawDefault = true; };
            list.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs a) { a.DrawDefault = true; };
            colName = list.Columns.Add("App", 176);
            colCat = list.Columns.Add("Category", 118);
            colPkg = list.Columns.Add("Package", 250);
            colSize = list.Columns.Add("Size", 88, HorizontalAlignment.Right);
            colClass = list.Columns.Add("Class", 64);
            list.SetBounds(16, 36, 712, 488);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.Resize += delegate { SizeColumns(); };
            list.ItemChecked += delegate { if (!filling) UpdateRemoveButton(); };
            Controls.Add(list);
            SizeColumns();

            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.Menu(menu);
            miRemoveOne = new ToolStripMenuItem("Remove just this one");
            miRemoveOne.Click += delegate { RemoveSelectedOnly(); };
            miProtect = new ToolStripMenuItem("Never suggest this app (protect)");
            miProtect.Click += delegate { ProtectSelected(); };
            miCopy = new ToolStripMenuItem("Copy package name");
            miCopy.Click += delegate { CopySelected(); };
            menu.Items.Add(miRemoveOne);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miProtect);
            menu.Items.Add(miCopy);
            menu.Opening += MenuOpening;
            list.ContextMenuStrip = menu;

            btnScan = Theme.Action("Scan");
            btnScan.SetBounds(16, 536, 100, 30);
            btnScan.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnScan.Click += delegate { StartScan(); };
            Controls.Add(btnScan);

            btnStop = Theme.Quiet("Stop scan");
            btnStop.SetBounds(124, 536, 100, 30);
            btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnStop.Visible = false;
            btnStop.Click += delegate { if (scanner != null) scanner.Cancel(); };
            Controls.Add(btnStop);

            progress = Theme.Hint("no scan yet");
            progress.Font = Theme.Small();
            progress.SetBounds(232, 541, 220, 20);
            progress.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(progress);

            btnRemove = Theme.Dangerous("Remove checked");
            btnRemove.SetBounds(460, 536, 268, 30);
            btnRemove.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnRemove.Enabled = false;
            btnRemove.Click += delegate { Remove(Checked()); };
            Controls.Add(btnRemove);

            // Removing only the installed copy leaves the machine copy behind,
            // and a feature update or a new account quietly restores the app.
            // On by default because "stays gone" is what debloat means.
            chkDeprovision = new CheckBox();
            chkDeprovision.Text = "Also drop the machine copy, so new accounts and Windows updates do not bring it back";
            chkDeprovision.Checked = true;
            chkDeprovision.ForeColor = Theme.Fg;
            chkDeprovision.FlatStyle = FlatStyle.Flat;
            chkDeprovision.SetBounds(16, 574, 712, 22);
            chkDeprovision.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(chkDeprovision);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate { Drain(); };
            timer.Start();
        }

        private void SizeColumns()
        {
            int rest = colName.Width + colCat.Width + colSize.Width + colClass.Width;
            int w = list.ClientSize.Width - rest - 4;
            if (w > 80) colPkg.Width = w;
        }

        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                e.Graphics.FillRectangle(b, e.Bounds);
            TextFormatFlags align = e.ColumnIndex == 3
                ? TextFormatFlags.Right : TextFormatFlags.Left;
            Rectangle r = e.Bounds;
            r.Inflate(-6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, r, Theme.Dim,
                align | TextFormatFlags.VerticalCenter);
        }

        // ---- scanning

        public void StartScan()
        {
            if (working) return;
            working = true;
            scanner = new DebloatScanner(cfg);

            filling = true;
            list.Items.Clear();
            filling = false;
            lock (gate) { arrived.Clear(); phase = "starting..."; }

            btnScan.Enabled = false;
            btnStop.Visible = true;
            UpdateRemoveButton();
            log("-- debloat scan");

            DebloatScanner mine = scanner;
            Thread t = new Thread(delegate()
            {
                List<DebloatItem> all = null;
                try
                {
                    all = mine.Scan(
                        delegate(string where) { lock (gate) { phase = where; } },
                        delegate(DebloatItem it) { lock (gate) { arrived.Add(it); } });
                }
                catch (Exception ex) { log("   ! debloat scan failed: " + ex.Message); }
                try { BeginInvoke((Action)delegate { ScanDone(all); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Drain()
        {
            List<DebloatItem> take = null;
            string where;
            lock (gate)
            {
                where = phase;
                if (arrived.Count > 0)
                {
                    take = new List<DebloatItem>(arrived);
                    arrived.Clear();
                }
            }
            if (working) progress.Text = where;
            if (take == null) return;

            filling = true;
            list.BeginUpdate();
            try { foreach (DebloatItem it in take) AddRow(it); }
            finally { list.EndUpdate(); filling = false; }
            UpdateRemoveButton();
        }

        private void ScanDone(List<DebloatItem> all)
        {
            Drain();
            working = false;
            btnScan.Enabled = true;
            btnStop.Visible = false;

            int bloat = 0;
            foreach (ListViewItem row in list.Items)
                if (((DebloatItem)row.Tag).Safe) bloat++;
            bool cancelled = scanner != null && scanner.Cancelled;
            progress.Text = (cancelled ? "cancelled - " : "")
                + list.Items.Count + " removable apps, " + bloat + " known bloat";
            log("   = scan " + (cancelled ? "cancelled" : "finished") + ": "
                + list.Items.Count + " removable apps, " + bloat + " known bloat pre-ticked.");
            UpdateRemoveButton();
        }

        private void AddRow(DebloatItem it)
        {
            if (list.Items.ContainsKey(it.Key)) return;

            ListViewItem row = new ListViewItem(it.Name);
            row.Name = it.Key;
            row.Tag = it;
            row.UseItemStyleForSubItems = false;
            row.SubItems.Add(it.Category);
            row.SubItems.Add(it.Package);
            row.SubItems.Add(it.Bytes > 0 ? CleanupScanner.Nice(it.Bytes) : "?");
            row.SubItems.Add(it.Safe ? "bloat" : "review");
            row.SubItems[1].ForeColor = Theme.Dim;
            row.SubItems[2].ForeColor = Theme.Dim;
            row.SubItems[4].ForeColor = it.Safe ? Theme.Accent : Theme.Warn;
            row.ToolTipText = (it.Note.Length > 0 ? it.Note + "\n" : "")
                + (it.Provisioned ? "provisioned - Windows re-installs it for every new account\n" : "")
                + it.Where;
            list.Items.Insert(InsertAt(it), row);
            row.Checked = it.Safe;      // known bloat arrives ticked, the rest is your call
                                        // (set after the insert - a detached row forgets it)
        }

        private int InsertAt(DebloatItem it)
        {
            int mine = DebloatScanner.Rank(it.Category);
            for (int i = 0; i < list.Items.Count; i++)
            {
                DebloatItem other = (DebloatItem)list.Items[i].Tag;
                int r = DebloatScanner.Rank(other.Category);
                if (r > mine) return i;
                if (r == mine && other.Bytes < it.Bytes) return i;
            }
            return list.Items.Count;
        }

        // ---- removing

        private List<DebloatItem> Checked()
        {
            List<DebloatItem> picked = new List<DebloatItem>();
            foreach (ListViewItem row in list.Items)
                if (row.Checked) picked.Add((DebloatItem)row.Tag);
            return picked;
        }

        private void UpdateRemoveButton()
        {
            int n = 0;
            foreach (ListViewItem row in list.Items)
                if (row.Checked) n++;
            btnRemove.Enabled = n > 0 && !working;
            btnRemove.Text = n == 0
                ? "Remove checked"
                : "Remove checked  (" + n + " app" + (n == 1 ? "" : "s") + ")";
        }

        private void Remove(List<DebloatItem> picked)
        {
            if (working || picked.Count == 0) return;

            bool deprovision = chkDeprovision.Checked;
            string msg = "Uninstall " + picked.Count + " app" + (picked.Count == 1 ? "" : "s")
                + " for every account on this machine?\n\nThis is NOT the Recycle Bin:"
                + " an app removed here is gone until you reinstall it from the Microsoft Store."
                + (deprovision
                    ? "\n\nThe machine copy goes too, so new accounts and Windows updates"
                      + " will not bring these back."
                    : "");
            if (MessageBox.Show(this, msg, "Debloat", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            working = true;
            btnScan.Enabled = false;
            UpdateRemoveButton();
            progress.Text = "removing...";
            log("-- debloat");

            DebloatScanner guard = scanner != null ? scanner : new DebloatScanner(cfg);
            Thread t = new Thread(delegate()
            {
                List<string> gone = new List<string>();
                foreach (DebloatItem it in picked)
                {
                    if (!DebloatActions.Remove(it, deprovision, guard, log)) continue;
                    gone.Add(it.Key);
                }
                log("   = " + gone.Count + " of " + picked.Count + " apps uninstalled."
                    + (gone.Count > 0 ? " The Microsoft Store can reinstall any of them." : ""));
                try { BeginInvoke((Action)delegate { RemoveDone(gone, picked.Count); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RemoveDone(List<string> gone, int asked)
        {
            filling = true;
            foreach (string key in gone)
            {
                int at = list.Items.IndexOfKey(key);
                if (at >= 0) list.Items.RemoveAt(at);
            }
            filling = false;
            working = false;
            btnScan.Enabled = true;
            progress.Text = gone.Count == asked
                ? "removed - the Store can bring any of them back"
                : "partly removed - what refused is still listed";
            UpdateRemoveButton();
        }

        // ---- the right-click verdicts

        private DebloatItem Selected()
        {
            if (list.SelectedItems.Count == 0) return null;
            return (DebloatItem)list.SelectedItems[0].Tag;
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            DebloatItem it = Selected();
            if (it == null) { e.Cancel = true; return; }
            miRemoveOne.Enabled = !working;
            miRemoveOne.Text = "Remove just this one  (" + it.Name + ")";
        }

        private void RemoveSelectedOnly()
        {
            DebloatItem it = Selected();
            if (it == null) return;
            List<DebloatItem> one = new List<DebloatItem>();
            one.Add(it);
            Remove(one);
        }

        // The same recipe cleanup uses for "Never touch": write the decision
        // into the ini, reload the running config, drop the row.
        private void ProtectSelected()
        {
            DebloatItem it = Selected();
            if (it == null) return;

            if (!Config.Append("debloat.protect", it.Package))
            {
                log("   ! could not write " + it.Package + " into [debloat.protect].");
                return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                log(it.Package + " added to [debloat.protect] - debloat will never suggest it.");
            }
            catch (Exception ex) { log("   ! could not reload the config: " + ex.Message); }

            int at = list.Items.IndexOfKey(it.Key);
            if (at >= 0) list.Items.RemoveAt(at);
            UpdateRemoveButton();
        }

        private void CopySelected()
        {
            DebloatItem it = Selected();
            if (it == null) return;
            try { Clipboard.SetText(it.Package); }
            catch (Exception) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (scanner != null) scanner.Cancel();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ sentry

    // What the sentry hunts and how often, in one place: the active mode's kill
    // lists as checklists (add from what is running, type, remove, untick to
    // comment out), the timers, and the two "boost again" knobs. Saving
    // re-reads the config; a running sentry uses the new lists on its next sweep.
    internal sealed class SentryForm : Form
    {
        private readonly IniFile ini = new IniFile();
        private readonly List<ListPane> panes = new List<ListPane>();
        private readonly Dictionary<string, NumericUpDown> numbers = new Dictionary<string, NumericUpDown>();
        private readonly ComboBox timeoutAction;
        private readonly Label status;
        private readonly Func<Sentry> live;
        private readonly System.Windows.Forms.Timer timer;

        public bool Saved;

        public SentryForm(Func<Sentry> liveSentry, string armedMode)
        {
            live = liveSentry;

            Theme.Form(this);
            Text = "IDLE MASTER - sentry";
            Size = new Size(820, 660);
            MinimumSize = new Size(720, 560);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Sentry now = live();
            string mode = now != null && now.Alive ? now.Mode : armedMode;
            if (mode != "idle") mode = "boost";

            Label cap = Theme.Caption("SENTRY  -  " + mode.ToUpperInvariant());
            cap.SetBounds(16, 12, 300, 18);
            Controls.Add(cap);

            status = Theme.Hint("");
            status.Font = Theme.Small();
            status.TextAlign = ContentAlignment.MiddleRight;
            status.SetBounds(320, 12, 468, 18);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(status);

            Label hint = Theme.Hint(mode == "idle"
                ? "Absolute idle enforces these ON TOP of the boost lists (Settings > Advanced > Boost now)."
                : "Untick = commented out, not deleted. 'Never touch' still wins over anything here.");
            hint.Font = Theme.Small();
            hint.SetBounds(16, 32, 780, 16);
            Controls.Add(hint);

            ListPane left = new ListPane(ini, mode + ".kill",
                "Processes it hunts  [" + mode + ".kill]", false);
            left.SetBounds(12, 52, 388, 340);
            left.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(left);
            panes.Add(left);

            ListPane right = new ListPane(ini, mode + ".services",
                "Services it re-stops  [" + mode + ".services]", true);
            right.SetBounds(408, 52, 388, 340);
            right.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(right);
            panes.Add(right);

            // ---- the timers, two columns
            int y0 = 660 - 250;
            Label t1 = Theme.Caption("How often");
            t1.SetBounds(16, y0, 200, 18);
            t1.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(t1);
            int y = y0 + 24;
            y = Num("SentrySeconds", 16, y);
            y = Num("SentryServiceMinutes", 16, y);
            y = Num("SentryTrimMinutes", 16, y);
            y = Num("SentryBackoffMinutes", 16, y);

            Label t2 = Theme.Caption("Boost again, and asking");
            t2.SetBounds(412, y0, 300, 18);
            t2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(t2);
            y = y0 + 24;
            y = Num("SentryFullPassMinutes", 412, y);
            y = Num("BoostWhenFreeBelowMb", 412, y);
            y = Num("AskAboveMb", 412, y);
            y = Num("AskTimeoutSeconds", 412, y);

            Label tl = new Label();
            tl.Text = "no answer means";
            tl.SetBounds(412, y + 3, 200, 20);
            tl.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(tl);
            timeoutAction = SettingSpec.Choice(ini.GetSetting("AskTimeoutAction"));
            timeoutAction.SetBounds(640, y, 150, 22);
            timeoutAction.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(timeoutAction);

            Button save = Theme.Action("Save");
            save.SetBounds(796 - 208, 660 - 76, 100, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Close");
            cancel.SetBounds(796 - 100, 660 - 76, 100, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { Refresh_(); };
            timer.Start();
            Refresh_();
        }

        private int Num(string key, int x, int y)
        {
            string[] spec = SettingSpec.Number(key);
            Label l = new Label();
            l.Text = Short(spec[1]);
            l.SetBounds(x, y + 3, 300, 20);
            l.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(l);

            NumericUpDown u = new NumericUpDown();
            u.Minimum = decimal.Parse(spec[2], CultureInfo.InvariantCulture);
            u.Maximum = decimal.Parse(spec[3], CultureInfo.InvariantCulture);
            u.Value = SettingSpec.Clamp(u, ini.GetSetting(key), spec[4]);
            u.SetBounds(x + 300, y, 80, 22);
            u.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Theme.Input_(u);
            Controls.Add(u);
            numbers[key] = u;
            return y + 28;
        }

        // The advanced window has room for the long labels; this one does not.
        private static string Short(string label)
        {
            switch (label)
            {
                case "Sweep for new junk every (seconds)": return "sweep processes every (s)";
                case "Re-stop restarted services every (minutes)": return "re-stop services every (min)";
                case "Re-trim RAM every (minutes)": return "re-trim RAM every (min)";
                case "...and leave it alone for (minutes)": return "'Keep it' / backoff lasts (min)";
                case "Ask about unlisted newcomers bigger than (MB, 0 = off)": return "also ask about anything new over (MB, 0 = off)";
                case "Dialog answers itself after (seconds)": return "dialog answers itself after (s)";
            }
            if (label.StartsWith("Repeat a whole boost pass")) return "whole boost pass every (min, 0 = off)";
            if (label.StartsWith("...and do one now")) return "...and one now when free RAM < (MB, 0 = off)";
            return label;
        }

        private void Refresh_()
        {
            Sentry s = live();
            if (s != null && s.Alive)
            {
                status.Text = "on watch since " + s.Since.ToString("HH:mm") + "  -  " + s.Reaped
                    + " reaped, " + Engine.Size(s.Reclaimed) + " held off"
                    + (s.Restopped > 0 ? ", " + s.Restopped + " services re-stopped" : "")
                    + (s.FullPasses > 0 ? ", " + s.FullPasses + " full passes" : "");
                status.ForeColor = Theme.Accent;
            }
            else
            {
                status.Text = "not on watch right now - these lists apply the moment it is";
                status.ForeColor = Theme.Dim;
            }
        }

        private void Persist()
        {
            try
            {
                foreach (KeyValuePair<string, NumericUpDown> kv in numbers)
                    ini.SetSetting(kv.Key, ((int)kv.Value.Value).ToString(CultureInfo.InvariantCulture));
                ini.SetSetting("AskTimeoutAction", SettingSpec.ChoiceValue(timeoutAction));
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------ net guard

    // The network guard's own page: what it sees right now, a check on demand,
    // its switches, and the Wi-Fi networks it may reconnect to. Same shape as
    // the sentry's page, saved straight to the ini.
    internal sealed class NetGuardForm : Form
    {
        private readonly IniFile ini = new IniFile();
        private readonly Func<NetGuard> live;
        private readonly Action checkNow;
        private readonly bool wanted;
        private readonly Label status;
        private readonly TextBox report;
        private readonly Dictionary<string, CheckBox> flags = new Dictionary<string, CheckBox>();
        private readonly NumericUpDown seconds;
        private readonly ListPane wifi;
        private readonly System.Windows.Forms.Timer timer;

        public bool Saved;

        public NetGuardForm(Func<NetGuard> liveGuard, Action checkNowAction, bool guardWanted)
        {
            live = liveGuard; checkNow = checkNowAction; wanted = guardWanted;

            Theme.Form(this);
            Text = "IDLE MASTER - network guard";
            Size = new Size(820, 660);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("NETWORK GUARD");
            cap.SetBounds(16, 12, 300, 18);
            Controls.Add(cap);

            status = Theme.Hint("");
            status.Font = Theme.Small();
            status.TextAlign = ContentAlignment.MiddleRight;
            status.SetBounds(320, 12, 468, 18);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(status);

            Label hint = Theme.Hint("Link, internet, Tailscale, Sunshine - measured on a timer; the first thing wrong "
                + "gets repaired, then measured again. Quiet while all is well.");
            hint.Font = Theme.Small();
            hint.SetBounds(16, 32, 780, 16);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(hint);

            // What it last saw: the same four lines --network prints.
            report = new TextBox();
            report.Multiline = true;
            report.ReadOnly = true;
            report.BackColor = Theme.LogBg;
            report.ForeColor = Theme.LogFg;
            report.Font = Theme.Mono();
            report.BorderStyle = BorderStyle.FixedSingle;
            report.SetBounds(12, 54, 784, 84);
            report.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(report);

            // Left: the Wi-Fi it may reconnect to.
            wifi = new ListPane(ini, "network.wifi", "Wi-Fi it may reconnect to, best first  [network.wifi]", "wifi");
            wifi.SetBounds(12, 150, 388, 372);
            wifi.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(wifi);

            // Right: the switches.
            int x = 412, y = 150;
            Label t = Theme.Caption("Switches");
            t.SetBounds(x + 4, y + 6, 300, 18);
            Controls.Add(t);
            y += 30;
            y = Flag("NetworkGuard", "Guard the connection",
                "The whole feature. Off = no watch, and no check after a run either.", x, y);
            y = Flag("NetworkGuardWifi", "Reconnect Wi-Fi on its own",
                "The list on the left first, then every saved network in Windows' order.", x, y);
            y = Flag("NetworkGuardKeepWifiAwake", "Keep the Wi-Fi adapter awake",
                "Stop Windows powering it down to save energy. Best effort.", x, y);
            y = Flag("NetworkGuardScan", "Scan for what is in range",
                "Windows calls that location and asks you once. Off = it never asks.", x, y);

            y += 6;
            string[] spec = SettingSpec.Number("NetworkGuardSeconds");
            Label sl = new Label();
            sl.Text = "check every (seconds)";
            sl.SetBounds(x + 4, y + 3, 200, 20);
            Controls.Add(sl);
            seconds = new NumericUpDown();
            seconds.Minimum = decimal.Parse(spec[2], CultureInfo.InvariantCulture);
            seconds.Maximum = decimal.Parse(spec[3], CultureInfo.InvariantCulture);
            seconds.Value = SettingSpec.Clamp(seconds, ini.GetSetting("NetworkGuardSeconds"), spec[4]);
            seconds.SetBounds(x + 214, y, 80, 22);
            Theme.Input_(seconds);
            Controls.Add(seconds);
            y += 40;

            Button check = Theme.Quiet("Check now");
            check.SetBounds(x + 4, y, 120, 28);
            check.Click += delegate { checkNow(); };
            Controls.Add(check);

            Label ch = Theme.Hint("Looks now; the whole picture goes to the log.");
            ch.Font = Theme.Small();
            ch.SetBounds(x + 132, y + 6, 260, 16);
            Controls.Add(ch);

            Button save = Theme.Action("Save");
            save.SetBounds(796 - 208, 660 - 76, 100, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Close");
            cancel.SetBounds(796 - 100, 660 - 76, 100, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { Refresh_(); };
            timer.Start();
            Refresh_();
        }

        private int Flag(string key, string label, string hint, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label;
            c.Checked = SettingSpec.Truthy(ini.GetSetting(key), SettingSpec.FlagDefault(key));
            c.SetBounds(x + 4, y, 380, 22);
            c.ForeColor = Theme.Fg;
            Controls.Add(c);
            flags[key] = c;

            Label h = Theme.Hint(hint);
            h.Font = Theme.Small();
            h.SetBounds(x + 22, y + 22, 370, 16);
            Controls.Add(h);
            return y + 44;
        }

        private void Refresh_()
        {
            NetGuard g = live();
            NetReport r = g != null ? g.Last : null;
            if (g != null && g.Alive)
            {
                string s = "on watch since " + g.Since.ToString("HH:mm") + "  -  " + g.Checks + " check" + (g.Checks == 1 ? "" : "s")
                    + ", " + g.FixCount + " fix" + (g.FixCount == 1 ? "" : "es");
                if (r == null) { s += "  -  first look in a moment"; status.ForeColor = Theme.Accent; }
                else if (r.Healthy) { s += "  -  all good at " + g.LastCheck.ToString("HH:mm:ss"); status.ForeColor = Theme.Accent; }
                else
                {
                    s += "  -  " + (g.Busy ? "FIXING" : "TROUBLE") + (g.Attempt > 1 ? " (try " + g.Attempt + ")" : "")
                        + " at " + g.LastCheck.ToString("HH:mm:ss");
                    status.ForeColor = Theme.Warn;
                }
                status.Text = s;
            }
            else if (g != null && g.Refused.Length > 0)
            {
                status.Text = g.Refused;
                status.ForeColor = Theme.Dim;
            }
            else if (g != null && r != null)
            {
                status.Text = "not on watch  -  checked by hand at " + g.LastCheck.ToString("HH:mm:ss")
                    + (r.Healthy ? ", all good" : ", TROUBLE");
                status.ForeColor = r.Healthy ? Theme.Dim : Theme.Warn;
            }
            else
            {
                status.Text = wanted ? "starting..." : "not on watch  -  nothing watches the connection";
                status.ForeColor = Theme.Dim;
            }

            string text;
            if (r == null)
                text = "(nothing measured yet - Check now, or wait for the first look)";
            else
            {
                List<string> lines = r.Lines();
                foreach (string f in r.Fixes) lines.Add("* " + f);
                text = string.Join(Environment.NewLine, lines.ToArray());
            }
            if (report.Text != text) report.Text = text;
        }

        private void Persist()
        {
            try
            {
                foreach (KeyValuePair<string, CheckBox> kv in flags)
                    ini.SetSetting(kv.Key, kv.Value.Checked ? "1" : "0");
                ini.SetSetting("NetworkGuardSeconds", ((int)seconds.Value).ToString(CultureInfo.InvariantCulture));
                wifi.Save(ini);
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------- remote desktop setup

    // The remote-desktop page: the network guard's live picture on top, and
    // under it the apps that must stay connected - the common remote stacks
    // are offered first in the picker, but literally any app can be chosen.
    // Calibrate snapshots "connected" as it looks right now (exe, service,
    // listening ports per app); from then on the guard reconnects whatever
    // drifts from that picture.
    internal sealed class RemoteForm : Form
    {
        private readonly IniFile ini = new IniFile();
        private readonly Func<NetGuard> live;
        private readonly Action checkNow;
        private readonly Action<List<string>> calibrate;
        private readonly Label status;
        private readonly TextBox report;
        private readonly ListPane apps;
        private readonly System.Windows.Forms.Timer timer;

        public bool Saved;

        public RemoteForm(Func<NetGuard> liveGuard, Action checkNowAction, Action<List<string>> calibrateAction)
        {
            live = liveGuard; checkNow = checkNowAction; calibrate = calibrateAction;

            Theme.Form(this);
            Text = "IDLE MASTER - remote desktop setup";
            Size = new Size(820, 660);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("REMOTE DESKTOP SETUP");
            cap.SetBounds(16, 12, 300, 18);
            Controls.Add(cap);

            status = Theme.Hint("");
            status.Font = Theme.Small();
            status.TextAlign = ContentAlignment.MiddleRight;
            status.SetBounds(320, 12, 468, 18);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(status);

            Label hint = Theme.Hint("The network guard keeps the link, Tailscale and Sunshine alive on its own. "
                + "This page adds YOUR apps to that watch.");
            hint.Font = Theme.Small();
            hint.SetBounds(16, 32, 780, 16);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(hint);

            // The live picture: the guard's four lines, then one line per app.
            report = new TextBox();
            report.Multiline = true;
            report.ReadOnly = true;
            report.ScrollBars = ScrollBars.Vertical;
            Theme.Scrollbars(report);
            report.BackColor = Theme.LogBg;
            report.ForeColor = Theme.LogFg;
            report.Font = Theme.Mono();
            report.BorderStyle = BorderStyle.FixedSingle;
            report.SetBounds(12, 54, 784, 148);
            report.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(report);

            apps = new ListPane(ini, "remote.apps",
                "Apps that must stay connected  [remote.apps]", "remote");
            apps.SetBounds(12, 214, 388, 366);
            apps.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(apps);

            int x = 412, y = 220;
            Label t = Theme.Caption("Calibrated reconnects");
            t.SetBounds(x + 4, y, 300, 18);
            Controls.Add(t);
            y += 24;

            Label how = Theme.Hint("Get everything connected the way you like it - stream running, apps "
                + "signed in - then hit Calibrate. The guard remembers each app's exe, its service and "
                + "the ports it is listening on. From then on, every check also checks this list: an app "
                + "that is gone, or no longer on its calibrated ports, is restarted or relaunched until "
                + "the picture matches again.");
            how.Font = Theme.Small();
            how.SetBounds(x + 4, y, 380, 96);
            Controls.Add(how);
            y += 104;

            Button cal = Theme.Action("Calibrate now");
            cal.SetBounds(x + 4, y, 130, 30);
            cal.Click += delegate { Calibrate(); };
            Controls.Add(cal);

            Button check = Theme.Quiet("Check now");
            check.SetBounds(x + 144, y, 110, 30);
            check.Click += delegate { checkNow(); };
            Controls.Add(check);
            y += 38;

            Label ch = Theme.Hint("Calibrate saves the list first, so what you see ticked is what gets "
                + "calibrated. Apps that are not running are skipped - start them, then calibrate again.");
            ch.Font = Theme.Small();
            ch.SetBounds(x + 4, y, 380, 44);
            Controls.Add(ch);
            y += 52;

            Label nb = Theme.Hint("Timing, Wi-Fi and the guard's own switches live on its page - the "
                + "'Network guard' button in the main window.");
            nb.Font = Theme.Small();
            nb.SetBounds(x + 4, y, 380, 32);
            Controls.Add(nb);

            Button save = Theme.Action("Save");
            save.SetBounds(796 - 208, 660 - 76, 100, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = Theme.Quiet("Close");
            cancel.SetBounds(796 - 100, 660 - 76, 100, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { Refresh_(); };
            timer.Start();
            Refresh_();
        }

        // Calibrate against what is ticked on screen: persist the list, then
        // let the owner snapshot it. Saved is set so the main window reloads.
        private void Calibrate()
        {
            try
            {
                apps.Save(ini);
                ini.Save();
                Saved = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the list first:\n\n" + ex.Message,
                    "Idle Master", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            calibrate(apps.CheckedNames());
        }

        private void Refresh_()
        {
            NetGuard g = live();
            NetReport r = g != null ? g.Last : null;

            if (g != null && g.Alive)
            {
                status.Text = "guard on watch since " + g.Since.ToString("HH:mm") + "  -  "
                    + g.Checks + " check" + (g.Checks == 1 ? "" : "s") + ", " + g.FixCount + " fix" + (g.FixCount == 1 ? "" : "es")
                    + (g.AppsOk ? "" : "  -  AN APP NEEDS ATTENTION");
                status.ForeColor = g.AppsOk ? Theme.Accent : Theme.Warn;
            }
            else
            {
                status.Text = "guard not on watch - the app checkers run with it (turn the network guard on)";
                status.ForeColor = Theme.Dim;
            }

            List<string> lines = new List<string>();
            if (r == null)
                lines.Add("(nothing measured yet - Check now, or wait for the guard's next look)");
            else
                lines.AddRange(r.Lines());
            string[] appLines = g != null ? g.AppLines : new string[0];
            if (appLines.Length > 0)
            {
                lines.Add("");
                lines.AddRange(appLines);
            }
            else if (r != null)
                lines.Add("(no apps on the watch yet - add some below and Save)");
            string text = string.Join(Environment.NewLine, lines.ToArray());
            if (report.Text != text) report.Text = text;
        }

        private void Persist()
        {
            try
            {
                apps.Save(ini);
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------- gui

    internal sealed class MainForm : Form, IRestyle
    {
        private readonly Config cfg;
        private readonly Engine engine;
        private readonly TextBox logBox;
        private readonly MemGauge gauge;
        private readonly Button btnBoost, btnIdle, btnRestore, btnEaters, btnTrim, btnConfig, btnCleanup, btnBackup, btnSentry, btnNetGuard, btnDebloat, btnRemote, btnWinUtil, btnZoic, btnFeedback;
        private readonly UpdateBadge updateBadge;   // the corner arrow: white waiting, green when a release is out
        private readonly CheckBox chkSentry;
        private readonly CheckBox chkOverclock;
        private readonly RepeatBadge repeatBadge;   // the repeat loop, riding on BOOST NOW
        private readonly ListBadge listBoost, listIdle;   // each button's own lists, riding on its left
        private readonly ToolTip listTip = new ToolTip();
        private readonly ToolTip repeatTip = new ToolTip();
        private readonly ToolTip updateTip = new ToolTip();
        private readonly Label sentryLabel;
        private readonly Label updateLabel;
        private readonly System.Windows.Forms.Timer timer;
        private readonly System.Windows.Forms.Timer updateTimer;
        private Sentry sentry;
        private LogTail tail;                   // following another process's sentry log
        private NetGuard guard;                 // the standing watch, when this window holds it
        private NetGuard lastOnce;              // a check by hand while the guard was off
        private bool forceGuard;                // --guard: on whatever the ini says
        private bool checkingByHand;
        private DateTime nextGuardRetry = DateTime.MinValue;
        private NotifyIcon tray;
        private ToolStripMenuItem trayUpdate;
        private Updater.Release pending;        // a newer release we know about
        private DateTime nextUpdateCheck;
        private EatersForm eatersWin;
        private CleanupForm cleanupWin;
        private DebloatForm debloatWin;
        private BackupForm backupWin;
        private bool reallyExit;
        private bool askedTheme;                // the first-run picker has had its turn
        private CaptionBar caption;             // the theme's own title bar, when it draws one
        private bool chromeCustom;              // ...and whether it does
        private bool startHidden;
        private bool watchMode;
        private bool busyNow;                   // a mode is running right now
        private bool repeatOn;                  // the repeat loop is armed
        private int repeatMinutes = 30;         // ...every this many minutes
        private int drop;                       // how far the header pushed the column below it
        private bool syncingRepeat;             // the badge is following the ini, not the user
        private DateTime nextRepeat = DateTime.MaxValue;
        private ToolStripMenuItem trayRepeat;

        public MainForm(Config c)
        {
            cfg = c;
            engine = new Engine(cfg, AppendLog);

            Theme.Form(this);
            Text = "IDLE MASTER";
            StartPosition = FormStartPosition.CenterScreen;

            // Not resizable. Every number in this window is a pixel typed into
            // a SetBounds - there is no layout engine underneath to re-flow it
            // - so dragging an edge only ever moved the console and stretched
            // the rows out of line with the rules above them. Theme.Fit() sizes
            // the window to the screen it opens on, which is the part a drag
            // was standing in for.
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // The three header lines are measured, not typed. A 20pt title in
            // a 34px box had one pixel of slack at 9pt and none at all the
            // moment anything made the font render bigger - a theme with a
            // larger UI size, or a display that scales the text without
            // scaling the layout - and the first thing the app said about
            // itself came out sheared off halfway down the letters.
            //
            // So the title takes whatever height its own font needs, the
            // subtitle starts where the title ends, and the gauge sits under
            // whatever those two came to. At 9pt on a 96dpi screen every one
            // of them lands on the pixel it used to be pinned to; above that
            // they give ground downwards instead of cutting the text.
            Label title = new Label();
            title.Text = "IDLE MASTER";
            title.Font = Theme.Title();
            title.ForeColor = Theme.Accent;
            title.AutoSize = true;
            title.Location = new Point(20, 5);
            title.Size = title.PreferredSize;   // so Bottom is true before it is parented

            Label sub = Theme.Hint("Sunshine + Tailscale stay up. Everything else is negotiable.   v"
                + App.Version);
            sub.SetBounds(22, title.Bottom, 500, Math.Max(18, Theme.Base().Height));

            // Everything under the header is one column, and where it starts is
            // now a measurement rather than a number. Measuring the title fixed
            // the title and handed the problem straight to the next control: on
            // a screen that scales the text but not the layout the gauge came
            // down far enough to sit on the boost slab. So the gauge follows the
            // subtitle, the big buttons follow the gauge, and everything below
            // them - the four bands, the three centred lines, the switches, the
            // console - takes the same drop. The window grows by exactly that
            // much, so the console keeps its height instead of paying for the
            // header. At 9pt on a 96dpi screen the drop is zero and every one of
            // them lands on the pixel it used to be pinned to.
            int gaugeY = Math.Max(66, sub.Bottom + 6);
            int boostY = Math.Max(108, gaugeY + 34 + 8);
            drop = boostY - 108;

            // Before the first anchored control goes in: an anchor remembers its
            // distance to the edge it was added under, so a form resized after
            // the fact drags them all out of place.
            Size = new Size(700, 882 + drop);
            MinimumSize = new Size(560, 640 + drop);

            Controls.Add(title);
            Controls.Add(sub);

            // Updates live in the corner now instead of in the button grid: an
            // arrow pointing up beside the title, white while there is nothing
            // to say, green the moment a newer release is known. Clicking it
            // asks GitHub; clicking it once it is green installs. The line under
            // the buttons still carries the words - the arrow is the colour.
            updateBadge = new UpdateBadge();
            updateBadge.SetBounds(620, 10, 34, 34);
            updateBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            updateBadge.Click += delegate { CheckUpdates(); };
            Controls.Add(updateBadge);
            updateTip.SetToolTip(updateBadge, "Check for updates");

            gauge = new MemGauge();
            gauge.SetBounds(22, gaugeY, 640, 34);
            gauge.Font = Theme.Bold();
            gauge.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gauge);

            btnBoost = BigButton("BOOST NOW",
                "Kill the background junk.", Theme.Good, boostY);
            btnBoost.Click += delegate { Run("boost"); };

            // The repeat loop. Not a second kind of boost: it is this button,
            // clicked for you every N minutes for as long as the window is up.
            // The sentry keeps hunting between the runs; this is the whole pass
            // coming round again.
            //
            // It rides on the button rather than under it: a refresh arrow on
            // the right of the blue slab, the interval in its middle, the ring
            // filling as the next one comes round. Clicking the arrow opens the
            // menu that sets it - the boost itself is the rest of the button.
            //
            // Placed off btnBoost's own edge and anchored to the right, because
            // the window is sizable: a fixed x would drift off the slab the
            // first time somebody widened it.
            repeatOn = cfg.RepeatBoostMinutes > 0;
            repeatMinutes = cfg.RepeatBoostMinutes > 0
                ? Math.Min(1440, Math.Max(1, cfg.RepeatBoostMinutes)) : 30;

            repeatBadge = new RepeatBadge();
            repeatBadge.SetBounds(btnBoost.Right - 58,
                                  btnBoost.Top + (btnBoost.Height - 46) / 2, 46, 46);
            repeatBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            repeatBadge.Click += delegate { ShowRepeatMenu(); };
            Controls.Add(repeatBadge);
            repeatBadge.BringToFront();
            repeatTip.SetToolTip(repeatBadge, "Repeat boost - click to set the interval");

            if (repeatOn) ArmRepeat();

            listBoost = ListsHandle(btnBoost, Theme.Good, "boost");
            listTip.SetToolTip(listBoost, "What Boost closes and stops - click to edit");

            btnIdle = BigButton("ABSOLUTE IDLE",
                "Strip to Windows vitals", Theme.Danger, 188 + drop);
            btnIdle.Click += delegate { ConfirmIdle(); };

            listIdle = ListsHandle(btnIdle, Theme.Danger, "idle");
            listTip.SetToolTip(listIdle, "What Absolute Idle closes and stops - click to edit");

            // ---- the toolbox, in bands ---------------------------------
            //
            // A dozen identical gray slabs in one grid read as a wall: nothing
            // said which button belonged with which, so "Trim RAM now" sat
            // beside "Settings" and the two doors to somebody else's installer
            // sat wherever a gap was left over.
            //
            // Four bands now, each headed by a rule with the band's name in
            // the middle of it. A band's buttons share the full width of the
            // window between them - three wide ones or four narrower ones - so
            // every row runs edge to edge and the rules are the only thing
            // dividing them.
            //
            // The height had to come from somewhere and it was not going to
            // come from the console: a rule is one 7.5pt line and the header
            // gives up a dozen pixels.
            //
            // The version line no longer rides on the fourth row beside the two
            // buttons - it and the sentry's status line are centred under them,
            // three lines reading down the middle. That cost 56px and the
            // window is 882 tall now, so it NO LONGER fits an 816px desktop
            // standing up. The console kept its 212px rather than paying for
            // the change, which was the trade made on purpose; if the window
            // has to come back under 816 it is the log box that has to give.

            Band("AFTER THE BOOST", Theme.Accent, BoostBandY,
                 "Put the desktop back, or look at what the boost went after.");
            btnRestore = Slot("Restore desktop", 0, 3, BoostBandY);
            btnRestore.Click += delegate { Run("restore"); };
            btnEaters = Slot("Task manager", 1, 3, BoostBandY);
            btnEaters.Click += delegate { OpenEaters(); };
            btnTrim = Slot("Trim RAM now", 2, 3, BoostBandY);
            btnTrim.Click += delegate { Run("trim"); };

            // Red, because this is the band that takes things away and they
            // stay away - a boost is over when you reboot, this is not. The
            // last two are doors to other people's debloaters, launched rather
            // than imitated; they belong here, with ours.
            Band("DISK AND SYSTEM", Theme.Warn, DiskBandY,
                 "Removal that survives a reboot - disk, apps, and other people's debloaters.");
            btnCleanup = Slot("Disk cleanup", 0, 4, DiskBandY);
            btnCleanup.Click += delegate { OpenCleanup(); };
            btnDebloat = Slot("Debloat", 1, 4, DiskBandY);
            btnDebloat.Click += delegate { OpenDebloat(); };
            btnWinUtil = Slot("ChrisTitus WinUtil", 2, 4, DiskBandY);
            btnWinUtil.Click += delegate { RunWinUtil(); };
            btnZoic = Slot("Zoicware", 3, 4, DiskBandY);
            btnZoic.Click += delegate { RunZoicware(); };

            // How you get back to a machine you have stripped from across the
            // house: the link stays up, the desktop stays reachable, and the
            // kit rebuilds the box if neither of those held.
            Band("THE WAY BACK", Theme.Mix(Theme.Accent, Theme.Dim, 0.5), BackBandY,
                 "Keeping the machine reachable - and rebuildable if it is not.");

            // The network guard's page. The button itself goes red while the
            // guard is fighting something, the way the corner arrow goes green
            // when there is news.
            btnNetGuard = Slot("Network guard", 0, 3, BackBandY);
            btnNetGuard.Click += delegate { OpenNetGuard(); };
            btnRemote = Slot("Remote desktop setup", 1, 3, BackBandY);
            btnRemote.Click += delegate { OpenRemote(); };
            btnBackup = Slot("Backup kit", 2, 3, BackBandY);
            btnBackup.Click += delegate { OpenBackup(); };

            // The program itself, not the machine - gray, so it stays out of
            // the way of the three bands that do something to Windows. Two
            // buttons and then the words the corner arrow does not say: the
            // version, and whatever the update check last found.
            Band("IDLE MASTER", Theme.Dim, IdleBandY,
                 "This program: its switches, its version, and where to say it went wrong.");
            // This band has two members, not four. Left-aligned on a
            // four-column grid they sat under "Disk cleanup" and "Debloat"
            // with half a row of nothing beside them, which reads as a bug
            // rather than as a short band. Centred, at the same width the
            // four-up rows use, so the columns still line up vertically.
            int idleW = (RowRight - BandLeft - 3 * RowGap) / 4;
            int idleX = BandLeft + ((RowRight - BandLeft) - (2 * idleW + RowGap)) / 2;

            btnConfig = SlotAt("Settings", idleX, IdleBandY, idleW);
            btnConfig.Click += delegate { EditConfig(); };

            // The bug report door: whatever looked wrong, say so from right
            // here. Opens a pre-typed GitHub issue - the preview shows every
            // byte first, and nothing is sent until Submit in the browser.
            btnFeedback = SlotAt("Report a bug", idleX + idleW + RowGap, IdleBandY, idleW);
            btnFeedback.Click += delegate { OpenFeedback(); };

            updateLabel = Theme.Hint("running v" + App.Version + " - " + Updater.Repo);
            updateLabel.SetBounds(BandLeft, VersionY + drop, RowRight - BandLeft, 18);
            updateLabel.TextAlign = ContentAlignment.MiddleCenter;
            updateLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            updateLabel.AutoEllipsis = true;
            Controls.Add(updateLabel);

            chkSentry = new CheckBox();
            chkSentry.Text = "Keep hunting after boost";
            chkSentry.Checked = cfg.Sentry;
            chkSentry.SetBounds(24, LowerY + drop + 4, 190, 22);
            chkSentry.ForeColor = Theme.Fg;
            chkSentry.FlatStyle = FlatStyle.Flat;
            chkSentry.Click += delegate { ToggleSentry(); };
            Controls.Add(chkSentry);

            // It used to sit at x=220 w=300, ending at 520, with "Sentry
            // lists & timers" starting at 510 - ten pixels of overlap, and it
            // is anchored to both edges so widening the window made it worse.
            // Under the version now, centred on the same column, with the
            // separating gap below it.
            sentryLabel = Theme.Hint("");
            sentryLabel.SetBounds(BandLeft, SentryMsgY + drop, RowRight - BandLeft, 18);
            sentryLabel.TextAlign = ContentAlignment.MiddleCenter;
            sentryLabel.AutoEllipsis = true;
            sentryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(sentryLabel);

            btnSentry = Theme.Quiet("Sentry lists && timers");
            btnSentry.SetBounds(510, LowerY + drop, 152, 30);
            btnSentry.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSentry.Click += delegate { OpenSentry(); };
            Controls.Add(btnSentry);

            // The away switch: red when armed, because it means "kill everything
            // not protected, no questions asked". Toggle it, click ABSOLUTE
            // IDLE, walk away.
            chkOverclock = new CheckBox();
            chkOverclock.Text = "Overclocked sentry - while hunting, kill EVERYTHING not protected (for when you are away)";
            chkOverclock.Checked = cfg.OverclockedSentry;
            chkOverclock.SetBounds(24, LowerY + drop + 36, 636, 22);
            chkOverclock.ForeColor = cfg.OverclockedSentry ? Theme.Warn : Theme.Fg;
            chkOverclock.FlatStyle = FlatStyle.Flat;
            chkOverclock.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            chkOverclock.Click += delegate { ToggleOverclock(); };
            Controls.Add(chkOverclock);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Theme.LogBg;
            logBox.ForeColor = Theme.LogFg;
            logBox.Font = Theme.Mono();
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.SetBounds(22, LowerY + drop + 66, 640, 212);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(logBox);
            Theme.Scrollbars(logBox);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { UpdateMemory(); UpdateSentry(); UpdateGuard(); RepeatTick(); };
            timer.Start();
            UpdateMemory();
            UpdateSentry();
            RepeatTick();

            // The quiet update check: a minute after start, then every
            // UpdateCheckHours. Finding something only changes the button, the
            // tray, and one line here - installing is still your click.
            nextUpdateCheck = DateTime.Now.AddMinutes(1);
            updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 30000;
            updateTimer.Tick += delegate { QuietCheckTick(); };
            updateTimer.Start();

            AppendLog("Ready. Config: " + Config.Path_);
            if (repeatOn)
                AppendLog("Repeat loop armed from the config: BOOST NOW every "
                    + repeatMinutes + " min. First one in " + repeatMinutes
                    + " min - the arrow on the BOOST NOW button turns it off.");
            StateFile st = StateFile.Load();
            if (st.Mode.Length > 0)
                AppendLog("Note: last run was '" + st.Mode + "' and has not been restored yet ("
                    + st.StoppedServices.Count + " services still stopped).");

            if (cfg.Tray) BuildTray();

            // A mode was run earlier and never restored - pick the watch back
            // up. If some other process already holds it, this window does not
            // start a second one: it follows that sentry's log instead.
            if (st.Mode.Length > 0 && st.SentryArmed && cfg.Sentry)
            {
                if (!Sentry.IsRunningSomewhere()) StartSentry(st.Mode);
                else FollowForeignSentry();
            }

            // The guard does not wait for a mode: the connection matters from
            // the moment the app is up.
            if (cfg.NetworkGuard) StartGuard();
            UpdateGuard();

            FormClosing += OnClosing;

            // A second launch of the exe does not run: it fires the global
            // "show yourself" flag and this - the one Idle Master - comes to
            // the front instead.
            SoloInstance.WatchForShow(delegate
            {
                try { BeginInvoke((MethodInvoker)delegate { ShowWindow(); }); }
                catch (Exception) { }
            });

            // Last, because it slides everything above down to make room: a
            // theme that draws its own title bar gets Windows' one taken away.
            // Only if the theme asked - the default is the frame you know.
            if (Chrome.Wanted)
            {
                try { caption = Chrome.Install(this); chromeCustom = true; }
                catch (Exception ex) { AppendLog("! could not build the window frame: " + ex.Message); }
            }
        }

        // The theme's own title bar needs the eight resize grips and the
        // maximise rectangle handing back by hand; a system-framed window must
        // never see any of it.
        protected override void WndProc(ref Message m)
        {
            if (chromeCustom && Chrome.WndProc(this, ref m)) return;
            base.WndProc(ref m);
        }

        // --guard: the guard alone, whatever the ini says about it.
        public void ForceGuard()
        {
            forceGuard = true;
            StartGuard();
            UpdateGuard();
        }

        private bool GuardWanted { get { return cfg.NetworkGuard || forceGuard; } }

        private void StartGuard()
        {
            if (guard != null && guard.Alive) return;
            guard = new NetGuard(cfg, engine, AppendLog);
            guard.Start();          // a refusal is kept in guard.Refused for the page

            // The sentry's own 5-minute pass now notices a wedged daemon. It has
            // no repair ladder of its own, so hand it straight to the guard that
            // does rather than waiting out the rest of NetworkGuardSeconds.
            engine.OnNetworkTrouble = delegate
            {
                NetGuard g = guard;
                if (g != null && g.Alive) g.CheckNow();
            };
        }

        private void StopGuard()
        {
            if (guard == null) return;
            if (guard.Alive) guard.Stop();
            guard = null;
        }

        // "Check now": the running guard looks now and says everything; without
        // a guard, a one-off does the same from a worker thread, and its result
        // is kept for the page to show.
        private void CheckConnection()
        {
            if (guard != null && guard.Alive)
            {
                guard.CheckNow();
                return;
            }
            if (checkingByHand) return;
            checkingByHand = true;
            NetGuard once = new NetGuard(cfg, engine, AppendLog);
            lastOnce = once;
            Thread t = new Thread(delegate()
            {
                try { once.Check(true, true); }
                catch (Exception ex) { AppendLog("! connection check failed: " + ex.Message.Split('\n')[0]); }
                finally
                {
                    try { BeginInvoke((Action)delegate { checkingByHand = false; UpdateGuard(); }); }
                    catch (Exception) { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // What the page reads: the live guard if this window holds it (or was
        // refused it), otherwise the last check made by hand.
        private NetGuard GuardForPage()
        {
            if (guard != null && (guard.Alive || guard.Refused.Length > 0)) return guard;
            return lastOnce;
        }

        // The network guard's own page: its picture, a check on demand, its
        // switches, the Wi-Fi list. Saved straight to the ini, like the sentry's.
        private void OpenNetGuard()
        {
            using (NetGuardForm f = new NetGuardForm(GuardForPage, CheckConnection, cfg.NetworkGuard || forceGuard))
            {
                f.Location = new Point(Location.X + Width - 40, Location.Y + 60);
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                AppendLog("Network guard settings saved - "
                    + (cfg.NetworkGuard ? "on, every " + cfg.NetworkGuardSeconds + " s" : "OFF - nothing watches the connection")
                    + (cfg.NetworkWifi.Count > 0 ? "; " + cfg.NetworkWifi.Count + " preferred Wi-Fi network" + (cfg.NetworkWifi.Count == 1 ? "" : "s") : "")
                    + ".");
                if (cfg.NetworkGuard) StartGuard(); else if (!forceGuard) StopGuard();
                UpdateGuard();
            }
            catch (Exception ex) { AppendLog("! could not reload the config: " + ex.Message); }
        }

        // The remote-desktop page: the guard's picture, the apps that must stay
        // connected, and the Calibrate button. Saved straight to the ini.
        private void OpenRemote()
        {
            using (RemoteForm f = new RemoteForm(GuardForPage, CheckConnection, CalibrateRemote))
            {
                f.Location = new Point(Location.X + Width - 40, Location.Y + 60);
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                AppendLog("Remote desktop setup saved - " + cfg.RemoteApps.Count
                    + " app" + (cfg.RemoteApps.Count == 1 ? "" : "s") + " on the watch"
                    + (cfg.NetworkGuard || forceGuard
                        ? "; checked with the guard every " + cfg.NetworkGuardSeconds + " s."
                        : ". The network guard is OFF - turn it on for the watch to run."));
            }
            catch (Exception ex) { AppendLog("! could not reload the config: " + ex.Message); }
        }

        // The form saved its list before asking; snapshot against it and give
        // the running guard a nudge so the page shows the result right away.
        private void CalibrateRemote(List<string> names)
        {
            try { cfg.CopyFrom(Config.Load()); } catch (Exception) { }
            RemoteApps.Calibrate(names.Count > 0 ? names : cfg.RemoteApps, AppendLog);
            if (guard != null && guard.Alive) guard.CheckNow();
        }

        // The report page hands the report to the browser (or the clipboard);
        // the log notes which door it went out of.
        private void OpenFeedback()
        {
            using (FeedbackForm f = new FeedbackForm())
            {
                f.Location = new Point(Location.X + Width - 40, Location.Y + 60);
                f.ShowDialog(this);
                if (f.Opened)
                    AppendLog("Bug report opened on github.com - press Submit there to send it.");
                else if (f.Copied)
                    AppendLog("Bug report copied - paste it at " + Feedback.NewIssueUrl);
            }
        }

        // Chris Titus Tech's WinUtil, launched the way its README says to.
        // Their tool, their window - Idle Master only opens the door.
        private void RunWinUtil()
        {
            if (MessageBox.Show(this,
                "This launches Chris Titus Tech's Windows Utility (WinUtil) in its own elevated PowerShell:\n\n"
                + "    irm christitus.com/win | iex\n\n"
                + "That downloads the latest WinUtil from christitus.com and runs it - tweaks, debloat, "
                + "installs, all theirs. Close its window when you are done.\n\nLaunch it?",
                "ChrisTitus WinUtil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"irm 'https://christitus.com/win' | iex\"")
                { UseShellExecute = true });
                AppendLog("ChrisTitus WinUtil launched in its own PowerShell window.");
            }
            catch (Exception ex) { AppendLog("! could not launch WinUtil: " + ex.Message.Split('\n')[0]); }
        }

        // Zoicware ships as a release you download and run - no one-liner to
        // pipe, so the button opens the door to the right place instead.
        private void RunZoicware()
        {
            if (MessageBox.Show(this,
                "Zoicware is a Windows tweak/debloat pack that ships as a downloadable release.\n\n"
                + "This opens its GitHub releases page in your browser - grab the latest zip, extract it, "
                + "and run ZOICWARE.exe as admin. Their tool, their rules.\n\nOpen the page?",
                "Zoicware", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/zoicware/ZOICWARE/releases/latest")
                { UseShellExecute = true });
                AppendLog("Zoicware releases page opened in the browser.");
            }
            catch (Exception ex) { AppendLog("! could not open the page: " + ex.Message.Split('\n')[0]); }
        }

        private void UpdateGuard()
        {
            bool on = guard != null && guard.Alive;

            // Wanted but not running - another copy held the slot, or the thread
            // died. Try again every half minute; when the tray copy exits, this
            // window takes the guard over without being asked.
            if (!on && GuardWanted && !checkingByHand && DateTime.Now >= nextGuardRetry)
            {
                nextGuardRetry = DateTime.Now.AddSeconds(30);
                if (!NetGuard.IsRunningSomewhere())
                {
                    guard = null;
                    StartGuard();
                    on = guard != null && guard.Alive;
                }
            }

            NetReport r = on ? guard.Last : null;
            if (on && r != null && (!r.Healthy || !guard.AppsOk))
                PaintButton(btnNetGuard, "Network guard: " + (guard.Busy ? "fixing" : "trouble"), Theme.Danger, Theme.OnAccent);
            else if (!GuardWanted)
                PaintButton(btnNetGuard, "Network guard: off", Theme.Neutral, Theme.Dim);
            else
                PaintButton(btnNetGuard, "Network guard", Theme.Neutral, Theme.Fg);
        }

        private static void PaintButton(Button b, string text, Color back, Color fore)
        {
            if (b.Text == text && b.BackColor == back && b.ForeColor == fore) return;
            b.Text = text;
            b.BackColor = back;
            b.ForeColor = fore;
            b.FlatAppearance.MouseOverBackColor = Theme.Lift(back, 18);
            b.FlatAppearance.MouseDownBackColor = Theme.Lift(back, -12);
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

        // One cleanup window, reused if it is already open - same deal as the
        // task manager above.
        private void OpenCleanup()
        {
            if (cleanupWin != null && !cleanupWin.IsDisposed)
            {
                cleanupWin.Activate();
                return;
            }
            cleanupWin = new CleanupForm(cfg, AppendLog);
            cleanupWin.Location = new Point(Location.X + Width - 40, Location.Y + 80);
            cleanupWin.Show(this);
        }

        // One debloat window, same rule.
        private void OpenDebloat()
        {
            if (debloatWin != null && !debloatWin.IsDisposed)
            {
                debloatWin.Activate();
                return;
            }
            debloatWin = new DebloatForm(cfg, AppendLog);
            debloatWin.Location = new Point(Location.X + Width - 40, Location.Y + 100);
            debloatWin.Show(this);
        }

        // The sentry's own window: its lists and timers, saved straight to the ini.
        private void OpenSentry()
        {
            string armed = StateFile.Load().Mode;
            using (SentryForm f = new SentryForm(delegate { return sentry; }, armed))
            {
                f.Location = new Point(Location.X + Width - 40, Location.Y + 60);
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                AppendLog("Sentry lists saved. " + cfg.BoostKill.Count + " on the boost list, "
                    + cfg.IdleKill.Count + " more on the idle list"
                    + (cfg.SentryFullPassMinutes > 0 ? "; whole pass every " + cfg.SentryFullPassMinutes + " min" : "")
                    + (cfg.BoostWhenFreeBelowMb > 0 ? "; whole pass when free RAM < " + cfg.BoostWhenFreeBelowMb + " MB" : "")
                    + ".");
                if (sentry != null && sentry.Alive)
                    AppendLog("The sentry is using the new lists from its next sweep.");
                SyncRepeat();
                if (eatersWin != null && !eatersWin.IsDisposed) eatersWin.RefreshNow();
            }
            catch (Exception ex) { AppendLog("! could not reload the config: " + ex.Message); }
        }

        // One backup window, same rule.
        private void OpenBackup()
        {
            if (backupWin != null && !backupWin.IsDisposed)
            {
                backupWin.Activate();
                return;
            }
            backupWin = new BackupForm(AppendLog);
            backupWin.Location = new Point(Location.X + Width - 40, Location.Y + 120);
            backupWin.Show(this);
        }

        // The StartWithWindows logon task lands here (--startup): the window is
        // up like any launch, and StartupAction runs as if you had clicked the
        // button - no confirmation, because nobody is there to give one.
        public void RunOnLogon()
        {
            // Touching Handle builds the window now so Run's BeginInvoke has
            // somewhere to land.
            IntPtr forced = Handle;
            GC.KeepAlive(forced);
            AppendLog("Started at logon (StartWithWindows)."
                + (cfg.StartupAction == "boost" ? " Running BOOST NOW."
                 : cfg.StartupAction == "idle" ? " Running ABSOLUTE IDLE." : ""));
            if (cfg.StartupAction == "boost" || cfg.StartupAction == "idle")
                Run(cfg.StartupAction);
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
            // The 16 px frame, not a squashed 32 - the tray is unforgiving about that.
            try { tray.Icon = new Icon(App.Icon, 16, 16); }
            catch (Exception) { tray.Icon = App.Icon; }
            tray.Text = "Idle Master";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.Menu(menu);
            menu.Items.Add("Open Idle Master", null, delegate { ShowWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Boost now", null, delegate { ShowWindow(); Run("boost"); });
            trayRepeat = new ToolStripMenuItem("Repeat boost every " + repeatMinutes + " min");
            trayRepeat.Checked = repeatOn;
            trayRepeat.Click += delegate { SetRepeat(!repeatOn, repeatMinutes); };
            menu.Items.Add(trayRepeat);
            menu.Items.Add("Absolute idle", null, delegate { ShowWindow(); ConfirmIdle(); });
            menu.Items.Add("Restore desktop", null, delegate { ShowWindow(); Run("restore"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Stop hunting", null, delegate
            {
                chkSentry.Checked = false;
                StopSentry();
            });
            menu.Items.Add("Check the connection now", null, delegate { CheckConnection(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Disk cleanup...", null, delegate { ShowWindow(); OpenCleanup(); });
            menu.Items.Add("Debloat...", null, delegate { ShowWindow(); OpenDebloat(); });
            menu.Items.Add("Backup kit...", null, delegate { ShowWindow(); OpenBackup(); });
            menu.Items.Add("Sentry lists && timers...", null, delegate { ShowWindow(); OpenSentry(); });
            menu.Items.Add("Remote desktop setup...", null, delegate { ShowWindow(); OpenRemote(); });
            menu.Items.Add("Network guard...", null, delegate { ShowWindow(); OpenNetGuard(); });
            menu.Items.Add("Settings...", null, delegate { ShowWindow(); EditConfig(); });
            menu.Items.Add("Theme...", null, delegate { ShowWindow(); OpenThemes(); });
            menu.Items.Add("Check for updates", null, delegate { ShowWindow(); CheckUpdates(); });
            menu.Items.Add("Report a bug...", null, delegate { ShowWindow(); OpenFeedback(); });
            trayUpdate = new ToolStripMenuItem("Update now");
            trayUpdate.Visible = false;
            trayUpdate.Click += delegate { InstallPending(); };
            menu.Items.Add(trayUpdate);
            menu.Items.Add("Exit", null, delegate { reallyExit = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.BalloonTipClicked += delegate { if (pending != null) InstallPending(); };
            if (pending != null) Announce(pending, false);
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            ThemeIntro();
        }

        // ---- the look
        //
        // The picker is a pane over this window, not a dialog in front of it,
        // so it waits until there is something to blur. Once, ever: answering
        // it or waving it away both write ThemeIntro=1, because a prompt that
        // comes back every morning is a nag, and the tray menu and Settings
        // both lead here afterwards.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ThemeIntro();
        }

        private void ThemeIntro()
        {
            if (askedTheme || cfg.ThemeIntro || !Visible || IsDisposed) return;
            askedTheme = true;
            // After the paint that follows Shown, or the snapshot behind the
            // glass is a half-drawn window.
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    Refresh();
                    ThemeGate.Open(this, cfg, true, AppendLog);
                }
                catch (Exception ex) { AppendLog("! theme picker: " + ex.Message); }
            });
        }

        private void OpenThemes()
        {
            askedTheme = true;
            try
            {
                Refresh();
                ThemeGate.Open(this, cfg, false, AppendLog);
            }
            catch (Exception ex) { AppendLog("! theme picker: " + ex.Message); }
        }

        // The colours this window mixes for itself - a state colour on a
        // checkbox, a button painted to say the guard is in trouble - are not
        // in the palette, so the generic swap cannot find them. Redo them.
        public void Restyle()
        {
            try
            {
                logBox.BackColor = Theme.LogBg;
                logBox.ForeColor = Theme.LogFg;
                logBox.Font = Theme.Mono();
                chkOverclock.ForeColor = cfg.OverclockedSentry ? Theme.Warn : Theme.Fg;
                chkSentry.ForeColor = Theme.Fg;
                if (tray != null && tray.ContextMenuStrip != null) Theme.Menu(tray.ContextMenuStrip);
                UpdateGuard();
                if (pending != null) Announce(pending, false);   // repaint the arrow, do not re-toast
                gauge.Invalidate();
                repeatBadge.Invalidate();
                updateBadge.Invalidate();       // the one control that wears Theme.Ready
                listBoost.Invalidate();
                listIdle.Invalidate();
                if (caption != null) caption.Invalidate();
                Theme.Frame(this);              // light theme, light title bar
                Theme.Scrollbars(logBox);

                // Colours change under your hands; the frame cannot. Swapping
                // FormBorderStyle on a live window means destroying and
                // rebuilding its handle, which drops the tray hook, the solo
                // instance watch and every timer hanging off it - for a border.
                // So say so plainly instead of half-doing it.
                if (Chrome.Wanted != chromeCustom)
                    AppendLog("Theme: " + Theme.Current.Name + " asks for "
                        + (Chrome.Wanted ? "its own title bar" : "the Windows title bar")
                        + " - that part arrives on the next start. Everything else is already on.");
            }
            catch (Exception) { }
        }

        // Closing the window while the sentry is up would let RAM drift back, so
        // it goes to the tray instead. Exit from the tray menu when you mean it.
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            bool hunting = sentry != null && sentry.Alive;
            bool guarding = guard != null && guard.Alive;
            if (!reallyExit && cfg.Tray && (hunting || guarding) && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    if (hunting)
                        tray.ShowBalloonTip(4000, "Still hunting",
                            "Idle Master is holding " + sentry.Mode.ToUpperInvariant()
                            + " in the tray" + (guarding ? " and guarding the connection" : "")
                            + ". Right-click the icon to stop it.", ToolTipIcon.Info);
                    else
                        tray.ShowBalloonTip(4000, "Still guarding",
                            "Idle Master is keeping the connection up from the tray. "
                            + "Exit from the tray menu when you mean it.", ToolTipIcon.Info);
                }
                catch (Exception) { }
                return;
            }
            StopSentry(false);
            StopGuard();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        }

        private void StartSentry(string mode)
        {
            if (sentry != null && sentry.Alive) return;
            sentry = new Sentry(cfg, engine, AppendLog, mode);
            sentry.Ask = AskOnUiThread;
            if (!sentry.Start())
            {
                sentry = null;
                FollowForeignSentry();
            }
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
            // Show, not ShowDialog: modal would activate the toast and steal the
            // foreground from whatever you are typing or playing. The loop keeps
            // this call blocking (the sentry thread is parked in Invoke above)
            // while the rest of the UI stays alive.
            using (AskForm f = new AskForm(q, cfg.AskTimeoutSeconds, cfg.AskTimeoutAction))
            {
                f.Show();
                while (f.Visible && !f.IsDisposed)
                {
                    Application.DoEvents();
                    Thread.Sleep(30);
                }
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

        // ---- the repeat loop -------------------------------------------------
        // BOOST NOW, on a clock the user sets. It is the same run as the button:
        // the same lists, the same asking, the sentry re-armed after each one.
        // It lives in this window - close Idle Master and the loop is over, and
        // it lives on the button too: the refresh arrow at its right edge.

        private void ArmRepeat()
        {
            nextRepeat = DateTime.Now.AddMinutes(repeatMinutes);
        }

        // The little menu the arrow opens: the switch, a spinner, and the
        // intervals people actually pick. Clicking the arrow never boosts -
        // the rest of the button is still the boost.
        private void ShowRepeatMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.Menu(menu);

            bool spun = false;              // the spinner was touched this time round
            ToolStripMenuItem onOff = new ToolStripMenuItem(
                repeatOn ? "Repeat boost is ON" : "Repeat boost is off");
            onOff.Checked = repeatOn;
            onOff.Click += delegate { spun = false; SetRepeat(!repeatOn, repeatMinutes); };
            menu.Items.Add(onOff);
            menu.Items.Add(new ToolStripSeparator());

            // The spinner lives in the menu itself, so any minute count from 1
            // to a day is one row away with no second window. A spinner run
            // lands when the menu closes, so holding the arrow down is one
            // change and one log line, not thirty.
            NumericUpDown spin = new NumericUpDown();
            spin.Minimum = 1;
            spin.Maximum = 1440;
            spin.Value = repeatMinutes;
            spin.SetBounds(0, 1, 62, 22);
            Theme.Input_(spin);

            Label unit = Theme.Hint("minutes");
            unit.SetBounds(68, 4, 60, 18);

            Panel row = new Panel();
            row.BackColor = Theme.Panel;
            row.Size = new Size(132, 24);
            row.Controls.Add(spin);
            row.Controls.Add(unit);

            ToolStripControlHost host = new ToolStripControlHost(row);
            host.Margin = new Padding(6, 2, 6, 2);
            host.AutoSize = false;
            host.Size = row.Size;
            menu.Items.Add(host);
            menu.Items.Add(new ToolStripSeparator());

            spin.ValueChanged += delegate { spun = true; };
            menu.Closed += delegate
            {
                if (spun && (int)spin.Value != repeatMinutes) SetRepeat(true, (int)spin.Value);
            };

            int[] presets = new int[] { 5, 10, 15, 30, 60, 120 };
            foreach (int mins in presets)
            {
                int pick = mins;
                string label = mins < 60 ? "Every " + mins + " minutes"
                    : "Every " + (mins / 60) + (mins == 60 ? " hour" : " hours");

                // The menus here run without an image margin, so a tick mark
                // would not show: the one in force is bulleted and bold instead.
                bool current = repeatOn && repeatMinutes == mins;
                ToolStripMenuItem item = new ToolStripMenuItem((current ? "* " : "    ") + label);
                if (current) item.Font = Theme.Bold();
                item.Click += delegate { spun = false; SetRepeat(true, pick); };
                menu.Items.Add(item);
            }

            if (repeatOn)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem next = new ToolStripMenuItem(NextRepeatText());
                next.Enabled = false;
                menu.Items.Add(next);
            }

            menu.Show(repeatBadge, new Point(0, repeatBadge.Height + 2));
        }

        // The one door in and out of the loop: the badge menu and the tray both
        // come through here, so the state, the ini and the log never drift.
        private void SetRepeat(bool on, int minutes)
        {
            if (syncingRepeat) return;
            if (minutes < 1) minutes = 1;
            if (minutes > 1440) minutes = 1440;

            bool wasOn = repeatOn;
            int wasMins = repeatMinutes;
            repeatOn = on;
            repeatMinutes = minutes;

            if (on)
            {
                ArmRepeat();
                SaveRepeat(minutes);
                if (!wasOn)
                    AppendLog("Repeat loop ON - a full BOOST NOW every " + minutes
                        + " min, for as long as this window is open. First one in "
                        + minutes + " min.");
                else if (wasMins != minutes)
                    AppendLog("Repeat loop: every " + minutes + " min from now.");
            }
            else
            {
                nextRepeat = DateTime.MaxValue;
                SaveRepeat(0);
                if (wasOn) AppendLog("Repeat loop off. Boost is back to being your click.");
            }
            RepeatTick();
        }

        // Settings > Advanced has the same key. After a save there the badge
        // follows the file, silently - no re-save of its own.
        private void SyncRepeat()
        {
            bool on = cfg.RepeatBoostMinutes > 0;
            int mins = on ? Math.Min(1440, Math.Max(1, cfg.RepeatBoostMinutes)) : repeatMinutes;
            if (on == repeatOn && mins == repeatMinutes) return;

            syncingRepeat = true;
            try
            {
                repeatOn = on;
                repeatMinutes = mins;
            }
            finally { syncingRepeat = false; }

            if (on)
            {
                ArmRepeat();
                AppendLog("Repeat loop: BOOST NOW every " + mins + " min, from the config.");
            }
            else
            {
                nextRepeat = DateTime.MaxValue;
                AppendLog("Repeat loop off, from the config.");
            }
            RepeatTick();
        }

        private static string FirstLine(string text)
        {
            return text.Split(new char[] { (char)13, (char)10 })[0];
        }

        private void SaveRepeat(int minutes)
        {
            cfg.RepeatBoostMinutes = minutes;
            try
            {
                IniFile ini = new IniFile();
                ini.SetSetting("RepeatBoostMinutes", minutes.ToString(CultureInfo.InvariantCulture));
                ini.Save();
            }
            catch (Exception ex)
            { AppendLog("! could not save RepeatBoostMinutes: " + FirstLine(ex.Message)); }
        }

        // The countdown in words, for the tooltip and the menu's last line.
        private string NextRepeatText()
        {
            if (!repeatOn) return "";
            if (busyNow) return "boosting now; the clock restarts when it finishes";
            TimeSpan left = nextRepeat - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return "next boost in " + ((int)left.TotalMinutes) + ":"
                + left.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        // Runs off the 2-second tick. A run already in progress just delays the
        // next one - the loop never stacks two boosts on top of each other.
        private void RepeatTick()
        {
            if (repeatBadge == null) return;

            if (!repeatOn)
            {
                repeatBadge.Set(false, repeatMinutes, 0);
                repeatTip.SetToolTip(repeatBadge,
                    "Repeat boost is off - click to set the interval");
                if (trayRepeat != null)
                {
                    trayRepeat.Text = "Repeat boost every " + repeatMinutes + " min";
                    trayRepeat.Checked = false;
                }
                return;
            }

            if (busyNow)
                nextRepeat = DateTime.Now.AddMinutes(repeatMinutes);
            else if (DateTime.Now >= nextRepeat)
            {
                ArmRepeat();
                AppendLog("Repeat loop: boosting (every " + repeatMinutes + " min).");
                Run("boost");
            }

            // The ring fills as the interval runs down.
            double total = repeatMinutes * 60.0;
            double left_ = (nextRepeat - DateTime.Now).TotalSeconds;
            if (left_ < 0) left_ = 0;
            double through = total <= 0 ? 0 : (total - left_) / total;
            repeatBadge.Set(true, repeatMinutes, through);
            repeatTip.SetToolTip(repeatBadge,
                "BOOST NOW every " + repeatMinutes + " min - " + NextRepeatText());

            if (trayRepeat != null)
            {
                trayRepeat.Text = "Repeat boost every " + repeatMinutes + " min";
                trayRepeat.Checked = true;
            }
        }

        // Saved straight to the ini; a hunting sentry reads cfg every sweep, so
        // it goes overclocked (or calms down) without being restarted.
        private void ToggleOverclock()
        {
            cfg.OverclockedSentry = chkOverclock.Checked;
            chkOverclock.ForeColor = cfg.OverclockedSentry ? Theme.Warn : Theme.Fg;
            try
            {
                IniFile ini = new IniFile();
                ini.SetSetting("OverclockedSentry", cfg.OverclockedSentry ? "1" : "0");
                ini.Save();
            }
            catch (Exception ex) { AppendLog("! could not save OverclockedSentry: " + ex.Message.Split('\n')[0]); }

            if (cfg.OverclockedSentry)
            {
                AppendLog("OVERCLOCKED SENTRY ON - while hunting, everything not on the protect list dies."
                    + " No questions, no sparing open windows. Meant for ABSOLUTE IDLE while you are away.");
                if (sentry != null && sentry.Alive)
                    AppendLog("The sentry goes overclocked from its next sweep.");
            }
            else
                AppendLog("Overclocked sentry off - back to the normal lists and questions.");
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
            // Following another process's sentry: stop the tail the moment the
            // watch is ours, or the moment nobody holds it any more.
            if (tail != null)
            {
                bool ours = sentry != null && sentry.Alive;
                if (ours || !Sentry.IsRunningSomewhere())
                {
                    tail.Stop();
                    tail = null;
                    AppendLog(ours
                        ? "The watch is ours now - stopped following the other log."
                        : "The other sentry stood down - its log went quiet. Arm the sentry here to take the watch.");
                }
            }

            bool on = sentry != null && sentry.Alive;
            if (!on && sentry != null)
            {
                sentry = null;
                // --unwatch (or Restore from elsewhere) killed the watch, and a tray
                // app with nothing to do is just a stray icon. One that is still
                // guarding the connection has something to do.
                bool guarding = guard != null && guard.Alive;
                if (watchMode && !guarding) { reallyExit = true; Close(); return; }
            }

            if (on)
            {
                string txt = "hunting " + sentry.Mode.ToUpperInvariant() + " - "
                    + sentry.Reaped + " reaped, " + Engine.Size(sentry.Reclaimed) + " held off";
                if (sentry.Restopped > 0) txt += ", " + sentry.Restopped + " services re-stopped";
                sentryLabel.Text = txt;
                sentryLabel.ForeColor = Theme.Accent;
            }
            else if (tail != null)
            {
                sentryLabel.Text = "sentry found in another process - following its log";
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
            if (updateBadge.Busy) return;
            if (pending != null) { InstallPending(); return; }
            updateBadge.Set(false, true);
            Status("asking GitHub...", Theme.Dim);
            AppendLog("Checking " + Updater.Repo + " for releases newer than " + App.Version + "...");
            Ask_(true);
        }

        private void Ask_(bool loud)
        {
            Thread t = new Thread(delegate()
            {
                Updater.Release r = null;
                string failure = null;
                try { r = Updater.Latest(); }
                catch (Exception ex) { failure = ex.Message.Split('\n')[0]; }

                try { BeginInvoke((Action)delegate { if (loud) Finish(r, failure); else Quiet(r, failure); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // Every 30 s: is it time? UpdateCheckHours = 0 turns it off; a check
        // already announced is not repeated for the same tag.
        private void QuietCheckTick()
        {
            if (cfg.UpdateCheckHours <= 0 || pending != null) return;
            if (DateTime.Now < nextUpdateCheck) return;
            nextUpdateCheck = DateTime.Now.AddHours(cfg.UpdateCheckHours);
            Ask_(false);
        }

        // The automatic check says nothing unless there is something to say.
        private void Quiet(Updater.Release r, string failure)
        {
            if (failure != null || r == null || r.Tag.Length == 0 || !r.Newer || r.Url.Length == 0) return;
            Announce(r, true);
        }

        // A newer release is known: the corner arrow goes green, the tray says
        // so once, and one click on either does the whole thing.
        private void Announce(Updater.Release r, bool toast)
        {
            pending = r;
            updateBadge.Set(true, false);
            updateTip.SetToolTip(updateBadge, "Update to " + r.Tag + " - click to install");
            Status(r.Tag + " is available - one click installs it and brings Idle Master back", Theme.Accent);
            if (toast) AppendLog(r.Tag + " is available (you are on " + App.Version + "). "
                + "'Update to " + r.Tag + "' does it in one click; your idlemaster.ini is kept.");
            if (trayUpdate != null)
            {
                trayUpdate.Text = "Update to " + r.Tag + " now";
                trayUpdate.Visible = true;
            }
            if (toast && tray != null)
            {
                try
                {
                    tray.ShowBalloonTip(10000, "Idle Master " + r.Tag + " is out",
                        "Click here to update in place. Your config is kept and Idle Master "
                        + "comes back on its own.", ToolTipIcon.Info);
                }
                catch (Exception) { }
            }
        }

        // The one click: download, hand over to the installer silently, pointed
        // at this folder and told to relaunch, and get out of its way.
        private void InstallPending()
        {
            Updater.Release r = pending;
            if (r == null) return;
            updateBadge.Set(true, true);
            try
            {
                AppendLog("Downloading " + r.Tag + "...");
                Status("downloading " + r.Tag + "...", Theme.Accent);
                string setup = Updater.Fetch(r);
                AppendLog("Handing over to " + setup + " - Idle Master closes and comes back as " + r.Tag + ".");
                Process.Start(new ProcessStartInfo(setup,
                    "--silent --relaunch --dir \"" + App.Dir + "\"") { UseShellExecute = true });
                reallyExit = true;
                Close();
            }
            catch (Exception ex)
            {
                updateBadge.Set(true, false);
                Status("update failed - " + ex.Message.Split('\n')[0], Theme.Warn);
                AppendLog("! update failed: " + ex.Message.Split('\n')[0]);
            }
        }

        private void Finish(Updater.Release r, string failure)
        {
            updateBadge.Set(pending != null, false);

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

            Announce(r, false);
            if (MessageBox.Show(this,
                r.Tag + " is out - you are on " + App.Version + "."
                + "\n\nUpdate this copy in " + App.Dir + " now?"
                + "\n\nYour idlemaster.ini is kept exactly as it is. Idle Master closes while "
                + "the installer replaces it, and comes back on its own.",
                "Idle Master", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
            {
                AppendLog("Update available (" + r.Tag + ") - not installed; the corner arrow "
                    + "stays green and installs " + r.Tag + " whenever you want it.");
                return;
            }
            InstallPending();
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
            bool toTheme = false;
            using (QuickSettingsForm f = new QuickSettingsForm())
            {
                f.ShowDialog(this);
                toTheme = f.WantsTheme;
                if (!f.Saved) { if (toTheme) OpenThemes(); return; }
            }
            bool wasStartup = cfg.StartWithWindows;
            try
            {
                cfg.CopyFrom(Config.Load());
                chkSentry.Checked = cfg.Sentry;
                chkOverclock.Checked = cfg.OverclockedSentry;
                chkOverclock.ForeColor = cfg.OverclockedSentry ? Theme.Warn : Theme.Fg;
                SyncRepeat();
                if (cfg.StartWithWindows != wasStartup)
                    App.SyncStartupTask(cfg.StartWithWindows, AppendLog);
                AppendLog("Config saved. " + cfg.Protect.Count + " protected names, "
                    + cfg.BoostKill.Count + " on the boost list, "
                    + cfg.IdleKill.Count + " more on the idle list.");
                if (sentry != null && sentry.Alive)
                    AppendLog("The sentry is using the new lists from its next sweep.");
                if (cfg.Tray && tray == null) BuildTray();
                if (cfg.NetworkGuard) StartGuard(); else if (!forceGuard) StopGuard();
                UpdateGuard();
                if (eatersWin != null && !eatersWin.IsDisposed) eatersWin.RefreshNow();
            }
            catch (Exception ex) { AppendLog("! could not reload the config: " + ex.Message); }
        }

        private Button BigButton(string text, string sub, Color color, int y)
        {
            // Colour from the theme, height from the band layout: the themes
            // work owns what colour this is, the band work owns how much room
            // it may take above the four named bands below it.
            Button b = Theme.Button(text + "\n" + sub, color, Theme.OnAccent);
            b.SetBounds(22, y, 640, 72);
            b.Font = Theme.Big();
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(b);
            return b;
        }

        // The three bars on a big button's left edge. Placed off the button's
        // own bounds and anchored left, the way the repeat badge is placed off
        // its right edge and anchored right - the window is sizable and a fixed
        // x would drift off the slab.
        private ListBadge ListsHandle(Button on, Color slab, string which)
        {
            ListBadge b = new ListBadge(slab);
            b.SetBounds(on.Left + 12, on.Top + (on.Height - 46) / 2, 46, 46);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            b.Click += delegate { OpenLists(which); };
            Controls.Add(b);
            b.BringToFront();
            return b;
        }

        private void OpenLists(string which)
        {
            bool idle = which == "idle";
            ListsPopup f = idle
                ? new ListsPopup("Absolute idle - what it closes and stops",
                    "Applied ON TOP of the boost lists. Nobody is watching, so this is where the browsers and the desktop go.",
                    "idle.kill", "Also closed by Absolute Idle",
                    "idle.services", "Also stopped by Absolute Idle")
                : new ListsPopup("Boost now - what it closes and stops",
                    "Background junk that has no business running while you work. The desktop stays usable.",
                    "boost.kill", "Processes closed by Boost",
                    "boost.services", "Services stopped by Boost");

            using (f)
            {
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                AppendLog(idle
                    ? "Idle lists saved. " + cfg.IdleKill.Count + " processes, "
                        + cfg.IdleServices.Count + " services on top of boost."
                    : "Boost lists saved. " + cfg.BoostKill.Count + " processes, "
                        + cfg.BoostServices.Count + " services.");
                if (sentry != null && sentry.Alive)
                    AppendLog("The sentry is using the new lists from its next sweep.");
            }
            catch (Exception ex) { AppendLog("Could not reload the config: " + ex.Message); }
        }

        // ---- the bands under the big buttons

        // A band is one rule and the row of buttons under it. Rule and row
        // both run the full width of the window, the way the gauge and the
        // console do, so there is no dead column down the right: a band of
        // three splits that width three ways, a band of four splits it four
        // ways, and every row still ends on the same edge.
        private const int BandLeft = 22;
        private const int RowRight = 662;    // where the gauge and console end
        private const int RowGap   = 8;
        private const int SlotHigh = 28;
        private const int RuleHigh = 14;     // one line of 7.5pt, nothing more

        // How far under the rule the row starts. It was 15 - one pixel of
        // clearance - and the band's name sat right on the caps of the buttons
        // it names, which read as the name being crowded out rather than as a
        // heading. Six pixels of air now, and the pitch below carries the extra
        // down the wall so every band keeps the same 8px gap to the next rule.
        private const int RuleDrop  = 20;
        private const int BandPitch = RuleDrop + SlotHigh + RowGap;   // 56

        // The first band starts here, and every one after it is a pitch below.
        private const int FirstBandY = 270;
        private const int BoostBandY = FirstBandY;                    // 270
        private const int DiskBandY  = FirstBandY + BandPitch;        // 326
        private const int BackBandY  = FirstBandY + 2 * BandPitch;    // 382

        // The tail of the IDLE MASTER band: the two centred buttons, then the
        // version, then whatever the sentry is currently doing - three centred
        // lines reading down the middle. LowerY is deliberately 24 px below the
        // last of them and not 6, because that gap is what separates "the
        // program" above from the switches and the console below.
        private const int IdleBandY   = FirstBandY + 3 * BandPitch;   // 438
        private const int VersionY    = IdleBandY + RuleDrop + SlotHigh + 6;   // 492
        private const int SentryMsgY  = VersionY + 22;                          // 514
        private const int LowerY      = SentryMsgY + 40;                        // 554

        // The rule that heads a band. The name is as much as fits on a line
        // that short, so the longer answer goes in the tooltip.
        private BandRule Band(string name, Color ink, int y, string tip)
        {
            BandRule r = new BandRule(name, ink);
            r.SetBounds(BandLeft, y + drop, RowRight - BandLeft, RuleHigh);
            Controls.Add(r);
            listTip.SetToolTip(r, tip);
            return r;
        }

        // Button number i of the n in the band whose rule is at y. They share
        // the row evenly and the last one takes up whatever the division left
        // over, so a row of four ends on exactly the edge a row of three does.
        private Button Slot(string text, int i, int n, int y)
        {
            int w = (RowRight - BandLeft - (n - 1) * RowGap) / n;
            int x = BandLeft + i * (w + RowGap);
            if (i == n - 1) w = RowRight - x;
            return SlotAt(text, x, y, w);
        }

        // One row button at an explicit x and width, for a band that does not
        // fill its row and would rather sit in the middle of it.
        private Button SlotAt(string text, int x, int y, int w)
        {
            Button b = Theme.Quiet(text);
            b.SetBounds(x, y + RuleDrop + drop, w, SlotHigh);
            Controls.Add(b);
            return b;
        }

        private void ConfirmIdle()
        {
            string msg = "This closes every app you have open - browsers, Claude, all of it - and strips"
                + " the machine to Windows vitals."
                + (cfg.KillExplorer
                    ? "\n\nThe screen flashes black for a moment while Windows rebuilds the shell."
                      + " The desktop, taskbar and Start menu come back on their own, fresh - open"
                      + " File Explorer windows do not survive it."
                    : "")
                + "\n\nSunshine and Tailscale stay up, so you can still reach this machine."
                + " 'Restore desktop' brings everything back."
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
            busyNow = busy;
            // The interval box stays live while a run is on; only the buttons go.
            btnBoost.Enabled = btnIdle.Enabled = btnRestore.Enabled = btnTrim.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        // A sentry in some other process already has the watch. Nothing starts
        // here - "sentry found" - and that sentry's log (the shared
        // idlemaster.log) plays live in this console instead.
        private void FollowForeignSentry()
        {
            if (tail != null) return;
            AppendLog("Sentry found - another Idle Master already has the watch. Following its log:");
            tail = new LogTail(App.LogFile, AppendScreenOnly);
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
            if (tail != null) tail.NoteLocal(line);   // so the tail does not echo it back
            logBox.AppendText(line + Environment.NewLine);
        }

        // Lines that CAME from the shared log must not be written back into it.
        private void AppendScreenOnly(string line)
        {
            if (logBox.InvokeRequired)
            {
                try { logBox.BeginInvoke((Action<string>)AppendScreenOnly, line); }
                catch (Exception) { }
                return;
            }
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

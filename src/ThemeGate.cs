// IDLE MASTER - the look picker, and the frosted pane it sits on.
//
// The app starts the way it always did: the window comes up, the gauge is
// already reading, the log already says Ready. Then, the first time only, the
// whole thing goes soft behind a sheet of frosted glass and one card asks
// which look you want. Nothing is hidden and nothing is loading - what is
// behind the blur is the running app, and answering hands it straight back.
//
// Deliberately not a modal dialog. A dialog in front of the window says "this
// is a different thing now"; a pane over it says "this is the same thing,
// wearing something". The second one is true.
//
// The blur is bilinear, not gaussian: the snapshot is scaled down to a tenth
// and back up, twice, which is a box blur done by the graphics card for free.
// A real convolution over 700x800 pixels in managed code would take longer
// than the window took to open.
//
//   Frost      snapshot -> blurred snapshot
//   Swatch     one theme, previewed as a tiny mock of this window
//   ThemeGate  the pane, the card, and the two answers
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace IdleMaster
{
    internal static class Frost
    {
        // What the window looks like right now, as pixels. DrawToBitmap asks
        // every control to print itself, so this works whether or not the
        // window is on top of anything.
        public static Bitmap Snapshot(Control c)
        {
            int w = Math.Max(1, c.Width), h = Math.Max(1, c.Height);
            Bitmap b = new Bitmap(w, h);
            try { c.DrawToBitmap(b, new Rectangle(0, 0, w, h)); }
            catch (Exception)
            {
                using (Graphics g = Graphics.FromImage(b)) g.Clear(Theme.Bg);
            }
            return b;
        }

        // Down to a tenth and back, twice. Each round trip averages every
        // pixel with its neighbours; two of them is enough that no letter
        // survives, which is the whole point - you should be able to tell the
        // app is still there without being able to read it.
        public static Bitmap Blur(Bitmap src)
        {
            Bitmap a = Scale(src, 0.14f);
            Bitmap b = Scale(a, 0.55f);
            a.Dispose();
            Bitmap c = Scale(b, 2.2f);
            b.Dispose();
            Bitmap big = Scale(c, (float)src.Width / c.Width);
            c.Dispose();
            return big;
        }

        private static Bitmap Scale(Bitmap src, float f)
        {
            int w = Math.Max(2, (int)(src.Width * f));
            int h = Math.Max(2, (int)(src.Height * f));
            Bitmap dst = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                // Drawn one pixel proud on every side: bilinear clamps at the
                // edge otherwise and leaves a hard rim round the blur.
                g.DrawImage(src, new Rectangle(-1, -1, w + 2, h + 2));
            }
            return dst;
        }

        // A rounded rectangle, since every card and tile on this pane wants one
        // and GraphicsPath has no such constructor.
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // One theme, shown as what it would do to this window rather than as a row
    // of colour chips: a title line, the two mode buttons, and three lines of
    // log. You recognise the app in it, which is the only useful question a
    // preview can answer.
    internal sealed class Swatch : Control
    {
        private readonly Palette p;
        private bool hot;

        public bool Selected;
        public Palette Look { get { return p; } }

        public Swatch(Palette look)
        {
            p = look;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(168, 132);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle all = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath path = Frost.Round(all, 8))
            using (SolidBrush fill = new SolidBrush(p.Bg))
                g.FillPath(fill, path);

            // The mock. Everything inside is drawn from the theme being shown,
            // never from the theme currently in force.
            int m = 12;
            using (SolidBrush accent = new SolidBrush(p.Accent))
            using (Font f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                g.DrawString("IDLE MASTER", f, accent, m, m - 2);

            using (SolidBrush dim = new SolidBrush(p.Dim))
                g.FillRectangle(dim, m, m + 15, 62, 2);

            Rectangle good = new Rectangle(m, m + 25, 66, 18);
            Rectangle bad = new Rectangle(m + 72, m + 25, 66, 18);
            using (SolidBrush b1 = new SolidBrush(p.Good)) g.FillRectangle(b1, good);
            using (SolidBrush b2 = new SolidBrush(p.Danger)) g.FillRectangle(b2, bad);
            using (SolidBrush on = new SolidBrush(p.OnAccent))
            using (Font f = new Font("Segoe UI", 6f, FontStyle.Bold))
            {
                g.DrawString("BOOST", f, on, good.X + 6, good.Y + 4);
                g.DrawString("IDLE", f, on, bad.X + 6, bad.Y + 4);
            }

            // the gauge, half full
            Rectangle track = new Rectangle(m, m + 49, 138, 7);
            using (SolidBrush t = new SolidBrush(p.Track)) g.FillRectangle(t, track);
            using (SolidBrush v = new SolidBrush(p.GaugeOk))
                g.FillRectangle(v, track.X, track.Y, (int)(track.Width * 0.62f), track.Height);

            // the console
            Rectangle log = new Rectangle(m, m + 62, 138, 32);
            using (SolidBrush lb = new SolidBrush(p.LogBg)) g.FillRectangle(lb, log);
            using (SolidBrush lf = new SolidBrush(p.LogFg))
            {
                g.FillRectangle(lf, log.X + 5, log.Y + 6, 110, 2);
                g.FillRectangle(lf, log.X + 5, log.Y + 13, 84, 2);
                g.FillRectangle(lf, log.X + 5, log.Y + 20, 99, 2);
            }

            // the name, on the theme's own panel colour
            Rectangle strip = new Rectangle(1, Height - 25, Width - 3, 23);
            using (SolidBrush pan = new SolidBrush(p.Panel)) g.FillRectangle(pan, strip);
            using (SolidBrush fg = new SolidBrush(p.Fg))
            using (Font f = new Font("Segoe UI", 8.25f, FontStyle.Bold))
                g.DrawString(p.Name, f, fg, strip.X + 8, strip.Y + 4);

            Color edge = Selected ? p.Accent : (hot ? p.Dim : p.Track);
            using (GraphicsPath path = Frost.Round(all, 8))
            using (Pen pen = new Pen(edge, Selected ? 2f : 1f))
                g.DrawPath(pen, path);
        }
    }

    // The pane. A child of the window rather than a window of its own, so it
    // moves, sizes and closes with it and cannot be left behind on the desktop.
    internal sealed class ThemeGate : Panel, IRestyle
    {
        private readonly Form owner;
        private readonly Config cfg;
        private readonly Action<string> log;
        private readonly bool intro;              // first run: the two answers are the point

        private Bitmap frost;
        private Panel card;
        private Label title, blurb, about, footer;
        private FlowLayoutPanel rack;
        private Button use, more, later, folder;
        private Palette picked;
        private bool busy;

        public static bool Open(Form owner, Config cfg, bool intro, Action<string> log)
        {
            foreach (Control c in owner.Controls)
                if (c is ThemeGate) return false;         // already up
            ThemeGate g = new ThemeGate(owner, cfg, intro, log);
            owner.Controls.Add(g);
            g.Dock = DockStyle.Fill;
            g.BringToFront();
            g.Focus();
            return true;
        }

        private ThemeGate(Form f, Config c, bool isIntro, Action<string> logger)
        {
            owner = f;
            cfg = c;
            intro = isIntro;
            log = logger != null ? logger : delegate(string s) { };

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint, true);

            // Taken before this panel is on screen, so what gets blurred is the
            // window as the user just saw it.
            using (Bitmap shot = Frost.Snapshot(owner))
                frost = Frost.Blur(shot);

            picked = Theme.Current;
            BuildCard();
            Restyle();
        }

        // ------------------------------------------------------------- the card

        private void BuildCard()
        {
            card = new Panel();
            card.Size = new Size(CardW, 414);
            card.Anchor = AnchorStyles.None;
            card.Paint += CardPaint;
            Controls.Add(card);

            title = new Label();
            title.AutoSize = false;
            title.SetBounds(28, 26, 544, 30);
            card.Controls.Add(title);

            blurb = new Label();
            blurb.AutoSize = false;
            blurb.SetBounds(28, 58, 544, 46);
            card.Controls.Add(blurb);

            rack = new FlowLayoutPanel();
            rack.SetBounds(24, 110, 552, TileH + 8);
            rack.AutoScroll = true;
            rack.WrapContents = true;
            rack.FlowDirection = FlowDirection.LeftToRight;
            card.Controls.Add(rack);

            about = new Label();
            about.AutoSize = false;
            about.Size = new Size(544, 34);
            card.Controls.Add(about);

            use = new Button();
            use.Size = new Size(214, 36);
            use.Click += delegate { Keep(); };
            card.Controls.Add(use);

            more = new Button();
            more.Size = new Size(178, 36);
            more.Click += delegate { GetMore(); };
            card.Controls.Add(more);

            later = new Button();
            later.Size = new Size(132, 36);
            later.Click += delegate { Dismiss(); };
            card.Controls.Add(later);

            footer = new Label();
            footer.AutoSize = false;
            footer.Size = new Size(400, 40);
            card.Controls.Add(footer);

            folder = new Button();
            folder.Size = new Size(132, 28);
            folder.Click += delegate { OpenFolder(); };
            card.Controls.Add(folder);

            Fill();
        }

        // The card is as tall as it needs to be. One row of tiles to start
        // with; a downloaded bundle pushes it to two and the card grows under
        // it rather than hiding the rest behind a scrollbar.
        private const int CardW = 600;
        private const int TileH = 140;          // a Swatch plus its margins
        private int rule2;                      // where the lower hairline goes

        private void Relayout()
        {
            int rows = (rack.Controls.Count + 2) / 3;
            if (rows < 1) rows = 1;
            if (rows > 2) rows = 2;
            rack.Height = rows * TileH + 8;

            int y = rack.Bottom + 8;
            about.Location = new Point(28, y);
            y = about.Bottom + 4;
            rule2 = y;
            y += 10;

            use.Location = new Point(28, y);
            more.Location = new Point(252, y);
            later.Location = new Point(440, y);
            y += use.Height + 12;

            footer.Location = new Point(28, y);
            folder.Location = new Point(440, y + 2);

            card.Size = new Size(CardW, footer.Bottom + 20);
            Centre();
            card.Invalidate();
        }

        // Everything the card says, in the palette currently being previewed.
        // Called again on every selection, which is what makes clicking a tile
        // feel like trying the theme on rather than ticking a radio button.
        public void Restyle()
        {
            Palette p = picked != null ? picked : Theme.Current;

            title.Text = intro ? "PICK A LOOK" : "THEME";
            title.Font = new Font(Safe(p.UiFont), p.UiSize * 1.55f, FontStyle.Bold);
            title.ForeColor = p.Accent;
            title.BackColor = p.Panel;

            blurb.Text = intro
                ? "Idle Master runs the same whichever one you pick - this is only paint. "
                  + "Minimalistic is what you are looking at through the glass. There is a "
                  + "second one built in, and more on the release post."
                : "Click one to try it on. The card changes first; the window follows when you keep it.";
            blurb.Font = new Font(Safe(p.UiFont), p.UiSize * 0.92f);
            blurb.ForeColor = p.Dim;
            blurb.BackColor = p.Panel;

            rack.BackColor = p.Panel;

            about.Text = p.About + (p.Author.Length > 0 ? "   - " + p.Author : "");
            about.Font = new Font(Safe(p.UiFont), p.UiSize * 0.92f, FontStyle.Italic);
            about.ForeColor = p.ListFg;
            about.BackColor = p.Panel;

            Slab(use, p.Good, p.OnAccent, p);
            use.Text = busy ? "working..." : "Use " + p.Name;
            use.Enabled = !busy;

            Slab(more, p.Neutral, p.Fg, p);
            more.Text = busy ? "downloading..." : "Get more themes";
            more.Enabled = !busy;

            Slab(later, p.Neutral, p.Fg, p);
            later.Text = intro ? "Not now" : "Close";
            later.Enabled = !busy;

            Slab(folder, p.Neutral, p.ListFg, p);
            folder.Text = "Open themes\\";
            folder.Enabled = !busy;

            // The one thing worth saying out loud about the download: it is a
            // separate file on purpose. Three themes are inside the exe and
            // that is where it stops - the installer stays one small download
            // instead of carrying a gallery most people will never open.
            footer.Text = "Themes are text files in themes\\ next to the exe - copy one, change the "
                        + "colours, restart. The extra ones are a separate download on purpose: the "
                        + "installer stays small and the app stays quick, so the gallery is fetched "
                        + "only if you want it.";
            footer.Font = new Font(Safe(p.UiFont), p.UiSize * 0.85f);
            footer.ForeColor = p.Dim;
            footer.BackColor = p.Panel;

            card.BackColor = p.Panel;
            foreach (Control c in rack.Controls)
            {
                Swatch s = c as Swatch;
                if (s != null) s.Selected = picked != null && s.Look.Key == picked.Key;
                c.Invalidate();
            }
            card.Invalidate();
        }

        private static string Safe(string family)
        {
            return string.IsNullOrEmpty(family) ? "Segoe UI" : family;
        }

        private static void Slab(Button b, Color back, Color fore, Palette p)
        {
            b.BackColor = back;
            b.ForeColor = fore;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Lift(back, 18);
            b.FlatAppearance.MouseDownBackColor = Theme.Lift(back, -12);
            b.UseVisualStyleBackColor = false;
            b.Font = new Font(Safe(p.UiFont), p.UiSize * 0.95f, FontStyle.Bold);
        }

        // Rebuilds the row of tiles from whatever is installed right now - which
        // is also what a finished download calls.
        private void Fill()
        {
            List<Palette> all = Themes.All();
            rack.Controls.Clear();
            foreach (Palette p in all)
            {
                Swatch s = new Swatch(p);
                s.Margin = new Padding(4, 4, 4, 4);
                Palette bound = p;
                s.Click += delegate { Choose(bound); };
                rack.Controls.Add(s);
            }
            if (picked != null)
            {
                Palette still = Themes.Find(picked.Name);
                picked = still != null ? still : (all.Count > 0 ? all[0] : picked);
            }
            Relayout();
        }

        private void Choose(Palette p)
        {
            picked = p;
            Restyle();
        }

        // ------------------------------------------------------------ the answers

        // Write it down, put it on, and hand the window back. The ini is the
        // record; the walk over the open forms is what makes it instant.
        private void Keep()
        {
            Palette p = picked != null ? picked : Themes.Minimalistic();
            try
            {
                IniFile ini = new IniFile();
                ini.SetSetting("Theme", p.Name);
                ini.SetSetting("ThemeIntro", "1");
                ini.Save();
                cfg.Theme = p.Name;
                cfg.ThemeIntro = true;
            }
            catch (Exception ex)
            {
                log("! could not remember the theme: " + ex.Message);
            }

            Theme.Apply(p);
            log("Theme: " + p.Name + (p.Builtin ? " (built in)" : " - " + p.File));
            Close();
        }

        // "Not now" is still an answer: the pane does not come back on its own
        // after this, because a prompt that reappears every start is a nag.
        // Settings > Theme is the door from here on.
        private void Dismiss()
        {
            if (intro)
            {
                try
                {
                    IniFile ini = new IniFile();
                    ini.SetSetting("ThemeIntro", "1");
                    ini.Save();
                    cfg.ThemeIntro = true;
                }
                catch (Exception) { }
                log("Theme: staying on " + Theme.Current.Name
                    + ". Settings > Theme changes it whenever you like.");
            }
            Close();
        }

        // The bundle, off the same release post the app updates from. On a
        // worker, because the window behind the glass is still ticking a gauge
        // and running a sentry and must not stop for a download.
        private void GetMore()
        {
            if (busy) return;
            busy = true;
            Restyle();

            Thread t = new Thread(delegate()
            {
                int n = 0;
                string err = null;
                try { n = Themes.Download(log); }
                catch (Exception ex) { err = ex.Message; }

                int got = n;
                string trouble = err;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { Landed(got, trouble); });
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Landed(int n, string err)
        {
            busy = false;
            if (err != null)
            {
                log("! theme bundle: " + err);
                MessageBox.Show(owner, err, "Idle Master - themes",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Restyle();
                return;
            }
            log("Theme bundle: " + n + " theme" + (n == 1 ? "" : "s") + " unpacked into " + Themes.Dir);
            Fill();
            Restyle();
        }

        private void OpenFolder()
        {
            try
            {
                Themes.Seed();
                Process.Start(new ProcessStartInfo(Themes.Dir) { UseShellExecute = true });
            }
            catch (Exception ex) { log("! could not open " + Themes.Dir + ": " + ex.Message); }
        }

        private void Close()
        {
            Form host = owner;
            if (host != null && !host.IsDisposed) host.Controls.Remove(this);
            Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && frost != null) { frost.Dispose(); frost = null; }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------- painting

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Centre();
        }

        private void Centre()
        {
            if (card == null) return;
            card.Location = new Point(Math.Max(0, (Width - card.Width) / 2),
                                      Math.Max(0, (Height - card.Height) / 2));
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Palette p = picked != null ? picked : Theme.Current;

            if (frost != null)
                g.DrawImage(frost, new Rectangle(0, 0, Width, Height));
            else
                using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            // The wash. Without it the card fights the blur for attention and
            // loses; with it the window reads as "behind glass" rather than
            // "broken".
            using (SolidBrush wash = new SolidBrush(Color.FromArgb(168, Theme.Bg)))
                g.FillRectangle(wash, ClientRectangle);

            if (card == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // A shadow under the card, so the glass has a thickness.
            Rectangle shade = new Rectangle(card.Left - 6, card.Top - 4, card.Width + 12, card.Height + 14);
            for (int i = 6; i >= 1; i--)
            {
                using (GraphicsPath path = Frost.Round(
                           new Rectangle(shade.X - i, shade.Y - i, shade.Width + i * 2, shade.Height + i * 2), 16 + i))
                using (Pen pen = new Pen(Color.FromArgb(10, 0, 0, 0), 2f))
                    g.DrawPath(pen, path);
            }

            using (Pen edge = new Pen(Color.FromArgb(90, p.Accent)))
                g.DrawRectangle(edge, card.Left - 1, card.Top - 1, card.Width + 1, card.Height + 1);
        }

        private void CardPaint(object sender, PaintEventArgs e)
        {
            Palette p = picked != null ? picked : Theme.Current;
            Graphics g = e.Graphics;

            // One hairline under the heading and one over the footer: the card
            // has three jobs and they should look like three.
            using (Pen line = new Pen(p.Track))
            {
                g.DrawLine(line, 28, 102, card.Width - 28, 102);
                if (rule2 > 0) g.DrawLine(line, 28, rule2, card.Width - 28, rule2);
            }
            using (SolidBrush a = new SolidBrush(p.Accent))
                g.FillRectangle(a, 0, 0, 4, card.Height);
        }
    }
}

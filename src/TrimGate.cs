// IDLE MASTER - the one-time notice about the RAM trim.
//
// Up to v0.25 the app trimmed working sets and purged the standby list at the
// end of every boost, and again every ten minutes for as long as the sentry was
// armed. That was wrong, and v0.26 turned it off - but only for people who
// install fresh. An update keeps idlemaster.ini exactly as it was found, which
// is the right promise to keep and also means every machine already running
// Idle Master carries the old default forward and goes on trimming.
//
// So it gets told, once, in the same frosted pane the theme picker uses: this
// is what it was doing, this is why it stopped being the default, here are two
// buttons. Answering either one writes TrimNotice=1 and it never comes back -
// a prompt that reappears every start is a nag, and the whole point of the
// change is to stop doing things to your machine that you did not ask for.
//
// A fresh install ships TrimNotice=1 in its ini and never sees this at all.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IdleMaster
{
    internal sealed class TrimGate : Panel, IRestyle
    {
        private readonly Form owner;
        private readonly Config cfg;
        private readonly Action<string> log;
        private readonly Action answered;         // let the window's own switch follow this

        private Bitmap frost;
        private Panel card;
        private Label title, blurb, body, footer;
        private Button off, keep;

        private const int CardW = 600;

        // Only ever opened by Ui when the ini says this machine is still on the
        // old behaviour AND has not been told. Returns false if a pane - this
        // one or the theme picker - already has the window.
        public static bool Open(Form owner, Config cfg, Action<string> log)
        {
            return Open(owner, cfg, log, null);
        }

        public static bool Open(Form owner, Config cfg, Action<string> log, Action answered)
        {
            foreach (Control c in owner.Controls)
                if (c is TrimGate || c is ThemeGate) return false;
            TrimGate g = new TrimGate(owner, cfg, log, answered);
            owner.Controls.Add(g);
            g.Dock = DockStyle.Fill;
            g.BringToFront();
            g.Focus();
            return true;
        }

        private TrimGate(Form f, Config c, Action<string> logger, Action onAnswered)
        {
            owner = f;
            cfg = c;
            log = logger != null ? logger : delegate(string s) { };
            answered = onAnswered;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint, true);

            using (Bitmap shot = Frost.Snapshot(owner))
                frost = Frost.Blur(shot);

            BuildCard();
            Restyle();
        }

        // ------------------------------------------------------------- the card

        private void BuildCard()
        {
            card = new Panel();
            card.Size = new Size(CardW, 360);
            card.Anchor = AnchorStyles.None;
            card.Paint += CardPaint;
            Controls.Add(card);

            title = new Label();
            title.AutoSize = false;
            title.SetBounds(28, 26, 544, 30);
            card.Controls.Add(title);

            blurb = new Label();
            blurb.AutoSize = false;
            blurb.SetBounds(28, 58, 544, 40);
            card.Controls.Add(blurb);

            body = new Label();
            body.AutoSize = false;
            body.Size = new Size(544, 120);
            card.Controls.Add(body);

            off = new Button();
            off.Size = new Size(268, 36);
            off.Click += delegate { Answer(true); };
            card.Controls.Add(off);

            keep = new Button();
            keep.Size = new Size(248, 36);
            keep.Click += delegate { Answer(false); };
            card.Controls.Add(keep);

            footer = new Label();
            footer.AutoSize = false;
            footer.Size = new Size(544, 40);
            card.Controls.Add(footer);
        }

        private int rule2;                      // where the lower hairline goes

        private void Relayout()
        {
            int y = 112;

            body.Location = new Point(28, y);
            body.Height = TextRenderer.MeasureText(body.Text, body.Font,
                new Size(body.Width, 0), TextFormatFlags.WordBreak).Height + 4;

            y = body.Bottom + 6;
            rule2 = y;
            y += 12;

            off.Location = new Point(28, y);
            keep.Location = new Point(28 + off.Width + 12, y);
            y += off.Height + 12;

            footer.Height = TextRenderer.MeasureText(footer.Text, footer.Font,
                new Size(footer.Width, 0), TextFormatFlags.WordBreak).Height + 4;
            footer.Location = new Point(28, y);

            card.Size = new Size(CardW, footer.Bottom + 20);
            Centre();
            card.Invalidate();
        }

        public void Restyle()
        {
            Palette p = Theme.Current;

            title.Text = "ABOUT THAT RAM TRIM";
            title.Font = new Font(Safe(p.UiFont), p.UiSize * 1.55f, FontStyle.Bold);
            title.ForeColor = p.Accent;
            title.BackColor = p.Panel;

            blurb.Text = "This copy has been squeezing every running process at the end of each "
                       + "boost, and again every " + cfg.SentryTrimMinutes + " minutes. It is no "
                       + "longer the default, and your settings were left alone.";
            blurb.Font = new Font(Safe(p.UiFont), p.UiSize * 0.92f);
            blurb.ForeColor = p.Dim;
            blurb.BackColor = p.Panel;

            // The honest version, in the order that makes it land: what it does,
            // why that is not free, and what it costs on this machine in
            // particular - which is the one whose whole job is a steady frame.
            body.Text =
                "Trimming does not free memory. It evicts pages from processes that are still "
              + "running and still wanted: the clean ones go to standby, the changed ones head "
              + "for the pagefile, and every one of them faults back the moment you touch the "
              + "app again. Purging the standby list on top turns those cheap returns into "
              + "reads from disk, for memory that was already available to whatever asked next."
              + "\r\n\r\n"
              + "So the megabytes it reported were never really freed - and the bill arrived "
              + "later, as a stutter when you went back to whatever had been squeezed. Every "
              + "sweep. On a machine that exists to hand Sunshine a steady frame, that is a "
              + "hitch you would blame on the network.";
            body.Font = new Font(Safe(p.UiFont), p.UiSize * 0.92f);
            body.ForeColor = p.ListFg;
            body.BackColor = p.Panel;

            Slab(off, p.Good, p.OnAccent, p);
            off.Text = "Stop trimming automatically";

            Slab(keep, p.Neutral, p.Fg, p);
            keep.Text = "Leave it as it is";

            footer.Text = "Either way, Trim RAM now stays on the window and still works - it is "
                        + "worth a click right before you launch something big, when you are "
                        + "leaving those apps anyway. Advanced settings has TrimWorkingSets and "
                        + "ClearStandbyList if you change your mind.";
            footer.Font = new Font(Safe(p.UiFont), p.UiSize * 0.85f);
            footer.ForeColor = p.Dim;
            footer.BackColor = p.Panel;

            card.BackColor = p.Panel;
            card.Invalidate();
            Relayout();
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

        // ------------------------------------------------------------ the answer

        // Both answers write TrimNotice=1: having been asked is the thing being
        // remembered, not which way it went. Turning it off writes the two
        // settings as well, into the ini line by line, so every comment the user
        // has in that file survives - and the running Config is updated in place
        // so the very next boost obeys it without a restart.
        private void Answer(bool turnOff)
        {
            try
            {
                IniFile ini = new IniFile();
                if (turnOff)
                {
                    ini.SetSetting("TrimWorkingSets", "0");
                    ini.SetSetting("ClearStandbyList", "0");
                }
                ini.SetSetting("TrimNotice", "1");
                ini.Save();

                if (turnOff)
                {
                    cfg.TrimWorkingSets = false;
                    cfg.ClearStandbyList = false;
                }
                cfg.TrimNotice = true;
            }
            catch (Exception ex)
            {
                log("! could not write idlemaster.ini: " + ex.Message);
            }

            if (turnOff)
                log("Trim: off. Boosts now report only what was actually closed, and the sentry "
                    + "no longer trims on a clock. Trim RAM now still works.");
            else
                log("Trim: left on. Working sets are still squeezed after every boost and every "
                    + cfg.SentryTrimMinutes + " minutes - the switch on the window turns it off.");

            if (answered != null)
                try { answered(); } catch (Exception) { }
            Close();
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
            Palette p = Theme.Current;

            if (frost != null)
                g.DrawImage(frost, new Rectangle(0, 0, Width, Height));
            else
                using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            using (SolidBrush wash = new SolidBrush(Color.FromArgb(168, Theme.Bg)))
                g.FillRectangle(wash, ClientRectangle);

            if (card == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

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
            Palette p = Theme.Current;
            Graphics g = e.Graphics;

            using (Pen line = new Pen(p.Track))
            {
                g.DrawLine(line, 28, 104, card.Width - 28, 104);
                if (rule2 > 0) g.DrawLine(line, 28, rule2, card.Width - 28, rule2);
            }
            using (SolidBrush a = new SolidBrush(p.Accent))
                g.FillRectangle(a, 0, 0, 4, card.Height);
        }
    }
}

// IDLE MASTER - the repeat dial, and the wheel it opens.
//
// The repeat loop used to be a checkbox, a spinner and a countdown label on a
// row of its own under BOOST NOW. It is one small dial on the face of the
// button now: the repeat arrow, the interval under it, and a hairline that
// fills as the next run comes round. Click it and a wheel of minutes drops
// out; pick one, or pick "off". Clicking the dial never boosts - it is a child
// of the button, so the button underneath never sees the click.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IdleMaster
{
    // ------------------------------------------------------------- the dial

    internal sealed class RepeatDial : Control
    {
        private const string Glyph = "↻";      // clockwise open circle arrow

        private readonly Font glyphFont = new Font("Segoe UI", 15f, FontStyle.Regular);
        private readonly Font numberFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        private readonly StringFormat center = new StringFormat();

        private int minutes;            // 0 = off
        private double progress;        // 0..1 through the current interval
        private bool hot;
        private bool down;

        public RepeatDial()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            BackColor = Theme.Good;
            Cursor = Cursors.Hand;
            TabStop = false;
            AccessibleName = "Repeat boost";
        }

        // 0 = the loop is off; the dial says so instead of a number.
        public int Minutes
        {
            get { return minutes; }
            set { if (minutes == value) return; minutes = value; Invalidate(); }
        }

        // How far through the current wait we are. Only redrawn when it moves
        // enough to be a different pixel, so the two-second tick is free.
        public double Progress
        {
            get { return progress; }
            set
            {
                double v = value < 0 ? 0 : (value > 1 ? 1 : value);
                if (Math.Abs(progress - v) * Math.Max(1, Width) < 1) return;
                progress = v;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        { hot = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        { down = true; Invalidate(); base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color face = down ? Theme.Lift(Theme.Good, -14)
                : (hot ? Theme.Lift(Theme.Good, 26) : Theme.Good);
            using (SolidBrush b = new SolidBrush(face))
                g.FillRectangle(b, ClientRectangle);

            bool on = minutes > 0;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Round(box, 6))
            using (Pen pen = new Pen(Color.FromArgb(on ? 130 : 70, 255, 255, 255)))
                g.DrawPath(pen, p);

            Color ink = Color.FromArgb(on ? 255 : 150, 255, 255, 255);
            using (SolidBrush b = new SolidBrush(ink))
            {
                g.DrawString(Glyph, glyphFont, b,
                    new RectangleF(0, 4, Width, 28), center);
                g.DrawString(on ? minutes.ToString() : "off", numberFont, b,
                    new RectangleF(0, 30, Width, 18), center);
            }

            // The hairline: the wait, drawn instead of written. Nothing when the
            // loop is off, so the dial stays two marks and no more.
            if (!on) return;
            int y = Height - 9, x0 = 9, x1 = Width - 9;
            using (Pen track = new Pen(Color.FromArgb(45, 255, 255, 255)))
                g.DrawLine(track, x0, y, x1, y);
            int w = (int)Math.Round((x1 - x0) * progress);
            if (w <= 0) return;
            using (Pen fill = new Pen(Theme.Accent))
                g.DrawLine(fill, x0, y, x0 + w, y);
        }

        private static GraphicsPath Round(Rectangle r, int radius)
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                glyphFont.Dispose();
                numberFont.Dispose();
                center.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------- the wheel

    // A little wheel of intervals under the dial. It is a dropdown, so clicking
    // anywhere else puts it away without choosing anything, and the mouse wheel
    // spins it because the thing inside is an ordinary list.
    internal static class RepeatWheel
    {
        private static readonly int[] Steps =
        { 0, 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440 };

        public static string Label(int m)
        {
            if (m <= 0) return "off";
            if (m < 60) return m + " min";
            if (m % 60 == 0) return (m / 60) + (m == 60 ? " hour" : " hours");
            return m + " min";
        }

        // Opens under 'anchor'. 'pick' runs only when something is chosen.
        public static void Open(Control anchor, int current, Action<int> pick)
        {
            List<int> values = new List<int>(Steps);
            if (current > 0 && !values.Contains(current)) { values.Add(current); values.Sort(); }

            ListBox list = new ListBox();
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = 26;
            list.IntegralHeight = false;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Panel;
            list.ForeColor = Theme.Fg;
            list.Font = Theme.Base();
            list.Size = new Size(146, 26 * 7);
            foreach (int v in values) list.Items.Add(v);

            list.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                int v = (int)list.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (SolidBrush b = new SolidBrush(sel ? Theme.Neutral : Theme.Panel))
                    e.Graphics.FillRectangle(b, e.Bounds);
                using (SolidBrush b = new SolidBrush(sel ? Theme.Accent : Theme.Fg))
                {
                    StringFormat f = new StringFormat();
                    f.LineAlignment = StringAlignment.Center;
                    e.Graphics.DrawString(Label(v), list.Font, b,
                        new RectangleF(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), f);
                    f.Dispose();
                }
            };

            int at = values.IndexOf(current > 0 ? current : 0);
            if (at < 0) at = 0;
            list.SelectedIndex = at;
            list.TopIndex = Math.Max(0, at - 3);

            ToolStripDropDown drop = new ToolStripDropDown();
            drop.AutoSize = false;
            drop.Padding = new Padding(1);
            drop.BackColor = Theme.Track;
            drop.DropShadowEnabled = true;

            ToolStripControlHost host = new ToolStripControlHost(list);
            host.AutoSize = false;
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            host.Size = list.Size;
            drop.Items.Add(host);
            drop.Size = new Size(list.Width + 2, list.Height + 2);

            list.MouseClick += delegate(object s, MouseEventArgs e)
            {
                int i = list.IndexFromPoint(e.Location);
                if (i < 0) return;
                int v = (int)list.Items[i];
                drop.Close();
                pick(v);
            };
            list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { drop.Close(); return; }
                if (e.KeyCode != Keys.Enter || list.SelectedIndex < 0) return;
                int v = (int)list.Items[list.SelectedIndex];
                drop.Close();
                pick(v);
            };
            drop.Closed += delegate { drop.Dispose(); };

            drop.Show(anchor, new Point(0, anchor.Height + 6));
            list.Focus();
        }
    }
}

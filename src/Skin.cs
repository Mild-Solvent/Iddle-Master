// IDLE MASTER - the parts of a look that are shape rather than colour.
//
// Themes.cs made the palette data. This makes the *chrome* data: corner radius,
// gradient depth, the bloom under a primary button, the outline, and whether
// the window wears Windows' own title bar or one the theme draws itself.
//
// The whole file is built around one rule: **every knob here defaults to off,
// and off means the app renders exactly as it always has.** A theme that says
// nothing about radius or gradient gets a stock FlatStyle.Flat button, painted
// by WinForms, not by us - not "our code configured to look the same", but
// literally the same code path. That is what keeps Minimalistic pixel-identical
// while Cortex gets to look like something else entirely, and it is why the
// regression test can still demand zero differing pixels.
//
//   SkinButton   a Button that owner-draws only when the theme asks
//   CaptionBar   the title strip a theme draws instead of Windows'
//   Chrome       borderless-window plumbing: hit-testing, snap, maximise
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleMaster
{
    // ------------------------------------------------------------------ button

    // Stock until the theme says otherwise. The moment a palette asks for a
    // radius, a gradient, a glow or a border, this takes over its own painting;
    // until then UserPaint is off and WinForms draws the button, which is the
    // only way to be certain the default look did not drift by a shade.
    internal sealed class SkinButton : Button, IRestyle
    {
        private bool skinned;
        private bool hot, down;

        public SkinButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Reskin();
        }

        // Called on construction and again whenever the look changes. Turning
        // UserPaint on and off at runtime needs the handle rebuilt, so only
        // touch it when the answer actually changed.
        public void Restyle() { Reskin(); }

        public void Reskin()
        {
            bool want = Theme.Current.Shaped;
            if (want == skinned) { Invalidate(); return; }
            skinned = want;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, want);
            if (IsHandleCreated) RecreateHandle();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; if (skinned) Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = down = false; if (skinned) Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; if (skinned) Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; if (skinned) Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!skinned) { base.OnPaint(e); return; }

            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill = BackColor;
            if (!Enabled) fill = Theme.Lift(fill, -18);
            else if (down) fill = Theme.Lift(fill, -12);
            else if (hot) fill = Theme.Lift(fill, 18);

            // The parent shows through the rounded corners, so start from it.
            using (SolidBrush bg = new SolidBrush(Parent != null ? Parent.BackColor : p.Bg))
                g.FillRectangle(bg, ClientRectangle);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width < 2 || r.Height < 2) return;

            using (GraphicsPath path = Frost.Round(r, p.Radius))
            {
                if (p.Gradient > 0)
                {
                    // Lit from above, the way a physical key is. The bottom is
                    // the theme's own colour so a flat-ish gradient still reads
                    // as the shade the palette named.
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                               new Rectangle(r.X, r.Y, r.Width, r.Height + 1),
                               Theme.Lift(fill, p.Gradient), fill, LinearGradientMode.Vertical))
                        g.FillPath(lg, path);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                }
            }

            // The bloom: rings drawn just inside the edge, fading inwards. A
            // glow that spilled outside would need to paint over its neighbours,
            // which a child control cannot do - so it blooms in.
            if (p.Glow > 0)
            {
                int a = hot ? Math.Min(255, p.Glow + 45) : p.Glow;
                for (int i = 0; i < 3; i++)
                {
                    int alpha = (int)(a * (i == 0 ? 1.0 : i == 1 ? 0.5 : 0.22));
                    if (alpha <= 0) continue;
                    Rectangle ring = new Rectangle(r.X + i, r.Y + i, r.Width - i * 2, r.Height - i * 2);
                    if (ring.Width < 2 || ring.Height < 2) break;
                    using (GraphicsPath path = Frost.Round(ring, Math.Max(0, p.Radius - i)))
                    using (Pen pen = new Pen(Color.FromArgb(alpha, p.Accent)))
                        g.DrawPath(pen, path);
                }
            }

            if (p.BorderWidth > 0)
            {
                Color edge = p.Border.IsEmpty ? Theme.Lift(fill, 40) : p.Border;
                using (GraphicsPath path = Frost.Round(r, p.Radius))
                using (Pen pen = new Pen(edge, p.BorderWidth))
                    g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, Text, Font, Inner(), Enabled ? ForeColor : Theme.Dim, Flags());
        }

        private Rectangle Inner()
        {
            Rectangle r = ClientRectangle;
            r.X += Padding.Left; r.Width -= Padding.Horizontal;
            r.Y += Padding.Top;  r.Height -= Padding.Vertical;
            return r;
        }

        // WinForms' own button text comes out of TextRenderer too, so matching
        // its flags is what keeps a skinned button from re-wrapping its label
        // differently from the unskinned one beside it.
        private TextFormatFlags Flags()
        {
            TextFormatFlags f = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
            switch (TextAlign)
            {
                case ContentAlignment.TopLeft:      f |= TextFormatFlags.Top | TextFormatFlags.Left; break;
                case ContentAlignment.TopCenter:    f |= TextFormatFlags.Top | TextFormatFlags.HorizontalCenter; break;
                case ContentAlignment.TopRight:     f |= TextFormatFlags.Top | TextFormatFlags.Right; break;
                case ContentAlignment.MiddleLeft:   f |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left; break;
                case ContentAlignment.MiddleRight:  f |= TextFormatFlags.VerticalCenter | TextFormatFlags.Right; break;
                case ContentAlignment.BottomLeft:   f |= TextFormatFlags.Bottom | TextFormatFlags.Left; break;
                case ContentAlignment.BottomCenter: f |= TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter; break;
                case ContentAlignment.BottomRight:  f |= TextFormatFlags.Bottom | TextFormatFlags.Right; break;
                default: f |= TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter; break;
            }
            return f;
        }
    }

    // ----------------------------------------------------------------- caption

    // The title strip a theme draws in place of Windows'. Three glyphs on the
    // right, drawn as lines and rectangles rather than a font, because the
    // Segoe MDL2 icons Windows uses are not on every machine this might run on
    // and a missing glyph is a tofu box where the close button should be.
    internal sealed class CaptionBar : Control, IRestyle
    {
        public const int H = 34;

        private readonly Form host;
        private int hotBox = -1;            // 0 minimise, 1 maximise, 2 close

        public CaptionBar(Form f)
        {
            host = f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = H;
            Dock = DockStyle.Top;
        }

        public void Restyle() { Invalidate(); }

        private Rectangle Box(int i)
        {
            int w = 46, h = Height;
            return new Rectangle(Width - w * (3 - i), 0, w, h);
        }

        private int BoxAt(Point p)
        {
            for (int i = 0; i < 3; i++) if (Box(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int was = hotBox;
            hotBox = BoxAt(e.Location);
            if (was != hotBox) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hotBox != -1) { hotBox = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = BoxAt(e.Location);
            if (i == 0) { host.WindowState = FormWindowState.Minimized; return; }
            if (i == 1) { Chrome.ToggleMax(host); return; }
            if (i == 2) { host.Close(); return; }

            // Hand the drag to Windows rather than moving the form by hand:
            // this is what gets Aero snap, the shake gesture, and the
            // multi-monitor edge behaviour for free.
            if (e.Button == MouseButtons.Left) Chrome.BeginDrag(host);
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (BoxAt(e.Location) < 0) Chrome.ToggleMax(host);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette p = Theme.Current;
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(p.Caption.IsEmpty ? p.Panel : p.Caption))
                g.FillRectangle(b, ClientRectangle);

            try
            {
                using (Icon small = new Icon(App.Icon, 16, 16))
                    g.DrawIcon(small, new Rectangle(10, (Height - 16) / 2, 16, 16));
            }
            catch (Exception) { }

            TextRenderer.DrawText(g, host.Text, Theme.Bold(),
                new Rectangle(34, 0, Width - 180, Height), p.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            for (int i = 0; i < 3; i++)
            {
                Rectangle r = Box(i);
                if (hotBox == i)
                    using (SolidBrush b = new SolidBrush(i == 2 ? p.Danger : Theme.Lift(p.Panel, 22)))
                        g.FillRectangle(b, r);

                Color ink = (hotBox == i && i == 2) ? p.OnAccent : p.Fg;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                using (Pen pen = new Pen(ink, 1.2f))
                {
                    if (i == 0) g.DrawLine(pen, cx - 5, cy + 3, cx + 5, cy + 3);
                    else if (i == 1)
                    {
                        if (host.WindowState == FormWindowState.Maximized)
                        {
                            g.DrawRectangle(pen, cx - 5, cy - 2, 7, 7);
                            g.DrawLine(pen, cx - 2, cy - 5, cx + 5, cy - 5);
                            g.DrawLine(pen, cx + 5, cy - 5, cx + 5, cy + 2);
                        }
                        else g.DrawRectangle(pen, cx - 5, cy - 4, 9, 9);
                    }
                    else
                    {
                        g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                        g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                    }
                }
            }

            using (Pen line = new Pen(p.Track))
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    // ------------------------------------------------------------------ chrome

    // Borderless-window plumbing. A FormBorderStyle.None window has no
    // non-client area, so Windows stops offering the eight resize grips and the
    // snap behaviour that come with a real frame - WM_NCHITTEST is where you
    // hand them back, by claiming the outer few pixels of the client area are
    // really the border.
    internal static class Chrome
    {
        public const int Grip = 6;          // how far in from the edge still resizes

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCLBUTTONDOWN = 0x00A1;

        private const int HTCLIENT = 1, HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        public static bool Wanted
        {
            get { return string.Equals(Theme.Current.Chrome, "custom", StringComparison.OrdinalIgnoreCase); }
        }

        // Puts the theme's own title bar on a window and takes Windows' away.
        // Everything already on the form slides down to make room, which is why
        // this runs at the end of a constructor rather than the start.
        public static CaptionBar Install(Form f)
        {
            f.FormBorderStyle = FormBorderStyle.None;

            foreach (Control c in f.Controls) c.Top += CaptionBar.H;
            f.Height += CaptionBar.H;
            if (!f.MinimumSize.IsEmpty)
                f.MinimumSize = new Size(f.MinimumSize.Width, f.MinimumSize.Height + CaptionBar.H);

            CaptionBar bar = new CaptionBar(f);
            f.Controls.Add(bar);
            bar.BringToFront();
            return bar;
        }

        public static void BeginDrag(Form f)
        {
            if (f.WindowState == FormWindowState.Maximized) return;
            try
            {
                ReleaseCapture();
                SendMessage(f.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch (Exception) { }
        }

        public static void ToggleMax(Form f)
        {
            f.WindowState = f.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        // Returns true if it handled the message. Call it first thing in the
        // form's WndProc, and only while the theme wants custom chrome - a
        // system-framed window must see none of this.
        public static bool WndProc(Form f, ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HitTest(f, m.LParam);
                return true;
            }
            if (m.Msg == WM_GETMINMAXINFO)
            {
                return MaxToWorkArea(f, ref m);
            }
            return false;
        }

        private static int HitTest(Form f, IntPtr lparam)
        {
            if (f.WindowState == FormWindowState.Maximized) return HTCLIENT;

            int raw = lparam.ToInt32();
            Point p = f.PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
            bool left = p.X <= Grip, right = p.X >= f.ClientSize.Width - Grip;
            bool top = p.Y <= Grip, bottom = p.Y >= f.ClientSize.Height - Grip;

            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTCLIENT;
        }

        // A borderless window maximises over the taskbar unless it is told the
        // work area explicitly. This is that telling.
        private static bool MaxToWorkArea(Form f, ref Message m)
        {
            try
            {
                IntPtr mon = MonitorFromWindow(f.Handle, 2 /* NEAREST */);
                if (mon == IntPtr.Zero) return false;

                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return false;

                MINMAXINFO mm = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                mm.ptMaxPosition.x = mi.rcWork.Left - mi.rcMonitor.Left;
                mm.ptMaxPosition.y = mi.rcWork.Top - mi.rcMonitor.Top;
                mm.ptMaxSize.x = mi.rcWork.Right - mi.rcWork.Left;
                mm.ptMaxSize.y = mi.rcWork.Bottom - mi.rcWork.Top;
                mm.ptMinTrackSize.x = f.MinimumSize.Width;
                mm.ptMinTrackSize.y = f.MinimumSize.Height;
                Marshal.StructureToPtr(mm, m.LParam, false);
                m.Result = IntPtr.Zero;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}

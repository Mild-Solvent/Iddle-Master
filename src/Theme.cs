// IDLE MASTER - the one place the dark theme lives. Every form pulls its
// colours, fonts, and button styling from here instead of carrying its own
// copy of the palette.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace IdleMaster
{
    internal static class Theme
    {
        // ---- palette: "Ice"
        //
        // One accent hue. Steel blue carries everything positive - titles, the
        // log, the boost button, the sentry line. The neutrals are the same gray
        // ladder tinted cold, and red is reserved for absolute idle and for
        // destroying things. Nothing else gets a colour of its own.

        public static readonly Color Bg      = Color.FromArgb(17, 19, 24);
        public static readonly Color Panel   = Color.FromArgb(26, 29, 37);
        public static readonly Color Input   = Color.FromArgb(13, 15, 19);
        public static readonly Color LogBg   = Color.FromArgb(11, 13, 17);
        public static readonly Color LogFg   = Color.FromArgb(168, 203, 232);
        public static readonly Color ListFg  = Color.FromArgb(174, 200, 222);
        public static readonly Color Fg      = Color.FromArgb(226, 230, 236);
        public static readonly Color Dim     = Color.FromArgb(120, 128, 140);
        public static readonly Color Accent  = Color.FromArgb(143, 193, 240);
        public static readonly Color Good    = Color.FromArgb(30, 78, 120);     // primary action (boost)
        public static readonly Color Danger  = Color.FromArgb(110, 40, 48);
        public static readonly Color Neutral = Color.FromArgb(35, 40, 51);
        public static readonly Color Warn    = Color.FromArgb(208, 132, 132);   // soft red - failures, idle tag
        public static readonly Color Track   = Color.FromArgb(35, 40, 51);

        // The one green in the palette, and it means exactly one thing: a newer
        // release is sitting there waiting for a click. Nothing else in the app
        // is allowed to go green, so the corner arrow turning colour IS the news.
        public static readonly Color Ready   = Color.FromArgb(56, 170, 104);

        // Blue while fine, reddening as RAM runs out - the only place a colour
        // change is the message itself.
        public static readonly Color GaugeOk   = Color.FromArgb(61, 126, 191);
        public static readonly Color GaugeWarn = Color.FromArgb(176, 96, 96);
        public static readonly Color GaugeBad  = Color.FromArgb(200, 72, 72);

        // ---- fonts

        public static Font Base()  { return new Font("Segoe UI", 9f); }
        public static Font Bold()  { return new Font("Segoe UI", 9f, FontStyle.Bold); }
        public static Font Small() { return new Font("Segoe UI", 8f); }
        public static Font Big()   { return new Font("Segoe UI", 13f, FontStyle.Bold); }
        public static Font Title() { return new Font("Segoe UI", 20f, FontStyle.Bold); }
        public static Font Mono()  { return new Font("Consolas", 9f); }

        // ---- form setup

        // Colours, base font, and DPI scaling in one call. Must run before any
        // controls are added, or AutoScale measures the wrong baseline.
        public static void Form(Form f)
        {
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.BackColor = Bg;
            f.ForeColor = Fg;
            f.Font = Base();
            try { f.Icon = App.Icon; } catch (Exception) { }
        }

        // ---- controls

        // Hover/press shades are derived, not hand-picked, so every button
        // reacts to the mouse without each call site thinking about it.
        public static Color Lift(Color c, int d)
        {
            return Color.FromArgb(Cap(c.R + d), Cap(c.G + d), Cap(c.B + d));
        }

        private static int Cap(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        public static Button Button(string text, Color back, Color fore)
        {
            Button b = new Button();
            b.Text = text;
            b.BackColor = back;
            b.ForeColor = fore;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Lift(back, 18);
            b.FlatAppearance.MouseDownBackColor = Lift(back, -12);
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static Button Action(string text)   { return Button(text, Good, Color.White); }
        public static Button Quiet(string text)    { return Button(text, Neutral, Fg); }
        public static Button Dangerous(string text){ return Button(text, Danger, Color.White); }

        public static void Input_(Control c)
        {
            c.BackColor = Input;
            c.ForeColor = Fg;
            TextBox t = c as TextBox;
            if (t != null) t.BorderStyle = BorderStyle.FixedSingle;
            NumericUpDown n = c as NumericUpDown;
            if (n != null) n.BorderStyle = BorderStyle.FixedSingle;
            CheckedListBox l = c as CheckedListBox;
            if (l != null) { l.BorderStyle = BorderStyle.FixedSingle; l.ForeColor = ListFg; }
        }

        public static Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Accent;
            l.Font = Bold();
            return l;
        }

        public static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Dim;
            return l;
        }

        // Context menus (tray + the process list) get the same dark treatment.
        public static void Menu(ContextMenuStrip m)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new DarkTable());
            m.BackColor = Panel;
            m.ForeColor = Fg;
            m.ShowImageMargin = false;
        }

        private sealed class DarkTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Panel; } }
            public override Color MenuItemSelected { get { return Neutral; } }
            public override Color MenuItemSelectedGradientBegin { get { return Neutral; } }
            public override Color MenuItemSelectedGradientEnd { get { return Neutral; } }
            public override Color MenuItemBorder { get { return Neutral; } }
            public override Color MenuBorder { get { return Track; } }
            public override Color ImageMarginGradientBegin { get { return Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Panel; } }
            public override Color ImageMarginGradientEnd { get { return Panel; } }
            public override Color SeparatorDark { get { return Track; } }
            public override Color SeparatorLight { get { return Track; } }
        }
    }
}

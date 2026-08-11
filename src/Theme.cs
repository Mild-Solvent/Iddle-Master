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
        // ---- palette

        public static readonly Color Bg      = Color.FromArgb(18, 18, 22);
        public static readonly Color Panel   = Color.FromArgb(24, 24, 30);
        public static readonly Color Input   = Color.FromArgb(14, 14, 18);
        public static readonly Color LogBg   = Color.FromArgb(12, 12, 15);
        public static readonly Color LogFg   = Color.FromArgb(180, 220, 190);
        public static readonly Color ListFg  = Color.FromArgb(210, 225, 215);
        public static readonly Color Fg      = Color.FromArgb(225, 225, 232);
        public static readonly Color Dim     = Color.FromArgb(120, 120, 132);
        public static readonly Color Accent  = Color.FromArgb(120, 200, 255);
        public static readonly Color Good    = Color.FromArgb(28, 92, 58);
        public static readonly Color Danger  = Color.FromArgb(110, 40, 40);
        public static readonly Color Neutral = Color.FromArgb(42, 42, 52);
        public static readonly Color Warn    = Color.FromArgb(220, 140, 80);
        public static readonly Color Track   = Color.FromArgb(38, 38, 46);

        public static readonly Color GaugeOk   = Color.FromArgb(90, 190, 120);
        public static readonly Color GaugeWarn = Color.FromArgb(220, 180, 70);
        public static readonly Color GaugeBad  = Color.FromArgb(220, 80, 80);

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

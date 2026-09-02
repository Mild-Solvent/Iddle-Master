// IDLE MASTER - the one place a look lives. Every form pulls its colours,
// fonts, and button styling from here instead of carrying its own copy of the
// palette, which is what makes swapping the whole look a single assignment.
//
// Two halves:
//
//   Palette   one look, as data - nineteen colours and two font families.
//             Themes.cs reads these out of .imtheme files on disk.
//   Theme     the facade every window already calls (Theme.Fg, Theme.Quiet,
//             Theme.Form). These used to be constants; now they read whichever
//             Palette is current, so nothing else in the app had to change.
//
// Apply() also repaints what is already on screen: it maps the outgoing
// palette's colours onto the incoming one and walks every open window swapping
// them, so picking a theme is instant instead of "restart to see it".
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace IdleMaster
{
    // A control that colours itself by hand - a state colour, a derived shade,
    // anything the generic swap below cannot know about - says so with this,
    // and gets told when the look changed.
    internal interface IRestyle
    {
        void Restyle();
    }

    // One look. Nothing here is behaviour: a theme is nineteen colours, two
    // font families, and the three lines that say who wrote it.
    internal sealed class Palette
    {
        public string Name = "Untitled";
        public string Author = "";
        public string About = "";

        public Color Bg      = Color.FromArgb(17, 19, 24);
        public Color Panel   = Color.FromArgb(26, 29, 37);
        public Color Input   = Color.FromArgb(13, 15, 19);
        public Color LogBg   = Color.FromArgb(11, 13, 17);
        public Color LogFg   = Color.FromArgb(168, 203, 232);
        public Color ListFg  = Color.FromArgb(174, 200, 222);
        public Color Fg      = Color.FromArgb(226, 230, 236);
        public Color Dim     = Color.FromArgb(120, 128, 140);
        public Color Accent  = Color.FromArgb(143, 193, 240);
        public Color Good    = Color.FromArgb(30, 78, 120);
        public Color Danger  = Color.FromArgb(110, 40, 48);
        public Color Neutral = Color.FromArgb(35, 40, 51);
        public Color Warn    = Color.FromArgb(208, 132, 132);
        public Color Track   = Color.FromArgb(35, 40, 51);
        public Color OnAccent = Color.White;        // text on top of Good / Danger

        // Reserved for exactly one thing: a newer release is sitting there
        // waiting for a click, and the corner arrow turning this colour IS the
        // news. Nothing else in the app is allowed to wear it - which is why a
        // theme picks it rather than inheriting Accent. In a green theme it had
        // better not be green.
        public Color Ready = Color.FromArgb(56, 170, 104);

        public Color GaugeOk   = Color.FromArgb(61, 126, 191);
        public Color GaugeWarn = Color.FromArgb(176, 96, 96);
        public Color GaugeBad  = Color.FromArgb(200, 72, 72);

        public string UiFont   = "Segoe UI";
        public string MonoFont = "Consolas";
        public float  UiSize   = 9f;
        public float  MonoSize = 9f;

        // ---- shape
        //
        // Every one of these is zero or "system" by default, and that default
        // is not "our painting code, configured to look flat" - it is WinForms
        // painting the button, the same code path the app has always used. A
        // theme that mentions none of them cannot look even a shade different
        // from the way it did before any of this existed.

        public int Radius = 0;              // corner radius on buttons
        public int Gradient = 0;            // how much lighter the top of a slab is; 0 = flat
        public int Glow = 0;                // accent bloom inside the edge, 0-255
        public int BorderWidth = 0;         // outline on every button
        public Color Border = Color.Empty;  // ...in this colour. Empty = derived from the fill.

        public string Chrome = "system";    // "system" or "custom" title bar
        public Color Caption = Color.Empty; // the custom strip's background. Empty = Panel.

        // Is any of it on? One question, asked on every button paint, so it
        // stays a field comparison rather than anything cleverer.
        public bool Shaped
        {
            get { return Radius > 0 || Gradient > 0 || Glow > 0 || BorderWidth > 0; }
        }

        // Where this one came from: a built-in, or a file somebody can edit.
        public string File = "";
        public bool Builtin;

        public string Key
        {
            get { return (Name == null ? "" : Name.Trim().ToLowerInvariant()); }
        }

        // The colours in a fixed order, so one palette can be lined up against
        // another - which is the whole trick behind repainting live.
        public Color[] Ramp()
        {
            return new Color[]
            {
                Bg, Panel, Input, LogBg, LogFg, ListFg, Fg, Dim, Accent,
                Good, Danger, Neutral, Warn, Track, OnAccent, Ready,
                GaugeOk, GaugeWarn, GaugeBad
            };
        }

        public Palette Clone()
        {
            return (Palette)MemberwiseClone();
        }
    }

    internal static class Theme
    {
        // ---- the current look
        //
        // Starts as the shipped default so anything that draws before a theme
        // has been loaded - an error box on a bad ini, say - still has colours.

        private static Palette cur = Themes.Minimalistic();

        public static Palette Current
        {
            get { return cur; }
        }

        public static Color Bg      { get { return cur.Bg; } }
        public static Color Panel   { get { return cur.Panel; } }
        public static Color Input   { get { return cur.Input; } }
        public static Color LogBg   { get { return cur.LogBg; } }
        public static Color LogFg   { get { return cur.LogFg; } }
        public static Color ListFg  { get { return cur.ListFg; } }
        public static Color Fg      { get { return cur.Fg; } }
        public static Color Dim     { get { return cur.Dim; } }
        public static Color Accent  { get { return cur.Accent; } }
        public static Color Good    { get { return cur.Good; } }
        public static Color Danger  { get { return cur.Danger; } }
        public static Color Neutral { get { return cur.Neutral; } }
        public static Color Warn    { get { return cur.Warn; } }
        public static Color Track   { get { return cur.Track; } }
        public static Color OnAccent { get { return cur.OnAccent; } }
        public static Color Ready   { get { return cur.Ready; } }
        public static Color GaugeOk   { get { return cur.GaugeOk; } }
        public static Color GaugeWarn { get { return cur.GaugeWarn; } }
        public static Color GaugeBad  { get { return cur.GaugeBad; } }

        // ---- fonts
        //
        // Sizes are relative to the theme's own base, so a theme that asks for
        // a 10pt UI font gets a proportionally larger title too.

        public static Font Base()  { return Ui(1.00f, FontStyle.Regular); }
        public static Font Bold()  { return Ui(1.00f, FontStyle.Bold); }
        public static Font Small() { return Ui(0.89f, FontStyle.Regular); }
        public static Font Big()   { return Ui(1.44f, FontStyle.Bold); }
        public static Font Title() { return Ui(2.22f, FontStyle.Bold); }
        public static Font Mono()  { return Safe(cur.MonoFont, cur.MonoSize, FontStyle.Regular, "Consolas"); }

        private static Font Ui(float scale, FontStyle style)
        {
            return Safe(cur.UiFont, cur.UiSize * scale, style, "Segoe UI");
        }

        // A theme naming a font nobody has installed must not take the app
        // down: GDI+ falls back silently for some names and throws for others,
        // so ask for the shipped one instead when it does.
        private static Font Safe(string family, float size, FontStyle style, string fallback)
        {
            if (size < 5f) size = 5f;
            if (size > 48f) size = 48f;
            try { return new Font(family, size, style); }
            catch (Exception) { }
            try { return new Font(fallback, size, style); }
            catch (Exception) { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

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
            SkinButton b = new SkinButton();
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

        public static Button Action(string text)   { return Button(text, Good, OnAccent); }
        public static Button Quiet(string text)    { return Button(text, Neutral, Fg); }
        public static Button Dangerous(string text){ return Button(text, Danger, OnAccent); }

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

        // The grey prompt inside an empty text box. Cheaper than a label beside
        // it that has to be shown and hidden in step with the text - and it
        // does not eat a strip of the row that something else could use.
        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, string lp);

        public static void Cue(TextBox t, string text)
        {
            try { SendMessage(t.Handle, EM_SETCUEBANNER, (IntPtr)1, text); }
            catch (Exception) { }
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

        // ---- switching looks
        //
        // Before any window exists this is a plain assignment. Afterwards, every
        // control on screen is already holding a copy of the colour it was given
        // at build time, and there is no "re-run the constructor" in WinForms -
        // so the swap is done by identity instead: line the outgoing palette up
        // against the incoming one, and anywhere a control is wearing colour N
        // of the old look, give it colour N of the new one.
        //
        // That covers everything built through this class, which is everything.
        // The handful of controls that mix their own shades implement IRestyle
        // and are asked to redo them afterwards.

        public static void Set(Palette p)
        {
            if (p != null) cur = p;
        }

        public static void Apply(Palette next)
        {
            if (next == null) return;
            Palette prev = cur;
            if (prev == next) return;
            cur = next;

            Dictionary<int, Color> map = Line(prev, next);
            List<Form> open = new List<Form>();
            foreach (Form f in Application.OpenForms) open.Add(f);
            foreach (Form f in open)
            {
                try { Swap(f, map, prev, next); }
                catch (Exception) { }
            }
        }

        // First colour of the old palette wins a tie: Neutral and Track are the
        // same shade in more than one theme, and "the quiet surface" is the
        // better guess for a control wearing it than "the divider line".
        private static Dictionary<int, Color> Line(Palette from, Palette to)
        {
            Dictionary<int, Color> map = new Dictionary<int, Color>();
            Color[] a = from.Ramp(), b = to.Ramp();
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                int k = a[i].ToArgb();
                if (!map.ContainsKey(k)) map[k] = b[i];
            }
            return map;
        }

        private static void Swap(Control c, Dictionary<int, Color> map, Palette from, Palette to)
        {
            Color back, fore;
            if (map.TryGetValue(c.BackColor.ToArgb(), out back)) c.BackColor = back;
            if (map.TryGetValue(c.ForeColor.ToArgb(), out fore)) c.ForeColor = fore;

            Font f = Refont(c.Font, from, to);
            if (f != null) c.Font = f;

            Button b = c as Button;
            if (b != null && b.FlatStyle == FlatStyle.Flat)
            {
                b.FlatAppearance.MouseOverBackColor = Lift(b.BackColor, 18);
                b.FlatAppearance.MouseDownBackColor = Lift(b.BackColor, -12);
            }

            if (c.ContextMenuStrip != null) Menu(c.ContextMenuStrip);

            IRestyle r = c as IRestyle;
            if (r != null) { try { r.Restyle(); } catch (Exception) { } }

            foreach (Control kid in c.Controls) Swap(kid, map, from, to);
            c.Invalidate(true);
        }

        // Only fonts that came out of this class are touched - a control that
        // asked for something of its own keeps it. Sizes ride along so a theme
        // can set the whole app a size larger without every call site knowing.
        private static Font Refont(Font f, Palette from, Palette to)
        {
            if (f == null) return null;
            string fam = f.FontFamily.Name;
            if (string.Equals(fam, from.MonoFont, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(from.MonoFont, to.MonoFont, StringComparison.OrdinalIgnoreCase)
                    && from.MonoSize == to.MonoSize) return null;
                return Safe(to.MonoFont, f.Size * (to.MonoSize / Nz(from.MonoSize)), f.Style, "Consolas");
            }
            if (string.Equals(fam, from.UiFont, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(from.UiFont, to.UiFont, StringComparison.OrdinalIgnoreCase)
                    && from.UiSize == to.UiSize) return null;
                return Safe(to.UiFont, f.Size * (to.UiSize / Nz(from.UiSize)), f.Style, "Segoe UI");
            }
            return null;
        }

        private static float Nz(float v) { return v <= 0.1f ? 9f : v; }

        // ---- reading colours out of a theme file

        // "#1a1d25", "1a1d25", "26,29,37" - and a name Windows already knows.
        public static bool TryColor(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.StartsWith("#")) t = t.Substring(1);

            if (t.IndexOf(',') > 0)
            {
                string[] p = t.Split(',');
                if (p.Length < 3) return false;
                int[] v = new int[4];
                v[3] = 255;
                for (int i = 0; i < p.Length && i < 4; i++)
                    if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                        return false;
                c = p.Length >= 4
                    ? Color.FromArgb(Cap(v[3]), Cap(v[0]), Cap(v[1]), Cap(v[2]))
                    : Color.FromArgb(Cap(v[0]), Cap(v[1]), Cap(v[2]));
                return true;
            }

            uint n;
            if ((t.Length == 6 || t.Length == 8)
                && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
            {
                c = t.Length == 6
                    ? Color.FromArgb((int)((n >> 16) & 0xFF), (int)((n >> 8) & 0xFF), (int)(n & 0xFF))
                    : Color.FromArgb((int)((n >> 24) & 0xFF), (int)((n >> 16) & 0xFF),
                                     (int)((n >> 8) & 0xFF), (int)(n & 0xFF));
                return true;
            }
            if (t.Length == 3 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
            {
                int r = (int)((n >> 8) & 0xF), g = (int)((n >> 4) & 0xF), bl = (int)(n & 0xF);
                c = Color.FromArgb(r * 17, g * 17, bl * 17);
                return true;
            }

            try
            {
                Color known = Color.FromName(t);
                if (known.IsKnownColor) { c = Color.FromArgb(known.R, known.G, known.B); return true; }
            }
            catch (Exception) { }
            return false;
        }

        public static string Hex(Color c)
        {
            return "#" + c.R.ToString("x2", CultureInfo.InvariantCulture)
                       + c.G.ToString("x2", CultureInfo.InvariantCulture)
                       + c.B.ToString("x2", CultureInfo.InvariantCulture);
        }
    }
}

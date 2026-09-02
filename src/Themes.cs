// IDLE MASTER - where looks come from.
//
// A theme is a text file. Not a plugin, not a DLL, not a manifest with a
// schema: nineteen colours and two font names in the same key=value shape as
// idlemaster.ini, saved as themes\something.imtheme next to the exe. The app
// ships three of them and writes them out on first start, precisely so that the
// way to make a fourth is "copy one and edit it" - no build, no account, no
// asking anybody. That is the whole extension story.
//
// Three sources, in the order they win:
//
//   built in     Minimalistic, Terminal and Cortex are compiled into the exe, so
//                the app always has something to draw with even if themes\ is
//                empty or every file in it is broken.
//   themes\      anything on disk, including edited copies of those three -
//                a file named the same as a built-in replaces it.
//   the release  "Get more themes" pulls IdleMasterThemes.zip off the newest
//                GitHub release that carries one and unpacks the .imtheme
//                files into themes\. Same release post as the installer; no
//                second server, nothing to trust that you were not already
//                trusting to update the app itself.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace IdleMaster
{
    internal static class Themes
    {
        public const string Ext = ".imtheme";
        public const string Default = "Minimalistic";

        // The zip on the release post. One asset, flat, full of .imtheme files.
        public const string Asset = "IdleMasterThemes.zip";

        public static string Dir
        {
            get { return Path.Combine(App.Dir, "themes"); }
        }

        // ------------------------------------------------------------ built in

        // The look the app has always had. One accent hue: steel blue carries
        // everything positive - titles, the log, the boost button, the sentry
        // line. The neutrals are the same grey ladder tinted cold, and red is
        // reserved for absolute idle and for destroying things.
        public static Palette Minimalistic()
        {
            Palette p = new Palette();
            p.Name = Default;
            p.Author = "Idle Master";
            p.About = "One accent hue on a cold grey ladder. The default, and the quiet one.";
            p.Builtin = true;
            return p;                              // Palette's own field values ARE this theme
        }

        // The other one: a phosphor terminal. The log was always green-on-black
        // in spirit - this is the rest of the window agreeing with it. Amber for
        // trouble, because that is the other colour a CRT had.
        public static Palette Terminal()
        {
            Palette p = new Palette();
            p.Name = "Terminal";
            p.Author = "Idle Master";
            p.About = "Green phosphor on black, amber for trouble. The console, all the way out to the edges.";
            p.Builtin = true;

            p.Bg       = Color.FromArgb(6, 10, 7);
            p.Panel    = Color.FromArgb(12, 20, 14);
            p.Input    = Color.FromArgb(3, 6, 4);
            p.LogBg    = Color.FromArgb(2, 5, 3);
            p.LogFg    = Color.FromArgb(75, 239, 122);
            p.ListFg   = Color.FromArgb(111, 214, 138);
            p.Fg       = Color.FromArgb(200, 245, 210);
            p.Dim      = Color.FromArgb(77, 122, 88);
            p.Accent   = Color.FromArgb(92, 255, 143);
            p.Good     = Color.FromArgb(18, 71, 31);
            p.Danger   = Color.FromArgb(74, 20, 16);
            p.Neutral  = Color.FromArgb(22, 40, 26);
            p.Warn     = Color.FromArgb(255, 180, 84);
            p.Track    = Color.FromArgb(16, 30, 19);
            p.OnAccent = Color.FromArgb(217, 255, 227);

            // Everything in this theme is already green, so the one colour that
            // means "there is a newer release" cannot be. Cyan is the only other
            // thing a phosphor tube ever did that reads as news rather than
            // trouble - amber is spoken for by Warn.
            p.Ready = Color.FromArgb(80, 220, 255);

            p.GaugeOk   = Color.FromArgb(47, 191, 85);
            p.GaugeWarn = Color.FromArgb(192, 138, 42);
            p.GaugeBad  = Color.FromArgb(214, 74, 58);

            p.MonoFont = "Lucida Console";
            return p;
        }

        // The third one, and the reason the shape knobs exist at all: a theme
        // that changes more than colour. Rounded slabs lit from above, a green
        // bloom inside every edge, and the window's own title bar instead of
        // Windows'. It is here as the worked example - everything it does is a
        // line in a text file anybody can copy.
        public static Palette Cortex()
        {
            Palette p = new Palette();
            p.Name = "Cortex";
            p.Author = "Idle Master";
            p.About = "Rounded, lit from above, and wearing its own title bar. The one that shows off the shape keys.";
            p.Builtin = true;

            p.Bg       = Color.FromArgb(16, 17, 16);
            p.Panel    = Color.FromArgb(26, 28, 26);
            p.Input    = Color.FromArgb(10, 11, 10);
            p.LogBg    = Color.FromArgb(11, 13, 11);
            p.LogFg    = Color.FromArgb(142, 232, 122);
            p.ListFg   = Color.FromArgb(201, 212, 198);
            p.Fg       = Color.FromArgb(232, 236, 231);
            p.Dim      = Color.FromArgb(122, 133, 122);
            p.Accent   = Color.FromArgb(68, 214, 44);
            p.Good     = Color.FromArgb(31, 107, 22);
            p.Danger   = Color.FromArgb(122, 31, 31);
            p.Neutral  = Color.FromArgb(31, 35, 32);
            p.Warn     = Color.FromArgb(255, 176, 32);
            p.Track    = Color.FromArgb(38, 42, 37);
            p.OnAccent = Color.FromArgb(240, 255, 238);
            p.Ready    = Color.FromArgb(44, 214, 214);      // green is the theme; news has to be cyan
            p.GaugeOk   = Color.FromArgb(68, 214, 44);
            p.GaugeWarn = Color.FromArgb(255, 176, 32);
            p.GaugeBad  = Color.FromArgb(226, 72, 58);

            p.Radius = 4;
            p.Gradient = 22;
            p.Glow = 64;
            p.BorderWidth = 1;
            p.Border = Color.FromArgb(47, 122, 36);
            p.Chrome = "custom";
            p.Caption = Color.FromArgb(10, 11, 10);
            return p;
        }

        public static List<Palette> Builtins()
        {
            List<Palette> b = new List<Palette>();
            b.Add(Minimalistic());
            b.Add(Terminal());
            b.Add(Cortex());
            return b;
        }

        // ------------------------------------------------------------- catalog

        // Everything installable right now, default first, the rest by name.
        // A file whose name matches a built-in replaces it: editing the shipped
        // minimalistic.imtheme is meant to work, not to be silently ignored.
        public static List<Palette> All()
        {
            Dictionary<string, Palette> by = new Dictionary<string, Palette>();
            List<string> order = new List<string>();

            foreach (Palette b in Builtins())
            {
                by[b.Key] = b;
                order.Add(b.Key);
            }

            foreach (Palette f in OnDisk())
            {
                if (!by.ContainsKey(f.Key)) order.Add(f.Key);
                by[f.Key] = f;
            }

            order.Sort(delegate(string x, string y)
            {
                bool dx = x == Default.ToLowerInvariant(), dy = y == Default.ToLowerInvariant();
                if (dx != dy) return dx ? -1 : 1;
                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            });

            List<Palette> all = new List<Palette>();
            foreach (string k in order) all.Add(by[k]);
            return all;
        }

        public static List<Palette> OnDisk()
        {
            List<Palette> found = new List<Palette>();
            string dir = Dir;
            if (!Directory.Exists(dir)) return found;
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + Ext); }
            catch (Exception) { return found; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                Palette p = Read(f);
                if (p != null) found.Add(p);
            }
            return found;
        }

        public static Palette Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string want = name.Trim().ToLowerInvariant();
            foreach (Palette p in All())
                if (p.Key == want) return p;
            return null;
        }

        // ------------------------------------------------------------- startup

        // Lay the built-ins down as editable files (once - a hand-edited copy is
        // never overwritten), then hand back whichever theme the ini names.
        public static Palette Startup(Config cfg)
        {
            Seed();
            Palette p = Find(cfg == null ? Default : cfg.Theme);
            if (p == null) p = Find(Default);
            if (p == null) p = Minimalistic();
            Theme.Set(p);
            return p;
        }

        public static void Seed()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                foreach (Palette b in Builtins())
                {
                    string path = Path.Combine(Dir, b.Key + Ext);
                    if (File.Exists(path)) continue;
                    File.WriteAllText(path, Write(b), new UTF8Encoding(false));
                }
                string readme = Path.Combine(Dir, "README.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme, ReadmeText, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // -------------------------------------------------------------- format

        public static Palette Read(string path)
        {
            try
            {
                Palette p = Parse(File.ReadAllText(path));
                if (p == null) return null;
                p.File = path;
                p.Builtin = false;
                if (p.Name == null || p.Name.Trim().Length == 0)
                    p.Name = Path.GetFileNameWithoutExtension(path);
                return p;
            }
            catch (Exception) { return null; }
        }

        // Unknown keys are ignored and a colour that will not parse keeps the
        // default: a theme with one typo in it is still a theme, and saying so
        // in a dialog nobody asked for helps nothing.
        public static Palette Parse(string text)
        {
            if (text == null) return null;
            Palette p = new Palette();
            bool any = false;

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = Bare(line.Substring(eq + 1).Trim());
                any = true;

                switch (k)
                {
                    case "name":     p.Name = v; break;
                    case "author":   p.Author = v; break;
                    case "about":    p.About = v; break;
                    case "uifont":   if (v.Length > 0) p.UiFont = v; break;
                    case "monofont": if (v.Length > 0) p.MonoFont = v; break;
                    case "uisize":   p.UiSize = Num(v, p.UiSize); break;
                    case "monosize": p.MonoSize = Num(v, p.MonoSize); break;

                    case "radius":      p.Radius = Whole(v, p.Radius, 0, 24); break;
                    case "gradient":    p.Gradient = Whole(v, p.Gradient, 0, 90); break;
                    case "glow":        p.Glow = Whole(v, p.Glow, 0, 255); break;
                    case "borderwidth": p.BorderWidth = Whole(v, p.BorderWidth, 0, 4); break;
                    case "border":      Col(v, ref p.Border); break;
                    case "caption":     Col(v, ref p.Caption); break;
                    case "chrome":
                        p.Chrome = v.Equals("custom", StringComparison.OrdinalIgnoreCase)
                            ? "custom" : "system";
                        break;

                    case "bg":        Col(v, ref p.Bg); break;
                    case "panel":     Col(v, ref p.Panel); break;
                    case "input":     Col(v, ref p.Input); break;
                    case "logbg":     Col(v, ref p.LogBg); break;
                    case "logfg":     Col(v, ref p.LogFg); break;
                    case "listfg":    Col(v, ref p.ListFg); break;
                    case "fg":        Col(v, ref p.Fg); break;
                    case "dim":       Col(v, ref p.Dim); break;
                    case "accent":    Col(v, ref p.Accent); break;
                    case "good":      Col(v, ref p.Good); break;
                    case "danger":    Col(v, ref p.Danger); break;
                    case "neutral":   Col(v, ref p.Neutral); break;
                    case "warn":      Col(v, ref p.Warn); break;
                    case "track":     Col(v, ref p.Track); break;
                    case "onaccent":  Col(v, ref p.OnAccent); break;
                    case "ready":     Col(v, ref p.Ready); break;
                    case "gaugeok":   Col(v, ref p.GaugeOk); break;
                    case "gaugewarn": Col(v, ref p.GaugeWarn); break;
                    case "gaugebad":  Col(v, ref p.GaugeBad); break;
                }
            }
            return any ? p : null;
        }

        // The value without the note somebody wrote after it. A '#' only ends
        // the value when there is whitespace in front of it, because the very
        // next thing after 'bg=' is usually a '#' that means hex - which is
        // also why the files this writes put two spaces before every comment.
        private static string Bare(string v)
        {
            for (int i = 1; i < v.Length; i++)
            {
                if (v[i] != '#' && v[i] != ';') continue;
                if (!char.IsWhiteSpace(v[i - 1])) continue;
                return v.Substring(0, i).Trim();
            }
            return v;
        }

        private static void Col(string v, ref Color target)
        {
            Color c;
            if (Theme.TryColor(v, out c)) target = c;
        }

        // A shape knob, clamped. Out-of-range is a typo, not a request: a
        // radius of 400 on a 30px button is not a look, it is a crash waiting
        // for a GraphicsPath.
        private static int Whole(string v, int fallback, int lo, int hi)
        {
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return fallback;
            return n < lo ? lo : (n > hi ? hi : n);
        }

        private static float Num(string v, float fallback)
        {
            float f;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback;
        }

        // What lands in themes\ - the palette, plus enough prose that somebody
        // opening it in Notepad can work out what every line does.
        public static string Write(Palette p)
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("# IDLE MASTER theme");
            s.AppendLine("#");
            s.AppendLine("# Copy this file, rename it, change the colours, restart Idle Master -");
            s.AppendLine("# it shows up in Settings > Theme. Colours are #rrggbb, r,g,b, or any");
            s.AppendLine("# name Windows knows. Anything unreadable falls back to the default,");
            s.AppendLine("# so a typo costs you one colour, not the app.");
            s.AppendLine();
            s.AppendLine("name=" + p.Name);
            s.AppendLine("author=" + p.Author);
            s.AppendLine("about=" + p.About);
            s.AppendLine();
            s.AppendLine("# --- surfaces");
            s.AppendLine("bg=" + Theme.Hex(p.Bg) + "                 # the window itself");
            s.AppendLine("panel=" + Theme.Hex(p.Panel) + "              # popups, menus, cards");
            s.AppendLine("input=" + Theme.Hex(p.Input) + "              # text boxes and spinners");
            s.AppendLine("logbg=" + Theme.Hex(p.LogBg) + "              # the console at the bottom");
            s.AppendLine("track=" + Theme.Hex(p.Track) + "              # gauge troughs, separators");
            s.AppendLine();
            s.AppendLine("# --- text");
            s.AppendLine("fg=" + Theme.Hex(p.Fg) + "                 # normal writing");
            s.AppendLine("dim=" + Theme.Hex(p.Dim) + "                # hints, the small print");
            s.AppendLine("accent=" + Theme.Hex(p.Accent) + "             # titles and captions");
            s.AppendLine("logfg=" + Theme.Hex(p.LogFg) + "              # the log lines");
            s.AppendLine("listfg=" + Theme.Hex(p.ListFg) + "             # list and tree rows");
            s.AppendLine("warn=" + Theme.Hex(p.Warn) + "               # failures, the idle tag, away mode");
            s.AppendLine("ready=" + Theme.Hex(p.Ready) + "              # ONLY the corner arrow when a release is waiting.");
            s.AppendLine("#                             Nothing else wears it, so make it a colour");
            s.AppendLine("#                             nothing else in your theme uses.");
            s.AppendLine();
            s.AppendLine("# --- buttons");
            s.AppendLine("good=" + Theme.Hex(p.Good) + "               # BOOST NOW and every primary action");
            s.AppendLine("danger=" + Theme.Hex(p.Danger) + "             # ABSOLUTE IDLE and every destructive one");
            s.AppendLine("neutral=" + Theme.Hex(p.Neutral) + "            # the quiet ones");
            s.AppendLine("onaccent=" + Theme.Hex(p.OnAccent) + "           # writing ON good/danger - flip this for a light theme");
            s.AppendLine();
            s.AppendLine("# --- the RAM gauge, which reddens as memory runs out");
            s.AppendLine("gaugeok=" + Theme.Hex(p.GaugeOk));
            s.AppendLine("gaugewarn=" + Theme.Hex(p.GaugeWarn));
            s.AppendLine("gaugebad=" + Theme.Hex(p.GaugeBad));
            s.AppendLine();
            s.AppendLine("# --- shape. All of these are off at 0, and off means WinForms paints");
            s.AppendLine("# the button exactly as it always did - not an imitation of it. Turn any");
            s.AppendLine("# one of them up and the app starts drawing its own buttons instead.");
            s.AppendLine("radius=" + p.Radius.ToString(CultureInfo.InvariantCulture)
                       + "                     # corner rounding, 0-24");
            s.AppendLine("gradient=" + p.Gradient.ToString(CultureInfo.InvariantCulture)
                       + "                   # how much lighter the top of a slab is, 0-90");
            s.AppendLine("glow=" + p.Glow.ToString(CultureInfo.InvariantCulture)
                       + "                       # accent bloom just inside the edge, 0-255");
            s.AppendLine("borderwidth=" + p.BorderWidth.ToString(CultureInfo.InvariantCulture)
                       + "                # outline on every button, 0-4");
            s.AppendLine("border=" + (p.Border.IsEmpty ? "" : Theme.Hex(p.Border))
                       + "                      # ...in this colour. Blank = mixed from the fill.");
            s.AppendLine();
            s.AppendLine("# --- the window frame. 'custom' means the theme draws the title bar");
            s.AppendLine("# instead of Windows: same drag, snap and double-click-to-maximise, but");
            s.AppendLine("# in your colours. Changing this one takes effect on the next start -");
            s.AppendLine("# everything else on this page changes the moment you pick the theme.");
            s.AppendLine("chrome=" + p.Chrome);
            s.AppendLine("caption=" + (p.Caption.IsEmpty ? "" : Theme.Hex(p.Caption))
                       + "                     # that strip's background. Blank = panel.");
            s.AppendLine();
            s.AppendLine("# --- type. uisize is the base; captions and titles scale off it.");
            s.AppendLine("# The window layout is fixed-pixel, so a much wider face or a much");
            s.AppendLine("# bigger size will clip long labels. Consider that a dare.");
            s.AppendLine("uifont=" + p.UiFont);
            s.AppendLine("uisize=" + p.UiSize.ToString(CultureInfo.InvariantCulture));
            s.AppendLine("monofont=" + p.MonoFont);
            s.AppendLine("monosize=" + p.MonoSize.ToString(CultureInfo.InvariantCulture));
            return s.ToString();
        }

        private const string ReadmeText =
@"IDLE MASTER - themes
====================

Every .imtheme file in this folder is a look you can pick in
Settings > Theme. They are plain text. Open one in Notepad.

Making your own
---------------
1. Copy minimalistic.imtheme to mine.imtheme
2. Change 'name=' to something of your own - that is what the picker shows
3. Change the colours
4. Restart Idle Master and pick it

Deleting a file removes the theme. Deleting minimalistic.imtheme or
terminal.imtheme just brings back the built-in copy: those two are inside
the exe as well, so the app can never end up with nothing to draw with.

Getting more
------------
The extra themes are NOT in the installer, and that is deliberate. Idle Master
is one small exe you download once; bundling a gallery of looks most people
never open would make every install bigger and every update slower, for paint.
So three ship inside the app and the rest are fetched only if you ask.

Settings > Theme > Get more themes downloads IdleMasterThemes.zip from the
same GitHub release the app updates itself from, and unpacks the .imtheme
files in it here. Nothing else in the zip is read. It is a few KB of text -
themes have no images and no code in them, which is also why they are safe to
swap around.

Sharing one
-----------
Send somebody the file. That is it. Or open a pull request against
https://github.com/Mild-Solvent/Iddle-Master and it ships in the bundle.
";

        // ------------------------------------------------------------ download

        // Pulls the bundle off the release post and unpacks the themes in it.
        // Returns how many landed. Everything that is not a .imtheme file at
        // the top of the zip is ignored, and the name is taken apart to its
        // last segment, so an entry called ..\..\Windows\System32\evil.imtheme
        // can only ever write themes\evil.imtheme.
        public static int Download(Action<string> log)
        {
            if (log == null) log = delegate(string s) { };

            string url = Updater.FindAsset(Asset);
            if (url == null || url.Length == 0)
                throw new Exception("No " + Asset + " on the last few releases of "
                    + Updater.Repo + " yet. Themes in this folder still work - "
                    + "the bundle is just not published.");

            string tmp = Path.Combine(Path.GetTempPath(), Asset);
            log("Theme bundle: downloading " + url);
            using (WebClient w = new WebClient())
            {
                w.Headers.Add("User-Agent", "IdleMaster/" + App.Version);
                w.DownloadFile(url, tmp);
            }

            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

            int wrote = 0;
            using (FileStream fs = File.OpenRead(tmp))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string leaf = Leaf(e.FullName);
                    if (leaf == null) continue;
                    if (!leaf.EndsWith(Ext, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Length > 256 * 1024) { log("  skipped " + leaf + " - too big to be a theme"); continue; }

                    string to = Path.Combine(Dir, leaf);
                    try
                    {
                        using (Stream src = e.Open())
                        using (FileStream dst = File.Create(to))
                            src.CopyTo(dst);

                        // A file that does not parse is worse than no file: it
                        // would sit in the picker doing nothing.
                        if (Read(to) == null)
                        {
                            File.Delete(to);
                            log("  dropped " + leaf + " - not a readable theme");
                            continue;
                        }
                        wrote++;
                        log("  " + leaf);
                    }
                    catch (Exception ex) { log("  ! " + leaf + ": " + ex.Message); }
                }
            }

            try { File.Delete(tmp); } catch (Exception) { }
            return wrote;
        }

        // The last path segment of a zip entry, or null if it is a directory,
        // a traversal attempt, or anything Windows would refuse as a name.
        private static string Leaf(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return null;
            string s = entry.Replace('/', '\\');
            if (s.EndsWith("\\")) return null;
            int cut = s.LastIndexOf('\\');
            if (cut >= 0) s = s.Substring(cut + 1);
            if (s.Length == 0 || s == "." || s == "..") return null;
            if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return s;
        }
    }
}

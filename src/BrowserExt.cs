// IDLE MASTER - the browser extension door. The browser is the biggest thing
// on most machines and the one thing the boost can only close, not slim. Idle
// Master Tabs is the part that works from the inside: tabs and extensions put to
// sleep and woken again, one button for all of it.
//
// The rule of the door: a Chromium browser will not let a program put an
// extension into it, and will not even open its extensions page for one - an
// address like chrome://extensions handed to it from outside comes up as an
// empty new tab. That is its defence against exactly the software that would
// try, and there is no way round it worth having. So the button does
// everything up to that line - the folder is unpacked, its path is on the
// clipboard, a page with the steps is open in the browser - and the presses
// are the user's.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace IdleMaster
{
    internal static class BrowserExt
    {
        // build.ps1 zips the extension folder and puts it inside the exe.
        public const string Resource = "browser-extension.zip";

        // The steps, as a page. It lives in the extension's folder but is no
        // part of the extension - the manifest does not name it.
        public const string GuideFile = "install.html";

        // Beside the exe, like the themes. The browser loads the extension
        // from this folder every time it starts, so it has to stay put.
        public static string Dir
        {
            get { return Path.Combine(App.Dir, "browser-extension"); }
        }

        public sealed class Browser
        {
            public string Name;
            public string Process;
            public string Exe;
            public string Page;      // what to type in the address bar
            public bool Running;
        }

        // Name, process name, path under an install root, extensions page.
        private static readonly string[][] Known = new string[][]
        {
            new string[] { "Brave",  "brave",  @"BraveSoftware\Brave-Browser\Application\brave.exe", "brave://extensions" },
            new string[] { "Chrome", "chrome", @"Google\Chrome\Application\chrome.exe",              "chrome://extensions" },
            new string[] { "Edge",   "msedge", @"Microsoft\Edge\Application\msedge.exe",             "edge://extensions" },
        };

        private static List<string> Roots()
        {
            List<string> roots = new List<string>();
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            foreach (string name in new string[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string root = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(root) && !roots.Contains(root)) roots.Add(root);
            }
            return roots;
        }

        // The browser that is open wins, because that is the one being used.
        // Failing that, the first one installed. Null when there is none.
        public static Browser Find()
        {
            Browser installed = null;
            foreach (string[] k in Known)
            {
                string exe = null;
                foreach (string root in Roots())
                {
                    string at = Path.Combine(root, k[2]);
                    if (File.Exists(at)) { exe = at; break; }
                }
                if (exe == null) continue;

                Browser b = new Browser();
                b.Name = k[0];
                b.Process = k[1];
                b.Exe = exe;
                b.Page = k[3];
                b.Running = IsRunning(k[1]);
                if (b.Running) return b;
                if (installed == null) installed = b;
            }
            return installed;
        }

        private static bool IsRunning(string process)
        {
            Process[] found = System.Diagnostics.Process.GetProcessesByName(process);
            foreach (Process p in found) p.Dispose();
            return found.Length > 0;
        }

        // Writes the extension out. True when a copy was already there, which
        // makes this an update: the browser already knows the folder and only
        // needs telling to read it again.
        public static bool Unpack()
        {
            string dir = Dir;
            bool wasThere = File.Exists(Path.Combine(dir, "manifest.json"));
            string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource))
            {
                if (s == null) throw new InvalidOperationException(
                    "This build has no " + Resource + " inside it - build.ps1 embeds it.");
                using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        // Older zip writers on Windows use the wrong slash.
                        string name = e.FullName.Replace('/', '\\');
                        if (name.Length == 0 || name.EndsWith("\\")) continue;

                        string to = Path.GetFullPath(Path.Combine(dir, name));
                        if (!to.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The extension zip has a bad entry: " + e.FullName);

                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        using (Stream from = e.Open())
                        using (FileStream o = new FileStream(to, FileMode.Create, FileAccess.Write))
                            from.CopyTo(o);
                    }
                }
            }
            return wasThere;
        }

        // The extensions page by whatever name this browser gives it.
        public static string PageOf(Browser b)
        {
            return b != null ? b.Page : "chrome://extensions";
        }

        public static string[] Steps(Browser b, bool update)
        {
            string open = "Open the extensions page: type " + PageOf(b)
                + " in the address bar, or menu > Extensions > Manage extensions.";
            if (update)
                return new string[]
                {
                    open,
                    "Find the Idle Master Tabs card and press the round reload arrow on it."
                };
            return new string[]
            {
                open,
                "Switch on Developer mode (top right of that page).",
                "Press Load unpacked.",
                "Paste the folder into the box (Ctrl+V), press Enter, then Select Folder."
            };
        }

        // The same steps as a page, because a message box is gone by the time
        // the second step is reached and a tab is still there.
        public static string WriteGuide(Browser b, bool update)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Idle Master Tabs - " + (update ? "update" : "install") + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{margin:0;background:#0a0e13;color:#c9d6e3;font:15px/1.6 Consolas,monospace}");
            sb.AppendLine("main{max-width:760px;margin:48px auto;padding:0 24px}");
            sb.AppendLine("h1{color:#9fd3ff;font-size:18px;letter-spacing:.12em}");
            sb.AppendLine("p{color:#6f8196}");
            sb.AppendLine("li{margin:14px 0}");
            sb.AppendLine("code{display:inline-block;padding:2px 8px;border:1px solid #1f2a37;background:#111821;"
                + "color:#9fd3ff;user-select:all}");
            sb.AppendLine("</style></head><body><main>");
            sb.AppendLine("<h1>IDLE MASTER TABS</h1>");
            sb.AppendLine("<p>Idle Master has " + (update ? "updated" : "unpacked")
                + " the extension. A browser does not let a program install one, so these presses are yours.</p>");
            sb.AppendLine("<ol>");
            foreach (string step in Steps(b, update))
                sb.AppendLine("<li>" + WebUtility.HtmlEncode(step) + "</li>");
            sb.AppendLine("</ol>");
            sb.AppendLine("<p>The page: <code>" + WebUtility.HtmlEncode(PageOf(b)) + "</code></p>");
            sb.AppendLine("<p>The folder, already on the clipboard: <code>" + WebUtility.HtmlEncode(Dir) + "</code></p>");
            sb.AppendLine("<p>Afterwards, pin Idle Master Tabs from the extensions menu so its button stays on the toolbar.</p>");
            sb.AppendLine("</main></body></html>");

            string at = Path.Combine(Dir, GuideFile);
            File.WriteAllText(at, sb.ToString(), new UTF8Encoding(false));
            return at;
        }

        // Puts the guide in front of the user. Idle Master is elevated and a
        // browser it started would be too, so a browser that is not open yet
        // is started by Explorer, which is not - and the page is then handed
        // to the one that is running, which is all a second launch of a
        // Chromium browser ever does.
        public static void OpenGuide(Browser b, string guide)
        {
            if (!IsRunning(b.Process))
            {
                System.Diagnostics.Process.Start(
                    new ProcessStartInfo("explorer.exe", "\"" + b.Exe + "\"") { UseShellExecute = true });
                for (int i = 0; i < 40 && !IsRunning(b.Process); i++) Thread.Sleep(250);
                if (!IsRunning(b.Process))
                    throw new InvalidOperationException(b.Name + " did not start - open it and go to " + b.Page);
                Thread.Sleep(2000);   // a window first, then the page in it
            }
            System.Diagnostics.Process.Start(
                new ProcessStartInfo(b.Exe, "\"" + new Uri(guide).AbsoluteUri + "\"") { UseShellExecute = false });
        }
    }
}

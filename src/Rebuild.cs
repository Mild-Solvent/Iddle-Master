// IDLE MASTER REBUILD - the exe that lives inside a backup kit.
//
// Idle Master's "Backup kit" writes one zip: this exe, a rebuild.ini that says
// what you picked, an apps.json for winget, the files you asked to keep, and
// (when it had one) the Idle Master installer plus your idlemaster.ini.
//
// On a fresh Windows you unzip it anywhere, run this, and it puts things back:
//
//   1. your files, where they were (or into one folder on the Desktop)
//   2. your apps, through winget - one at a time, so one failure costs one app
//   3. Idle Master itself, with the config you had
//   4. Zoicware's RemoveWindowsAI - Copilot, Recall and the rest, gone
//   5. Chris Titus's WinUtil, one preset applied without clicking - and left
//      open at the end if you want anything more from it
//
// Every step is a checkbox. Nothing runs that you did not tick.
//
// Built with the in-box .NET Framework compiler, same as the app: C# 5, no
// string interpolation, no ?., no expression-bodied members.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Idle Master Rebuild")]
[assembly: AssemblyDescription("Puts a Windows install back together from an Idle Master backup kit")]
[assembly: AssemblyProduct("Idle Master")]
[assembly: AssemblyVersion("0.11.0.0")]
[assembly: AssemblyFileVersion("0.11.0.0")]

namespace IdleMasterRebuild
{
    // ---------------------------------------------------------------- the kit

    internal sealed class KitApp
    {
        public string Source = "winget";
        public string Id = "";
        public string Name = "";
    }

    internal sealed class KitFile
    {
        public string InKit = "";       // files\Documents  (relative to the kit)
        public string Original = "";    // C:\Users\you\Documents
    }

    // rebuild.ini, read once. Everything the backup side decided lives here;
    // the checkboxes below start from these values and you can flip them.
    internal sealed class Kit
    {
        public string Dir = "";
        public string Created = "";
        public string Machine = "";
        public string Profile = "";         // the old %USERPROFILE%, for remapping
        public string IdleMasterVersion = "";

        public bool WantApps = true;
        public bool WantFiles = true;
        public bool WantIdleMaster = true;
        public bool WantRemoveAi = true;
        public bool WantWinUtil = true;
        public bool WantWinUtilOpen = true;
        public string WinUtilPreset = "Standard";

        public readonly List<KitApp> Apps = new List<KitApp>();
        public readonly List<KitFile> Files = new List<KitFile>();

        public const string ManifestName = "rebuild.ini";
        public const string SetupName = "IdleMasterSetup.exe";
        public const string IniName = "idlemaster.ini";
        public const string SetupUrl =
            "https://github.com/Mild-Solvent/Iddle-Master/releases/latest/download/IdleMasterSetup.exe";

        public string SetupPath { get { return Path.Combine(Dir, SetupName); } }
        public string IniPath { get { return Path.Combine(Dir, IniName); } }
        public bool HasSetup { get { return File.Exists(SetupPath); } }
        public bool HasIni { get { return File.Exists(IniPath); } }

        public static Kit Load(string dir)
        {
            Kit k = new Kit();
            k.Dir = dir;
            string path = Path.Combine(dir, ManifestName);
            if (!File.Exists(path)) return null;

            string section = "";
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                switch (section)
                {
                    case "kit":
                    case "options":
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();
                        bool b = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        switch (key)
                        {
                            case "created": k.Created = val; break;
                            case "machine": k.Machine = val; break;
                            case "profile": k.Profile = val; break;
                            case "idlemaster": k.IdleMasterVersion = val; break;
                            case "apps": k.WantApps = b; break;
                            case "files": k.WantFiles = b; break;
                            case "installidlemaster": k.WantIdleMaster = b; break;
                            case "removeai": k.WantRemoveAi = b; break;
                            case "winutil": k.WantWinUtil = b; break;
                            case "winutilopen": k.WantWinUtilOpen = b; break;
                            case "winutilpreset": if (val.Length > 0) k.WinUtilPreset = val; break;
                        }
                        break;
                    }
                    case "apps":
                    {
                        // source  id  name with spaces
                        string[] bits = line.Split(new char[] { ' ', '\t' }, 3,
                            StringSplitOptions.RemoveEmptyEntries);
                        if (bits.Length < 2) continue;
                        KitApp a = new KitApp();
                        a.Source = bits[0];
                        a.Id = bits[1];
                        a.Name = bits.Length > 2 ? bits[2].Trim() : bits[1];
                        k.Apps.Add(a);
                        break;
                    }
                    case "files":
                    {
                        // files\Documents = C:\Users\you\Documents  (the left side
                        // is ours and never contains '=', the right side is anything)
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        KitFile f = new KitFile();
                        f.InKit = line.Substring(0, eq).Trim();
                        f.Original = line.Substring(eq + 1).Trim();
                        if (f.InKit.Length > 0 && f.Original.Length > 0) k.Files.Add(f);
                        break;
                    }
                }
            }
            return k;
        }

        // C:\Users\olduser\Documents -> C:\Users\newuser\Documents. A fresh
        // Windows very often means a fresh account name; the folder inside
        // the profile is what you actually meant.
        public string Remap(string original)
        {
            string me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Profile.Length == 0 || me.Length == 0) return original;
            string oldp = Profile.TrimEnd('\\');
            if (original.StartsWith(oldp + "\\", StringComparison.OrdinalIgnoreCase))
                return me.TrimEnd('\\') + original.Substring(oldp.Length);
            if (original.Equals(oldp, StringComparison.OrdinalIgnoreCase)) return me;
            return original;
        }
    }

    // ---------------------------------------------------------------- the work

    internal sealed class Rebuilder
    {
        private readonly Kit kit;
        private readonly Action<string> say;
        public volatile bool Cancel;

        public Rebuilder(Kit k, Action<string> log) { kit = k; say = log; }

        // ---- files

        public void RestoreFiles(bool toDesktopFolder, bool overwrite)
        {
            say("-- files");
            string bucket = null;
            if (toDesktopFolder)
            {
                bucket = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Restored files");
                Directory.CreateDirectory(bucket);
            }

            int copied = 0, skipped = 0, failed = 0;
            foreach (KitFile f in kit.Files)
            {
                if (Cancel) return;
                string src = Path.Combine(kit.Dir, f.InKit);
                string dst = toDesktopFolder
                    ? Path.Combine(bucket, Path.GetFileName(f.InKit.TrimEnd('\\')))
                    : kit.Remap(f.Original);

                if (Directory.Exists(src))
                {
                    say("   " + f.Original + "  ->  " + dst);
                    CopyTree(src, dst, overwrite, ref copied, ref skipped, ref failed);
                }
                else if (File.Exists(src))
                {
                    say("   " + f.Original + "  ->  " + dst);
                    CopyOne(src, dst, overwrite, ref copied, ref skipped, ref failed);
                }
                else say("   ! not in the kit: " + f.InKit);
            }
            say("   = " + copied + " files put back"
                + (skipped > 0 ? ", " + skipped + " already there (left alone)" : "")
                + (failed > 0 ? ", " + failed + " FAILED" : "") + ".");
        }

        private void CopyTree(string src, string dst, bool overwrite,
                              ref int copied, ref int skipped, ref int failed)
        {
            try { Directory.CreateDirectory(dst); }
            catch (Exception ex) { say("   ! cannot create " + dst + ": " + ex.Message); failed++; return; }

            string[] files;
            try { files = Directory.GetFiles(src); }
            catch (Exception) { files = new string[0]; }
            foreach (string f in files)
            {
                if (Cancel) return;
                CopyOne(f, Path.Combine(dst, Path.GetFileName(f)), overwrite,
                    ref copied, ref skipped, ref failed);
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(src); }
            catch (Exception) { dirs = new string[0]; }
            foreach (string d in dirs)
            {
                if (Cancel) return;
                CopyTree(d, Path.Combine(dst, Path.GetFileName(d)), overwrite,
                    ref copied, ref skipped, ref failed);
            }
        }

        private void CopyOne(string src, string dst, bool overwrite,
                             ref int copied, ref int skipped, ref int failed)
        {
            try
            {
                if (File.Exists(dst) && !overwrite) { skipped++; return; }
                string parent = Path.GetDirectoryName(dst);
                if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                File.Copy(src, dst, true);
                copied++;
            }
            catch (Exception ex)
            {
                failed++;
                say("   ! " + dst + ": " + ex.Message.Split('\n')[0]);
            }
        }

        // ---- apps

        public void InstallApps()
        {
            say("-- apps (" + kit.Apps.Count + " through winget)");
            if (kit.Apps.Count == 0) return;

            if (Run("winget", "--version", null, false) != 0)
            {
                say("   ! winget is not available on this Windows yet.");
                say("     Open the Microsoft Store, update 'App Installer', then run this again -");
                say("     or hand apps.json from the kit to 'winget import' later.");
                return;
            }

            int ok = 0, bad = 0, n = 0;
            foreach (KitApp a in kit.Apps)
            {
                if (Cancel) return;
                n++;
                say("   [" + n + "/" + kit.Apps.Count + "] " + a.Name + "  (" + a.Id + ")");
                string args = "install --id \"" + a.Id + "\" --exact --source " + a.Source
                    + " --silent --accept-package-agreements --accept-source-agreements"
                    + " --disable-interactivity";
                int code = Run("winget", args, "      ", true);
                // 0 = installed. -1978335189 (0x8A15002B) = already installed /
                // no applicable upgrade - which for a rebuild is also fine.
                if (code == 0 || code == -1978335189 || code == -1978335135) ok++;
                else { bad++; say("      ! winget exit " + code); }
            }
            say("   = " + ok + " installed" + (bad > 0 ? ", " + bad + " failed - see above" : "") + ".");
        }

        // ---- idle master

        public void InstallIdleMaster()
        {
            say("-- Idle Master");
            string setup = kit.SetupPath;
            if (!kit.HasSetup)
            {
                setup = Path.Combine(Path.GetTempPath(), Kit.SetupName);
                say("   the kit has no installer inside - fetching the latest from GitHub...");
                try
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
                    catch (Exception) { }
                    using (WebClient w = new WebClient())
                    {
                        w.Headers.Add("User-Agent", "IdleMasterRebuild/"
                            + Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                        w.DownloadFile(Kit.SetupUrl, setup);
                    }
                    say("   downloaded " + Kit.SetupName);
                }
                catch (Exception ex)
                {
                    say("   ! download failed: " + ex.Message.Split('\n')[0]);
                    say("     get it by hand: " + Kit.SetupUrl);
                    return;
                }
            }

            // Your config first, so the installer finds it and keeps it. The
            // setup goes to %LOCALAPPDATA%\Programs\IdleMaster by default; we
            // are elevated but still the same account, so that resolves to
            // the same folder the app will read.
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine("Programs", "IdleMaster"));
            if (kit.HasIni)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string to = Path.Combine(dir, Kit.IniName);
                    if (File.Exists(to)) say("   an idlemaster.ini is already there - keeping that one");
                    else { File.Copy(kit.IniPath, to); say("   your idlemaster.ini is back"); }
                }
                catch (Exception ex) { say("   ! could not place idlemaster.ini: " + ex.Message.Split('\n')[0]); }
            }

            int code = Run(setup, "--silent --desktop --dir \"" + dir + "\"", "   ", true);
            if (code == 0) say("   = installed to " + dir + " (Start menu + desktop shortcut).");
            else say("   ! the installer returned " + code);
        }

        // ---- windows ai

        public void RemoveWindowsAi()
        {
            say("-- Windows AI removal (zoicware/RemoveWindowsAI, all options, no prompts)");
            say("   this downloads and runs the script from GitHub - a few minutes.");
            string cmd = "& ([scriptblock]::Create((irm "
                + "'https://raw.githubusercontent.com/zoicware/RemoveWindowsAI/main/RemoveWindowsAi.ps1')))"
                + " -nonInteractive -AllOptions";
            int code = PowerShell(cmd, "   ", true);
            say(code == 0 ? "   = done." : "   ! the script returned " + code + " - read the lines above.");
        }

        // ---- winutil

        public void WinUtilPreset(string preset)
        {
            say("-- WinUtil (Chris Titus): applying the '" + preset + "' preset without the window");
            say("   this downloads and runs the script from christitus.com - a few minutes.");
            string cmd = "& ([ScriptBlock]::Create((irm https://christitus.com/win))) -Preset " + preset;
            int code = PowerShell(cmd, "   ", true);
            say(code == 0 ? "   = done." : "   ! winutil returned " + code + " - read the lines above.");
        }

        public void WinUtilOpen()
        {
            say("-- WinUtil: opening it and leaving it to you.");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://christitus.com/win | iex\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { say("   ! could not start it: " + ex.Message.Split('\n')[0]); }
        }

        // ---- plumbing

        private int PowerShell(string command, string indent, bool echo)
        {
            // Windows PowerShell 5.1 on purpose: both scripts want it, and it is
            // the one guaranteed to exist on a fresh install.
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell\\v1.0\\powershell.exe");
            string args = "-NoProfile -ExecutionPolicy Bypass -NonInteractive -Command \""
                + command.Replace("\"", "\\\"") + "\"";
            return Run(ps, args, indent, echo);
        }

        // Runs a program, streams what it prints into the log, returns the exit
        // code. -1 means it could not even start.
        private int Run(string exe, string args, string indent, bool echo)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                Process p = Process.Start(psi);
                if (p == null) return -1;

                DataReceivedEventHandler h = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null || !echo) return;
                    string line = e.Data.TrimEnd();
                    // winget's progress bars arrive as \r-refreshed junk; keep
                    // the words, drop the paint.
                    if (line.Length == 0) return;
                    if (line.IndexOf('\r') >= 0) line = line.Substring(line.LastIndexOf('\r') + 1);
                    if (line.Trim().Length == 0 || IsSpinner(line)) return;
                    say(indent + line.Trim());
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (!p.WaitForExit(500))
                {
                    if (Cancel)
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return -2;
                    }
                }
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                say(indent + "! cannot run " + Path.GetFileName(exe) + ": " + ex.Message.Split('\n')[0]);
                return -1;
            }
        }

        private static bool IsSpinner(string s)
        {
            string t = s.Trim();
            if (t.Length > 4) return false;
            foreach (char c in t)
                if (c != '-' && c != '\\' && c != '|' && c != '/' && c != '█' && c != '▒' && c != ' ')
                    return false;
            return true;
        }
    }

    // ---------------------------------------------------------------- window

    internal sealed class RebuildForm : Form
    {
        private readonly Kit kit;
        private readonly CheckBox chkFiles, chkApps, chkIdle, chkAi, chkWinUtil, chkOpen, chkOverwrite;
        private readonly RadioButton rbOriginal, rbDesktop;
        private readonly ComboBox preset;
        private readonly TextBox log;
        private readonly Button go, close;
        private Rebuilder worker;
        private bool running;

        private static readonly Color Bg = Color.FromArgb(17, 19, 24);
        private static readonly Color Fg = Color.FromArgb(226, 230, 236);
        private static readonly Color Dim = Color.FromArgb(120, 128, 140);
        private static readonly Color Accent = Color.FromArgb(143, 193, 240);
        private static readonly Color Panel = Color.FromArgb(26, 29, 37);

        public RebuildForm(Kit k, bool auto)
        {
            kit = k;

            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Text = "Idle Master - Rebuild kit";
            ClientSize = new Size(640, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            Label title = new Label();
            title.Text = "REBUILD KIT";
            title.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            title.ForeColor = Accent;
            title.SetBounds(20, 14, 400, 34);
            Controls.Add(title);

            long bytes = KitBytes();
            Label sub = new Label();
            sub.Text = "made on " + (kit.Machine.Length > 0 ? kit.Machine : "another machine")
                + (kit.Created.Length > 0 ? ", " + kit.Created : "")
                + "  -  " + kit.Apps.Count + " apps, " + kit.Files.Count + " file sets ("
                + Nice(bytes) + ")";
            sub.ForeColor = Dim;
            sub.SetBounds(22, 50, 600, 20);
            Controls.Add(sub);

            int y = 84;
            chkFiles = Check("Put " + kit.Files.Count + " file set(s) back  (" + Nice(bytes) + ")",
                22, y, kit.WantFiles && kit.Files.Count > 0);
            chkFiles.Enabled = kit.Files.Count > 0;

            rbOriginal = Radio("where they were", 44, y + 24, true);
            rbOriginal.Width = 130;
            rbDesktop = Radio("into Desktop\\Restored files", 180, y + 24, false);
            rbDesktop.Width = 200;
            chkOverwrite = Check("overwrite files that already exist", 390, y + 24, false);
            chkOverwrite.Width = 240;
            y += 52;

            chkApps = Check("Reinstall " + kit.Apps.Count + " app(s) with winget",
                22, y, kit.WantApps && kit.Apps.Count > 0);
            chkApps.Enabled = kit.Apps.Count > 0;
            Label appsHint = Hint(AppsPreview(), 44, y + 22, 580);
            y += 46;

            string imv = kit.HasSetup ? "the bundled installer" : "the latest release from GitHub";
            chkIdle = Check("Install Idle Master (" + imv + ")"
                + (kit.HasIni ? " with your idlemaster.ini" : ""), 22, y, kit.WantIdleMaster);
            y += 26;

            chkAi = Check("Remove Copilot, Recall and the other Windows AI  (zoicware/RemoveWindowsAI)",
                22, y, kit.WantRemoveAi);
            y += 26;

            chkWinUtil = Check("Apply Chris Titus WinUtil tweaks, preset:", 22, y, kit.WantWinUtil);
            chkWinUtil.Width = 270;
            preset = new ComboBox();
            preset.DropDownStyle = ComboBoxStyle.DropDownList;
            preset.Items.AddRange(new object[] { "Standard", "Minimal", "Advanced" });
            preset.SelectedItem = preset.Items.Contains(kit.WinUtilPreset) ? kit.WinUtilPreset : "Standard";
            preset.SetBounds(296, y, 110, 22);
            preset.BackColor = Panel;
            preset.ForeColor = Fg;
            preset.FlatStyle = FlatStyle.Flat;
            Controls.Add(preset);
            y += 26;

            chkOpen = Check("Leave WinUtil open when everything is done, for anything else you want from it",
                22, y, kit.WantWinUtilOpen);
            y += 30;

            Label note = Hint("Runs as administrator. The AI removal and WinUtil steps download their scripts "
                + "from GitHub / christitus.com when they run - they are not inside the kit. "
                + "A restore point beforehand is never a bad idea.", 22, y, 600);
            note.Height = 34;
            y += 40;

            log = new TextBox();
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.SetBounds(22, y, 596, 600 - y - 62);
            log.BackColor = Color.FromArgb(11, 13, 17);
            log.ForeColor = Color.FromArgb(168, 203, 232);
            log.Font = new Font("Consolas", 8.5f);
            log.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(log);

            go = new Button();
            go.Text = "Rebuild";
            go.SetBounds(400, 556, 110, 32);
            go.BackColor = Color.FromArgb(30, 78, 120);
            go.ForeColor = Color.White;
            go.FlatStyle = FlatStyle.Flat;
            go.FlatAppearance.BorderSize = 0;
            go.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            go.Click += delegate { if (running) Stop(); else Go(); };
            Controls.Add(go);
            AcceptButton = go;

            close = new Button();
            close.Text = "Close";
            close.SetBounds(518, 558, 100, 28);
            close.BackColor = Color.FromArgb(35, 40, 51);
            close.ForeColor = Fg;
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 0;
            close.Click += delegate { Close(); };
            Controls.Add(close);
            CancelButton = close;

            Say("Kit: " + kit.Dir);
            Say("Tick what you want, then Rebuild. Each step says what it did.");
            if (!kit.HasSetup) Say("(no IdleMasterSetup.exe in the kit - the Idle Master step downloads the latest)");

            if (auto) Shown += delegate { Go(); };
        }

        private CheckBox Check(string text, int x, int y, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.SetBounds(x, y, 600, 22);
            c.ForeColor = Fg;
            c.FlatStyle = FlatStyle.Flat;
            Controls.Add(c);
            return c;
        }

        private RadioButton Radio(string text, int x, int y, bool on)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.Checked = on;
            r.SetBounds(x, y, 150, 22);
            r.ForeColor = Fg;
            r.FlatStyle = FlatStyle.Flat;
            Controls.Add(r);
            return r;
        }

        private Label Hint(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Dim;
            l.SetBounds(x, y, w, 20);
            Controls.Add(l);
            return l;
        }

        private string AppsPreview()
        {
            if (kit.Apps.Count == 0) return "no apps in this kit";
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (KitApp a in kit.Apps)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(a.Name);
                if (++n >= 6) break;
            }
            if (kit.Apps.Count > n) sb.Append(" and " + (kit.Apps.Count - n) + " more");
            return sb.ToString();
        }

        private long KitBytes()
        {
            long total = 0;
            foreach (KitFile f in kit.Files)
            {
                string p = Path.Combine(kit.Dir, f.InKit);
                try
                {
                    if (File.Exists(p)) total += new FileInfo(p).Length;
                    else if (Directory.Exists(p)) total += DirBytes(p);
                }
                catch (Exception) { }
            }
            return total;
        }

        private static long DirBytes(string dir)
        {
            long n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    try { n += new FileInfo(f).Length; } catch (Exception) { }
                foreach (string d in Directory.GetDirectories(dir))
                    n += DirBytes(d);
            }
            catch (Exception) { }
            return n;
        }

        public static string Nice(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0") + " KB";
            return b + " B";
        }

        private void Say(string s)
        {
            if (log.InvokeRequired)
            {
                try { log.BeginInvoke((Action<string>)Say, s); } catch (Exception) { }
                return;
            }
            log.AppendText(s + Environment.NewLine);
        }

        private void SetRunning(bool on)
        {
            running = on;
            go.Text = on ? "Stop" : "Rebuild";
            go.BackColor = on ? Color.FromArgb(110, 40, 48) : Color.FromArgb(30, 78, 120);
            foreach (Control c in Controls)
                if (c is CheckBox || c is RadioButton || c is ComboBox) c.Enabled = !on;
            if (!on)
            {
                chkFiles.Enabled = kit.Files.Count > 0;
                chkApps.Enabled = kit.Apps.Count > 0;
            }
            close.Enabled = !on;
        }

        private void Stop()
        {
            if (worker != null) worker.Cancel = true;
            Say("stopping after the current step...");
        }

        private void Go()
        {
            if (running) return;
            bool files = chkFiles.Checked, apps = chkApps.Checked, idle = chkIdle.Checked,
                 ai = chkAi.Checked, wu = chkWinUtil.Checked, open = chkOpen.Checked;
            bool desk = rbDesktop.Checked, over = chkOverwrite.Checked;
            string pre = preset.SelectedItem == null ? "Standard" : preset.SelectedItem.ToString();
            if (!files && !apps && !idle && !ai && !wu && !open)
            {
                Say("nothing ticked - nothing to do.");
                return;
            }

            SetRunning(true);
            worker = new Rebuilder(kit, Say);
            Rebuilder mine = worker;
            Thread t = new Thread(delegate()
            {
                DateTime t0 = DateTime.Now;
                try
                {
                    Say("");
                    Say("== rebuild started " + t0.ToString("yyyy-MM-dd HH:mm"));
                    if (files && !mine.Cancel) mine.RestoreFiles(desk, over);
                    if (apps && !mine.Cancel) mine.InstallApps();
                    if (idle && !mine.Cancel) mine.InstallIdleMaster();
                    if (ai && !mine.Cancel) mine.RemoveWindowsAi();
                    if (wu && !mine.Cancel) mine.WinUtilPreset(pre);
                    if (open && !mine.Cancel) mine.WinUtilOpen();
                    Say(mine.Cancel
                        ? "== stopped after " + Elapsed(t0) + "."
                        : "== done in " + Elapsed(t0) + ". A reboot finishes what the scripts started.");
                }
                catch (Exception ex) { Say("!! " + ex.ToString()); }
                try { BeginInvoke((Action)delegate { SetRunning(false); }); } catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string Elapsed(DateTime from)
        {
            TimeSpan d = DateTime.Now - from;
            if (d.TotalMinutes >= 1) return ((int)d.TotalMinutes) + " min " + d.Seconds + " s";
            return d.Seconds + " s";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running && MessageBox.Show(this,
                "A rebuild is still running. Close anyway?", "Rebuild kit",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (worker != null) worker.Cancel = true;
            base.OnFormClosing(e);
        }
    }

    // ---------------------------------------------------------------- entry

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] argv)
        {
            bool auto = false;
            foreach (string a in argv)
            {
                string s = a.TrimStart('-', '/').ToLowerInvariant();
                if (s == "auto" || s == "go") auto = true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            Kit kit = Kit.Load(dir);
            if (kit == null)
            {
                MessageBox.Show(
                    "No " + Kit.ManifestName + " next to this exe.\n\n"
                    + "Extract the WHOLE zip somewhere (right-click > Extract All...), "
                    + "then run " + Path.GetFileName(Application.ExecutablePath)
                    + " from inside that folder - it needs the files that came with it.",
                    "Idle Master - Rebuild kit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            Application.Run(new RebuildForm(kit, auto));
            return 0;
        }
    }
}

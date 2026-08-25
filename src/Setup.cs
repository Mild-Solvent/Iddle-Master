// IDLE MASTER SETUP - the thing you download, click, and forget about.
//
// It carries IdleMaster.exe inside it as a resource, so there is one file to
// publish and one file to download. Installing and updating are the same code
// path: write the exe, keep the config, refresh the shortcuts.
//
// Per-user by default (%LOCALAPPDATA%\Programs\IdleMaster), so installing needs
// no administrator. The app itself asks for elevation when it runs, which is
// where it is actually needed.
//
// Built with the in-box .NET Framework compiler, same as the app: C# 5, no
// string interpolation, no ?., no expression-bodied members.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Keep in step with IdleMaster.cs - the installer showing 0.0.0.0 in its file
// properties looks like something half-built.
[assembly: AssemblyTitle("Idle Master Setup")]
[assembly: AssemblyDescription("Installer for Idle Master")]
[assembly: AssemblyProduct("Idle Master")]
[assembly: AssemblyVersion("0.9.0.0")]
[assembly: AssemblyFileVersion("0.9.0.0")]

namespace IdleMasterSetup
{
    internal static class Setup
    {
        public const string Product = "Idle Master";
        public const string ExeName = "IdleMaster.exe";
        public const string SetupName = "IdleMasterSetup.exe";
        public const string ResourceName = "IdleMaster.exe";
        private const string UninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IdleMaster";

        public static string DefaultDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine("Programs", "IdleMaster"));
            }
        }

        // Where a previous install put itself, if there is one.
        public static string InstalledDir()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    if (k == null) return null;
                    object v = k.GetValue("InstallLocation");
                    return v == null ? null : v.ToString();
                }
            }
            catch (Exception) { return null; }
        }

        // The v0.1 releases were a bare exe - no installer, no registry entry.
        // The only trace of such a copy is the process itself, so ask a running
        // one where it lives and offer to update it in place instead of quietly
        // planting a second install next to it.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr h, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        // PROCESS_QUERY_LIMITED_INFORMATION - the one right an ordinary process
        // still has on an elevated one, which the app always is.
        private const uint QueryLimited = 0x1000;

        public static string RunningCopyDir()
        {
            foreach (Process p in Process.GetProcessesByName("IdleMaster"))
            {
                try
                {
                    IntPtr h = OpenProcess(QueryLimited, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        int len = sb.Capacity;
                        if (!QueryFullProcessImageName(h, 0, sb, ref len)) continue;
                        string dir = Path.GetDirectoryName(sb.ToString());
                        if (dir != null && File.Exists(Path.Combine(dir, ExeName)))
                            return dir;
                    }
                    finally { CloseHandle(h); }
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            return null;
        }

        public static bool AppRunning()
        {
            Process[] live = Process.GetProcessesByName("IdleMaster");
            foreach (Process p in live) { try { p.Dispose(); } catch (Exception) { } }
            return live.Length > 0;
        }

        public static string VersionOf(string exe)
        {
            if (exe == null || !File.Exists(exe)) return null;
            try
            {
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(exe);
                return fv.FileMajorPart + "." + fv.FileMinorPart + "." + fv.FileBuildPart;
            }
            catch (Exception) { return null; }
        }

        // The version of the app we are carrying, read from the payload itself so
        // there is only ever one place the version is written down.
        public static string PayloadVersion(string extractedExe)
        {
            string v = VersionOf(extractedExe);
            return v == null ? "?" : v;
        }

        public static byte[] Payload()
        {
            using (Stream s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidOperationException(
                    "This setup was built without " + ResourceName + " inside it.");
                byte[] buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return buf;
            }
        }

        // ---- install

        public static void Install(string dir, bool desktopShortcut, bool logonTask,
                                   bool launch, Action<string> say)
        {
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, ExeName);

            StandDown(dir, say);
            WaitForExit(say);

            // A running exe cannot be overwritten, but it can be renamed out of the
            // way - which is what makes "update while it is running" work at all.
            if (File.Exists(exe))
            {
                try
                {
                    File.Delete(exe);
                }
                catch (Exception)
                {
                    string parked = Path.Combine(dir, "IdleMaster.old-"
                        + DateTime.Now.ToString("yyyyMMddHHmmss") + ".exe");
                    try
                    {
                        File.Move(exe, parked);
                        say("the running copy was moved aside as " + Path.GetFileName(parked));
                    }
                    catch (Exception ex)
                    {
                        throw new IOException(
                            "Idle Master is running and will not let go of " + ExeName + "."
                            + "\r\n\r\nRight-click the tray icon and choose Exit, then run this "
                            + "setup again.\r\n\r\n(" + ex.Message.Split('\n')[0] + ")", ex);
                    }
                }
            }
            Sweep(dir);

            File.WriteAllBytes(exe, Payload());
            say("installed " + ExeName + " " + PayloadVersion(exe));

            // The config is yours. An update must never overwrite it; the app writes
            // a default one on first run if it is missing.
            string ini = Path.Combine(dir, "idlemaster.ini");
            say(File.Exists(ini)
                ? "kept your existing idlemaster.ini"
                : "no config yet - the app writes a default one on first run");

            // Keep the installer around so Windows can uninstall later.
            string here = Application.ExecutablePath;
            string parked2 = Path.Combine(dir, SetupName);
            try
            {
                if (!string.Equals(here, parked2, StringComparison.OrdinalIgnoreCase))
                    File.Copy(here, parked2, true);
            }
            catch (Exception) { }

            Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Product + ".lnk"), exe, dir);
            say("start menu shortcut created");

            string desk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Product + ".lnk");
            if (desktopShortcut) { Shortcut(desk, exe, dir); say("desktop shortcut created"); }
            else Delete(desk);

            Register(dir, PayloadVersion(exe));

            // The exe on disk just changed and now carries an icon it did not
            // have before v0.6; without this nudge Explorer keeps showing the
            // blank one it cached for the old file.
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }   // SHCNE_ASSOCCHANGED
            catch (Exception) { }

            if (logonTask)
            {
                // The task runs with highest privileges, so creating it needs an
                // elevated process - the app does it for us and UAC asks you.
                try
                {
                    Process p = Process.Start(new ProcessStartInfo(exe, "--installtask")
                    { UseShellExecute = true, Verb = "runas" });
                    if (p != null) p.WaitForExit(20000);
                    say("logon task requested");
                }
                catch (Exception ex) { say("! logon task refused: " + ex.Message.Split('\n')[0]); }
            }

            // Anyone updating by hand from v0.1/v0.2 (no update button back then)
            // very likely still has the old copy running - it survived the rename
            // and is still hunting with the old code. Launching the new one next
            // to it would just mean two tray icons and a sentry that cannot take
            // the watch, so say what is actually going on instead.
            if (AppRunning())
            {
                say("! the OLD version is still running - right-click its tray icon,");
                say("  choose Exit, then start Idle Master from the Start menu.");
                if (launch) say("  (not launching the new one until then)");
                return;
            }

            if (launch)
            {
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
                catch (Exception ex) { say("! could not launch: " + ex.Message.Split('\n')[0]); }
            }
        }

        // Ask a running copy to stop hunting, so we are not fighting a sentry that
        // is holding handles while we replace the binary underneath it.
        private static void StandDown(string dir, Action<string> say)
        {
            string exe = Path.Combine(dir, ExeName);
            if (!File.Exists(exe)) return;
            if (!AppRunning()) return;
            try
            {
                Process p = Process.Start(new ProcessStartInfo(exe, "--unwatch")
                { UseShellExecute = false, CreateNoWindow = true });
                if (p != null) p.WaitForExit(8000);
                say("asked the running copy to stand down");
            }
            catch (Exception)
            {
                // The app requires administrator and this setup does not, so the
                // quiet start dies with "elevation required" (it has since v0.1).
                // Retry through the shell, where UAC can ask; declining is fine -
                // the rename path below still gets the binary replaced.
                try
                {
                    Process p = Process.Start(new ProcessStartInfo(exe, "--unwatch")
                    { UseShellExecute = true, Verb = "runas" });
                    if (p != null) p.WaitForExit(8000);
                    say("asked the running copy to stand down");
                }
                catch (Exception) { }
            }
        }

        // When the app updates itself it launches this and then closes, so give it
        // a moment to actually be gone before we start replacing files underneath it.
        private static void WaitForExit(Action<string> say)
        {
            bool waited = false;
            for (int i = 0; i < 16; i++)
            {
                Process[] live = Process.GetProcessesByName("IdleMaster");
                try
                {
                    if (live.Length == 0)
                    {
                        if (waited) say("it has closed");
                        return;
                    }
                    if (!waited) { say("waiting for Idle Master to close..."); waited = true; }
                }
                finally
                {
                    foreach (Process p in live) { try { p.Dispose(); } catch (Exception) { } }
                }
                Thread.Sleep(500);
            }
            // Still up after 8s - it is probably a window somebody left open. The
            // rename path below handles that, or fails with something readable.
        }

        private static void Sweep(string dir)
        {
            try
            {
                // Two park styles exist in the wild: this setup's
                // "IdleMaster.old-*.exe" and build.ps1's "IdleMaster.exe.old-*".
                foreach (string old in Directory.GetFiles(dir, "IdleMaster.old*.exe"))
                {
                    try { File.Delete(old); }
                    catch (Exception) { }   // still running; next time
                }
                foreach (string old in Directory.GetFiles(dir, "IdleMaster.exe.old*"))
                {
                    try { File.Delete(old); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // ---- uninstall

        public static void Uninstall(Action<string> say, bool keepConfig)
        {
            string dir = InstalledDir();
            if (dir == null || !Directory.Exists(dir)) { say("nothing is installed."); return; }

            StandDown(dir, say);
            RemoveTask(say);

            Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Product + ".lnk"));
            Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Product + ".lnk"));
            say("shortcuts removed");

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
            catch (Exception) { }

            foreach (string f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                bool config = name.Equals("idlemaster.ini", StringComparison.OrdinalIgnoreCase);
                if (keepConfig && config) continue;
                if (name.Equals(SetupName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch (Exception) { }
            }
            say(keepConfig
                ? "removed. Your idlemaster.ini was left in " + dir
                : "removed everything in " + dir);
        }

        private static void RemoveTask(Action<string> say)
        {
            try
            {
                Process p = Process.Start(new ProcessStartInfo("schtasks.exe",
                    "/Delete /TN \"IdleMaster Sentry\" /F")
                { UseShellExecute = false, CreateNoWindow = true });
                if (p != null) p.WaitForExit(10000);
            }
            catch (Exception) { }
        }

        // ---- windows plumbing

        private static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        // WScript.Shell through late binding, so the build needs no COM reference.
        private static void Shortcut(string lnk, string target, string workingDir)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnk });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link,
                    new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                    new object[] { workingDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                    new object[] { "Reclaim RAM; keep Sunshine and Tailscale alive" });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                    new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception) { }
        }

        private static void Register(string dir, string version)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", Product);
                    k.SetValue("DisplayVersion", version);
                    k.SetValue("Publisher", "Mild-Solvent");
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("DisplayIcon", Path.Combine(dir, ExeName));
                    k.SetValue("UninstallString",
                        "\"" + Path.Combine(dir, SetupName) + "\" --uninstall");
                    k.SetValue("URLInfoAbout", "https://github.com/Mild-Solvent/Iddle-Master");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try
                    {
                        FileInfo fi = new FileInfo(Path.Combine(dir, ExeName));
                        k.SetValue("EstimatedSize", (int)(fi.Length / 1024), RegistryValueKind.DWord);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // ---- entry point

        [STAThread]
        public static int Main(string[] argv)
        {
            bool silent = false, uninstall = false, desktop = false, relaunch = false, quiet = false;
            string dir = null;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i].TrimStart('-', '/').ToLowerInvariant();
                if (a == "s" || a == "silent") silent = true;
                else if (a == "quiet") { silent = true; quiet = true; }  // ...and no error box either
                else if (a == "uninstall" || a == "remove") uninstall = true;
                else if (a == "desktop") desktop = true;   // silent + a desktop shortcut too
                else if (a == "relaunch") relaunch = true; // silent + start the app when done
                else if ((a == "dir" || a == "d") && i + 1 < argv.Length) dir = argv[++i];
            }

            if (silent || uninstall)
            {
                StringBuilder said = new StringBuilder();
                Action<string> say = delegate(string s) { Console.WriteLine(s); said.AppendLine(s); };
                try
                {
                    if (uninstall) Uninstall(say, true);
                    else Install(dir != null ? dir
                            : (InstalledDir() ?? RunningCopyDir() ?? DefaultDir),
                                 desktop, false, relaunch, say);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("failed: " + ex.Message);
                    // The one-click update from inside the app runs this way, and
                    // a winexe has no console - so a failure must still be seen.
                    if (!quiet)
                        MessageBox.Show(said + "\r\nfailed: " + ex.Message, "Idle Master Setup",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dir));
            return 0;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox path;
        private readonly CheckBox desktop, logon, launch;
        private readonly TextBox log;
        private readonly Button install, remove;

        private static readonly Color Bg = Color.FromArgb(18, 18, 22);
        private static readonly Color Fg = Color.FromArgb(225, 225, 232);
        private static readonly Color Dim = Color.FromArgb(120, 120, 132);

        public SetupForm(string preset)
        {
            // Either a registered install, the app pointing us at its own folder
            // to update a portable copy in place, or - for the v0.1 bare-exe
            // releases that never touched the registry - wherever a running copy
            // says it lives.
            string already = Setup.InstalledDir();
            bool foundRunning = false;
            if (already == null)
            {
                already = Setup.RunningCopyDir();
                foundRunning = already != null;
            }
            if (preset != null && File.Exists(Path.Combine(preset, Setup.ExeName)))
                already = preset;
            bool updating = already != null && Directory.Exists(already)
                            && File.Exists(Path.Combine(already, Setup.ExeName));

            Text = "Idle Master Setup";
            ClientSize = new Size(560, 430);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            Label title = new Label();
            title.Text = "IDLE MASTER";
            title.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(120, 200, 255);
            title.SetBounds(20, 16, 400, 34);
            Controls.Add(title);

            Label sub = new Label();
            string have = updating ? Setup.VersionOf(Path.Combine(already, Setup.ExeName)) : null;
            sub.Text = updating
                ? "Updating your install" + (have != null ? " (you have " + have + ")" : "")
                : "Reclaim RAM. Keep Sunshine and Tailscale alive.";
            sub.ForeColor = Dim;
            sub.SetBounds(22, 52, 520, 20);
            Controls.Add(sub);

            Label where = new Label();
            where.Text = "Install to";
            where.SetBounds(22, 88, 100, 18);
            Controls.Add(where);

            path = new TextBox();
            path.Text = preset != null ? preset : (updating ? already : Setup.DefaultDir);
            path.SetBounds(22, 108, 430, 22);
            path.BackColor = Color.FromArgb(14, 14, 18);
            path.ForeColor = Fg;
            path.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(path);

            Button browse = Small("Browse", 458, 107, 80);
            browse.Click += delegate
            {
                using (FolderBrowserDialog f = new FolderBrowserDialog())
                {
                    f.SelectedPath = path.Text;
                    if (f.ShowDialog(this) == DialogResult.OK) path.Text = f.SelectedPath;
                }
            };

            desktop = Check("Desktop shortcut", 22, 144, true);
            launch = Check("Run Idle Master when this finishes", 22, 170, true);
            logon = Check("Start hunting at every logon (asks for administrator)", 22, 196, false);

            Label note = new Label();
            note.Text = "No administrator needed to install - it goes in your own profile. "
                      + "Your idlemaster.ini is never overwritten by an update.";
            note.ForeColor = Dim;
            note.SetBounds(22, 224, 516, 34);
            Controls.Add(note);

            log = new TextBox();
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.SetBounds(22, 264, 516, 110);
            log.BackColor = Color.FromArgb(12, 12, 15);
            log.ForeColor = Color.FromArgb(180, 220, 190);
            log.Font = new Font("Consolas", 8.5f);
            log.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(log);

            install = new Button();
            install.Text = updating ? "Update" : "Install";
            install.SetBounds(330, 386, 100, 32);
            install.BackColor = Color.FromArgb(28, 92, 58);
            install.ForeColor = Color.White;
            install.FlatStyle = FlatStyle.Flat;
            install.FlatAppearance.BorderSize = 0;
            install.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            install.Click += delegate { Go(); };
            Controls.Add(install);
            AcceptButton = install;

            remove = Small("Uninstall", 22, 388, 100);
            remove.Enabled = updating;
            remove.Click += delegate { Remove(); };

            Button close = Small("Close", 438, 388, 100);
            close.Click += delegate { Close(); };
            CancelButton = close;

            Say(updating
                ? (foundRunning
                    ? "Found a running copy in " + already + " - updating it in place."
                    : "Found an install in " + already)
                : "Ready to install.");
        }

        private Button Small(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.BackColor = Color.FromArgb(42, 42, 52);
            b.ForeColor = Fg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        private CheckBox Check(string text, int x, int y, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.SetBounds(x, y, 500, 22);
            c.ForeColor = Fg;
            Controls.Add(c);
            return c;
        }

        private void Say(string s)
        {
            log.AppendText(s + Environment.NewLine);
        }

        private void Go()
        {
            install.Enabled = remove.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                Setup.Install(path.Text.Trim(), desktop.Checked, logon.Checked,
                    launch.Checked, Say);
                Say("");
                Say("Done.");
                remove.Enabled = true;
            }
            catch (Exception ex)
            {
                Say("! " + ex.Message);
                MessageBox.Show(this, ex.Message, "Idle Master Setup",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                install.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        private void Remove()
        {
            DialogResult keep = MessageBox.Show(this,
                "Keep your idlemaster.ini (the kill lists and settings)?"
                + "\n\nYes = keep it, No = delete everything.",
                "Uninstall Idle Master", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (keep == DialogResult.Cancel) return;

            install.Enabled = remove.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try { Setup.Uninstall(Say, keep == DialogResult.Yes); }
            catch (Exception ex) { Say("! " + ex.Message); }
            finally
            {
                install.Enabled = true;
                Cursor = Cursors.Default;
            }
        }
    }
}

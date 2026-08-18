// IDLE MASTER - the backup kit.
//
// "Backup kit" builds one zip that can put a fresh Windows back the way this
// one is: the apps you tick (reinstalled through winget), the files and
// folders you tick (copied back where they were), Idle Master itself with your
// idlemaster.ini, and two scripts a lot of people run on a new install anyway -
// Zoicware's RemoveWindowsAI and Chris Titus's WinUtil.
//
// Inside the zip is IdleMasterRebuild.exe (carried inside this exe as a
// resource, the same trick the installer uses), a plain-text rebuild.ini that
// says what you chose, an apps.json winget can import by hand, and files\.
// Nothing here changes this machine: it reads, it zips, that is all.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IdleMaster
{
    // ------------------------------------------------------------- inventory

    internal sealed class InstalledApp
    {
        public string Name = "";
        public string Id = "";
        public string Version = "";
        public string Source = "";      // "winget", "msstore", or "" (no source = no reinstall)

        // ARP\... and MSIX\... are winget's names for things it found but has
        // no package for. They cannot be reinstalled from a kit.
        public bool Reinstallable
        {
            get
            {
                return Source.Length > 0
                    && !Id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase)
                    && !Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Ships with Windows, or is put back by Windows on its own - ticking
        // it would only make the rebuild slower.
        public bool Bundled
        {
            get
            {
                string i = Id.ToLowerInvariant();
                return i == "microsoft.appinstaller" || i == "microsoft.edge"
                    || i == "microsoft.edgewebview2runtime" || i == "microsoft.onedrive"
                    || i.StartsWith("microsoft.ui.xaml") || i.StartsWith("microsoft.vclibs")
                    || i.StartsWith("microsoft.dotnet.desktopruntime")
                    || i.StartsWith("microsoft.dotnet.runtime")
                    || i.StartsWith("microsoft.dotnet.aspnetcore")
                    || i.StartsWith("microsoft.vcredist");
            }
        }
    }

    internal static class AppInventory
    {
        // Runs "winget list" and reads its table. The columns are wherever the
        // header says they are - winget widens them to fit, so nothing is
        // hard-coded, and the Id column is the one thing it never truncates.
        public static List<InstalledApp> Scan(Action<string> log)
        {
            List<InstalledApp> found = new List<InstalledApp>();
            string text;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("winget",
                    "list --accept-source-agreements --disable-interactivity");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                Process p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("could not start winget");
                p.StandardError.ReadToEndAsync();
                text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
            }
            catch (Exception ex)
            {
                log("   ! winget is not available: " + ex.Message.Split('\n')[0]);
                log("     (App Installer from the Microsoft Store provides it)");
                return found;
            }

            string[] lines = text.Replace("\r", "").Split('\n');
            int header = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("Name") && l.IndexOf(" Id ") > 0 && l.IndexOf("Version") > 0)
                { header = i; break; }
            }
            if (header < 0)
            {
                log("   ! could not read winget's table (" + lines.Length + " lines)");
                return found;
            }

            string h = lines[header];
            int cId = h.IndexOf(" Id ") + 1;
            int cVer = h.IndexOf("Version", cId);
            int cAvail = h.IndexOf("Available", cVer);
            int cSrc = h.IndexOf("Source", cVer);
            int verEnd = cAvail > 0 ? cAvail : (cSrc > 0 ? cSrc : -1);

            for (int i = header + 2; i < lines.Length; i++)     // +2 skips the dashes
            {
                string l = lines[i];
                if (l.Trim().Length == 0) continue;
                if (l.Length < cVer) continue;
                InstalledApp a = new InstalledApp();
                a.Name = Cut(l, 0, cId);
                a.Id = Cut(l, cId, cVer);
                a.Version = Cut(l, cVer, verEnd);
                a.Source = cSrc > 0 ? Cut(l, cSrc, -1) : "";
                if (a.Id.Length == 0) continue;
                found.Add(a);
            }
            return found;
        }

        private static string Cut(string s, int from, int to)
        {
            if (from >= s.Length) return "";
            if (to < 0 || to > s.Length) to = s.Length;
            if (to <= from) return "";
            return s.Substring(from, to - from).Trim();
        }
    }

    // ------------------------------------------------------------------- kit

    internal sealed class KitEntry
    {
        public string Path = "";
        public bool IsDir;
        public long Bytes = -1;         // -1 = still counting
        public string InKit = "";       // files\Documents
    }

    internal sealed class KitOptions
    {
        public bool InstallIdleMaster = true;
        public bool RemoveAi = true;
        public bool WinUtil = true;
        public string WinUtilPreset = "Standard";
        public bool WinUtilOpen = true;
    }

    // Writes the zip. Everything it puts in is listed in rebuild.ini, so the
    // exe on the other end never has to guess.
    internal sealed class KitWriter
    {
        public const string RebuildExe = "IdleMasterRebuild.exe";
        public const string SetupExe = "IdleMasterSetup.exe";

        private readonly Action<string> log;
        public volatile bool Cancel;
        public volatile string Phase = "";
        public long DoneBytes;
        public long TotalBytes;
        public int Files;
        public int Skipped;

        public KitWriter(Action<string> logger) { log = logger; }

        public void Build(string zipPath, List<InstalledApp> apps, List<KitEntry> entries,
                          KitOptions opt)
        {
            TotalBytes = 0;
            foreach (KitEntry e in entries) if (e.Bytes > 0) TotalBytes += e.Bytes;
            AssignKitNames(entries);

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string setup = System.IO.Path.Combine(App.Dir, SetupExe);
            bool bundleSetup = File.Exists(setup) && SameVersion(setup);

            using (FileStream fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Phase = "the rebuild exe";
                Resource(zip, RebuildExe, RebuildExe);

                Phase = "rebuild.ini";
                Text(zip, "rebuild.ini", Manifest(apps, entries, opt, profile));
                Text(zip, "apps.json", AppsJson(apps));
                Text(zip, "README.txt", Readme(apps.Count, entries.Count, bundleSetup));

                if (bundleSetup)
                {
                    Phase = SetupExe;
                    FileEntry(zip, setup, SetupExe, CompressionLevel.Optimal);
                }
                if (File.Exists(Config.Path_))
                    FileEntry(zip, Config.Path_, "idlemaster.ini", CompressionLevel.Optimal);

                foreach (KitEntry e in entries)
                {
                    if (Cancel) break;
                    if (e.IsDir) Tree(zip, e.Path, e.InKit, zipPath);
                    else FileEntry(zip, e.Path, e.InKit, CompressionLevel.Fastest);
                }
            }
        }

        // files\Documents, files\Desktop, files\notes.txt - and files\Documents-2
        // when two picks share a leaf name.
        private static void AssignKitNames(List<KitEntry> entries)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KitEntry e in entries)
            {
                string leaf = System.IO.Path.GetFileName(e.Path.TrimEnd('\\'));
                if (leaf.Length == 0) leaf = e.Path.Replace(":", "").Replace("\\", "");   // "C:\" -> "C"
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
                string name = leaf;
                int n = 2;
                while (used.Contains(name)) name = leaf + "-" + (n++);
                used.Add(name);
                e.InKit = "files\\" + name;
            }
        }

        private static bool SameVersion(string exe)
        {
            try
            {
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(exe);
                return (fv.FileMajorPart + "." + fv.FileMinorPart + "." + fv.FileBuildPart) == App.Version;
            }
            catch (Exception) { return false; }
        }

        private void Resource(ZipArchive zip, string resource, string entry)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException(
                    "This build has no " + resource + " inside it - build.ps1 embeds it.");
                ZipArchiveEntry z = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using (Stream o = z.Open()) s.CopyTo(o);
            }
        }

        private static void Text(ZipArchive zip, string entry, string text)
        {
            ZipArchiveEntry z = zip.CreateEntry(entry, CompressionLevel.Optimal);
            using (Stream o = z.Open())
            {
                byte[] b = new UTF8Encoding(false).GetBytes(text);
                o.Write(b, 0, b.Length);
            }
        }

        private void FileEntry(ZipArchive zip, string path, string entry, CompressionLevel level)
        {
            try
            {
                using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                     FileShare.ReadWrite | FileShare.Delete))
                {
                    ZipArchiveEntry z = zip.CreateEntry(entry.Replace('\\', '/'), level);
                    try { z.LastWriteTime = File.GetLastWriteTime(path); } catch (Exception) { }
                    using (Stream o = z.Open())
                    {
                        byte[] buf = new byte[256 * 1024];
                        int n;
                        while ((n = f.Read(buf, 0, buf.Length)) > 0)
                        {
                            o.Write(buf, 0, n);
                            DoneBytes += n;
                            if (Cancel) return;
                        }
                    }
                }
                Files++;
            }
            catch (Exception ex)
            {
                Skipped++;
                log("   skipped " + path + " (" + ex.Message.Split('\n')[0].Trim() + ")");
            }
        }

        private void Tree(ZipArchive zip, string dir, string inKit, string zipPath)
        {
            if (Cancel) return;
            Phase = inKit.Substring(6);      // past "files\"
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) { Skipped++; log("   skipped " + dir + " (" + ex.Message.Split('\n')[0] + ")"); return; }
            foreach (string f in files)
            {
                if (Cancel) return;
                // The kit itself, if you are writing it into a folder you ticked.
                if (f.Equals(zipPath, StringComparison.OrdinalIgnoreCase)) continue;
                FileEntry(zip, f, inKit + "\\" + System.IO.Path.GetFileName(f), CompressionLevel.Fastest);
            }
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            foreach (string d in dirs)
            {
                if (Cancel) return;
                // Junctions and symlinks ("My Music" inside Documents, OneDrive
                // mirrors) would loop or double everything - the real folder is
                // reachable on its own if you want it.
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception) { continue; }
                Tree(zip, d, inKit + "\\" + System.IO.Path.GetFileName(d), zipPath);
            }
        }

        // ---- the texts

        private static string Manifest(List<InstalledApp> apps, List<KitEntry> entries,
                                       KitOptions opt, string profile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Idle Master rebuild kit - read by " + RebuildExe + ".");
            sb.AppendLine("# Plain text: delete a line to skip that app or folder, flip a 1 to 0 to");
            sb.AppendLine("# change what is ticked when the exe opens. Every step still asks.");
            sb.AppendLine();
            sb.AppendLine("[kit]");
            sb.AppendLine("created = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("machine = " + Environment.MachineName);
            sb.AppendLine("profile = " + profile);
            sb.AppendLine("idlemaster = " + App.Version);
            sb.AppendLine();
            sb.AppendLine("[options]");
            sb.AppendLine("files = " + (entries.Count > 0 ? "1" : "0"));
            sb.AppendLine("apps = " + (apps.Count > 0 ? "1" : "0"));
            sb.AppendLine("installidlemaster = " + (opt.InstallIdleMaster ? "1" : "0"));
            sb.AppendLine("removeai = " + (opt.RemoveAi ? "1" : "0"));
            sb.AppendLine("winutil = " + (opt.WinUtil ? "1" : "0"));
            sb.AppendLine("winutilpreset = " + opt.WinUtilPreset);
            sb.AppendLine("winutilopen = " + (opt.WinUtilOpen ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("[apps]");
            sb.AppendLine("# source  id  name");
            foreach (InstalledApp a in apps)
                sb.AppendLine(a.Source + "  " + a.Id + "  " + a.Name);
            sb.AppendLine();
            sb.AppendLine("[files]");
            sb.AppendLine("# where it is in the kit = where it came from");
            foreach (KitEntry e in entries)
                sb.AppendLine(e.InKit + " = " + e.Path);
            return sb.ToString();
        }

        // The format "winget export" writes and "winget import" reads, so the
        // list is usable without the exe: winget import -i apps.json
        private static string AppsJson(List<InstalledApp> apps)
        {
            Dictionary<string, List<InstalledApp>> bySource = new Dictionary<string, List<InstalledApp>>();
            List<string> order = new List<string>();
            foreach (InstalledApp a in apps)
            {
                List<InstalledApp> l;
                if (!bySource.TryGetValue(a.Source, out l))
                {
                    l = new List<InstalledApp>();
                    bySource[a.Source] = l;
                    order.Add(a.Source);
                }
                l.Add(a);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"$schema\": \"https://aka.ms/winget-packages.schema.2.0.json\",");
            sb.AppendLine("  \"CreationDate\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff-00:00") + "\",");
            sb.AppendLine("  \"Sources\": [");
            for (int i = 0; i < order.Count; i++)
            {
                string src = order[i];
                sb.AppendLine("    {");
                sb.AppendLine("      \"Packages\": [");
                List<InstalledApp> l = bySource[src];
                for (int j = 0; j < l.Count; j++)
                    sb.AppendLine("        { \"PackageIdentifier\": \"" + Json(l[j].Id) + "\" }"
                        + (j + 1 < l.Count ? "," : ""));
                sb.AppendLine("      ],");
                if (src == "msstore")
                {
                    sb.AppendLine("      \"SourceDetails\": { \"Argument\": \"https://storeedgefd.dsx.mp.microsoft.com/v9.0\","
                        + " \"Identifier\": \"StoreEdgeFD\", \"Name\": \"msstore\", \"Type\": \"Microsoft.Rest\" }");
                }
                else
                {
                    sb.AppendLine("      \"SourceDetails\": { \"Argument\": \"https://cdn.winget.microsoft.com/cache\","
                        + " \"Identifier\": \"Microsoft.Winget.Source_8wekyb3d8bbwe\", \"Name\": \"" + Json(src)
                        + "\", \"Type\": \"Microsoft.PreIndexed.Package\" }");
                }
                sb.AppendLine("    }" + (i + 1 < order.Count ? "," : ""));
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Json(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Readme(int apps, int sets, bool bundledSetup)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("IDLE MASTER REBUILD KIT");
            sb.AppendLine("=======================");
            sb.AppendLine();
            sb.AppendLine("Made by Idle Master " + App.Version + " on " + Environment.MachineName
                + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".");
            sb.AppendLine();
            sb.AppendLine("On a fresh Windows:");
            sb.AppendLine();
            sb.AppendLine("  1. Extract this WHOLE zip somewhere (right-click > Extract All...).");
            sb.AppendLine("  2. Run " + RebuildExe + " from inside that folder. It asks for administrator.");
            sb.AppendLine("  3. Tick what you want, press Rebuild, read the log.");
            sb.AppendLine();
            sb.AppendLine("What is in here:");
            sb.AppendLine();
            sb.AppendLine("  " + RebuildExe.PadRight(24) + "the thing that does the work");
            sb.AppendLine("  rebuild.ini             what you picked, plain text - edit it if you like");
            sb.AppendLine("  apps.json               " + apps + " app(s), winget import format:  winget import -i apps.json");
            sb.AppendLine("  files\\                  " + sets + " folder(s)/file(s), exactly as they were");
            if (bundledSetup)
                sb.AppendLine("  " + SetupExe.PadRight(24) + "Idle Master's installer, this same version");
            sb.AppendLine("  idlemaster.ini          your Idle Master config");
            sb.AppendLine();
            sb.AppendLine("The Windows AI removal (github.com/zoicware/RemoveWindowsAI) and WinUtil");
            sb.AppendLine("(christitus.com/win) steps download their scripts when they run; they are");
            sb.AppendLine("not in this kit and need the machine online.");
            sb.AppendLine();
            sb.AppendLine("https://github.com/Mild-Solvent/Iddle-Master");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------- the window

    internal sealed class BackupForm : Form
    {
        private readonly Action<string> log;
        private readonly BufferedListView apps, files;
        private readonly ColumnHeader aName, aId, aVer, aSrc, fPath, fSize;
        private readonly Button btnAll, btnNone, btnRescan, btnAddDir, btnAddFile, btnRemove, btnBuild;
        private readonly CheckBox chkIdle, chkAi, chkWinUtil, chkOpen;
        private readonly ComboBox preset;
        private readonly Label appsCap, filesCap, status;
        private readonly System.Windows.Forms.Timer timer;
        private KitWriter writer;
        private bool scanning, building, filling;
        private string builtPath;

        public BackupForm(Action<string> logger)
        {
            log = logger;

            Theme.Form(this);
            Text = "IDLE MASTER - backup kit";
            Size = new Size(840, 700);
            MinimumSize = new Size(700, 560);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            Label cap = Theme.Caption("BACKUP KIT");
            cap.SetBounds(16, 12, 180, 18);
            Controls.Add(cap);

            Label hint = Theme.Hint("tick what a fresh Windows should get back - Build writes one zip that puts it all back");
            hint.Font = Theme.Small();
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.SetBounds(200, 12, 608, 18);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(hint);

            // ---- apps

            appsCap = Theme.Hint("Apps to reinstall through winget");
            appsCap.SetBounds(16, 40, 580, 18);
            Controls.Add(appsCap);

            btnRescan = Tiny("Rescan", 808 - 66, 36);
            btnRescan.Click += delegate { ScanApps(); };
            btnNone = Tiny("None", 808 - 66 - 60, 36);
            btnNone.Click += delegate { TickAll(false); };
            btnAll = Tiny("All", 808 - 66 - 120, 36);
            btnAll.Click += delegate { TickAll(true); };

            apps = List_();
            aName = apps.Columns.Add("App", 250);
            aId = apps.Columns.Add("winget id", 300);
            aVer = apps.Columns.Add("Version", 110);
            aSrc = apps.Columns.Add("Source", 70);
            apps.SetBounds(16, 62, 792, 236);
            apps.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            apps.Resize += delegate { SizeColumns(); };
            apps.ItemChecked += delegate { if (!filling) UpdateButtons(); };
            Controls.Add(apps);

            // ---- files

            filesCap = Theme.Hint("Files and folders to keep");
            filesCap.SetBounds(16, 310, 300, 18);
            Controls.Add(filesCap);

            btnRemove = Tiny("Remove", 808 - 66, 306);
            btnRemove.Click += delegate { RemoveSelected(); };
            btnAddFile = Tiny("Add file...", 808 - 66 - 80, 306);
            btnAddFile.Width = 76;
            btnAddFile.Click += delegate { AddFile(); };
            btnAddDir = Tiny("Add folder...", 808 - 66 - 80 - 94, 306);
            btnAddDir.Width = 90;
            btnAddDir.Click += delegate { AddFolder(); };
            foreach (Button b in new Button[] { btnRemove, btnAddFile, btnAddDir, btnRescan, btnNone, btnAll })
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            files = List_();
            fPath = files.Columns.Add("Path", 640);
            fSize = files.Columns.Add("Size", 100, HorizontalAlignment.Right);
            files.SetBounds(16, 332, 792, 700 - 332 - 172);
            files.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            files.Resize += delegate { SizeColumns(); };
            files.ItemChecked += delegate { if (!filling) UpdateButtons(); };
            Controls.Add(files);
            SizeColumns();

            // ---- what else the rebuild should do

            int oy = 700 - 164;
            Label opts = Theme.Hint("On the new machine, also:");
            opts.SetBounds(16, oy, 200, 18);
            opts.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(opts);

            chkIdle = Opt("Install Idle Master with this idlemaster.ini", 16, oy + 20, 300);
            chkAi = Opt("Remove Copilot, Recall and the other Windows AI  (zoicware/RemoveWindowsAI)", 320, oy + 20, 480);
            chkWinUtil = Opt("Apply Chris Titus WinUtil tweaks, preset:", 16, oy + 44, 250);
            preset = new ComboBox();
            preset.DropDownStyle = ComboBoxStyle.DropDownList;
            preset.Items.AddRange(new object[] { "Standard", "Minimal", "Advanced" });
            preset.SelectedIndex = 0;
            preset.SetBounds(268, oy + 44, 100, 22);
            preset.FlatStyle = FlatStyle.Flat;
            preset.BackColor = Theme.Input;
            preset.ForeColor = Theme.Fg;
            preset.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(preset);
            chkOpen = Opt("...and leave WinUtil open at the end for anything more", 380, oy + 44, 420);

            // ---- bottom row

            status = Theme.Hint("");
            status.Font = Theme.Small();
            status.SetBounds(16, 700 - 78, 500, 20);
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(status);

            btnBuild = Theme.Action("Build kit...");
            btnBuild.SetBounds(808 - 280, 700 - 84, 280, 30);
            btnBuild.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnBuild.Click += delegate { if (building) StopBuild(); else Build(); };
            Controls.Add(btnBuild);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 300;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            SuggestFolders();
            ScanApps();
        }

        // ---- widgets

        private BufferedListView List_()
        {
            BufferedListView l = new BufferedListView();
            l.View = View.Details;
            l.CheckBoxes = true;
            l.FullRowSelect = true;
            l.HideSelection = false;
            l.ShowItemToolTips = true;
            l.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            l.BorderStyle = BorderStyle.FixedSingle;
            l.BackColor = Theme.Input;
            l.ForeColor = Theme.Fg;
            l.OwnerDraw = true;
            l.DrawColumnHeader += DrawHeader;
            l.DrawItem += delegate(object s, DrawListViewItemEventArgs a) { a.DrawDefault = true; };
            l.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs a) { a.DrawDefault = true; };
            return l;
        }

        private Button Tiny(string text, int x, int y)
        {
            Button b = Theme.Quiet(text);
            b.Font = Theme.Small();
            b.SetBounds(x, y, 56, 22);
            Controls.Add(b);
            return b;
        }

        private CheckBox Opt(string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = true;
            c.SetBounds(x, y, w, 22);
            c.ForeColor = Theme.Fg;
            c.FlatStyle = FlatStyle.Flat;
            c.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(c);
            return c;
        }

        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                e.Graphics.FillRectangle(b, e.Bounds);
            ListView owner = sender as ListView;
            bool right = owner == files && e.ColumnIndex == 1;
            Rectangle r = e.Bounds;
            r.Inflate(-6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, r, Theme.Dim,
                (right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter);
        }

        private void SizeColumns()
        {
            // The scrollbar is not part of ClientSize until it appears, so
            // leave room for it or the header grows a bar of its own.
            int sb = SystemInformation.VerticalScrollBarWidth + 4;
            int w = apps.ClientSize.Width - aVer.Width - aSrc.Width - sb;
            if (w > 200) { aName.Width = w * 45 / 100; aId.Width = w - aName.Width; }
            int fw = files.ClientSize.Width - fSize.Width - sb;
            if (fw > 100) fPath.Width = fw;
        }

        // ---- apps

        private void ScanApps()
        {
            if (scanning) return;
            scanning = true;
            btnRescan.Enabled = false;
            appsCap.Text = "Apps to reinstall through winget  -  asking winget what is installed...";
            log("-- backup kit: reading the installed apps (winget list)");

            Thread t = new Thread(delegate()
            {
                List<InstalledApp> found = AppInventory.Scan(log);
                try { BeginInvoke((Action)delegate { ShowApps(found); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ShowApps(List<InstalledApp> found)
        {
            scanning = false;
            btnRescan.Enabled = true;

            found.Sort(delegate(InstalledApp a, InstalledApp b)
            {
                int r = (b.Reinstallable ? 1 : 0) - (a.Reinstallable ? 1 : 0);
                if (r != 0) return r;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            filling = true;
            apps.BeginUpdate();
            apps.Items.Clear();
            int can = 0;
            foreach (InstalledApp a in found)
            {
                ListViewItem row = new ListViewItem(a.Name);
                row.Tag = a;
                row.UseItemStyleForSubItems = false;
                row.SubItems.Add(a.Reinstallable ? a.Id : "no winget package - reinstall by hand");
                row.SubItems.Add(a.Version);
                row.SubItems.Add(a.Reinstallable ? a.Source : "-");
                row.SubItems[1].ForeColor = Theme.Dim;
                row.SubItems[2].ForeColor = Theme.Dim;
                row.SubItems[3].ForeColor = a.Reinstallable ? Theme.Accent : Theme.Dim;
                if (!a.Reinstallable) row.ForeColor = Theme.Dim;
                row.ToolTipText = a.Reinstallable ? "" : a.Id;
                apps.Items.Add(row);
                // Ticking is set after the add - a detached row forgets it.
                row.Checked = a.Reinstallable && !a.Bundled;
                if (a.Reinstallable) can++;
            }
            apps.EndUpdate();
            filling = false;

            appsCap.Text = "Apps to reinstall through winget  -  " + can + " of " + found.Count
                + " installed apps have a winget package";
            log("   " + found.Count + " apps found, " + can + " reinstallable through winget.");
            UpdateButtons();
        }

        private void TickAll(bool on)
        {
            filling = true;
            foreach (ListViewItem row in apps.Items)
            {
                InstalledApp a = (InstalledApp)row.Tag;
                row.Checked = on && a.Reinstallable;
            }
            filling = false;
            UpdateButtons();
        }

        // ---- files

        // The usual suspects, sizes counted in the background. The first three
        // are ticked; Downloads and the media folders are listed but off -
        // they are usually the big ones, and yours to decide.
        private void SuggestFolders()
        {
            AddEntry(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true, true);
            AddEntry(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true, true);
            AddEntry(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), true, true);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddEntry(Path.Combine(profile, "Downloads"), true, false);
            AddEntry(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), true, false);
            AddEntry(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), true, false);
            AddEntry(Path.Combine(profile, ".ssh"), true, true);
        }

        private void AddEntry(string path, bool isDir, bool ticked)
        {
            if (path == null || path.Length == 0) return;
            if (isDir ? !Directory.Exists(path) : !File.Exists(path)) return;
            foreach (ListViewItem r in files.Items)
                if (((KitEntry)r.Tag).Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return;

            KitEntry e = new KitEntry();
            e.Path = path;
            e.IsDir = isDir;
            ListViewItem row = new ListViewItem(path);
            row.Tag = e;
            row.UseItemStyleForSubItems = false;
            row.SubItems.Add("...");
            row.SubItems[1].ForeColor = Theme.Dim;
            filling = true;
            files.Items.Add(row);
            row.Checked = ticked;
            filling = false;

            Thread t = new Thread(delegate()
            {
                long n = 0;
                try { n = isDir ? DirBytes(path) : new FileInfo(path).Length; }
                catch (Exception) { }
                e.Bytes = n;
                try { BeginInvoke((Action)delegate { row.SubItems[1].Text = Nice(n); UpdateButtons(); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
            UpdateButtons();
        }

        private static long DirBytes(string dir)
        {
            long n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    try { n += new FileInfo(f).Length; } catch (Exception) { }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; }
                    catch (Exception) { continue; }
                    n += DirBytes(d);
                }
            }
            catch (Exception) { }
            return n;
        }

        private void AddFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Pick a folder to keep in the kit";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) == DialogResult.OK) AddEntry(d.SelectedPath, true, true);
            }
        }

        private void AddFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Pick files to keep in the kit";
                d.Multiselect = true;
                d.CheckFileExists = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                    foreach (string f in d.FileNames) AddEntry(f, false, true);
            }
        }

        private void RemoveSelected()
        {
            filling = true;
            List<ListViewItem> gone = new List<ListViewItem>();
            foreach (ListViewItem r in files.SelectedItems) gone.Add(r);
            foreach (ListViewItem r in gone) files.Items.Remove(r);
            filling = false;
            UpdateButtons();
        }

        // ---- the button and the status line

        private List<InstalledApp> PickedApps()
        {
            List<InstalledApp> l = new List<InstalledApp>();
            foreach (ListViewItem r in apps.Items)
                if (r.Checked && ((InstalledApp)r.Tag).Reinstallable) l.Add((InstalledApp)r.Tag);
            return l;
        }

        private List<KitEntry> PickedFiles()
        {
            List<KitEntry> l = new List<KitEntry>();
            foreach (ListViewItem r in files.Items)
                if (r.Checked) l.Add((KitEntry)r.Tag);
            return l;
        }

        private void UpdateButtons()
        {
            if (building) return;
            int na = PickedApps().Count;
            List<KitEntry> pf = PickedFiles();
            long bytes = 0;
            bool counting = false;
            foreach (KitEntry e in pf)
            {
                if (e.Bytes < 0) counting = true;
                else bytes += e.Bytes;
            }
            btnBuild.Text = "Build kit...  (" + na + " apps, " + pf.Count + " file sets, "
                + (counting ? "counting..." : Nice(bytes)) + ")";
            btnBuild.Enabled = !scanning;
            if (builtPath == null)
                status.Text = "the zip lands wherever you say; nothing on this machine is changed";
        }

        public static string Nice(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0") + " KB";
            return b + " B";
        }

        // ---- building

        private void Build()
        {
            if (building) return;
            List<InstalledApp> pa = PickedApps();
            List<KitEntry> pf = PickedFiles();
            if (pa.Count == 0 && pf.Count == 0 && !chkIdle.Checked && !chkAi.Checked && !chkWinUtil.Checked)
            {
                status.Text = "nothing ticked - nothing to put in a kit";
                return;
            }

            string name = "IdleMaster-Kit-" + Environment.MachineName + "-"
                + DateTime.Now.ToString("yyyyMMdd") + ".zip";
            string to;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Where should the kit go?";
                d.Filter = "Zip archive (*.zip)|*.zip";
                d.FileName = name;
                d.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                d.OverwritePrompt = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                to = d.FileName;
            }

            KitOptions opt = new KitOptions();
            opt.InstallIdleMaster = chkIdle.Checked;
            opt.RemoveAi = chkAi.Checked;
            opt.WinUtil = chkWinUtil.Checked;
            opt.WinUtilPreset = preset.SelectedItem == null ? "Standard" : preset.SelectedItem.ToString();
            opt.WinUtilOpen = chkOpen.Checked;

            building = true;
            builtPath = null;
            btnBuild.Text = "Stop";
            btnBuild.BackColor = Theme.Danger;
            SetPickersEnabled(false);
            log("-- backup kit: writing " + to);
            log("   " + pa.Count + " apps, " + pf.Count + " file sets, Idle Master "
                + (opt.InstallIdleMaster ? "yes" : "no") + ", AI removal " + (opt.RemoveAi ? "yes" : "no")
                + ", WinUtil " + (opt.WinUtil ? opt.WinUtilPreset : "no")
                + (opt.WinUtilOpen ? " + left open" : ""));

            writer = new KitWriter(log);
            KitWriter mine = writer;
            Thread t = new Thread(delegate()
            {
                string failure = null;
                try { mine.Build(to, pa, pf, opt); }
                catch (Exception ex) { failure = ex.Message.Split('\n')[0]; }
                if (mine.Cancel || failure != null)
                {
                    try { File.Delete(to); } catch (Exception) { }
                }
                try { BeginInvoke((Action)delegate { BuildDone(to, failure, mine); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void StopBuild()
        {
            if (writer != null) writer.Cancel = true;
            status.Text = "stopping...";
        }

        private void BuildDone(string to, string failure, KitWriter w)
        {
            building = false;
            btnBuild.BackColor = Theme.Good;
            SetPickersEnabled(true);
            UpdateButtons();

            if (w.Cancel)
            {
                status.Text = "stopped - the half-written kit was deleted";
                log("   = stopped, nothing written.");
                return;
            }
            if (failure != null)
            {
                status.Text = "failed: " + failure;
                status.ForeColor = Theme.Warn;
                log("   ! kit failed: " + failure);
                return;
            }

            long size = 0;
            try { size = new FileInfo(to).Length; } catch (Exception) { }
            builtPath = to;
            status.ForeColor = Theme.Accent;
            status.Text = "kit written: " + Path.GetFileName(to) + "  (" + Nice(size) + ", "
                + w.Files + " files" + (w.Skipped > 0 ? ", " + w.Skipped + " skipped" : "") + ")";
            log("   = " + to + " (" + Nice(size) + "; " + w.Files + " files"
                + (w.Skipped > 0 ? ", " + w.Skipped + " skipped - in use or unreadable" : "") + ")");
            log("   On the new machine: extract it, run " + KitWriter.RebuildExe + ", tick, Rebuild.");
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + to + "\"")
                { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        private void SetPickersEnabled(bool on)
        {
            apps.Enabled = files.Enabled = on;
            btnAll.Enabled = btnNone.Enabled = btnRescan.Enabled = on;
            btnAddDir.Enabled = btnAddFile.Enabled = btnRemove.Enabled = on;
            chkIdle.Enabled = chkAi.Enabled = chkWinUtil.Enabled = chkOpen.Enabled = preset.Enabled = on;
        }

        private void Tick()
        {
            if (!building || writer == null) return;
            string phase = writer.Phase;
            long done = writer.DoneBytes, total = writer.TotalBytes;
            status.ForeColor = Theme.Dim;
            status.Text = "writing " + phase + "  -  " + Nice(done)
                + (total > 0 ? " of " + Nice(total) : "") + ", " + writer.Files + " files";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (writer != null) writer.Cancel = true;
            base.OnFormClosed(e);
        }
    }
}

// IDLE MASTER - disk cleanup, the engine side. RAM comes back on its own;
// disk junk just sits there until somebody weighs it. This file does the
// weighing. Nothing here touches a window - CleanupForm (Ui.cs) owns the
// checkboxes, this owns the facts.
//
//   Phase 0: the disk map      - DiskScan.cs reads each drive's file table,
//                                so every phase after it looks sizes up in
//                                memory instead of stat-ing files one by one.
//   Phase 1: known junk spots  - temp, caches, dumps, update leftovers.
//   Phase 2: big folders       - queried straight out of the map.
//   Phase 3: possible leftovers - folders no installed program claims.
//
// Everything found is a SUGGESTION until you tick it and press Clean, every
// delete goes through the shell to the Recycle Bin, and [cleanup.protect]
// wins over all of it - the same protect-list-first design the process
// killer uses. The app runs elevated, so the list is the only guardrail.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace IdleMaster
{
    // ------------------------------------------------------------- findings

    // One thing the scanner found. Path is the anchor the row and the protect
    // menu act on; Parts, when set, are the actual files/folders that go.
    internal sealed class CleanupItem
    {
        public string Name;             // "Windows temp", "brave cache (denis)"
        public string Path;             // absolute anchor path
        public string Category;         // the group header in the window
        public long Bytes;
        public bool Safe;               // true = known junk, arrives pre-checked
        public bool ContentsOnly;       // delete the children, keep the folder
        public bool IsRecycleBin;       // "clean" means empty the bin - permanent
        public string Note = "";
        public List<string> Parts;      // explicit targets; overrides the above

        // Where this finding lives in the disk map, when the map has it -
        // the window uses it to open the row into a tree of what is inside.
        public DiskTree Tree;
        public int Node = -1;

        public string Key { get { return Path.ToLowerInvariant(); } }
    }

    // -------------------------------------------------------------- scanner

    internal sealed class CleanupScanner
    {
        // Big-folder suggestions live this close to a drive root. Deeper than
        // this and the report stops being a map and starts being a file list.
        private const int ReportDepth = 3;

        private readonly Config cfg;
        private volatile bool cancel;
        private List<string[]> programs;    // installed apps, for orphans and owners
        private readonly Dictionary<string, string> ownerCache
            = new Dictionary<string, string>();

        // One map per fixed drive, filled by phase 0. The window reads these
        // too - they are what the "disk map" rows open into.
        public readonly List<DiskTree> Trees = new List<DiskTree>();

        public CleanupScanner(Config c) { cfg = c; }

        public void Cancel() { cancel = true; }
        public bool Cancelled { get { return cancel; } }

        // The whole scan. 'progress' gets a short where-are-we line, 'found'
        // gets each finding as it lands - the form marshals both to its thread.
        public List<CleanupItem> Scan(Action<string> progress, Action<CleanupItem> found)
        {
            List<CleanupItem> all = new List<CleanupItem>();
            Action<CleanupItem> keep = delegate(CleanupItem it)
            {
                if (cancel) return;
                if (it.Bytes <= 0) return;
                if (!it.IsRecycleBin && IsProtectedPath(it.Path)) return;
                if (it.Node < 0) Resolve(it);
                all.Add(it);
                found(it);
            };

            MapDrives(progress);
            if (!cancel) ScanKnownSpots(progress, keep);
            if (!cancel) ScanOrphans(progress, keep);
            if (!cancel) ScanBigFolders(progress, keep);
            progress(cancel ? "scan cancelled" : "scan finished");
            return all;
        }

        // ---- phase 0: the disk map

        private void MapDrives(Action<string> progress)
        {
            Trees.Clear();
            ownerCache.Clear();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (cancel) return;
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                }
                catch (Exception) { continue; }

                progress(drive.Name + " reading the file table...");
                try
                {
                    DiskTree t = DiskScanner.ScanDrive(drive, progress,
                        delegate() { return cancel; });
                    if (t != null && t.RootNode >= 0) Trees.Add(t);
                }
                catch (Exception) { }
            }
        }

        // Find a path in whichever map covers it.
        public bool Resolve(string path, out DiskTree tree, out int node)
        {
            tree = null; node = -1;
            if (path == null || path.Length < 2) return false;
            foreach (DiskTree t in Trees)
            {
                if (!path.StartsWith(t.Root, StringComparison.OrdinalIgnoreCase)) continue;
                int n = t.Lookup(path);
                if (n >= 0) { tree = t; node = n; return true; }
                return false;
            }
            return false;
        }

        private void Resolve(CleanupItem it)
        {
            DiskTree t; int n;
            if (Resolve(it.Path, out t, out n)) { it.Tree = t; it.Node = n; }
        }

        // The protect list wins - checked before a finding is shown AND again
        // before every delete. A plain path protects its whole subtree; '*'
        // patterns cover whatever they match, above or below.
        public bool IsProtectedPath(string path)
        {
            foreach (string p in cfg.CleanupProtect)
            {
                if (p.Length == 0) continue;
                if (Engine.Match(p, path)) return true;
                if (Engine.Match(p.TrimEnd('\\') + "\\*", path)) return true;
            }
            return false;
        }

        // ---- phase 1: known junk spots

        private void ScanKnownSpots(Action<string> progress, Action<CleanupItem> found)
        {
            progress("weighing the known junk spots...");
            string win = Environment.GetEnvironmentVariable("SystemRoot");
            if (win == null || win.Length == 0) win = @"C:\Windows";

            Spot(found, "Windows temp", System.IO.Path.Combine(win, "Temp"),
                "Temp files", true, true, "");
            Spot(found, "Windows update leftovers",
                System.IO.Path.Combine(win, @"SoftwareDistribution\Download"),
                "Windows update", true, true, "already-installed update payloads");
            Spot(found, "Minidumps", System.IO.Path.Combine(win, "Minidump"),
                "Crash dumps", true, true, "");
            FileSpot(found, "Kernel memory dump", System.IO.Path.Combine(win, "memory.dmp"),
                "Crash dumps", true, "");

            // The app runs elevated, so %TEMP% points at whoever elevated it.
            // Walking every profile under \Users instead means the junk of the
            // account actually being used never hides from the scan.
            foreach (string profile in UserProfiles())
            {
                if (cancel) return;
                string user = System.IO.Path.GetFileName(profile);
                progress("weighing " + user + "'s junk...");

                Spot(found, "temp files (" + user + ")",
                    System.IO.Path.Combine(profile, @"AppData\Local\Temp"),
                    "Temp files", true, true, "");
                Spot(found, "crash dumps (" + user + ")",
                    System.IO.Path.Combine(profile, @"AppData\Local\CrashDumps"),
                    "Crash dumps", true, true, "");
                ThumbCache(found, profile, user);
                BrowserCaches(found, profile, user);
                OldInstallers(found, profile, user);
            }

            // NOT here on purpose: \Windows\Prefetch. It looks like junk and
            // deleting it makes every app start SLOWER until Windows relearns
            // the layouts. A cleaner that hurts you is worse than none.

            WindowsOld(found);
            RecycleBinRow(found);
        }

        // A directory spot: weigh it, and suggest its contents (or the whole
        // thing) if there is anything in it worth mentioning.
        private void Spot(Action<CleanupItem> found, string name, string path,
                          string category, bool safe, bool contentsOnly, string note)
        {
            if (cancel) return;
            try
            {
                if (!Directory.Exists(path)) return;
                long bytes = DirSize(path);
                if (bytes <= 0) return;

                CleanupItem it = new CleanupItem();
                it.Name = name;
                it.Path = path;
                it.Category = category;
                it.Bytes = bytes;
                it.Safe = safe;
                it.ContentsOnly = contentsOnly;
                it.Note = note;
                found(it);
            }
            catch (Exception) { }
        }

        private void FileSpot(Action<CleanupItem> found, string name, string path,
                              string category, bool safe, string note)
        {
            try
            {
                if (!File.Exists(path)) return;
                CleanupItem it = new CleanupItem();
                it.Name = name;
                it.Path = path;
                it.Category = category;
                it.Bytes = new FileInfo(path).Length;
                it.Safe = safe;
                it.Note = note;
                found(it);
            }
            catch (Exception) { }
        }

        private void ThumbCache(Action<CleanupItem> found, string profile, string user)
        {
            try
            {
                string dir = System.IO.Path.Combine(profile,
                    @"AppData\Local\Microsoft\Windows\Explorer");
                if (!Directory.Exists(dir)) return;

                List<string> parts = new List<string>();
                long bytes = 0;
                foreach (string f in Directory.EnumerateFiles(dir, "thumbcache_*.db"))
                {
                    try { bytes += new FileInfo(f).Length; parts.Add(f); }
                    catch (Exception) { }
                }
                if (parts.Count == 0 || bytes <= 0) return;

                CleanupItem it = new CleanupItem();
                it.Name = "thumbnail cache (" + user + ")";
                it.Path = dir;
                it.Category = "Caches";
                it.Bytes = bytes;
                it.Safe = true;
                it.Parts = parts;
                it.Note = "Explorer rebuilds these; the live one may refuse to go";
                found(it);
            }
            catch (Exception) { }
        }

        private void BrowserCaches(Action<CleanupItem> found, string profile, string user)
        {
            // The Chromium family all hide the same three cache folders per
            // browser profile. Firefox keeps one cache2 per profile instead.
            string[][] chromium = new string[][]
            {
                new string[] { "chrome", @"AppData\Local\Google\Chrome\User Data" },
                new string[] { "edge",   @"AppData\Local\Microsoft\Edge\User Data" },
                new string[] { "brave",  @"AppData\Local\BraveSoftware\Brave-Browser\User Data" },
            };
            foreach (string[] b in chromium)
            {
                if (cancel) return;
                string root = System.IO.Path.Combine(profile, b[1]);
                if (!Directory.Exists(root)) continue;

                List<string> parts = new List<string>();
                long bytes = 0;
                try
                {
                    foreach (string prof in Directory.EnumerateDirectories(root))
                    {
                        foreach (string sub in new string[] { "Cache", "Code Cache", "GPUCache" })
                        {
                            string dir = System.IO.Path.Combine(prof, sub);
                            if (!Directory.Exists(dir)) continue;
                            parts.Add(dir);
                            bytes += DirSize(dir);
                        }
                    }
                }
                catch (Exception) { }
                Emit(found, b[0] + " cache (" + user + ")", root, "Caches", bytes, parts,
                    "close " + b[0] + " first for a clean sweep");
            }

            try
            {
                string ff = System.IO.Path.Combine(profile,
                    @"AppData\Local\Mozilla\Firefox\Profiles");
                if (Directory.Exists(ff))
                {
                    List<string> parts = new List<string>();
                    long bytes = 0;
                    foreach (string prof in Directory.EnumerateDirectories(ff))
                    {
                        string dir = System.IO.Path.Combine(prof, "cache2");
                        if (!Directory.Exists(dir)) continue;
                        parts.Add(dir);
                        bytes += DirSize(dir);
                    }
                    Emit(found, "firefox cache (" + user + ")", ff, "Caches", bytes, parts,
                        "close firefox first for a clean sweep");
                }
            }
            catch (Exception) { }
        }

        private static void Emit(Action<CleanupItem> found, string name, string anchor,
                                 string category, long bytes, List<string> parts, string note)
        {
            if (parts == null || parts.Count == 0 || bytes <= 0) return;
            CleanupItem it = new CleanupItem();
            it.Name = name;
            it.Path = anchor;
            it.Category = category;
            it.Bytes = bytes;
            it.Safe = true;
            it.Parts = parts;
            it.Note = note;
            found(it);
        }

        // Installers gathering dust in Downloads. One row per file and never
        // pre-checked - "old" is a hint, not a verdict, and Downloads is the
        // one folder people keep things in on purpose.
        private void OldInstallers(Action<CleanupItem> found, string profile, string user)
        {
            try
            {
                string dl = System.IO.Path.Combine(profile, "Downloads");
                if (!Directory.Exists(dl)) return;
                DateTime cutoff = DateTime.Now.AddDays(-cfg.CleanupInstallerDays);

                foreach (string pattern in new string[] { "*.msi", "*.exe" })
                {
                    foreach (string f in Directory.EnumerateFiles(dl, pattern))
                    {
                        if (cancel) return;
                        try
                        {
                            FileInfo fi = new FileInfo(f);
                            if (fi.LastWriteTime >= cutoff) continue;
                            int days = (int)(DateTime.Now - fi.LastWriteTime).TotalDays;

                            CleanupItem it = new CleanupItem();
                            it.Name = fi.Name + "  (" + user + ")";
                            it.Path = f;
                            it.Category = "Old installers";
                            it.Bytes = fi.Length;
                            it.Safe = false;
                            it.Note = "untouched for " + days + " days";
                            found(it);
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        private void WindowsOld(Action<CleanupItem> found)
        {
            try
            {
                string old = System.IO.Path.Combine(
                    System.IO.Path.GetPathRoot(Environment.SystemDirectory), "Windows.old");
                if (!Directory.Exists(old)) return;

                CleanupItem it = new CleanupItem();
                it.Name = "Windows.old";
                it.Path = old;
                it.Category = "Windows update";
                it.Bytes = DirSize(old);
                it.Safe = false;
                it.Note = "the previous Windows - removing it ends the rollback option";
                found(it);
            }
            catch (Exception) { }
        }

        // The bin is a row, not a delete target like the others: cleaning it is
        // the one PERMANENT action here, and it is where everything else goes.
        // So it is never pre-checked, and CleanupForm empties it last.
        private void RecycleBinRow(Action<CleanupItem> found)
        {
            try
            {
                long items;
                long bytes = CleanupActions.BinBytes(out items);
                if (bytes <= 0) return;

                CleanupItem it = new CleanupItem();
                it.Name = "Recycle Bin  (" + items + " items)";
                it.Path = "::recycle-bin";
                it.Category = "Recycle bin";
                it.Bytes = bytes;
                it.Safe = false;
                it.IsRecycleBin = true;
                it.Note = "emptied for GOOD - this is where everything above goes";
                found(it);
            }
            catch (Exception) { }
        }

        // ---- phase 2: big folders

        private void ScanBigFolders(Action<string> progress, Action<CleanupItem> found)
        {
            long minBytes = (long)cfg.CleanupBigDirMinMb * 1024 * 1024;

            foreach (DiskTree tree in Trees)
            {
                if (cancel) return;
                progress("picking the big folders on " + tree.Root + " ...");
                List<CleanupItem> big = new List<CleanupItem>();
                PickBig(tree, tree.RootNode, 0, minBytes, big);
                if (cancel) return;

                // When one child carries >= 90% of a folder, the child is the
                // story - reporting both would just say the same thing twice.
                for (int i = big.Count - 1; i >= 0; i--)
                {
                    CleanupItem parent = big[i];
                    foreach (CleanupItem child in big)
                    {
                        if (child == parent) continue;
                        string up = null;
                        try { up = System.IO.Path.GetDirectoryName(child.Path); }
                        catch (Exception) { }
                        if (up == null ||
                            !up.Equals(parent.Path, StringComparison.OrdinalIgnoreCase)) continue;
                        if (child.Bytes * 10 >= parent.Bytes * 9) { big.RemoveAt(i); break; }
                    }
                }

                big.Sort(delegate(CleanupItem a, CleanupItem b)
                    { return b.Bytes.CompareTo(a.Bytes); });
                foreach (CleanupItem it in big) found(it);
            }
        }

        // The walk already happened in phase 0 - this just reads the map.
        private void PickBig(DiskTree tree, int node, int depth, long minBytes,
                             List<CleanupItem> big)
        {
            if (cancel) return;
            for (int c = tree.FirstChild[node]; c >= 0; c = tree.NextSibling[c])
            {
                if (!tree.IsDir(c) || tree.IsReparse(c)) continue;
                if (tree.Bytes[c] < minBytes) continue;
                if (SkipForBig(tree.Name[c], depth + 1)) continue;

                string path = tree.PathOf(c);
                if (depth + 1 <= ReportDepth && !IsProtectedPath(path))
                {
                    CleanupItem it = new CleanupItem();
                    it.Name = tree.Name[c];
                    it.Path = path;
                    it.Category = "Big folders";
                    it.Bytes = tree.Bytes[c];
                    it.Safe = false;
                    it.Note = "big, not necessarily junk - your call";
                    it.Tree = tree;
                    it.Node = c;
                    big.Add(it);
                }
                if (depth + 1 < ReportDepth) PickBig(tree, c, depth + 1, minBytes, big);
            }
        }

        // Hard exclusions, in code so no ini edit can point the scanner at the
        // OS itself. \Windows junk is phase 1's job, piece by careful piece.
        private static bool SkipForBig(string name, int depth)
        {
            if (depth == 1)
            {
                if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---- phase 3: possible leftovers

        // Folder names that look orphaned but never are. Wildcards, matched
        // against the folder NAME, not the path.
        private static readonly string[] NeverOrphan = new string[]
        {
            "common files", "microsoft*", "windows*", "internet explorer",
            "modifiablewindowsapps", "uninstall information", "installshield*",
            "intel", "nvidia*", "amd", "realtek", "dotnet", "docker*",
            "google", "mozilla*", "brave*", "packages", "programs", "temp",
            "cache*", "d3dscache", "comms", "connecteddevicesplatform",
            "squirreltemp", "peernetworking", "publishers", "onedrive",
            "wsl", "ssh", "default",
            // shared infrastructure nothing 'claims' but everything needs:
            // SDK targeting packs, and the VS installer's repair/uninstall cache
            "reference assemblies", "package cache",
        };

        private void ScanOrphans(Action<string> progress, Action<CleanupItem> found)
        {
            progress("reading the installed-programs list...");
            List<string[]> programs = Programs();
            if (programs.Count == 0) return;    // a broken registry read would
                                                // flag EVERYTHING - stand down

            List<string> roots = new List<string>();
            roots.Add(Environment.GetEnvironmentVariable("ProgramFiles"));
            roots.Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

            foreach (string root in roots)
            {
                if (cancel) return;
                if (root == null || root.Length == 0 || !Directory.Exists(root)) continue;
                progress("looking for leftovers in " + root + " ...");

                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        if (cancel) return;
                        try
                        {
                            if (IsReparse(dir)) continue;
                            string name = System.IO.Path.GetFileName(dir);
                            if (OnNeverOrphan(name)) continue;

                            string norm = Norm(name);
                            if (norm.Length < 3) continue;
                            if (Claimed(dir, norm, programs)) continue;

                            // Recently-touched folders belong to something alive,
                            // whatever the registry says.
                            DateTime touched = Directory.GetLastWriteTime(dir);
                            if (touched > DateTime.Now.AddDays(-30)) continue;

                            long bytes = DirSize(dir);
                            if (bytes < 5L * 1024 * 1024) continue;   // dust, not a leftover

                            CleanupItem it = new CleanupItem();
                            it.Name = name;
                            it.Path = dir;
                            it.Category = "Possible leftovers";
                            it.Bytes = bytes;
                            it.Safe = false;
                            it.Note = "no installed program claims it; last touched "
                                + touched.ToString("yyyy-MM-dd");
                            found(it);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        }

        private static bool OnNeverOrphan(string name)
        {
            foreach (string pat in NeverOrphan)
                if (Engine.Match(pat, name)) return true;
            return false;
        }

        // [ normalized DisplayName, lowercased InstallLocation, DisplayName ]
        // per program, from every Uninstall hive - 64-bit, 32-bit, per-user.
        // Loaded once per scanner; the owner column asks about it constantly.
        private List<string[]> Programs()
        {
            if (programs != null) return programs;
            List<string[]> list = new List<string[]>();
            ReadUninstall(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list);
            ReadUninstall(Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", list);
            ReadUninstall(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list);
            programs = list;
            return list;
        }

        // ---- whose poop is it
        //
        // Best-effort name of the app (or person, or Windows) a path belongs
        // to, for the owner column. A heuristic, not a verdict - it points,
        // the human decides.

        public string OwnerOf(string path)
        {
            if (path == null || path.Length < 4) return "";
            string key = path.ToLowerInvariant();
            string owner;
            lock (ownerCache)
            {
                if (ownerCache.TryGetValue(key, out owner)) return owner;
            }
            owner = OwnerWork(path);
            lock (ownerCache)
            {
                if (ownerCache.Count > 200000) ownerCache.Clear();
                ownerCache[key] = owner;
            }
            return owner;
        }

        private string OwnerWork(string path)
        {
            string[] seg;
            try { seg = path.Substring(System.IO.Path.GetPathRoot(path).Length).Split('\\'); }
            catch (Exception) { return ""; }
            if (seg.Length == 0 || seg[0].Length == 0) return "";
            string top = seg[0];

            // Steam libraries live anywhere; the game name is two hops down.
            for (int i = 0; i + 2 < seg.Length; i++)
                if (seg[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                    && seg[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase))
                    return seg[i + 2] + " (Steam)";

            if (top.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
            if (top.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return "Recycle Bin";
            if (top.StartsWith("pagefile", StringComparison.OrdinalIgnoreCase)
                || top.StartsWith("hiberfil", StringComparison.OrdinalIgnoreCase)
                || top.StartsWith("swapfile", StringComparison.OrdinalIgnoreCase)) return "Windows";

            if (top.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                || top.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || top.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))
            {
                if (seg.Length < 2) return "";
                string claimed = ClaimedBy(path, seg[1], seg.Length > 2 ? seg[2] : null);
                return claimed.Length > 0 ? claimed : seg[1] + " (unclaimed)";
            }

            if (top.Equals("Users", StringComparison.OrdinalIgnoreCase))
            {
                if (seg.Length < 3) return seg.Length > 1 ? seg[1] : "";
                string user = seg[1];
                if (!seg[2].Equals("AppData", StringComparison.OrdinalIgnoreCase))
                    return user;                             // their Documents, Desktop, ...
                // Users\u\AppData\{Local,Roaming,LocalLow}\Vendor\App\...
                if (seg.Length < 5) return user;
                string vendor = seg[4];
                string app = seg.Length > 5 ? seg[5] : null;
                string claimed = ClaimedBy(path, vendor, app);
                if (claimed.Length == 0 || claimed == vendor)
                    return vendor + " (" + user + ")";
                return claimed;
            }

            return top;
        }

        // Umbrella vendor folders shared by many programs. Matching these
        // against the programs list by name would credit the first
        // "Microsoft anything" install with half the disk.
        private static readonly string[] UmbrellaVendors = new string[]
        {
            "microsoft", "windows", "google", "adobe", "nvidia", "nvidiacorporation",
            "intel", "amd", "apple", "mozilla", "oracle", "razer", "lenovo",
            "commonfiles", "packages", "temp", "programs", "cache", "caches",
        };

        private static bool IsUmbrella(string norm)
        {
            foreach (string u in UmbrellaVendors)
                if (norm == u) return true;
            return false;
        }

        // Match a vendor/app folder pair against the installed-programs list:
        // install path first (the strong claim), then name similarity. Empty
        // string when nobody claims it - the caller picks the fallback.
        private string ClaimedBy(string path, string vendor, string app)
        {
            List<string[]> progs = Programs();
            string low = path.ToLowerInvariant();
            foreach (string[] p in progs)
            {
                if (p[1].Length <= 3 || !low.StartsWith(p[1])) continue;
                if (low.Length > p[1].Length && low[p[1].Length] != '\\') continue;
                if (p[2].Length > 0) return p[2];
            }

            string normApp = app == null ? "" : Norm(app);
            string normVendor = vendor == null ? "" : Norm(vendor);
            if (normApp.Length >= 3 && !IsUmbrella(normApp))
            {
                foreach (string[] p in progs)
                {
                    if (p[0].Length < 3 || p[2].Length == 0) continue;
                    if (p[0].Contains(normApp) || normApp.Contains(p[0])) return p[2];
                }
            }
            if (normVendor.Length >= 3)
            {
                if (IsUmbrella(normVendor)) return vendor;
                foreach (string[] p in progs)
                {
                    if (p[0].Length < 3 || p[2].Length == 0) continue;
                    if (p[0].Contains(normVendor) || normVendor.Contains(p[0])) return p[2];
                }
            }
            return "";
        }

        private static void ReadUninstall(RegistryKey root, string path, List<string[]> into)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey e = k.OpenSubKey(sub))
                            {
                                if (e == null) continue;
                                string name = e.GetValue("DisplayName") as string;
                                string loc = e.GetValue("InstallLocation") as string;
                                if ((name == null || name.Length == 0)
                                    && (loc == null || loc.Length == 0)) continue;
                                into.Add(new string[]
                                {
                                    name == null ? "" : Norm(name),
                                    loc == null ? "" : loc.Trim().TrimEnd('\\').ToLowerInvariant(),
                                    name == null ? "" : name.Trim(),
                                });
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        private static bool Claimed(string dir, string norm, List<string[]> programs)
        {
            string low = dir.ToLowerInvariant();
            foreach (string[] p in programs)
            {
                if (p[1].Length > 0 && (low.StartsWith(p[1]) || p[1].StartsWith(low)))
                    return true;
                if (p[0].Length >= 3 && (p[0].Contains(norm) || norm.Contains(p[0])))
                    return true;
            }
            return false;
        }

        // Lowercase letters and digits only, so "Notepad++ (x64)" and a folder
        // called "notepad++" still find each other.
        private static string Norm(string s)
        {
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(ch)) b.Append(ch);
            return b.ToString();
        }

        // ---- shared plumbing

        // Every real profile under \Users. Reparse points out - that skips the
        // legacy "Documents and Settings"-style junctions for free.
        private static List<string> UserProfiles()
        {
            List<string> list = new List<string>();
            try
            {
                string drive = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                foreach (string d in Directory.EnumerateDirectories(
                    System.IO.Path.Combine(drive, "Users")))
                {
                    try
                    {
                        if (IsReparse(d)) continue;
                        string name = System.IO.Path.GetFileName(d);
                        if (name.Equals("Public", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(d);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return list;
        }

        private static bool IsReparse(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return true; }
        }

        // Everything under 'path'. The disk map answers instantly when it
        // covers the path; the walk below is the fallback for whatever it
        // does not (scan cancelled mid-map, exotic mounts).
        public long DirSize(string path)
        {
            DiskTree t; int n;
            if (Resolve(path, out t, out n)) return t.Bytes[n];

            long total = 0;
            Stack<string> work = new Stack<string>();
            work.Push(path);
            while (work.Count > 0)
            {
                if (cancel) return total;
                string dir = work.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        try { total += new FileInfo(f).Length; }
                        catch (Exception) { }
                    }
                    foreach (string d in Directory.EnumerateDirectories(dir))
                    {
                        if (IsReparse(d)) continue;
                        work.Push(d);
                    }
                }
                catch (Exception) { }
            }
            return total;
        }

        // Engine.Size speaks MB because RAM is MB-sized. Disks are not.
        public static string Nice(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1024L * 1024)
                return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }
    }

    // -------------------------------------------------------------- actions

    internal static class CleanupActions
    {
        // Sends one finding to the Recycle Bin. ContentsOnly and Parts items
        // batch their targets into a single shell call - one undo entry, not
        // thousands. Locked files fail inside the batch; we report and let a
        // rescan tell the truth rather than fight the lock.
        // Some findings have no business going to the Recycle Bin, and the
        // Windows update payloads are the case that proved it: 9.6 GB across
        // 313,000 files. Recycling those means moving every one individually
        // and writing a $I record for each - it runs for hours, and at the end
        // of it the bin is holding 9.6 GB, so nothing has actually been freed
        // until you empty it. Windows' own Disk Cleanup deletes them outright
        // for exactly this reason, and they are payloads for updates that are
        // already installed - there is nothing to restore them for.
        //
        // The rule lives here rather than at the scan, so the confirmation
        // dialog and the delete itself can never disagree about what is about
        // to happen.
        public static bool IsPermanent(CleanupItem it)
        {
            if (it == null) return false;
            if (it.IsRecycleBin) return true;      // emptying the bin already is
            return string.Equals(it.Category, "Windows update",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool Recycle(CleanupItem it, CleanupScanner guard, Action<string> log)
        {
            if (it.IsRecycleBin) return EmptyBin(it, log);

            if (guard.IsProtectedPath(it.Path))
            {
                log("   . skipped (protected): " + it.Path);
                return false;
            }

            List<string> targets = new List<string>();
            if (it.Parts != null)
                targets.AddRange(it.Parts);
            else if (it.ContentsOnly)
            {
                try
                {
                    foreach (string e in Directory.EnumerateFileSystemEntries(it.Path))
                        targets.Add(e);
                }
                catch (Exception) { }
            }
            else
                targets.Add(it.Path);

            for (int i = targets.Count - 1; i >= 0; i--)
                if (guard.IsProtectedPath(targets[i])) targets.RemoveAt(i);

            if (targets.Count == 0)
            {
                log("   . nothing to remove under " + it.Path);
                return false;
            }

            bool permanent = IsPermanent(it);

            Native.SHFILEOPSTRUCT op = new Native.SHFILEOPSTRUCT();
            op.wFunc = Native.FO_DELETE;
            op.pFrom = string.Join("\0", targets.ToArray()) + "\0";

            // FOF_WANTNUKEWARNING is gone with the undo. It exists to warn that
            // something is about to be destroyed rather than recycled, and it
            // partially overrides FOF_NOCONFIRMATION to do it - which on a
            // permanent delete is a prompt nobody would ever see, because
            // FOF_SILENT means there is no progress window for it to sit on.
            op.fFlags = (ushort)(Native.FOF_NOCONFIRMATION | Native.FOF_SILENT
                | Native.FOF_NOERRORUI
                | (permanent ? 0 : Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING));

            int rc = Native.SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted)
            {
                log("   x " + it.Name + " - " + CleanupScanner.Nice(it.Bytes)
                    + (permanent ? " deleted for good" : " to the bin"));
                return true;
            }
            log("   ! could not fully remove " + it.Name
                + (op.fAnyOperationsAborted ? " (aborted)" : " (in use, or access denied)"));
            return false;
        }

        private static bool EmptyBin(CleanupItem it, Action<string> log)
        {
            int rc = Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            if (rc == 0)
            {
                log("   x Recycle Bin emptied - " + CleanupScanner.Nice(it.Bytes) + " gone for good");
                return true;
            }
            log("   ! could not empty the Recycle Bin");
            return false;
        }

        public static long BinBytes(out long items)
        {
            items = 0;
            try
            {
                Native.SHQUERYRBINFO info = new Native.SHQUERYRBINFO();
                info.cbSize = (uint)System.Runtime.InteropServices.Marshal
                    .SizeOf(typeof(Native.SHQUERYRBINFO));
                if (Native.SHQueryRecycleBin(null, ref info) != 0) return 0;
                items = info.i64NumItems;
                return info.i64Size;
            }
            catch (Exception) { return 0; }
        }
    }
}

// IDLE MASTER - two-mode RAM reclaimer for an always-on Sunshine/Tailscale host.
//
//   BOOST NOW      : kill background bloat, keep a usable desktop.
//   ABSOLUTE IDLE  : strip down to Windows vitals + Sunshine + Tailscale.
//   RESTORE        : undo whatever the last mode did.
//   SENTRY         : after a mode runs, keep hunting so the RAM stays clean.
//   NETWORK GUARD  : keep the link, Tailscale and Sunshine up, always (NetGuard.cs).
//
// Built against the in-box .NET Framework compiler, so this is C# 5:
// no string interpolation, no ?., no nameof, no expression-bodied members.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Idle Master")]
[assembly: AssemblyDescription("Two-mode RAM reclaimer with a persistent sentry")]
[assembly: AssemblyProduct("Idle Master")]
[assembly: AssemblyVersion("0.9.0.0")]
[assembly: AssemblyFileVersion("0.9.0.0")]

namespace IdleMaster
{
    // ---------------------------------------------------------------- native

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        internal static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetProcessWorkingSetSizeEx(
            IntPtr hProcess, IntPtr dwMin, IntPtr dwMax, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(int dwProcessId);

        [DllImport("ntdll.dll")]
        internal static extern uint NtSetSystemInformation(int infoClass, IntPtr info, int length);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        // Which process owns the window the user is looking at right now.
        internal static int ForegroundPid()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                return (int)pid;
            }
            catch (Exception) { return 0; }
        }

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        // "Visible" is not enough: suspended UWP hosts keep a visible-but-cloaked
        // window forever. DWM knows the difference.
        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

        internal const int DWMWA_CLOAKED = 14;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        internal const uint TH32CS_SNAPPROCESS = 0x2;

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, int len, IntPtr prev, IntPtr retLen);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privilege;
        }

        internal const int SystemMemoryListInformation = 0x50;
        internal const int MemoryEmptyWorkingSets = 2;
        internal const int MemoryPurgeStandbyList = 4;
        internal const int MemoryPurgeLowPriorityStandbyList = 5;

        internal static bool EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out token))
                return false;
            LUID luid;
            if (!LookupPrivilegeValue(null, name, out luid))
                return false;
            TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Privilege.Luid = luid;
            tp.Privilege.Attributes = 0x00000002; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero);
        }

        internal static bool SetMemoryList(int command)
        {
            IntPtr p = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(p, command);
                return NtSetSystemInformation(SystemMemoryListInformation, p, sizeof(int)) == 0;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---- shell delete / recycle bin
        //
        // The shell delete is the only delete disk cleanup does: everything goes
        // to the Recycle Bin, so a wrong rule costs a restore, not the data.
        // Sequential layout matches the x64 headers; the build is /platform:x64
        // only (build.ps1), so the x86 Pack=1 quirk does not apply here.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;        // double-null-terminated list; build it as
            public string pTo;          // string.Join("\0", paths) + "\0" - the
                                        // marshaler supplies the final terminator.
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        internal const uint FO_DELETE = 0x0003;
        internal const ushort FOF_SILENT          = 0x0004;
        internal const ushort FOF_NOCONFIRMATION  = 0x0010;
        internal const ushort FOF_ALLOWUNDO       = 0x0040;
        internal const ushort FOF_NOERRORUI       = 0x0400;
        // Without this, NOCONFIRMATION lets the shell permanently nuke anything
        // too big for the bin without a word. This restores exactly that warning.
        internal const ushort FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SHQUERYRBINFO
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint flags);

        internal const uint SHERB_NOCONFIRMATION = 0x1;
        internal const uint SHERB_NOPROGRESSUI   = 0x2;
        internal const uint SHERB_NOSOUND        = 0x4;
    }

    // ---------------------------------------------------------------- config

    internal sealed class Config
    {
        public bool KillExplorer = true;
        public bool NetworkGuard = true;            // the guard: checks inside a run, and the standing watch (NetGuard)
        public bool TrimWorkingSets = true;
        public bool ClearStandbyList = true;
        public bool CloseBrowsersInBoost = false;

        // --- sentry: the thing that keeps hunting after the mode has run
        public bool Sentry = true;                  // arm it automatically after boost/idle
        public int SentrySeconds = 20;              // how often it sweeps processes
        public int SentryServiceMinutes = 5;        // how often it re-stops services that came back
        public int SentryTrimMinutes = 10;          // how often it re-trims + purges standby
        public int SentryGuardMinutes = 5;          // how often it verifies Sunshine + Tailscale
        public int SentryRespawnLimit = 6;          // kills of one name before it gives up on it
        public int SentryBackoffMinutes = 30;       // ...and for how long it leaves it alone
        public bool SentrySkipForeground = true;    // never kill what you are actively looking at
        public bool SkipOpenApps = true;            // never kill any app with a window open (boost only)
        public bool OverclockedSentry = false;      // away mode: the sentry kills EVERYTHING not protected - no asking, no sparing
        public int TrimWhenFreeBelowMb = 0;         // 0 = only on the timer
        public int SentryFullPassMinutes = 0;       // a whole boost pass (services + trim + guard) every N min. 0 = off
        public int BoostWhenFreeBelowMb = 0;        // ...and one right now when free RAM drops under this. 0 = off

        // --- ask first: anything that shows up AFTER the mode ran gets a dialog
        public bool AskBeforeKill = true;
        public int AskTimeoutSeconds = 47;          // no answer = AskTimeoutAction
        public string AskTimeoutAction = "trash";   // trash (once) | keep | always
        public int AskAboveMb = 250;                // also ask about newcomers this big
                                                    // that are on no list at all. 0 = off.
        public bool Tray = true;                    // tray icon; closing the window hides to it
        public int UpdateCheckHours = 6;            // ask GitHub for a newer release this often. 0 = only by hand
        public bool StartWithWindows = false;       // logon task: Idle Master opens as you log in
        public string StartupAction = "none";       // ...and then runs: none | boost | idle

        // --- network guard, the standing watch: the connection itself, independent of the sentry
        public bool NetworkGuardWifi = true;        // reconnect Wi-Fi to a known network on its own
        public bool NetworkGuardKeepWifiAwake = true;// stop Windows powering the Wi-Fi adapter down
        public bool NetworkGuardScan = false;       // look at what is in range / the SSID - Windows asks for location once
        public int NetworkGuardSeconds = 60;        // how often it checks

        // --- disk cleanup: the scanner only suggests, these tune the suggestions
        public int CleanupInstallerDays = 90;       // Downloads installers older than this
        public int CleanupBigDirMinMb = 500;        // big-folder suggestions start here

        public readonly List<string> Protect = new List<string>();
        public readonly List<string> ProtectServices = new List<string>();
        public readonly List<string> BoostKill = new List<string>();
        public readonly List<string> BoostServices = new List<string>();
        public readonly List<string> IdleKill = new List<string>();
        public readonly List<string> IdleServices = new List<string>();
        public readonly List<string> RestoreLaunch = new List<string>();
        public readonly List<string> CleanupProtect = new List<string>();
        public readonly List<string> DebloatProtect = new List<string>();
        public readonly List<string> NetworkWifi = new List<string>();    // preferred Wi-Fi profiles, in order
        public readonly List<string> RemoteApps = new List<string>();     // apps the remote-desktop watch keeps connected

        public static string Path_ { get { return System.IO.Path.Combine(App.Dir, "idlemaster.ini"); } }

        public static Config Load()
        {
            if (!File.Exists(Path_))
                File.WriteAllText(Path_, DefaultIni, new UTF8Encoding(false));

            Config c = new Config();
            string section = "";
            foreach (string raw in File.ReadAllLines(Path_))
            {
                string line = StripComment(raw);
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                if (section == "settings")
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    bool b = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                             || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    switch (k)
                    {
                        case "killexplorer": c.KillExplorer = b; break;
                        case "networkguard": c.NetworkGuard = b; break;
                        case "trimworkingsets": c.TrimWorkingSets = b; break;
                        case "clearstandbylist": c.ClearStandbyList = b; break;
                        case "closebrowsersinboost": c.CloseBrowsersInBoost = b; break;
                        case "sentry": c.Sentry = b; break;
                        case "sentryskipforeground": c.SentrySkipForeground = b; break;
                        case "skipopenapps": c.SkipOpenApps = b; break;
                        case "overclockedsentry": c.OverclockedSentry = b; break;
                        case "startwithwindows": c.StartWithWindows = b; break;
                        case "startupaction":
                        {
                            string sa = v.ToLowerInvariant();
                            c.StartupAction = (sa == "boost" || sa == "idle") ? sa : "none";
                            break;
                        }
                        case "sentryseconds": c.SentrySeconds = Int(v, c.SentrySeconds, 5); break;
                        case "sentryserviceminutes": c.SentryServiceMinutes = Int(v, c.SentryServiceMinutes, 1); break;
                        case "sentrytrimminutes": c.SentryTrimMinutes = Int(v, c.SentryTrimMinutes, 1); break;
                        case "sentryguardminutes": c.SentryGuardMinutes = Int(v, c.SentryGuardMinutes, 1); break;
                        case "sentryrespawnlimit": c.SentryRespawnLimit = Int(v, c.SentryRespawnLimit, 1); break;
                        case "sentrybackoffminutes": c.SentryBackoffMinutes = Int(v, c.SentryBackoffMinutes, 1); break;
                        case "trimwhenfreebelowmb": c.TrimWhenFreeBelowMb = Int(v, c.TrimWhenFreeBelowMb, 0); break;
                        case "sentryfullpassminutes": c.SentryFullPassMinutes = Int(v, c.SentryFullPassMinutes, 0); break;
                        case "boostwhenfreebelowmb": c.BoostWhenFreeBelowMb = Int(v, c.BoostWhenFreeBelowMb, 0); break;
                        case "askbeforekill": c.AskBeforeKill = b; break;
                        case "asktimeoutaction":
                        {
                            string a = v.ToLowerInvariant();
                            c.AskTimeoutAction = (a == "keep" || a == "always") ? a : "trash";
                            break;
                        }
                        case "tray": c.Tray = b; break;
                        case "updatecheckhours": c.UpdateCheckHours = Int(v, c.UpdateCheckHours, 0); break;
                        // 0.7.0/0.7.1 called it the remote guard; those keys still read.
                        case "remoteguard": c.NetworkGuard = b; break;
                        case "networkguardwifi": case "remoteguardwifi": c.NetworkGuardWifi = b; break;
                        case "networkguardkeepwifiawake": case "remoteguardkeepwifiawake": c.NetworkGuardKeepWifiAwake = b; break;
                        case "networkguardscan": case "remoteguardscan": c.NetworkGuardScan = b; break;
                        case "networkguardseconds": case "remoteguardseconds": c.NetworkGuardSeconds = Int(v, c.NetworkGuardSeconds, 15); break;
                        case "asktimeoutseconds": c.AskTimeoutSeconds = Int(v, c.AskTimeoutSeconds, 5); break;
                        case "askabovemb": c.AskAboveMb = Int(v, c.AskAboveMb, 0); break;
                        case "cleanupinstallerdays": c.CleanupInstallerDays = Int(v, c.CleanupInstallerDays, 7); break;
                        case "cleanupbigdirminmb": c.CleanupBigDirMinMb = Int(v, c.CleanupBigDirMinMb, 50); break;
                    }
                    continue;
                }
                string item = StripExe(line);
                switch (section)
                {
                    case "protect": c.Protect.Add(item); break;
                    case "protect.services": c.ProtectServices.Add(line); break;
                    case "boost.kill": c.BoostKill.Add(item); break;
                    case "boost.services": c.BoostServices.Add(line); break;
                    case "idle.kill": c.IdleKill.Add(item); break;
                    case "idle.services": c.IdleServices.Add(line); break;
                    case "restore.launch": c.RestoreLaunch.Add(line); break;
                    // Paths, not process names - StripExe must not touch these.
                    case "cleanup.protect": c.CleanupProtect.Add(line); break;
                    // Store package names, verbatim.
                    case "debloat.protect": c.DebloatProtect.Add(line); break;
                    // Wi-Fi profile names, verbatim.
                    case "network.wifi": c.NetworkWifi.Add(line); break;
                    // Process names the remote-desktop watch keeps connected.
                    case "remote.apps": c.RemoteApps.Add(item); break;
                }
            }
            return c;
        }

        // A line with everything after '#' or ';' removed. Entries written by the
        // dialogs carry a trailing "# you chose this on ..." note, so without this
        // they would be read as part of the process name and never match anything.
        public static string StripComment(string raw)
        {
            string s = raw;
            int cut = s.IndexOfAny(new char[] { '#', ';' });
            if (cut >= 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        // Overwrites this instance in place, so everything already holding a
        // reference (engine, sentry) sees the edits without being rebuilt.
        public void CopyFrom(Config o)
        {
            KillExplorer = o.KillExplorer; NetworkGuard = o.NetworkGuard;
            TrimWorkingSets = o.TrimWorkingSets; ClearStandbyList = o.ClearStandbyList;
            CloseBrowsersInBoost = o.CloseBrowsersInBoost;
            Sentry = o.Sentry; SentrySeconds = o.SentrySeconds;
            SentryServiceMinutes = o.SentryServiceMinutes; SentryTrimMinutes = o.SentryTrimMinutes;
            SentryGuardMinutes = o.SentryGuardMinutes; SentryRespawnLimit = o.SentryRespawnLimit;
            SentryBackoffMinutes = o.SentryBackoffMinutes;
            SentrySkipForeground = o.SentrySkipForeground;
            SkipOpenApps = o.SkipOpenApps;
            OverclockedSentry = o.OverclockedSentry;
            StartWithWindows = o.StartWithWindows;
            StartupAction = o.StartupAction;
            TrimWhenFreeBelowMb = o.TrimWhenFreeBelowMb;
            SentryFullPassMinutes = o.SentryFullPassMinutes;
            BoostWhenFreeBelowMb = o.BoostWhenFreeBelowMb;
            AskBeforeKill = o.AskBeforeKill; AskTimeoutSeconds = o.AskTimeoutSeconds;
            AskTimeoutAction = o.AskTimeoutAction;
            AskAboveMb = o.AskAboveMb; Tray = o.Tray;
            UpdateCheckHours = o.UpdateCheckHours;
            NetworkGuardWifi = o.NetworkGuardWifi;
            NetworkGuardKeepWifiAwake = o.NetworkGuardKeepWifiAwake;
            NetworkGuardScan = o.NetworkGuardScan;
            NetworkGuardSeconds = o.NetworkGuardSeconds;
            CleanupInstallerDays = o.CleanupInstallerDays;
            CleanupBigDirMinMb = o.CleanupBigDirMinMb;

            Swap(Protect, o.Protect); Swap(ProtectServices, o.ProtectServices);
            Swap(BoostKill, o.BoostKill); Swap(BoostServices, o.BoostServices);
            Swap(IdleKill, o.IdleKill); Swap(IdleServices, o.IdleServices);
            Swap(RestoreLaunch, o.RestoreLaunch);
            Swap(CleanupProtect, o.CleanupProtect);
            Swap(DebloatProtect, o.DebloatProtect);
            Swap(NetworkWifi, o.NetworkWifi);
            Swap(RemoteApps, o.RemoteApps);
        }

        // The names one section of the shipped default config carries, enabled
        // and commented-out suggestions alike, so the windows can mark which
        // entries are "base kit" and which the user (or a toast answer) added.
        private static IniFile kit;

        public static bool IsKitEntry(string section, string text)
        {
            if (kit == null)
                kit = new IniFile(DefaultIni.Replace("\r\n", "\n").Split('\n'));
            foreach (IniFile.Entry e in kit.Section(section))
                if (e.Text.Equals(text, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Swap(List<string> mine, List<string> theirs)
        {
            mine.Clear();
            mine.AddRange(theirs);
        }

        // Writes a decision you made in a dialog straight back into the ini, so it
        // survives a restart. Inserted right under the section header, tagged with
        // the date, so you can find and undo it later.
        public static bool Append(string section, string value)
        {
            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(Path_));
                string header = "[" + section + "]";
                int at = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
                    at = i;
                    break;
                }
                if (at < 0)
                {
                    lines.Add("");
                    lines.Add(header);
                    at = lines.Count - 1;
                }
                foreach (string l in lines)
                    if (l.Trim().Equals(value, StringComparison.OrdinalIgnoreCase)) return true;

                lines.Insert(at + 1, value + "   # you chose this on "
                    + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                File.WriteAllLines(Path_, lines.ToArray());
                return true;
            }
            catch (Exception) { return false; }
        }

        private static int Int(string v, int fallback, int min)
        {
            int n;
            if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return fallback;
            return n < min ? min : n;
        }

        private static string StripExe(string s)
        {
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - 4);
            return s;
        }

        // Written on first run. Everything below was derived from an actual scan
        // of this machine, so the names are real, not guesses.
        public const string DefaultIni = @"# IDLE MASTER configuration
# One entry per line. '*' works as a wildcard. Lines starting with # are off.
# Process names are matched WITHOUT the .exe (writing .exe is fine, it is stripped).

[settings]
# ABSOLUTE IDLE closes the Windows shell (explorer + start menu + search host).
# Frees ~450 MB. To get the desktop back: Ctrl+Shift+Esc -> Run new task -> IdleMaster.exe
KillExplorer=1
# The network guard: after every destructive step it verifies Sunshine + Tailscale
# are alive and restarts them if not - and, whenever Idle Master is running, it
# keeps watch over the connection itself (see NETWORK GUARD below).
NetworkGuard=1
# Squeeze the working set of every surviving process.
TrimWorkingSets=1
# Purge the standby (cached) list so Task Manager shows the memory as free.
ClearStandbyList=1
# BOOST NOW leaves browsers alone by default - you are working.
CloseBrowsersInBoost=0

# --- SENTRY -----------------------------------------------------------------
# A boost is a snapshot; the junk comes back. The sentry keeps sweeping the same
# lists after the mode has run, so RAM stays where you put it. It enforces
# whichever mode ran last, and stops the moment you hit Restore.
Sentry=1
# Seconds between process sweeps.
SentrySeconds=20
# Minutes between re-stopping services that trigger-started themselves again.
SentryServiceMinutes=5
# Minutes between working-set trims + standby purges.
SentryTrimMinutes=10
# Minutes between Sunshine/Tailscale health checks.
SentryGuardMinutes=5
# If one process name comes back this many times, stop fighting it for
# SentryBackoffMinutes and say so in the log. Prevents kill/respawn loops.
SentryRespawnLimit=6
SentryBackoffMinutes=30
# Never kill the window you are actually using (boost only; idle ignores this).
SentrySkipForeground=1
# Never kill ANY app with a window open on the desktop, helper processes
# included - WhatsApp and Discord do their real work in msedgewebview2 workers,
# and killing those crashes the app even though its own name is not listed.
# Boost only; ABSOLUTE IDLE still closes everything.
SkipOpenApps=1
# The away switch: while this is 1, a hunting sentry kills EVERYTHING that is
# not on [protect] - no questions, no sparing open windows, no foreground mercy.
# Toggle it in the window, click ABSOLUTE IDLE, walk away. Turn it off (or hit
# Restore) when you are back at the keyboard.
OverclockedSentry=0
# Emergency trim: also trim when free RAM drops under this many MB. 0 = off.
TrimWhenFreeBelowMb=0
# Repeated boost: every N minutes do a WHOLE pass at once - re-stop services,
# trim, purge, check the stream stack - not just the 20-second process sweep.
# 0 = each on its own timer above only.
SentryFullPassMinutes=0
# Dynamic boost: do that whole pass right now when free RAM drops under this
# many MB (at most once per 5 minutes). 0 = off.
BoostWhenFreeBelowMb=0

# --- ASK FIRST --------------------------------------------------------------
# The sentry takes a census on its first sweep. Everything already running that
# matches a list is junk you asked it to clear, and dies without a word. Anything
# that shows up AFTER that is something YOU started, so it gets a dialog instead:
#   Keep it      - left alone for SentryBackoffMinutes, then asked again
#   Always keep  - written into [protect] below, remembered forever
#   Trash once   - closed now; if it comes back you are asked again
#   Always trash - closed now and every time (unlisted names go into [boost.kill])
AskBeforeKill=1
# No answer in this many seconds = AskTimeoutAction.
AskTimeoutSeconds=47
# What no answer means: trash (= trash once), keep, or always (= always trash).
AskTimeoutAction=trash
# Also ask about newcomers that are on NO list at all but bigger than this many MB
# ('Always trash' adds them to [boost.kill]). 0 = only ask about listed processes.
AskAboveMb=250
# Tray icon. Closing the window hides to the tray and keeps hunting; exit from
# the tray menu when you actually want it gone.
Tray=1
# Ask GitHub for a newer release this many hours apart (first check a minute
# after start). Something newer = a tray toast and an ""Update to vX"" button;
# one click downloads it, installs it in place and brings Idle Master back.
# Your idlemaster.ini is never touched. 0 = only when you press the button.
UpdateCheckHours=6
# Start Idle Master as you log in (a logon scheduled task, created or removed
# when you save Settings - the checkbox there is the switch).
StartWithWindows=0
# What that logon start runs on its own: none, boost, or idle. 'Keep hunting'
# still applies afterwards, exactly as if you had clicked the button yourself.
StartupAction=none

# --- NETWORK GUARD ----------------------------------------------------------
# The sentry guards the RAM; this guards the way back in. With NetworkGuard=1,
# whenever Idle Master is running it checks, every NetworkGuardSeconds, that a
# network link is up, that the internet answers, that Tailscale is Running with
# an address, and that Sunshine is listening - and fixes what is not: restarts
# the services, turns the Wi-Fi radio back on, reconnects to a known network,
# renews DHCP, flushes DNS, bounces the adapter, runs 'tailscale up'. Quiet
# while all is well; every fix is one line in the log. --network does one check
# by hand. The Network guard button in the window is its page.
# Reconnect Wi-Fi on its own. [network.wifi] below says which networks first;
# with it empty, any network this machine has a saved profile for will do.
NetworkGuardWifi=1
# Keep Windows from powering the Wi-Fi adapter down to save energy - the usual
# reason a headless laptop drops off the network at 3am. Best effort.
NetworkGuardKeepWifiAwake=1
# Let it scan for which saved networks are in range (and name the one it is on).
# Windows counts that as LOCATION and asks you to allow it, once, for the app.
# Off = it never asks: reconnects go by [network.wifi] order, then Windows' own
# saved order, which is almost always the same thing a little slower.
NetworkGuardScan=0
# Seconds between checks. Each check is a few TCP connects; 60 is cheap.
NetworkGuardSeconds=60

# --- DISK CLEANUP -----------------------------------------------------------
# The cleanup window only suggests. Nothing is deleted until you tick it and
# press Clean, and everything ticked goes to the Recycle Bin, not into the void.
# Suggest installers sitting in Downloads untouched for this many days.
CleanupInstallerDays=90
# Point at folders this big and bigger when weighing the whole drive.
CleanupBigDirMinMb=500

# ---------------------------------------------------------------------------
# NEVER touched, whatever else any list says. This is the safety net.
# ---------------------------------------------------------------------------
[protect]
System
Idle
Registry
Secure System
Memory Compression
MemCompression
smss
csrss
wininit
winlogon
services
lsass
LsaIso
fontdrvhost
dwm
svchost
conhost
WUDFHost
LogonUI
audiodg
spoolsv
dasHost
wlanext
# the streaming stack itself. These four are also protected in code - no edit
# here or anywhere else can put them on a kill list. tailscale-ipn is the tray
# app: on Windows it is what tells the daemon to connect, so killing it strands
# tailscaled in NoState and takes the machine off the network.
sunshine
sunshinesvc
tailscaled
tailscale-ipn
# Docker and the WSL/Hyper-V machinery it rides on. Killing the desktop app while
# the engine has containers up is a good way to lose work, and the backend costs
# nothing once it is idle.
Docker Desktop
docker
dockerd
com.docker.*
docker-ai
vpnkit*
wslservice
wsl
wslhost
vmmem*
vmcompute
# Defender
MsMpEng
MpDefenderCoreService
NisSrv
SecurityHealthService
# us
IdleMaster

[protect.services]
SunshineService
Tailscale
# Docker: com.docker.service is the privileged helper the engine talks to, and
# vmms/WSLService/vmcompute are the backend it runs on.
com.docker.service
vmms
WSLService
LxssManager
vmcompute
HvHost
WinDefend
MDCoreSvc
WdNisSvc
mpssvc
BFE
Dhcp
Dnscache
nsi
netprofm
NlaSvc
WlanSvc
Wcmsvc
iphlpsvc
RpcSs
RpcEptMapper
DcomLaunch
LSM
ProfSvc
Power
PlugPlay
Winmgmt
EventLog
Schedule
CryptSvc
KeyIso
SamSs
Audiosrv
AudioEndpointBuilder
UserManager
SystemEventsBroker
BrokerInfrastructure
CoreMessagingRegistrar
NVDisplay.ContainerLocalSystem
Themes
TextInputManagementService

# ---------------------------------------------------------------------------
# BOOST NOW - background junk that has no business running while you work.
# ---------------------------------------------------------------------------
[boost.kill]
# Razer stack (~305 MB here)
RazerCortex
RazerCortex.Shell
Razer Central
RazerAppEngine
CefSharp.BrowserSubprocess
GameManagerService3
# Lenovo Vantage add-ins (~90 MB)
LenovoVantage-*
Lenovo.Modern.ImController*
LenovoVantage
# NordVPN desktop UI (~376 MB - the service keeps the tunnel up)
NordVPN
NordUpdateService
# ProtonVPN UI (you have both installed)
ProtonVPN*
# NVIDIA overlay / ShadowPlay (~107 MB) - NOT the display driver
NVIDIA Overlay
NVIDIA Share
NVIDIA Web Helper
nvsphelper64
# WebView2 hosts for widgets/tray apps (~536 MB) - they respawn on demand
msedgewebview2
# WhatsApp lingers in RAM after its window closes (host runs as WhatsApp.Root).
# SkipOpenApps spares the whole tree while the window is on screen; closed, it
# is background residue like the rest.
WhatsApp*
# Windows widgets & phone link junk
Widgets
WidgetService
BatteryWidgetHost
MessagingPlugin
CrossDeviceResume
AppProvisioningPlugin
PhoneExperienceHost
# launchers & chat that auto-start here
Discord
Update
Steam
steamwebhelper
EpicGamesLauncher
EpicWebHelper
OneDrive
Teams
ms-teams
msteams
# search indexer helpers
SearchProtocolHost
SearchFilterHost
# audio ""enhancement""
NhNotifSys
NahimicSvc*

[boost.services]
RzActionSvc
CortexLauncherService
LenovoVantageService
UDCService
NordUpdaterService
nordsec-threatprotection-service
NahimicService
WSearch
DoSvc
BITS
MapsBroker
SysMain
DiagTrack
dmwappushservice
PcaSvc
DPS
WdiSystemHost
UsoSvc

# ---------------------------------------------------------------------------
# ABSOLUTE IDLE - applied ON TOP of the boost lists. Nobody is watching.
# ---------------------------------------------------------------------------
[idle.kill]
# the actual monsters: ~5.0 GB of Brave, ~3.0 GB of Claude
brave
chrome
msedge
firefox
opera
claude
Code
# Docker used to be here. It is in [protect] now - the containers you left running
# matter more than the ~700 MB the backend costs. Delete it from [protect] if you
# would rather have the RAM.
# tray icons nobody can see. NOT tailscale-ipn: that one is the way back in
# (it is what connects the daemon), and the code refuses to kill it anyway.
SecurityHealthSystray
RtkAudUService64
FnHotkeyCapsLKNumLK
# shell pieces (only if KillExplorer=1 - explorer itself is handled separately)
StartMenuExperienceHost
SearchHost
ShellHost
ShellExperienceHost
TextInputHost
sihost
LockApp
backgroundTaskHost
RuntimeBroker
SearchIndexer
# Uncomment if you never leave a terminal running work overnight:
#WindowsTerminal
#OpenConsole
#powershell
#pwsh

[idle.services]
nordvpn-service
CoworkVMService
WpnService
CDPSvc
SSDPSRV
lfsvc
PhoneSvc
InstallService
webthreatdefsvc
whesvc
DusmSvc
SharedAccess
RasMan
SstpSvc
LanmanServer
# AGGRESSIVE - off by default. NvContainer hosts the NVIDIA App; stopping it has
# been known to upset NVENC on some drivers, which is exactly the failure you
# cannot debug from bed. Enable only after you have tested a stream without it.
#NvContainerLocalSystem
# Stopping Defender is possible but tamper protection will usually refuse it.
#WinDefend

# ---------------------------------------------------------------------------
# RESTORE - relaunched by ""Restore desktop"". Deliberately short: restore gives
# you a working machine back, not the bloat back. Uncomment what you miss.
# ---------------------------------------------------------------------------
[restore.launch]
C:\Program Files\Tailscale\tailscale-ipn.exe
#C:\Program Files\NordVPN\NordVPN.exe|--auto-start
#C:\Program Files (x86)\Razer\Razer Cortex\RazerCortex.exe|-autorun

# ---------------------------------------------------------------------------
# Paths disk cleanup must NEVER touch, whatever the scanner thinks. Full paths,
# '*' works, and a path protects everything underneath it. This is the same
# safety net [protect] is for processes.
# ---------------------------------------------------------------------------
[cleanup.protect]
#C:\Users\*\Documents
#D:\backups

# ---------------------------------------------------------------------------
# Store apps the debloater must NEVER suggest. Package names (the ones
# 'Get-AppxPackage' lists), '*' works. The Store itself, winget, the terminal,
# WSL and the codec packs are protected in code and cannot be listed at all -
# they are the way back when a removal turns out to be a mistake.
# ---------------------------------------------------------------------------
[debloat.protect]
#Microsoft.WindowsCalculator
#Microsoft.ScreenSketch

# ---------------------------------------------------------------------------
# Wi-Fi networks the network guard should reconnect to, best first. Names of
# SAVED profiles (the ones 'netsh wlan show profiles' lists) - the guard cannot
# type a password. Empty = every saved network, in Windows' own order.
# ---------------------------------------------------------------------------
[network.wifi]
#HomeNet
#HomeNet 5G

# ---------------------------------------------------------------------------
# REMOTE DESKTOP SETUP - apps the network guard must keep connected. Process
# names ('*' works); pick them in the window ('Remote desktop setup'), where
# the common streaming/remote tools are offered first but ANY app can be
# chosen. Click Calibrate there while everything is connected the way you
# like: the guard remembers each app's exe, service and listening ports, and
# whenever that picture changes it restarts/relaunches the app to get it back.
# ---------------------------------------------------------------------------
[remote.apps]
#sunshine
#parsecd
";
    }

    // --------------------------------------------------------------- ini edits

    // The config window edits the file line by line rather than regenerating it,
    // so every comment you (or the default config) wrote survives being saved.
    // Unchecking an entry comments it out instead of deleting it, which is exactly
    // what a '#' means in this file already.
    internal sealed class IniFile
    {
        private readonly List<string> lines;

        public IniFile() { lines = new List<string>(File.ReadAllLines(Config.Path_)); }

        // For parsing text that is not the config on disk - the shipped default,
        // mostly, so the windows can tell base-kit entries from added ones.
        public IniFile(string[] text) { lines = new List<string>(text); }

        public sealed class Entry
        {
            public readonly string Text;
            public bool Enabled;
            public readonly bool Chosen;      // written by a toast answer ("you chose this on ...")
            public Entry(string text, bool enabled) : this(text, enabled, false) { }
            public Entry(string text, bool enabled, bool chosen) { Text = text; Enabled = enabled; Chosen = chosen; }
        }

        private static bool IsHeader(string line, string section)
        {
            return line.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnyHeader(string line)
        {
            string t = line.Trim();
            return t.StartsWith("[") && t.EndsWith("]");
        }

        private int HeaderOf(string section)
        {
            for (int i = 0; i < lines.Count; i++)
                if (IsHeader(lines[i], section)) return i;
            return -1;
        }

        private int EndOf(int header)
        {
            for (int i = header + 1; i < lines.Count; i++)
                if (IsAnyHeader(lines[i])) return i;
            return lines.Count;
        }

        // The value of a line, whether or not it is commented out.
        private static string Bare(string raw)
        {
            string s = raw.Trim();
            while (s.StartsWith("#") || s.StartsWith(";")) s = s.Substring(1).Trim();
            int cut = s.IndexOfAny(new char[] { '#', ';' });
            if (cut >= 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        private static bool Disabled(string raw)
        {
            string t = raw.TrimStart();
            return t.StartsWith("#") || t.StartsWith(";");
        }

        // Entries of a list section, commented-out ones included so you can see and
        // re-enable what the default config ships as suggestions.
        public List<Entry> Section(string section)
        {
            List<Entry> found = new List<Entry>();
            int h = HeaderOf(section);
            if (h < 0) return found;
            int end = EndOf(h);
            for (int i = h + 1; i < end; i++)
            {
                string bare = Bare(lines[i]);
                if (bare.Length == 0) continue;                 // blank or pure prose comment
                if (bare.IndexOf('=') >= 0 && section == "settings") continue;
                if (Disabled(lines[i]) && IsProse(lines[i], bare)) continue;
                bool chosen = lines[i].IndexOf("you chose this on", StringComparison.OrdinalIgnoreCase) >= 0;
                found.Add(new Entry(bare, !Disabled(lines[i]), chosen));
            }
            return found;
        }

        // A commented-out line is either an entry switched off ("#Steam" - the
        // way this file and the windows write them, no space after the '#') or
        // a sentence somebody wrote for the reader ("# the streaming stack
        // itself"). The space is the first tell; for lines that do have one,
        // a few shapes only prose has decide the rest, so a hand-typed "# Steam"
        // still shows up as an entry.
        private static bool IsProse(string raw, string bare)
        {
            string t = raw.TrimStart();
            if (t.Length > 1 && !char.IsWhiteSpace(t[1])) return false;    // "#Steam"
            if (bare.IndexOf(":\\") >= 0) return false;                     // a path is a path
            if (bare.StartsWith("-")) return true;                            // "# ------ banner"
            if (bare.IndexOf('(') >= 0 || bare.IndexOf('"') >= 0) return true;
            if (bare.EndsWith(".") || bare.EndsWith(":") || bare.IndexOf(", ") >= 0) return true;
            string[] words = bare.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 4) return true;
            if (words.Length == 1) return bare.Length <= 3;                   // "# us"
            foreach (string w in words)
                foreach (char c in w)
                    if (char.IsUpper(c) || char.IsDigit(c) || c == '.' || c == '*') return false;
            return true;                                                      // "# search indexer helpers"
        }

        private int LineOf(string section, string text)
        {
            int h = HeaderOf(section);
            if (h < 0) return -1;
            int end = EndOf(h);
            for (int i = h + 1; i < end; i++)
                if (Bare(lines[i]).Equals(text, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void SetEnabled(string section, string text, bool on)
        {
            int i = LineOf(section, text);
            if (i < 0) { if (on) Add(section, text); return; }
            if (Disabled(lines[i]) == !on) return;
            lines[i] = on ? lines[i].TrimStart('#', ';', ' ', '\t') : "#" + lines[i];
        }

        public void Add(string section, string text)
        {
            if (LineOf(section, text) >= 0) { SetEnabled(section, text, true); return; }
            int h = HeaderOf(section);
            if (h < 0)
            {
                lines.Add("");
                lines.Add("[" + section + "]");
                h = lines.Count - 1;
            }
            lines.Insert(h + 1, text);
        }

        public void Remove(string section, string text)
        {
            int i = LineOf(section, text);
            if (i >= 0) lines.RemoveAt(i);
        }

        public string GetSetting(string key)
        {
            int h = HeaderOf("settings");
            if (h < 0) return null;
            int end = EndOf(h);
            for (int i = h + 1; i < end; i++)
            {
                string bare = Bare(lines[i]);
                int eq = bare.IndexOf('=');
                if (eq <= 0) continue;
                if (bare.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return bare.Substring(eq + 1).Trim();
            }
            return null;
        }

        public void SetSetting(string key, string value)
        {
            int h = HeaderOf("settings");
            if (h < 0)
            {
                lines.Insert(0, "[settings]");
                h = 0;
            }
            int end = EndOf(h);
            for (int i = h + 1; i < end; i++)
            {
                string bare = Bare(lines[i]);
                int eq = bare.IndexOf('=');
                if (eq <= 0) continue;
                if (!bare.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                lines[i] = key + "=" + value;
                return;
            }
            lines.Insert(h + 1, key + "=" + value);
        }

        public void Save()
        {
            File.WriteAllLines(Config.Path_, lines.ToArray(), new UTF8Encoding(false));
        }
    }

    // ----------------------------------------------------------------- state

    // Remembers what a mode changed so RESTORE can put it back exactly.
    internal sealed class StateFile
    {
        private static string Path_ { get { return Path.Combine(App.Dir, "idlemaster.state"); } }

        public string Mode = "";
        public readonly List<string> StoppedServices = new List<string>();
        public bool ExplorerKilled;
        public bool SentryArmed;

        public static StateFile Load()
        {
            StateFile s = new StateFile();
            if (!File.Exists(Path_)) return s;
            foreach (string line in File.ReadAllLines(Path_))
            {
                string[] p = line.Split('|');
                if (p.Length < 2) continue;
                if (p[0] == "mode") s.Mode = p[1];
                else if (p[0] == "svc") s.StoppedServices.Add(p[1]);
                else if (p[0] == "explorer") s.ExplorerKilled = p[1] == "1";
                else if (p[0] == "sentry") s.SentryArmed = p[1] == "1";
            }
            return s;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("mode|" + Mode);
            sb.AppendLine("explorer|" + (ExplorerKilled ? "1" : "0"));
            sb.AppendLine("sentry|" + (SentryArmed ? "1" : "0"));
            foreach (string s in StoppedServices) sb.AppendLine("svc|" + s);
            try { File.WriteAllText(Path_, sb.ToString()); }
            catch (Exception) { }
        }

        public static void Clear()
        {
            try { if (File.Exists(Path_)) File.Delete(Path_); }
            catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- engine

    internal struct KillHit
    {
        public readonly string Name;
        public readonly long Bytes;
        public KillHit(string name, long bytes) { Name = name; Bytes = bytes; }
    }

    // Every process sharing one name, treated as one thing you would recognise:
    // "Docker Desktop, 4 processes, 512 MB" rather than four separate questions.
    internal sealed class Candidate
    {
        public readonly string Name;
        public long Bytes;
        public readonly List<int> Pids = new List<int>();
        public Candidate(string name) { Name = name; }
        public string Key { get { return Name.ToLowerInvariant(); } }
    }

    // One row of "what is running right now", grouped by name and unfiltered -
    // protected processes appear too, tagged KEEP, because a RAM report that
    // hides the biggest consumers is a lie.
    internal sealed class ProcRow
    {
        public string Name;
        public int Count;
        public long Bytes;
        public string Tag = "";     // "KEEP" | "BOOST" | "IDLE" | ""
        public readonly List<int> Pids = new List<int>();
        public string Key { get { return Name.ToLowerInvariant(); } }
    }

    // One snapshot of "what does the user have open on the desktop": every
    // process owning a visible, non-cloaked top-level window, plus the whole
    // process tree, captured together so one sweep judges everything against
    // the same instant. A pid is spared when it - or any ancestor - owns a
    // window. The ancestor walk is what keeps a WebView2/Electron app alive:
    // its helper processes carry a different name (msedgewebview2), so the
    // per-name lists hit them even while the app's window is on screen, but
    // the window belongs to their parent.
    //
    // The shell is deliberately blind here: explorer owns the desktop and the
    // taskbar, and it is also the ancestor of everything launched from them -
    // counting its windows would spare the whole session.
    internal sealed class WindowGuard
    {
        private readonly HashSet<int> windowPids = new HashSet<int>();
        private readonly Dictionary<int, int> parentOf = new Dictionary<int, int>();

        // Names Spares() actually saved, so callers can log each once.
        public readonly HashSet<string> SparedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static WindowGuard Snapshot()
        {
            WindowGuard g = new WindowGuard();

            HashSet<int> shell = new HashSet<int>();
            IntPtr snap = IntPtr.Zero;
            try
            {
                snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
                if (snap != IntPtr.Zero && snap != (IntPtr)(-1))
                {
                    Native.PROCESSENTRY32W e = new Native.PROCESSENTRY32W();
                    e.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32W));
                    if (Native.Process32FirstW(snap, ref e))
                    {
                        do
                        {
                            g.parentOf[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                            if (string.Equals(e.szExeFile, "explorer.exe",
                                    StringComparison.OrdinalIgnoreCase))
                                shell.Add((int)e.th32ProcessID);
                        }
                        while (Native.Process32NextW(snap, ref e));
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                if (snap != IntPtr.Zero && snap != (IntPtr)(-1))
                    try { Native.CloseHandle(snap); } catch (Exception) { }
            }

            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr _)
                {
                    if (Native.IsWindowVisible(h) && !Cloaked(h))
                    {
                        uint pid;
                        Native.GetWindowThreadProcessId(h, out pid);
                        if (pid != 0 && !shell.Contains((int)pid))
                            g.windowPids.Add((int)pid);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }

            return g;
        }

        private static bool Cloaked(IntPtr h)
        {
            try
            {
                int cloaked;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED,
                        out cloaked, sizeof(int)) != 0)
                    return false;    // no DWM answer = assume a real window
                return cloaked != 0;
            }
            catch (Exception) { return false; }
        }

        // True when the pid, or any ancestor still in the tree, owns a window.
        // Capped and cycle-guarded: parent pids can be reused after the parent
        // dies, and a stale link must not turn the walk into a loop. A rare
        // false spare from pid reuse just postpones a kill to the next sweep.
        public bool Spares(int pid, string name)
        {
            int cur = pid;
            HashSet<int> seen = new HashSet<int>();
            for (int hops = 0; hops < 64 && cur > 4 && seen.Add(cur); hops++)
            {
                if (windowPids.Contains(cur))
                {
                    if (name != null) SparedNames.Add(name);
                    return true;
                }
                int up;
                if (!parentOf.TryGetValue(cur, out up)) break;
                cur = up;
            }
            return false;
        }
    }

    // Kill = close it now and every time it comes back (an unlisted name is
    // written into [boost.kill]); KillOnce = close it now, ask again if it
    // returns, nothing written anywhere.
    internal enum Verdict { Keep, KeepAlways, KillOnce, Kill, NoAnswer }

    // What the sentry wants to know about one newcomer.
    internal sealed class Question
    {
        public Candidate What;
        public bool OnKillList;      // false = on no list at all, just big and new
        public string Mode;
    }

    internal sealed class Engine
    {
        private readonly Config cfg;
        private readonly Action<string> log;
        private long freedBytes;

        public Engine(Config c, Action<string> logger) { cfg = c; log = logger; }

        // ---- memory helpers

        public static void ReadMemory(out ulong totalMb, out ulong freeMb)
        {
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            Native.GlobalMemoryStatusEx(ref m);
            totalMb = m.ullTotalPhys / (1024 * 1024);
            freeMb = m.ullAvailPhys / (1024 * 1024);
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        // ---- matching

        public static bool Match(string pattern, string text)
        {
            if (pattern.IndexOf('*') < 0)
                return string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase);
        }

        // The way back in, protected in code rather than in a list. On Windows
        // the Tailscale TRAY app is what tells the daemon which profile to
        // connect: kill it and tailscaled drops to NoState, where no service
        // restart reaches it - the machine is simply gone until somebody at
        // the keyboard starts the app again. That is not a trade any mode may
        // make on a box you only reach remotely, so no ini may list these,
        // the same way the debloat page may never remove the Store.
        private static readonly string[] NeverKill = new string[]
        {
            "tailscaled", "tailscale-ipn", "tailscale",
            "sunshine", "sunshinesvc",
        };

        public bool IsProtectedProcess(string name)
        {
            foreach (string p in NeverKill)
                if (Match(p, name)) return true;
            foreach (string p in cfg.Protect)
                if (Match(p, name)) return true;
            return false;
        }

        public bool IsProtectedService(string name)
        {
            foreach (string p in cfg.ProtectServices)
                if (Match(p, name)) return true;
            return false;
        }

        // One census of what is running, grouped by name, so the sentry can ask
        // about an app once instead of once per process. Nothing is killed here.
        // 'skip' holds names in respawn backoff; 'sparePid' is the foreground app;
        // 'guard' (may be null) drops every pid belonging to an open app.
        public List<Candidate> Census(ICollection<string> skip, int sparePid, WindowGuard guard)
        {
            Dictionary<string, Candidate> byName = new Dictionary<string, Candidate>();
            int me = Process.GetCurrentProcess().Id;

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string name;
                    long ws;
                    int pid;
                    try { name = p.ProcessName; ws = p.WorkingSet64; pid = p.Id; }
                    catch (Exception) { continue; }

                    if (pid == me || pid == sparePid) continue;
                    string key = name.ToLowerInvariant();
                    if (skip != null && skip.Contains(key)) continue;
                    if (IsProtectedProcess(name)) continue;
                    if (guard != null && guard.Spares(pid, name)) continue;

                    Candidate c;
                    if (!byName.TryGetValue(key, out c))
                    {
                        c = new Candidate(name);
                        byName[key] = c;
                    }
                    c.Bytes += ws;
                    c.Pids.Add(pid);
                }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }

            List<Candidate> list = new List<Candidate>();
            foreach (KeyValuePair<string, Candidate> kv in byName) list.Add(kv.Value);
            return list;
        }

        public bool OnList(List<string> patterns, string name)
        {
            foreach (string pat in patterns)
                if (Match(pat, name)) return true;
            return false;
        }

        // Which list a name sits on, highest claim first. "" = untouched.
        public string TagOf(string name)
        {
            if (IsProtectedProcess(name)) return "KEEP";
            if (OnList(cfg.BoostKill, name)) return "BOOST";
            if (OnList(cfg.IdleKill, name)) return "IDLE";
            if (cfg.KillExplorer && string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase))
                return "IDLE";
            return "";
        }

        // Everything running, grouped by name, sorted by appetite. The one
        // enumerator behind Report, the config picker, and the live list in the
        // window. Pass null to skip tagging (the picker does not need it).
        public static List<ProcRow> Snapshot(Engine tagger)
        {
            Dictionary<string, ProcRow> byName = new Dictionary<string, ProcRow>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string name;
                    long ws;
                    int pid;
                    try { name = p.ProcessName; ws = p.WorkingSet64; pid = p.Id; }
                    catch (Exception) { continue; }

                    string key = name.ToLowerInvariant();
                    ProcRow r;
                    if (!byName.TryGetValue(key, out r))
                    {
                        r = new ProcRow();
                        r.Name = name;
                        byName[key] = r;
                    }
                    r.Bytes += ws;
                    r.Count++;
                    r.Pids.Add(pid);
                }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }

            List<ProcRow> rows = new List<ProcRow>();
            foreach (KeyValuePair<string, ProcRow> kv in byName) rows.Add(kv.Value);
            rows.Sort(delegate(ProcRow a, ProcRow b) { return b.Bytes.CompareTo(a.Bytes); });

            if (tagger != null)
                foreach (ProcRow r in rows) r.Tag = tagger.TagOf(r.Name);
            return rows;
        }

        // Kills everything a Candidate covers. Returns only what actually died -
        // a process can exit or refuse between the census and here, and a window
        // can open in that gap too, so the guard is re-checked per pid.
        public List<KillHit> Reap(Candidate c, WindowGuard guard = null)
        {
            List<KillHit> hits = new List<KillHit>();
            foreach (int pid in c.Pids)
            {
                Process p = null;
                try
                {
                    p = Process.GetProcessById(pid);
                    long ws = p.WorkingSet64;
                    if (IsProtectedProcess(p.ProcessName)) continue;
                    if (guard != null && guard.Spares(pid, p.ProcessName)) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                    hits.Add(new KillHit(c.Name, ws));
                }
                catch (Exception) { /* gone already, or refuses - the next sweep retries */ }
                finally { if (p != null) { try { p.Dispose(); } catch (Exception) { } } }
            }
            freedBytes += TotalOf(hits);
            return hits;
        }

        public static long TotalOf(List<KillHit> hits)
        {
            long n = 0;
            foreach (KillHit h in hits) n += h.Bytes;
            return n;
        }

        public static string Size(long bytes) { return Mb(bytes); }

        // ---- process killing

        public void KillList(IEnumerable<string> patterns, string label, WindowGuard guard = null)
        {
            List<string> pats = new List<string>(patterns);
            if (pats.Count == 0) return;
            log("-- " + label);

            int me = Process.GetCurrentProcess().Id;
            long freed = 0;
            int count = 0;
            Dictionary<string, long> perName = new Dictionary<string, long>();

            foreach (Process p in Process.GetProcesses())
            {
                string name;
                long ws;
                try { name = p.ProcessName; ws = p.WorkingSet64; }
                catch (Exception) { continue; }

                if (p.Id == me) continue;
                if (IsProtectedProcess(name)) continue;

                bool hit = false;
                foreach (string pat in pats)
                    if (Match(pat, name)) { hit = true; break; }
                if (!hit) continue;

                if (guard != null && guard.Spares(p.Id, name)) continue;

                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    freed += ws;
                    count++;
                    if (perName.ContainsKey(name)) perName[name] += ws; else perName[name] = ws;
                }
                catch (Exception ex)
                {
                    log("   ! could not stop " + name + " (" + ex.GetType().Name + ")");
                }
            }

            foreach (KeyValuePair<string, long> kv in perName)
                log("   x " + kv.Key + "  " + Mb(kv.Value));
            if (guard != null)
                foreach (string spared in guard.SparedNames)
                    log("   . " + spared + " - window open, left alone");
            if (count == 0) log("   (nothing running)");
            else log("   = " + count + " processes, " + Mb(freed) + " released");
            freedBytes += freed;
        }

        // ---- services

        public void StopServices(IEnumerable<string> names, StateFile state, string label)
        {
            List<string> list = new List<string>(names);
            if (list.Count == 0) return;
            log("-- " + label);
            int stopped = 0;

            foreach (string name in list)
            {
                if (IsProtectedService(name))
                {
                    log("   . " + name + " is protected, skipped");
                    continue;
                }
                try
                {
                    ServiceController sc = new ServiceController(name);
                    if (sc.Status == ServiceControllerStatus.Stopped) continue;

                    foreach (ServiceController dep in sc.DependentServices)
                    {
                        if (IsProtectedService(dep.ServiceName)) continue;
                        if (dep.Status == ServiceControllerStatus.Stopped) continue;
                        try
                        {
                            dep.Stop();
                            dep.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                            Remember(state, dep.ServiceName);
                            log("   x " + dep.ServiceName + " (dependent of " + name + ")");
                        }
                        catch (Exception) { }
                    }

                    if (!sc.CanStop)
                    {
                        log("   . " + name + " refuses to stop");
                        continue;
                    }
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    Remember(state, name);
                    stopped++;
                    log("   x " + name);
                }
                catch (InvalidOperationException)
                {
                    // not installed on this box - fine, the list is generic
                }
                catch (Exception ex)
                {
                    log("   ! " + name + ": " + ex.Message.Split('\n')[0]);
                }
            }
            log("   = " + stopped + " services stopped");
        }

        // The sentry calls this every few minutes; most of the time nothing has
        // come back, so it must not write a line unless it actually did something.
        private static void Remember(StateFile state, string name)
        {
            foreach (string s in state.StoppedServices)
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return;
            state.StoppedServices.Add(name);
        }

        // Names from 'names' that are installed and currently running.
        public List<string> RunningAmong(IEnumerable<string> names)
        {
            List<string> found = new List<string>();
            foreach (string name in names)
            {
                if (IsProtectedService(name)) continue;
                try
                {
                    ServiceController sc = new ServiceController(name);
                    if (sc.Status == ServiceControllerStatus.Running) found.Add(name);
                    sc.Close();
                }
                catch (Exception) { }
            }
            return found;
        }

        // ---- the guard that keeps you able to get back in

        public bool NetworkGuard(bool loud)
        {
            if (!cfg.NetworkGuard) return true;
            bool ok = true;

            ok &= EnsureService("SunshineService", loud);
            ok &= EnsureService("Tailscale", loud);

            bool sunshinePort = false;
            try
            {
                IPEndPoint[] eps = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (IPEndPoint ep in eps)
                    if (ep.Port == 47984 || ep.Port == 47989 || ep.Port == 47990 || ep.Port == 48010)
                    { sunshinePort = true; break; }
            }
            catch (Exception) { }

            bool tailscaleUp = false;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ni.Name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up) tailscaleUp = true;
                    }
                }
            }
            catch (Exception) { }

            if (loud)
            {
                log("   " + (sunshinePort ? "+" : "!") + " Sunshine listening: " + (sunshinePort ? "yes" : "NO"));
                log("   " + (tailscaleUp ? "+" : "!") + " Tailscale adapter up: " + (tailscaleUp ? "yes" : "NO"));
            }
            return ok && sunshinePort && tailscaleUp;
        }

        public bool EnsureService(string name, bool loud)
        {
            try
            {
                ServiceController sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    if (loud) log("   + " + name + " running");
                    return true;
                }
                log("   ! " + name + " was not running - restarting it");
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                log("   ! " + name + " check failed: " + ex.Message.Split('\n')[0]);
                return false;
            }
        }

        // Is the service installed at all? Asking a missing one for its status throws.
        public static bool ServiceExists(string name)
        {
            try
            {
                ServiceController sc = new ServiceController(name);
                ServiceControllerStatus s = sc.Status;
                sc.Close();
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool ServiceRunning(string name)
        {
            try
            {
                ServiceController sc = new ServiceController(name);
                bool on = sc.Status == ServiceControllerStatus.Running;
                sc.Close();
                return on;
            }
            catch (Exception) { return false; }
        }

        // Stop + start, for a service that is running but no longer doing its job.
        public bool RestartService(string name)
        {
            try
            {
                ServiceController sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Stopped && sc.CanStop)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                bool ok = sc.Status == ServiceControllerStatus.Running;
                sc.Close();
                return ok;
            }
            catch (Exception ex)
            {
                log("   ! " + name + " restart failed: " + ex.Message.Split('\n')[0]);
                return false;
            }
        }

        // ---- trimming

        public void TrimAll() { TrimAll(true); }

        public long TrimAll(bool loud)
        {
            if (!cfg.TrimWorkingSets) return 0;
            if (loud) log("-- squeezing working sets");
            int n = 0;
            long before = 0, after = 0;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    before += p.WorkingSet64;
                    if (Native.EmptyWorkingSet(p.Handle)) n++;
                    p.Refresh();
                    after += p.WorkingSet64;
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            if (loud) log("   = trimmed " + n + " processes, " + Mb(before - after) + " pushed out of RAM");

            if (cfg.ClearStandbyList)
            {
                Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
                Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
                bool a = Native.SetMemoryList(Native.MemoryEmptyWorkingSets);
                bool b = Native.SetMemoryList(Native.MemoryPurgeStandbyList);
                if (loud) log("   = standby list purge: " + ((a || b) ? "done" : "refused by kernel"));
            }
            return before - after;
        }

        // ---- explorer

        private void KillExplorer(StateFile state)
        {
            log("-- closing the Windows shell");
            bool any = false;
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(4000); any = true; }
                catch (Exception ex) { log("   ! explorer: " + ex.Message.Split('\n')[0]); }
            }
            state.ExplorerKilled = any;
            log(any
                ? "   x explorer closed. Ctrl+Shift+Esc still opens Task Manager if you need a way back."
                : "   (explorer was not running)");
        }

        private void StartExplorer()
        {
            if (Process.GetProcessesByName("explorer").Length > 0) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                log("   + explorer restarted");
            }
            catch (Exception ex) { log("   ! could not start explorer: " + ex.Message); }
        }

        // ---- modes

        public void RunBoost()
        {
            ulong t, f0; ReadMemory(out t, out f0);
            Banner("BOOST NOW", t, f0);
            freedBytes = 0;

            StateFile state = StateFile.Load();
            state.Mode = "boost";

            // Browsers are exempt from the guard: CloseBrowsersInBoost is an
            // explicit "yes, even though they are open".
            KillList(cfg.BoostKill, "killing background clutter",
                cfg.SkipOpenApps ? WindowGuard.Snapshot() : null);
            if (cfg.CloseBrowsersInBoost)
                KillList(Browsers, "closing browsers");
            StopServices(cfg.BoostServices, state, "stopping non-essential services");

            log("-- checking your way back in");
            NetworkGuard(true);
            TrimAll();
            state.SentryArmed = cfg.Sentry;
            state.Save();
            Done(t, f0);
        }

        public static readonly string[] Browsers =
            new string[] { "brave", "chrome", "msedge", "firefox", "opera" };

        // Everything the sentry should keep hunting for a given mode.
        public List<string> PatternsFor(string mode)
        {
            List<string> pats = new List<string>(cfg.BoostKill);
            if (mode == "idle")
            {
                pats.AddRange(cfg.IdleKill);
                if (cfg.KillExplorer) pats.Add("explorer");
            }
            else if (cfg.CloseBrowsersInBoost)
            {
                pats.AddRange(Browsers);
            }
            return pats;
        }

        public List<string> ServicesFor(string mode)
        {
            List<string> svc = new List<string>(cfg.BoostServices);
            if (mode == "idle") svc.AddRange(cfg.IdleServices);
            return svc;
        }

        public void RunIdle()
        {
            ulong t, f0; ReadMemory(out t, out f0);
            Banner("ABSOLUTE IDLE", t, f0);
            freedBytes = 0;

            StateFile state = StateFile.Load();
            state.Mode = "idle";

            KillList(cfg.BoostKill, "killing background clutter");
            StopServices(cfg.BoostServices, state, "stopping non-essential services");

            log("-- checkpoint: is the stream stack still alive?");
            NetworkGuard(true);

            KillList(cfg.IdleKill, "closing everything a human would use");
            StopServices(cfg.IdleServices, state, "stopping the rest");

            if (cfg.KillExplorer) KillExplorer(state);

            log("-- final check on Sunshine + Tailscale");
            bool ok = NetworkGuard(true);
            if (!ok)
                log("   !! Remote access looks degraded. Fixing what I can - if this persists, "
                    + "hit Restore before you walk away.");

            TrimAll();
            state.SentryArmed = cfg.Sentry;
            state.Save();
            Done(t, f0);
        }

        public void RunRestore()
        {
            ulong t, f0; ReadMemory(out t, out f0);
            Banner("RESTORE", t, f0);

            // Anything still hunting must stand down before we start putting things
            // back, or it will shoot them again on its next sweep.
            if (Sentry.SignalStop())
                log("-- told the sentry to stand down");

            StateFile state = StateFile.Load();
            log("-- restarting services this tool stopped (" + state.StoppedServices.Count + ")");
            for (int i = state.StoppedServices.Count - 1; i >= 0; i--)
            {
                string name = state.StoppedServices[i];
                try
                {
                    ServiceController sc = new ServiceController(name);
                    if (sc.Status == ServiceControllerStatus.Running) continue;
                    if (StartTypeIsDisabled(name)) { log("   . " + name + " is disabled, left alone"); continue; }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    log("   + " + name);
                }
                catch (Exception ex) { log("   ! " + name + ": " + ex.Message.Split('\n')[0]); }
            }

            log("-- bringing the desktop back");
            StartExplorer();
            Thread.Sleep(1500);

            foreach (string entry in cfg.RestoreLaunch)
            {
                string[] parts = entry.Split('|');
                string exe = parts[0].Trim();
                string args = parts.Length > 1 ? parts[1].Trim() : "";
                if (!File.Exists(exe)) continue;
                if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0) continue;
                try
                {
                    Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                    log("   + " + Path.GetFileName(exe));
                }
                catch (Exception ex) { log("   ! " + Path.GetFileName(exe) + ": " + ex.Message); }
            }

            log("-- verifying remote access");
            NetworkGuard(true);
            StateFile.Clear();
            Done(t, f0);
        }

        private static bool StartTypeIsDisabled(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    if (k == null) return false;
                    object v = k.GetValue("Start");
                    return v != null && Convert.ToInt32(v) == 4;
                }
            }
            catch (Exception) { return false; }
        }

        public void Report()
        {
            ulong t, f;
            ReadMemory(out t, out f);
            log("RAM: " + (t - f) + " MB used of " + t + " MB  (" + f + " MB free)");
            log("");
            log("Biggest consumers right now:");

            int shown = 0;
            foreach (ProcRow r in Snapshot(this))
            {
                if (shown++ >= 25) break;
                string tag = r.Tag.Length == 0 ? "        " : (" [" + r.Tag + "]").PadRight(8);
                log(string.Format(CultureInfo.InvariantCulture, "  {0,8} {1,-32} x{2,-3} {3}",
                    tag, r.Name, r.Count, Mb(r.Bytes)));
            }

            log("");
            log("Services that would be stopped (only the ones actually running):");
            ReportServices(cfg.BoostServices, "BOOST");
            ReportServices(cfg.IdleServices, "IDLE ");

            log("");
            log("  [BOOST] closed by Boost Now   [IDLE] also closed by Absolute Idle   [KEEP] never touched");
            log("  Untagged rows are left alone. Add them to idlemaster.ini if you want them gone.");
        }

        private void ReportServices(List<string> names, string tag)
        {
            foreach (string name in names)
            {
                if (IsProtectedService(name)) continue;
                try
                {
                    ServiceController sc = new ServiceController(name);
                    if (sc.Status != ServiceControllerStatus.Running) continue;
                    log("   [" + tag + "] " + name + "  -  " + sc.DisplayName);
                }
                catch (Exception) { }
            }
        }

        private void Banner(string mode, ulong total, ulong free)
        {
            log("");
            log("=== " + mode + " === " + DateTime.Now.ToString("HH:mm:ss"));
            log("    starting at " + (total - free) + " MB used / " + total + " MB");
        }

        private void Done(ulong total, ulong freeBefore)
        {
            Thread.Sleep(800);
            ulong t, f;
            ReadMemory(out t, out f);
            long delta = (long)f - (long)freeBefore;
            log("");
            log("=== done: " + (t - f) + " MB used / " + t + " MB   ("
                + (delta >= 0 ? "+" : "") + delta + " MB freed)");
            log("");
        }
    }

    // ------------------------------------------------------------------ sentry

    // A mode is a snapshot: you boost, and twenty minutes later WebView2 is back,
    // the search indexer trigger-started itself, and you are down a gigabyte again.
    // The sentry re-applies the same lists on a timer until you Restore.
    //
    // Two things keep it from being a menace:
    //   - respawn backoff: a name that keeps coming back gets left alone rather
    //     than farmed in an endless kill/respawn loop,
    //   - foreground guard: in boost mode it never kills the window you are using.
    internal sealed class Sentry
    {
        private const string StopEvent = "Global\\IdleMasterSentryStop";
        private const string OnlyOne = "Global\\IdleMasterSentryRunning";

        private readonly Config cfg;
        private readonly Engine engine;
        private readonly Action<string> log;
        private readonly string mode;

        private Thread thread;
        private Semaphore slot;          // a Mutex would be thread-affine, and the
        private bool holding;            // watch can end on either thread
        private readonly object gate = new object();
        private EventWaitHandle stopFlag;
        private volatile bool stopping;

        // read by the UI
        public int Reaped;
        public long Reclaimed;
        public int Restopped;
        public int FullPasses;
        public DateTime Since;
        public DateTime LastHit;
        public bool Alive { get { return thread != null && thread.IsAlive; } }
        public string Mode { get { return mode; } }

        // name -> when its backoff expires; name -> how many times we have killed it
        private readonly Dictionary<string, DateTime> cooling = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> tally = new Dictionary<string, int>();

        // Kill-list names the window guard already announced, so "leaving it
        // alone" is said once per open window, not every 20 seconds.
        private readonly HashSet<string> sparedAnnounced =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Everything running when the watch began. Those are the ones the mode was
        // aimed at, so they die quietly. Anything not in here arrived afterwards,
        // which means you started it, which means it gets a dialog.
        private readonly HashSet<string> census = new HashSet<string>();
        private bool firstSweep = true;

        // Set by whoever owns a UI. Null means nobody can answer, so nothing is asked.
        public Func<Question, Verdict> Ask;

        public Sentry(Config c, Engine e, Action<string> logger, string modeName)
        {
            cfg = c; engine = e; log = logger;
            mode = (modeName == "idle") ? "idle" : "boost";
        }

        // Tell whatever sentry is running - this process or another - to stand down.
        public static bool SignalStop()
        {
            try
            {
                EventWaitHandle h;
                if (!EventWaitHandle.TryOpenExisting(StopEvent, out h)) return false;
                h.Set();
                h.Close();
                return true;
            }
            catch (Exception) { return false; }
        }

        // The handle outlives the watch, so existence proves nothing - only failing
        // to take the slot means somebody else really is holding it.
        public static bool IsRunningSomewhere()
        {
            try
            {
                Semaphore s;
                if (!Semaphore.TryOpenExisting(OnlyOne, out s)) return false;
                bool free = s.WaitOne(0);
                if (free) s.Release();
                s.Close();
                return !free;
            }
            catch (Exception) { return false; }
        }

        public bool Start()
        {
            if (Alive) return true;
            try
            {
                bool fresh;
                slot = new Semaphore(1, 1, OnlyOne, out fresh);
                holding = slot.WaitOne(0);
                if (!holding)
                {
                    slot.Close();
                    slot = null;
                    log("[sentry] sentry found - another watch already has the slot");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log("[sentry] could not claim the watch: " + ex.Message.Split('\n')[0]);
                return false;
            }

            try
            {
                bool created;
                stopFlag = new EventWaitHandle(false, EventResetMode.ManualReset, StopEvent, out created);
                stopFlag.Reset();
            }
            catch (Exception) { stopFlag = new EventWaitHandle(false, EventResetMode.ManualReset); }

            stopping = false;
            Reaped = 0; Reclaimed = 0; Restopped = 0; FullPasses = 0;
            Since = DateTime.Now;
            cooling.Clear();
            tally.Clear();
            sparedAnnounced.Clear();

            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();

            log("[sentry] on watch, enforcing " + mode.ToUpperInvariant()
                + " every " + cfg.SentrySeconds + "s. Restore turns it off.");
            if (cfg.OverclockedSentry)
                log("[sentry] OVERCLOCKED - everything not on the protect list is fair game.");
            return true;
        }

        public void Stop()
        {
            stopping = true;
            AskForm.CloseAll();     // a dialog nobody answered must not hold the join
            if (!Alive) { Release(); return; }
            try { if (stopFlag != null) stopFlag.Set(); }
            catch (Exception) { }
            try { thread.Join(4000); }
            catch (Exception) { }
            Release();
            log("[sentry] off watch. " + Reaped + " processes reaped, "
                + Engine.Size(Reclaimed) + " kept out of RAM.");
        }

        // Safe to call from either thread, and safe to call twice: whoever finishes
        // first frees the slot so the next sentry - in this process or another -
        // can take the watch immediately.
        private void ReleaseSlot()
        {
            lock (gate)
            {
                if (!holding || slot == null) return;
                try { slot.Release(); }
                catch (Exception) { }
                try { slot.Close(); }
                catch (Exception) { }
                holding = false;
                slot = null;
            }
        }

        private void Release()
        {
            ReleaseSlot();
            try { if (stopFlag != null) { stopFlag.Reset(); stopFlag.Close(); } }
            catch (Exception) { }
            stopFlag = null;
        }

        public void Join() { try { if (thread != null) thread.Join(); } catch (Exception) { } }

        private void Loop()
        {
            DateTime nextService = DateTime.Now.AddMinutes(cfg.SentryServiceMinutes);
            DateTime nextTrim = DateTime.Now.AddMinutes(cfg.SentryTrimMinutes);
            DateTime nextGuard = DateTime.Now.AddMinutes(cfg.SentryGuardMinutes);
            DateTime nextFull = DateTime.Now.AddMinutes(Math.Max(1, cfg.SentryFullPassMinutes));
            DateTime lastPressure = DateTime.MinValue;

            while (!stopping)
            {
                try
                {
                    // Rebuilt every sweep, so edits made in the config window take
                    // effect without restarting the watch.
                    SweepProcesses(engine.PatternsFor(mode));

                    // The "boost again" timers: a whole pass in one go, on a
                    // schedule and/or when RAM runs low. The processes were
                    // just swept above; this is the rest of what BOOST does.
                    string why = null;
                    if (cfg.SentryFullPassMinutes > 0 && DateTime.Now >= nextFull)
                        why = "scheduled, every " + cfg.SentryFullPassMinutes + " min";
                    if (cfg.BoostWhenFreeBelowMb > 0
                        && (DateTime.Now - lastPressure).TotalMinutes >= 5)
                    {
                        ulong t0, f0;
                        Engine.ReadMemory(out t0, out f0);
                        if (f0 < (ulong)cfg.BoostWhenFreeBelowMb)
                        {
                            why = "free RAM is down to " + f0 + " MB";
                            lastPressure = DateTime.Now;
                        }
                    }
                    if (why != null)
                    {
                        log("[sentry] full pass - " + why);
                        SweepServices(engine.ServicesFor(mode));
                        long pushed = engine.TrimAll(false);
                        engine.NetworkGuard(false);
                        log("[sentry] full pass done - " + Engine.Size(pushed) + " pushed out");
                        nextService = DateTime.Now.AddMinutes(cfg.SentryServiceMinutes);
                        nextTrim = DateTime.Now.AddMinutes(cfg.SentryTrimMinutes);
                        nextGuard = DateTime.Now.AddMinutes(cfg.SentryGuardMinutes);
                        FullPasses++;
                    }
                    if (cfg.SentryFullPassMinutes > 0 && DateTime.Now >= nextFull)
                        nextFull = DateTime.Now.AddMinutes(cfg.SentryFullPassMinutes);

                    if (DateTime.Now >= nextService)
                    {
                        SweepServices(engine.ServicesFor(mode));
                        nextService = DateTime.Now.AddMinutes(cfg.SentryServiceMinutes);
                    }

                    ulong total, free;
                    Engine.ReadMemory(out total, out free);
                    bool starved = cfg.TrimWhenFreeBelowMb > 0 && free < (ulong)cfg.TrimWhenFreeBelowMb;

                    if (DateTime.Now >= nextTrim || starved)
                    {
                        long pushed = engine.TrimAll(false);
                        if (starved)
                            log("[sentry] free RAM was " + free + " MB - trimmed, "
                                + Engine.Size(pushed) + " pushed out");
                        nextTrim = DateTime.Now.AddMinutes(cfg.SentryTrimMinutes);
                    }

                    if (DateTime.Now >= nextGuard)
                    {
                        engine.NetworkGuard(false);   // silent unless it has to fix something
                        nextGuard = DateTime.Now.AddMinutes(cfg.SentryGuardMinutes);
                    }
                }
                catch (Exception ex)
                {
                    log("[sentry] sweep failed: " + ex.Message.Split('\n')[0]);
                }

                if (stopping) break;
                try
                {
                    if (stopFlag != null && stopFlag.WaitOne(cfg.SentrySeconds * 1000)) break;
                    if (stopFlag == null) Thread.Sleep(cfg.SentrySeconds * 1000);
                }
                catch (Exception) { break; }
            }
            stopping = true;
            ReleaseSlot();     // stopped from outside: hand the watch back right away
        }

        private void SweepProcesses(List<string> patterns)
        {
            ExpireBackoff();

            HashSet<string> skip = new HashSet<string>();
            foreach (KeyValuePair<string, DateTime> kv in cooling) skip.Add(kv.Key);

            // Overclocked: nobody is at the keyboard, so nothing is spared and
            // everything not protected counts as listed. [protect] still wins -
            // Census never hands over a protected name.
            bool over = cfg.OverclockedSentry;

            int spare = 0;
            if (!over && mode == "boost" && cfg.SentrySkipForeground) spare = Native.ForegroundPid();

            WindowGuard guard = null;
            if (!over && mode == "boost" && cfg.SkipOpenApps) guard = WindowGuard.Snapshot();

            List<Candidate> all = engine.Census(skip, spare, guard);
            List<KillHit> hits = new List<KillHit>();

            foreach (Candidate c in all)
            {
                bool listed = over || engine.OnList(patterns, c.Name);

                if (firstSweep)
                {
                    // Opening census: everything present now is what the mode was for.
                    census.Add(c.Key);
                    if (listed) hits.AddRange(engine.Reap(c, guard));
                    continue;
                }

                bool newcomer = !census.Contains(c.Key);
                if (!listed && !newcomer) continue;                 // untouched, as always
                if (!listed && cfg.AskAboveMb <= 0) { census.Add(c.Key); continue; }
                if (!listed && c.Bytes < (long)cfg.AskAboveMb * 1024 * 1024)
                {
                    census.Add(c.Key);                              // small and harmless
                    continue;
                }

                // Something already known to be junk, respawning: no question.
                if (listed && !newcomer) { hits.AddRange(engine.Reap(c, guard)); continue; }

                Verdict v = Consult(c, listed);
                if (v == Verdict.NoAnswer)
                {
                    // Nobody answered: the ini says what that means.
                    if (cfg.AskTimeoutAction == "always") v = Verdict.Kill;
                    else if (cfg.AskTimeoutAction == "keep") v = Verdict.Keep;
                    else v = Verdict.KillOnce;
                    log("[sentry] no answer about " + c.Name + " - "
                        + (v == Verdict.Kill ? "trashing it, and every time"
                         : v == Verdict.Keep ? "leaving it alone" : "trashing it once"));
                }

                switch (v)
                {
                    case Verdict.KillOnce:
                        // Gone now; not on the census, so if it comes back after
                        // the backoff it is a newcomer again and you are asked again.
                        hits.AddRange(engine.Reap(c, guard));
                        cooling[c.Key] = DateTime.Now.AddMinutes(cfg.SentryBackoffMinutes);
                        log("[sentry] " + c.Name + " closed once - asking again if it returns");
                        break;

                    case Verdict.Kill:
                        hits.AddRange(engine.Reap(c, guard));
                        census.Add(c.Key);                          // silent from now on
                        if (!listed && Config.Append("boost.kill", c.Name))
                        {
                            cfg.BoostKill.Add(c.Name);
                            log("[sentry] " + c.Name + " added to the boost kill list");
                        }
                        break;

                    case Verdict.KeepAlways:
                        if (Config.Append("protect", c.Name))
                        {
                            cfg.Protect.Add(c.Name);
                            log("[sentry] " + c.Name + " is protected from now on");
                        }
                        break;

                    default:    // Keep, or nobody answered
                        cooling[c.Key] = DateTime.Now.AddMinutes(cfg.SentryBackoffMinutes);
                        log("[sentry] leaving " + c.Name + " alone for "
                            + cfg.SentryBackoffMinutes + " min");
                        break;
                }
            }

            firstSweep = false;

            if (guard != null)
            {
                // Only genuine saves get a line: names the lists would have
                // killed. Announced once; closing the window drops the name
                // here, so reopening it later earns a fresh line.
                foreach (string name in guard.SparedNames)
                {
                    if (!engine.OnList(patterns, name)) continue;
                    if (!sparedAnnounced.Add(name)) continue;
                    log("[sentry] " + name + " has a window open - leaving it alone");
                }
                sparedAnnounced.IntersectWith(guard.SparedNames);
            }

            if (hits.Count == 0) return;

            Dictionary<string, int> byName = new Dictionary<string, int>();
            long freed = 0;
            foreach (KillHit h in hits)
            {
                string key = h.Name.ToLowerInvariant();
                byName[h.Name] = byName.ContainsKey(h.Name) ? byName[h.Name] + 1 : 1;
                tally[key] = tally.ContainsKey(key) ? tally[key] + 1 : 1;
                freed += h.Bytes;
            }

            Reaped += hits.Count;
            Reclaimed += freed;
            LastHit = DateTime.Now;

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in byName)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key);
                if (kv.Value > 1) sb.Append(" x" + kv.Value);
            }
            log("[sentry] reaped " + sb + "  -  " + Engine.Size(freed) + " back");

            // Anything that keeps clawing its way back gets left alone for a while.
            foreach (KeyValuePair<string, int> kv in new List<KeyValuePair<string, int>>(tally))
            {
                if (kv.Value < cfg.SentryRespawnLimit) continue;
                if (cooling.ContainsKey(kv.Key)) continue;
                cooling[kv.Key] = DateTime.Now.AddMinutes(cfg.SentryBackoffMinutes);
                log("[sentry] " + kv.Key + " has come back " + kv.Value
                    + " times - something wants it alive, leaving it for "
                    + cfg.SentryBackoffMinutes + " min");
            }
        }

        // Put the question to whoever is at the keyboard. With no UI, or in idle
        // mode where nobody is watching, the lists decide on their own.
        private Verdict Consult(Candidate c, bool listed)
        {
            // Overclocked means away: nobody would answer a toast, and the whole
            // point is that everything unprotected dies.
            if (cfg.OverclockedSentry) return Verdict.Kill;
            if (!cfg.AskBeforeKill || Ask == null || mode == "idle" || stopping)
                return listed ? Verdict.Kill : Verdict.Keep;

            Question q = new Question();
            q.What = c;
            q.OnKillList = listed;
            q.Mode = mode;
            log("[sentry] " + c.Name + " showed up (" + Engine.Size(c.Bytes) + ") - asking you");
            try { return Ask(q); }
            catch (Exception) { return Verdict.Keep; }
        }

        private void ExpireBackoff()
        {
            if (cooling.Count == 0) return;
            List<string> done = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in cooling)
                if (DateTime.Now >= kv.Value) done.Add(kv.Key);
            foreach (string name in done)
            {
                cooling.Remove(name);
                tally.Remove(name);
                log("[sentry] " + name + " is back on the list");
            }
        }

        private void SweepServices(List<string> services)
        {
            List<string> back = engine.RunningAmong(services);
            if (back.Count == 0) return;

            StateFile state = StateFile.Load();
            engine.StopServices(back, state, "[sentry] services that restarted themselves");
            state.Mode = mode;
            state.SentryArmed = true;
            state.Save();
            Restopped += back.Count;
        }
    }

    // ------------------------------------------------------------------- app

    internal static class App
    {
        public static string Dir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); }
        }

        // The mark, at every size, from the .ico build.ps1 puts inside the exe.
        // Windows picks the frame it wants for a title bar or a tray slot; the
        // Shield fallback is what it drew before there was an icon at all.
        private static Icon icon;

        public static Icon Icon
        {
            get
            {
                if (icon != null) return icon;
                try
                {
                    using (Stream s = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("idlemaster.ico"))
                    {
                        if (s != null) icon = new Icon(s);
                    }
                }
                catch (Exception) { }
                if (icon == null) icon = SystemIcons.Shield;
                return icon;
            }
        }

        private static string LogPath { get { return Path.Combine(Dir, "idlemaster.log"); } }

        // The tail that follows another process's sentry needs to know where
        // everybody writes.
        public static string LogFile { get { return LogPath; } }

        public static void FileLog(string line)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
            }
            catch (Exception) { }
        }

        [STAThread]
        public static int Main(string[] argv)
        {
            string mode = "";
            bool watch = false, guardOnly = false;
            foreach (string a in argv)
            {
                string s = a.TrimStart('-', '/').ToLowerInvariant();
                if (s == "boost" || s == "idle" || s == "restore" || s == "report" || s == "help"
                    || s == "unwatch" || s == "stopwatch" || s == "installtask" || s == "removetask"
                    || s == "cleanup-report" || s == "debloat-report" || s == "network" || s == "guard"
                    || s == "startup")
                    mode = s;
                else if (s == "remote") mode = "network";     // 0.7.0's name for it
                else if (s == "watch" || s == "hunt")
                    watch = true;
                if (s == "guard") guardOnly = true;
                if (s == "installtask") mode = "installtask";   // "--installtask --guard": the task runs --guard
            }
            if (watch && mode == "") mode = "watch";

            Config cfg;
            try { cfg = Config.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("Bad idlemaster.ini:\n\n" + ex.Message, "Idle Master");
                return 2;
            }

            // One Idle Master per machine. Every long-lived shape of the app -
            // the window, --watch, --guard, --startup - claims the same global
            // slot before it puts anything in the tray, so a logon task plus a
            // manual launch can never stack two icons again. The loser of the
            // race does not run: a window launch wakes the running copy and
            // brings ITS window to the front instead.
            bool longLived = mode == "" || mode == "watch" || mode == "guard" || mode == "startup";
            if (longLived && !SoloInstance.Claim())
            {
                if (mode == "" || mode == "startup")
                {
                    SoloInstance.PokeRunning();
                    return 0;
                }
                // --watch / --guard come from scheduled tasks: no window is
                // popped, just a line for whoever reads the task's output.
                Native.AttachConsole(-1);
                Console.WriteLine(Sentry.IsRunningSomewhere()
                    ? "an Idle Master is already running and its sentry has the watch - nothing to do."
                    : "an Idle Master is already running - use it instead of starting a second copy.");
                return 1;
            }

            if (mode == "")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(cfg));
                return 0;
            }

            Native.AttachConsole(-1);
            StreamWriter so = new StreamWriter(Console.OpenStandardOutput());
            so.AutoFlush = true;
            Console.SetOut(so);

            Action<string> logger = delegate(string s) { Console.WriteLine(s); FileLog(s); };
            Engine eng = new Engine(cfg, logger);

            switch (mode)
            {
                case "boost": eng.RunBoost(); break;
                case "idle": eng.RunIdle(); break;
                case "restore": eng.RunRestore(); break;
                case "report": eng.Report(); break;
                case "cleanup-report": CleanupReport(cfg, logger); break;
                case "debloat-report": DebloatReport(cfg, logger); break;
                case "network": return NetworkCheck(cfg, eng, logger);
                case "guard":
                {
                    // The network guard alone, in the tray: no sentry, no window.
                    if (NetGuard.IsRunningSomewhere())
                    {
                        Console.WriteLine("another Idle Master is already guarding the connection - nothing to do.");
                        return 1;
                    }
                    Console.WriteLine("guarding the connection from the tray. Exit from the tray menu stops it.");
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    MainForm g = new MainForm(cfg);
                    g.HideOnStart("");
                    g.ForceGuard();
                    Application.Run(g);
                    return 0;
                }
                case "startup":
                {
                    // The logon task (StartWithWindows). The window opens like a
                    // normal launch, and StartupAction says what it does next.
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    MainForm sf = new MainForm(cfg);
                    sf.RunOnLogon();
                    Application.Run(sf);
                    return 0;
                }
                case "watch": break;          // no mode run, just take up the watch
                case "unwatch":
                case "stopwatch":
                    Console.WriteLine(Sentry.SignalStop()
                        ? "sentry told to stand down."
                        : "no sentry is running.");
                    return 0;
                case "installtask": return Task_(true, guardOnly);
                case "removetask": return Task_(false, false);
                default:
                    Console.WriteLine("IdleMaster.exe [--boost | --idle | --restore | --report]");
                    Console.WriteLine("  --watch           keep hunting after the mode, until --unwatch");
                    Console.WriteLine("  --unwatch         stop the sentry");
                    Console.WriteLine("  --installtask     run the sentry (and the network guard) at every logon (scheduled task)");
                    Console.WriteLine("  --installtask --guard   ...or only the network guard at logon");
                    Console.WriteLine("  --removetask      undo that");
                    Console.WriteLine("  --cleanup-report  scan the disk for junk and print what was found");
                    Console.WriteLine("  --debloat-report  list the preinstalled Store apps and which are known bloat");
                    Console.WriteLine("  --network         check link + internet + Tailscale + Sunshine now, fix what is down");
                    Console.WriteLine("  --guard           sit in the tray running only the network guard");
                    Console.WriteLine("  --startup         what the StartWithWindows logon task runs: open the window,");
                    Console.WriteLine("                    then run StartupAction (none/boost/idle) from the ini");
                    Console.WriteLine("  no arguments = open the window");
                    return 0;
            }

            if (watch && (mode == "boost" || mode == "idle" || mode == "watch"))
            {
                string enforce = mode;
                if (enforce == "watch")
                {
                    StateFile st = StateFile.Load();
                    enforce = st.Mode.Length > 0 ? st.Mode : "boost";
                    Console.WriteLine("no mode given, enforcing the last one: " + enforce);
                }

                if (Sentry.IsRunningSomewhere())
                {
                    Console.WriteLine("sentry found - one is already on watch. Its log: " + LogPath);
                    return 1;
                }

                // The watch runs as a tray app rather than a console loop: it needs
                // a message pump to put the "something just started" dialog on screen.
                Console.WriteLine("hunting " + enforce + " in the tray. --unwatch stops it.");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm form = new MainForm(cfg);
                form.HideOnStart(enforce);
                Application.Run(form);
            }
            return 0;
        }

        // One network-guard check by hand: measure, fix what is down, print the
        // picture. Exit 0 = everything up, 1 = something still is not - so a
        // scheduled task or a script can tell.
        private static int NetworkCheck(Config cfg, Engine eng, Action<string> log)
        {
            NetGuard g = new NetGuard(cfg, eng, log);
            NetReport r = g.Check(true, true);
            return r.Healthy ? 0 : 1;
        }

        // The console twin of the cleanup window: same scanner, same findings,
        // but strictly read-only - the CLI never deletes anything.
        private static void CleanupReport(Config cfg, Action<string> log)
        {
            log("-- disk cleanup scan (report only - nothing is deleted)");
            CleanupScanner scanner = new CleanupScanner(cfg);
            List<CleanupItem> found = scanner.Scan(
                delegate(string where) { },
                delegate(CleanupItem it) { });

            List<string> order = new List<string>();
            Dictionary<string, List<CleanupItem>> groups =
                new Dictionary<string, List<CleanupItem>>();
            foreach (CleanupItem it in found)
            {
                List<CleanupItem> g;
                if (!groups.TryGetValue(it.Category, out g))
                {
                    g = new List<CleanupItem>();
                    groups[it.Category] = g;
                    order.Add(it.Category);
                }
                g.Add(it);
            }

            long junk = 0;
            foreach (string cat in order)
            {
                log("-- " + cat);
                foreach (CleanupItem it in groups[cat])
                {
                    if (it.Safe) junk += it.Bytes;
                    // Suggestions carry their path - two folders can share a
                    // name ("Docker", "Blackmagic Design") and only the path
                    // says which is which.
                    bool wherePlease = cat == "Big folders" || cat == "Possible leftovers";
                    log("   " + (it.Safe ? "safe   " : "review ")
                        + CleanupScanner.Nice(it.Bytes).PadLeft(9) + "  " + it.Name
                        + (wherePlease ? "  - " + it.Path : "")
                        + (it.Note.Length > 0 ? "  (" + it.Note + ")" : ""));
                }
            }
            log("   = " + found.Count + " findings; " + CleanupScanner.Nice(junk)
                + " of known junk. The window ('Disk cleanup') does the actual cleaning.");
        }

        // The console twin of the debloat window: same scanner, same table,
        // but strictly read-only - the CLI never uninstalls anything.
        private static void DebloatReport(Config cfg, Action<string> log)
        {
            log("-- debloat scan (report only - nothing is removed)");
            DebloatScanner scanner = new DebloatScanner(cfg);
            List<DebloatItem> found = scanner.Scan(
                delegate(string where) { },
                delegate(DebloatItem it) { });

            found.Sort(delegate(DebloatItem a, DebloatItem b)
            {
                int r = DebloatScanner.Rank(a.Category).CompareTo(DebloatScanner.Rank(b.Category));
                return r != 0 ? r : b.Bytes.CompareTo(a.Bytes);
            });

            int bloat = 0;
            string last = null;
            foreach (DebloatItem it in found)
            {
                if (it.Category != last) { log("-- " + it.Category); last = it.Category; }
                if (it.Safe) bloat++;
                log("   " + (it.Safe ? "bloat  " : "review ")
                    + (it.Bytes > 0 ? CleanupScanner.Nice(it.Bytes).PadLeft(9) : "?".PadLeft(9))
                    + "  " + it.Name + "  - " + it.Package
                    + (it.Provisioned ? "  [comes back for new accounts]" : "")
                    + (it.Note.Length > 0 ? "  (" + it.Note + ")" : ""));
            }
            log("   = " + found.Count + " removable apps; " + bloat
                + " known bloat. The window ('Debloat') does the actual removing.");
        }

        // Keeps the StartWithWindows logon task in step with the setting: made
        // when it goes on, removed when it goes off. Quiet about a delete that
        // finds nothing - that is not news.
        public static void SyncStartupTask(bool on, Action<string> log)
        {
            string name = "IdleMaster Startup";
            string args = on
                ? "/Create /TN \"" + name + "\" /TR \"\\\"" + Application.ExecutablePath
                  + "\\\" --startup\" /SC ONLOGON /RL HIGHEST /F"
                : "/Delete /TN \"" + name + "\" /F";
            string outp;
            int rc = NetGuard.Exec("schtasks.exe", args, 20000, out outp);
            if (on && rc == 0)
                log("Idle Master now starts as you log in (scheduled task '" + name + "').");
            else if (on)
                log("! could not create the logon task: " + FirstLine(outp));
            else if (rc == 0)
                log("Idle Master no longer starts at logon.");
        }

        private static string FirstLine(string s)
        {
            if (s == null) return "";
            foreach (string line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t;
            }
            return "";
        }

        // Optional: a logon task so the watch - or just the guard - survives a
        // reboot. Nothing calls this on its own - you have to ask for it.
        private static int Task_(bool install, bool guardOnly)
        {
            string name = "IdleMaster Sentry";
            string args = install
                ? "/Create /TN \"" + name + "\" /TR \"\\\"" + Application.ExecutablePath
                  + "\\\" " + (guardOnly ? "--guard" : "--watch") + "\" /SC ONLOGON /RL HIGHEST /F"
                : "/Delete /TN \"" + name + "\" /F";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                Console.Write(p.StandardOutput.ReadToEnd());
                Console.Write(p.StandardError.ReadToEnd());
                p.WaitForExit();
                if (p.ExitCode == 0)
                    Console.WriteLine(install
                        ? (guardOnly ? "The network guard will start at logon. Remove it with --removetask."
                                     : "Sentry (and the network guard) will start at logon. Remove it with --removetask.")
                        : "Logon task removed.");
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine("schtasks failed: " + ex.Message);
                return 1;
            }
        }
    }

    // ---------------------------------------------------------------- updates

    // Asks GitHub what the newest release is and, if you say yes, hands over to the
    // installer for that release. The installer knows how to replace a running exe,
    // so updating is the same operation as installing.
    internal static class Updater
    {
        public const string Repo = "Mild-Solvent/Iddle-Master";
        // Deliberately NOT /releases/latest: that endpoint pretends prereleases do
        // not exist, and every release of this thing so far is a beta. The list
        // endpoint returns newest first and includes them.
        private const string Api = "https://api.github.com/repos/" + Repo + "/releases?per_page=10";
        private const string Asset = "IdleMasterSetup.exe";

        public sealed class Release
        {
            public string Tag = "";
            public string Url = "";
            public bool Newer;
        }

        public static Release Latest()
        {
            // .NET 4 defaults to SSL3/TLS1.0, which GitHub refused years ago.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            catch (Exception) { }

            using (WebClient w = new WebClient())
            {
                w.Headers.Add("User-Agent", "IdleMaster/" + App.Version);
                w.Headers.Add("Accept", "application/vnd.github+json");
                string json = w.DownloadString(Api);

                Release r = new Release();
                MatchCollection tags = Regex.Matches(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (tags.Count == 0) return r;

                r.Tag = tags[0].Groups[1].Value;

                // Assets are listed inside the release they belong to, so the
                // newest release's assets are everything before the next tag_name.
                int from = tags[0].Index;
                int to = tags.Count > 1 ? tags[1].Index : json.Length;
                Match url = Regex.Match(json.Substring(from, to - from),
                    "\"browser_download_url\"\\s*:\\s*\"([^\"]*" + Regex.Escape(Asset) + ")\"",
                    RegexOptions.IgnoreCase);
                if (url.Success) r.Url = url.Groups[1].Value;

                r.Newer = IsNewer(r.Tag, App.Version);
                return r;
            }
        }

        // "v0.3.0-beta" beats "0.2.0". Anything unparseable counts as not newer.
        public static bool IsNewer(string remote, string local)
        {
            int[] a = Parts(remote), b = Parts(local);
            for (int i = 0; i < 4; i++)
            {
                if (a[i] > b[i]) return true;
                if (a[i] < b[i]) return false;
            }
            return false;
        }

        private static int[] Parts(string tag)
        {
            int[] n = new int[4];
            if (string.IsNullOrEmpty(tag)) return n;
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            string[] bits = s.Split('.');
            for (int i = 0; i < bits.Length && i < 4; i++)
                int.TryParse(bits[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]);
            return n;
        }

        // Downloads the installer for a release into TEMP and returns its path.
        public static string Fetch(Release r)
        {
            string to = Path.Combine(Path.GetTempPath(), Asset);
            using (WebClient w = new WebClient())
            {
                w.Headers.Add("User-Agent", "IdleMaster/" + App.Version);
                w.DownloadFile(r.Url, to);
            }
            return to;
        }
    }

}

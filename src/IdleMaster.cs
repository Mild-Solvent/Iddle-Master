// IDLE MASTER - two-mode RAM reclaimer for an always-on Sunshine/Tailscale host.
//
//   BOOST NOW      : kill background bloat, keep a usable desktop.
//   ABSOLUTE IDLE  : strip down to Windows vitals + Sunshine + Tailscale.
//   RESTORE        : undo whatever the last mode did.
//   SENTRY         : after a mode runs, keep hunting so the RAM stays clean.
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
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

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
    }

    // ---------------------------------------------------------------- config

    internal sealed class Config
    {
        public bool KillExplorer = true;
        public bool NetworkGuard = true;
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
        public int TrimWhenFreeBelowMb = 0;         // 0 = only on the timer

        // --- ask first: anything that shows up AFTER the mode ran gets a dialog
        public bool AskBeforeKill = true;
        public int AskTimeoutSeconds = 25;          // no answer = keep it, and ask again later
        public int AskAboveMb = 250;                // also ask about newcomers this big
                                                    // that are on no list at all. 0 = off.
        public bool Tray = true;                    // tray icon; closing the window hides to it

        public readonly List<string> Protect = new List<string>();
        public readonly List<string> ProtectServices = new List<string>();
        public readonly List<string> BoostKill = new List<string>();
        public readonly List<string> BoostServices = new List<string>();
        public readonly List<string> IdleKill = new List<string>();
        public readonly List<string> IdleServices = new List<string>();
        public readonly List<string> RestoreLaunch = new List<string>();

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
                        case "sentryseconds": c.SentrySeconds = Int(v, c.SentrySeconds, 5); break;
                        case "sentryserviceminutes": c.SentryServiceMinutes = Int(v, c.SentryServiceMinutes, 1); break;
                        case "sentrytrimminutes": c.SentryTrimMinutes = Int(v, c.SentryTrimMinutes, 1); break;
                        case "sentryguardminutes": c.SentryGuardMinutes = Int(v, c.SentryGuardMinutes, 1); break;
                        case "sentryrespawnlimit": c.SentryRespawnLimit = Int(v, c.SentryRespawnLimit, 1); break;
                        case "sentrybackoffminutes": c.SentryBackoffMinutes = Int(v, c.SentryBackoffMinutes, 1); break;
                        case "trimwhenfreebelowmb": c.TrimWhenFreeBelowMb = Int(v, c.TrimWhenFreeBelowMb, 0); break;
                        case "askbeforekill": c.AskBeforeKill = b; break;
                        case "tray": c.Tray = b; break;
                        case "asktimeoutseconds": c.AskTimeoutSeconds = Int(v, c.AskTimeoutSeconds, 5); break;
                        case "askabovemb": c.AskAboveMb = Int(v, c.AskAboveMb, 0); break;
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
            TrimWhenFreeBelowMb = o.TrimWhenFreeBelowMb;
            AskBeforeKill = o.AskBeforeKill; AskTimeoutSeconds = o.AskTimeoutSeconds;
            AskAboveMb = o.AskAboveMb; Tray = o.Tray;

            Swap(Protect, o.Protect); Swap(ProtectServices, o.ProtectServices);
            Swap(BoostKill, o.BoostKill); Swap(BoostServices, o.BoostServices);
            Swap(IdleKill, o.IdleKill); Swap(IdleServices, o.IdleServices);
            Swap(RestoreLaunch, o.RestoreLaunch);
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
# After every destructive step, verify Sunshine + Tailscale are alive; restart them if not.
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
# Emergency trim: also trim when free RAM drops under this many MB. 0 = off.
TrimWhenFreeBelowMb=0

# --- ASK FIRST --------------------------------------------------------------
# The sentry takes a census on its first sweep. Everything already running that
# matches a list is junk you asked it to clear, and dies without a word. Anything
# that shows up AFTER that is something YOU started, so it gets a dialog instead:
# Keep it / Always keep / Trash it. ""Always keep"" writes the name into [protect]
# below, so it is remembered forever.
AskBeforeKill=1
# No answer in this many seconds = keep it, and ask again in SentryBackoffMinutes.
AskTimeoutSeconds=25
# Also ask about newcomers that are on NO list at all but bigger than this many MB
# ('Trash it' adds them to [boost.kill]). 0 = only ask about listed processes.
AskAboveMb=250
# Tray icon. Closing the window hides to the tray and keeps hunting; exit from
# the tray menu when you actually want it gone.
Tray=1

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
# the streaming stack itself
sunshine
sunshinesvc
tailscaled
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
# tray icons nobody can see
tailscale-ipn
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

        public sealed class Entry
        {
            public readonly string Text;
            public bool Enabled;
            public Entry(string text, bool enabled) { Text = text; Enabled = enabled; }
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
                found.Add(new Entry(bare, !Disabled(lines[i])));
            }
            return found;
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

    internal enum Verdict { Keep, KeepAlways, Kill, NoAnswer }

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

        public bool IsProtectedProcess(string name)
        {
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
        // 'skip' holds names in respawn backoff; 'sparePid' is the foreground app.
        public List<Candidate> Census(ICollection<string> skip, int sparePid)
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

        // Kills everything a Candidate covers. Returns only what actually died -
        // a process can exit or refuse between the census and here.
        public List<KillHit> Reap(Candidate c)
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

        public void KillList(IEnumerable<string> patterns, string label)
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

        private bool EnsureService(string name, bool loud)
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

            KillList(cfg.BoostKill, "killing background clutter");
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

            Dictionary<string, long> byName = new Dictionary<string, long>();
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    if (!byName.ContainsKey(n)) { byName[n] = 0; counts[n] = 0; }
                    byName[n] += p.WorkingSet64;
                    counts[n]++;
                }
                catch (Exception) { }
            }
            List<KeyValuePair<string, long>> sorted = new List<KeyValuePair<string, long>>(byName);
            sorted.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            { return b.Value.CompareTo(a.Value); });

            int shown = 0;
            foreach (KeyValuePair<string, long> kv in sorted)
            {
                if (shown++ >= 25) break;
                string tag = "        ";
                if (IsProtectedProcess(kv.Key)) tag = " [KEEP] ";
                else if (Matches(cfg.BoostKill, kv.Key)) tag = " [BOOST]";
                else if (Matches(cfg.IdleKill, kv.Key)) tag = " [IDLE] ";
                else if (cfg.KillExplorer && string.Equals(kv.Key, "explorer", StringComparison.OrdinalIgnoreCase))
                    tag = " [IDLE] ";
                log(string.Format(CultureInfo.InvariantCulture, "  {0,8} {1,-32} x{2,-3} {3}",
                    tag, kv.Key, counts[kv.Key], Mb(kv.Value)));
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

        private static bool Matches(List<string> pats, string name)
        {
            foreach (string p in pats) if (Match(p, name)) return true;
            return false;
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

    // ------------------------------------------------------------------ dialog

    // The toast that appears when something you started lands on a kill list.
    // Bottom-right, always on top, counts down, and defaults to leaving it alone -
    // the tool should never be the reason you lost work you were in the middle of.
    internal sealed class AskForm : Form
    {
        private static readonly List<AskForm> Open = new List<AskForm>();

        private readonly System.Windows.Forms.Timer countdown;
        private readonly Label ticker;
        private int left;

        public Verdict Choice = Verdict.NoAnswer;

        public AskForm(Question q, int seconds)
        {
            left = seconds;

            Text = "Idle Master";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 30);
            ForeColor = Color.FromArgb(225, 225, 232);
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(430, 176);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

            Label head = new Label();
            head.Text = q.What.Name;
            head.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(120, 200, 255);
            head.SetBounds(16, 14, 400, 26);
            Controls.Add(head);

            string size = Engine.Size(q.What.Bytes);
            string many = q.What.Pids.Count > 1 ? q.What.Pids.Count + " processes, " : "";
            Label body = new Label();
            body.Text = q.OnKillList
                ? "just started - " + many + size + ".\n\nIt is on your "
                  + q.Mode.ToUpperInvariant() + " kill list, so the sentry is about to close it."
                : "just started - " + many + size + ".\n\nIt is on no list, but it is big enough "
                  + "to be worth asking about.";
            body.SetBounds(16, 44, 400, 56);
            Controls.Add(body);

            ticker = new Label();
            ticker.SetBounds(16, 104, 400, 18);
            ticker.ForeColor = Color.FromArgb(120, 120, 132);
            Controls.Add(ticker);
            Tick();

            Button keep = Btn("Keep it", 16, Color.FromArgb(42, 42, 52));
            keep.Click += delegate { Answer(Verdict.Keep); };

            Button always = Btn("Always keep", 122, Color.FromArgb(28, 92, 58));
            always.Click += delegate { Answer(Verdict.KeepAlways); };

            Button kill = Btn("Trash it", 300, Color.FromArgb(110, 40, 40));
            kill.Click += delegate { Answer(Verdict.Kill); };

            AcceptButton = keep;
            CancelButton = keep;

            countdown = new System.Windows.Forms.Timer();
            countdown.Interval = 1000;
            countdown.Tick += delegate
            {
                left--;
                if (left <= 0) Answer(Verdict.NoAnswer);
                else Tick();
            };
            countdown.Start();

            lock (Open) Open.Add(this);
        }

        private void Tick()
        {
            ticker.Text = "no answer in " + left + "s = left alone, and asked again later";
        }

        private Button Btn(string text, int x, Color c)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, 132, 100, 30);
            b.BackColor = c;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        private void Answer(Verdict v)
        {
            Choice = v;
            countdown.Stop();
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            lock (Open) Open.Remove(this);
            base.OnFormClosed(e);
        }

        // Called when the sentry is told to stand down while a dialog is still up.
        public static void CloseAll()
        {
            List<AskForm> copy;
            lock (Open) copy = new List<AskForm>(Open);
            foreach (AskForm f in copy)
            {
                try { if (!f.IsDisposed) f.Answer(Verdict.NoAnswer); }
                catch (Exception) { }
            }
        }

        public static bool AnyOpen { get { lock (Open) return Open.Count > 0; } }
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
        public DateTime Since;
        public DateTime LastHit;
        public bool Alive { get { return thread != null && thread.IsAlive; } }
        public string Mode { get { return mode; } }

        // name -> when its backoff expires; name -> how many times we have killed it
        private readonly Dictionary<string, DateTime> cooling = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> tally = new Dictionary<string, int>();

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
                    log("[sentry] another sentry already has the watch - not starting a second one");
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
            Reaped = 0; Reclaimed = 0; Restopped = 0;
            Since = DateTime.Now;
            cooling.Clear();
            tally.Clear();

            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();

            log("[sentry] on watch, enforcing " + mode.ToUpperInvariant()
                + " every " + cfg.SentrySeconds + "s. Restore turns it off.");
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

            while (!stopping)
            {
                try
                {
                    // Rebuilt every sweep, so edits made in the config window take
                    // effect without restarting the watch.
                    SweepProcesses(engine.PatternsFor(mode));

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

            int spare = 0;
            if (mode == "boost" && cfg.SentrySkipForeground) spare = Native.ForegroundPid();

            List<Candidate> all = engine.Census(skip, spare);
            List<KillHit> hits = new List<KillHit>();

            foreach (Candidate c in all)
            {
                bool listed = engine.OnList(patterns, c.Name);

                if (firstSweep)
                {
                    // Opening census: everything present now is what the mode was for.
                    census.Add(c.Key);
                    if (listed) hits.AddRange(engine.Reap(c));
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
                if (listed && !newcomer) { hits.AddRange(engine.Reap(c)); continue; }

                switch (Consult(c, listed))
                {
                    case Verdict.Kill:
                        hits.AddRange(engine.Reap(c));
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

        private static string LogPath { get { return Path.Combine(Dir, "idlemaster.log"); } }

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
            bool watch = false;
            foreach (string a in argv)
            {
                string s = a.TrimStart('-', '/').ToLowerInvariant();
                if (s == "boost" || s == "idle" || s == "restore" || s == "report" || s == "help"
                    || s == "unwatch" || s == "stopwatch" || s == "installtask" || s == "removetask")
                    mode = s;
                else if (s == "watch" || s == "hunt")
                    watch = true;
            }
            if (watch && mode == "") mode = "watch";

            Config cfg;
            try { cfg = Config.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("Bad idlemaster.ini:\n\n" + ex.Message, "Idle Master");
                return 2;
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
                case "watch": break;          // no mode run, just take up the watch
                case "unwatch":
                case "stopwatch":
                    Console.WriteLine(Sentry.SignalStop()
                        ? "sentry told to stand down."
                        : "no sentry is running.");
                    return 0;
                case "installtask": return Task_(true);
                case "removetask": return Task_(false);
                default:
                    Console.WriteLine("IdleMaster.exe [--boost | --idle | --restore | --report]");
                    Console.WriteLine("  --watch        keep hunting after the mode, until --unwatch");
                    Console.WriteLine("  --unwatch      stop the sentry");
                    Console.WriteLine("  --installtask  run the sentry at every logon (scheduled task)");
                    Console.WriteLine("  --removetask   undo that");
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
                    Console.WriteLine("a sentry is already on watch - nothing to do.");
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

        // Optional: a logon task so the watch survives a reboot. Nothing calls this
        // on its own - you have to ask for it.
        private static int Task_(bool install)
        {
            string name = "IdleMaster Sentry";
            string args = install
                ? "/Create /TN \"" + name + "\" /TR \"\\\"" + Application.ExecutablePath
                  + "\\\" --watch\" /SC ONLOGON /RL HIGHEST /F"
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
                        ? "Sentry will start at logon. Remove it with --removetask."
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

    // ------------------------------------------------------------ config window

    // Pick names off the machine instead of typing them: running processes by how
    // much RAM they are actually costing, or installed services by display name.
    internal sealed class PickForm : Form
    {
        private readonly CheckedListBox box = new CheckedListBox();
        private readonly List<string> values = new List<string>();
        public readonly List<string> Picked = new List<string>();

        public PickForm(string title, bool services)
        {
            Text = title;
            Size = new Size(560, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 24, 30);
            ForeColor = Color.FromArgb(225, 225, 232);
            Font = new Font("Segoe UI", 9f);

            box.SetBounds(12, 12, 520, 420);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            box.BackColor = Color.FromArgb(14, 14, 18);
            box.ForeColor = Color.FromArgb(210, 225, 215);
            box.CheckOnClick = true;
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(box);

            if (services) FillServices(); else FillProcesses();

            Button ok = new Button();
            ok.Text = "Add selected";
            ok.SetBounds(316, 444, 105, 30);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.BackColor = Color.FromArgb(28, 92, 58);
            ok.ForeColor = Color.White;
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += delegate
            {
                foreach (int i in box.CheckedIndices) Picked.Add(values[i]);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(427, 444, 105, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.BackColor = Color.FromArgb(42, 42, 52);
            cancel.ForeColor = Color.FromArgb(225, 225, 232);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderSize = 0;
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private void FillProcesses()
        {
            Dictionary<string, long> ram = new Dictionary<string, long>();
            Dictionary<string, int> count = new Dictionary<string, int>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    if (!ram.ContainsKey(n)) { ram[n] = 0; count[n] = 0; }
                    ram[n] += p.WorkingSet64;
                    count[n]++;
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            List<KeyValuePair<string, long>> sorted = new List<KeyValuePair<string, long>>(ram);
            sorted.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            { return b.Value.CompareTo(a.Value); });

            foreach (KeyValuePair<string, long> kv in sorted)
            {
                values.Add(kv.Key);
                box.Items.Add(string.Format(CultureInfo.InvariantCulture, "{0,-34} {1,8}  {2}",
                    kv.Key, Engine.Size(kv.Value), count[kv.Key] > 1 ? "x" + count[kv.Key] : ""));
            }
        }

        private void FillServices()
        {
            List<ServiceController> all = new List<ServiceController>(ServiceController.GetServices());
            all.Sort(delegate(ServiceController a, ServiceController b)
            {
                int byState = (b.Status == ServiceControllerStatus.Running ? 1 : 0)
                            - (a.Status == ServiceControllerStatus.Running ? 1 : 0);
                if (byState != 0) return byState;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            foreach (ServiceController sc in all)
            {
                try
                {
                    values.Add(sc.ServiceName);
                    box.Items.Add((sc.Status == ServiceControllerStatus.Running ? "* " : "  ")
                        + sc.ServiceName + "   -   " + sc.DisplayName);
                }
                catch (Exception) { }
            }
        }
    }

    // One editable list: everything in a section, commented-out entries included
    // as unchecked rows so you can see what the config ships and turn it on.
    internal sealed class ListPane : Panel
    {
        private readonly string section;
        private readonly bool services;
        private readonly CheckedListBox box = new CheckedListBox();
        private readonly List<IniFile.Entry> before;

        public ListPane(IniFile ini, string sectionName, string caption, bool isServices)
        {
            section = sectionName;
            services = isServices;
            before = ini.Section(section);

            BackColor = Color.FromArgb(24, 24, 30);
            ForeColor = Color.FromArgb(225, 225, 232);

            Label head = new Label();
            head.Text = caption;
            head.ForeColor = Color.FromArgb(120, 200, 255);
            head.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            head.SetBounds(6, 6, 300, 18);
            Controls.Add(head);

            box.SetBounds(6, 28, 320, 300);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            box.BackColor = Color.FromArgb(14, 14, 18);
            box.ForeColor = Color.FromArgb(210, 225, 215);
            box.Font = new Font("Consolas", 9f);
            box.CheckOnClick = true;
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(box);

            foreach (IniFile.Entry e in before) box.Items.Add(e.Text, e.Enabled);

            Button add = Btn("Add from machine", 6, 334, 130);
            add.Click += delegate { Pick(); };

            Button typed = Btn("Type one", 142, 334, 88);
            typed.Click += delegate { Typed(); };

            Button del = Btn("Remove", 236, 334, 90);
            del.Click += delegate
            {
                for (int i = box.Items.Count - 1; i >= 0; i--)
                    if (box.SelectedIndices.Contains(i)) box.Items.RemoveAt(i);
            };
        }

        private Button Btn(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            b.BackColor = Color.FromArgb(42, 42, 52);
            b.ForeColor = Color.FromArgb(225, 225, 232);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        private void Pick()
        {
            using (PickForm f = new PickForm(services ? "Running services" : "Running processes", services))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (string v in f.Picked)
                {
                    if (Has(v)) continue;
                    box.Items.Add(v, true);
                }
            }
        }

        private void Typed()
        {
            using (Form f = new Form())
            {
                f.Text = "Add entry";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(340, 96);
                f.BackColor = Color.FromArgb(24, 24, 30);
                f.ForeColor = Color.FromArgb(225, 225, 232);
                f.MinimizeBox = f.MaximizeBox = false;

                Label l = new Label();
                l.Text = services ? "Service name:" : "Process name ('*' allowed):";
                l.SetBounds(12, 10, 300, 18);
                f.Controls.Add(l);

                TextBox t = new TextBox();
                t.SetBounds(12, 32, 316, 22);
                t.BackColor = Color.FromArgb(14, 14, 18);
                t.ForeColor = Color.FromArgb(225, 225, 232);
                t.BorderStyle = BorderStyle.FixedSingle;
                f.Controls.Add(t);

                Button ok = new Button();
                ok.Text = "Add";
                ok.SetBounds(228, 60, 100, 28);
                ok.BackColor = Color.FromArgb(28, 92, 58);
                ok.ForeColor = Color.White;
                ok.FlatStyle = FlatStyle.Flat;
                ok.FlatAppearance.BorderSize = 0;
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                if (f.ShowDialog(this) != DialogResult.OK) return;
                string v = t.Text.Trim();
                if (v.Length == 0 || Has(v)) return;
                box.Items.Add(v, true);
            }
        }

        private bool Has(string v)
        {
            foreach (object o in box.Items)
                if (string.Equals(o.ToString(), v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Save(IniFile ini)
        {
            List<string> now = new List<string>();
            for (int i = 0; i < box.Items.Count; i++) now.Add(box.Items[i].ToString());

            foreach (IniFile.Entry e in before)
            {
                int at = -1;
                for (int i = 0; i < now.Count; i++)
                    if (now[i].Equals(e.Text, StringComparison.OrdinalIgnoreCase)) { at = i; break; }

                if (at < 0) { ini.Remove(section, e.Text); continue; }
                bool on = box.GetItemChecked(at);
                if (on != e.Enabled) ini.SetEnabled(section, e.Text, on);
            }

            for (int i = 0; i < now.Count; i++)
            {
                bool old = false;
                foreach (IniFile.Entry e in before)
                    if (now[i].Equals(e.Text, StringComparison.OrdinalIgnoreCase)) { old = true; break; }
                if (old) continue;
                ini.Add(section, now[i]);
                if (!box.GetItemChecked(i)) ini.SetEnabled(section, now[i], false);
            }
        }
    }

    // The whole config, with no text editor in sight.
    internal sealed class ConfigForm : Form
    {
        // key, label
        private static readonly string[][] Flags = new string[][]
        {
            new string[] { "Sentry",               "Keep hunting after a mode has run" },
            new string[] { "AskBeforeKill",        "Ask before killing anything that started after the boost" },
            new string[] { "SentrySkipForeground", "Never kill the window you are using (boost only)" },
            new string[] { "Tray",                 "Tray icon - closing the window hides to it" },
            new string[] { "KillExplorer",         "Absolute idle also closes the shell (taskbar, desktop)" },
            new string[] { "NetworkGuard",         "Check Sunshine + Tailscale, restart them if they die" },
            new string[] { "TrimWorkingSets",      "Squeeze the working set of every surviving process" },
            new string[] { "ClearStandbyList",     "Purge the standby (cached) list" },
            new string[] { "CloseBrowsersInBoost", "Boost closes browsers too" },
        };

        // key, label, min, max, default
        private static readonly string[][] Numbers = new string[][]
        {
            new string[] { "SentrySeconds",        "Sweep for new junk every (seconds)",            "5", "3600", "20" },
            new string[] { "SentryServiceMinutes", "Re-stop restarted services every (minutes)",    "1", "1440", "5" },
            new string[] { "SentryTrimMinutes",    "Re-trim RAM every (minutes)",                   "1", "1440", "10" },
            new string[] { "SentryGuardMinutes",   "Check the stream stack every (minutes)",        "1", "1440", "5" },
            new string[] { "SentryRespawnLimit",   "Give up on a process after this many respawns", "1", "100",  "6" },
            new string[] { "SentryBackoffMinutes", "...and leave it alone for (minutes)",           "1", "1440", "30" },
            new string[] { "AskTimeoutSeconds",    "Dialog answers itself after (seconds)",         "5", "600",  "25" },
            new string[] { "AskAboveMb",           "Ask about unlisted newcomers bigger than (MB, 0 = off)", "0", "99999", "250" },
            new string[] { "TrimWhenFreeBelowMb",  "Emergency trim when free RAM drops below (MB, 0 = off)", "0", "99999", "0" },
        };

        private readonly IniFile ini = new IniFile();
        private readonly Dictionary<string, CheckBox> flags = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, NumericUpDown> numbers = new Dictionary<string, NumericUpDown>();
        private readonly List<ListPane> panes = new List<ListPane>();

        public bool Saved;

        public ConfigForm()
        {
            Text = "Idle Master - configuration";
            Size = new Size(780, 660);
            MinimumSize = new Size(680, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(18, 18, 22);
            ForeColor = Color.FromArgb(225, 225, 232);
            Font = new Font("Segoe UI", 9f);

            TabControl tabs = new TabControl();
            tabs.SetBounds(10, 10, 754, 560);
            tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(tabs);

            tabs.TabPages.Add(SettingsTab());
            tabs.TabPages.Add(Pair("Never touch", "protect", "Processes that survive everything",
                                                  "protect.services", "Services that survive everything"));
            tabs.TabPages.Add(Pair("Boost now", "boost.kill", "Processes closed by Boost",
                                                "boost.services", "Services stopped by Boost"));
            tabs.TabPages.Add(Pair("Absolute idle", "idle.kill", "Also closed by Absolute Idle",
                                                    "idle.services", "Also stopped by Absolute Idle"));
            tabs.TabPages.Add(Single("Restore", "restore.launch",
                "Relaunched by Restore desktop  (full path, optional |arguments)"));

            Label hint = new Label();
            hint.Text = "Unchecked entries stay in the file, commented out. Nothing here can override "
                      + "'Never touch'.";
            hint.ForeColor = Color.FromArgb(120, 120, 132);
            hint.SetBounds(14, 580, 520, 32);
            hint.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(hint);

            Button save = new Button();
            save.Text = "Save";
            save.SetBounds(556, 580, 100, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.BackColor = Color.FromArgb(28, 92, 58);
            save.ForeColor = Color.White;
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderSize = 0;
            save.Click += delegate { Persist(); };
            Controls.Add(save);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(664, 580, 100, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.BackColor = Color.FromArgb(42, 42, 52);
            cancel.ForeColor = Color.FromArgb(225, 225, 232);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderSize = 0;
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private TabPage SettingsTab()
        {
            TabPage page = new TabPage("Settings");
            page.BackColor = Color.FromArgb(24, 24, 30);
            page.ForeColor = Color.FromArgb(225, 225, 232);
            page.AutoScroll = true;

            int y = 12;
            foreach (string[] f in Flags)
            {
                CheckBox c = new CheckBox();
                c.Text = f[1];
                c.Checked = Truthy(ini.GetSetting(f[0]), true);
                c.SetBounds(16, y, 700, 24);
                c.ForeColor = Color.FromArgb(225, 225, 232);
                page.Controls.Add(c);
                flags[f[0]] = c;
                y += 26;
            }

            y += 10;
            foreach (string[] n in Numbers)
            {
                Label l = new Label();
                l.Text = n[1];
                l.SetBounds(16, y + 4, 480, 20);
                page.Controls.Add(l);

                NumericUpDown u = new NumericUpDown();
                u.Minimum = decimal.Parse(n[2], CultureInfo.InvariantCulture);
                u.Maximum = decimal.Parse(n[3], CultureInfo.InvariantCulture);
                u.Value = Clamp(u, ini.GetSetting(n[0]), n[4]);
                u.SetBounds(504, y, 90, 22);
                u.BackColor = Color.FromArgb(14, 14, 18);
                u.ForeColor = Color.FromArgb(225, 225, 232);
                u.BorderStyle = BorderStyle.FixedSingle;
                page.Controls.Add(u);
                numbers[n[0]] = u;
                y += 28;
            }
            return page;
        }

        private static decimal Clamp(NumericUpDown u, string raw, string fallback)
        {
            decimal d;
            if (raw == null || !decimal.TryParse(raw.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out d))
                d = decimal.Parse(fallback, CultureInfo.InvariantCulture);
            if (d < u.Minimum) d = u.Minimum;
            if (d > u.Maximum) d = u.Maximum;
            return d;
        }

        private static bool Truthy(string v, bool fallback)
        {
            if (v == null) return fallback;
            v = v.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private TabPage Pair(string title, string leftSection, string leftCaption,
                             string rightSection, string rightCaption)
        {
            TabPage page = new TabPage(title);
            page.BackColor = Color.FromArgb(24, 24, 30);

            ListPane left = new ListPane(ini, leftSection, leftCaption, false);
            left.SetBounds(4, 4, 366, 490);
            left.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            page.Controls.Add(left);

            ListPane right = new ListPane(ini, rightSection, rightCaption, true);
            right.SetBounds(376, 4, 366, 490);
            right.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(right);

            panes.Add(left);
            panes.Add(right);
            return page;
        }

        private TabPage Single(string title, string section, string caption)
        {
            TabPage page = new TabPage(title);
            page.BackColor = Color.FromArgb(24, 24, 30);

            ListPane pane = new ListPane(ini, section, caption, false);
            pane.SetBounds(4, 4, 738, 490);
            pane.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(pane);

            panes.Add(pane);
            return page;
        }

        private void Persist()
        {
            try
            {
                foreach (KeyValuePair<string, CheckBox> kv in flags)
                    ini.SetSetting(kv.Key, kv.Value.Checked ? "1" : "0");
                foreach (KeyValuePair<string, NumericUpDown> kv in numbers)
                    ini.SetSetting(kv.Key, ((int)kv.Value.Value).ToString(CultureInfo.InvariantCulture));
                foreach (ListPane p in panes) p.Save(ini);
                ini.Save();
                Saved = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the config:\n\n" + ex.Message,
                    "Idle Master", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ------------------------------------------------------------------- gui

    internal sealed class MainForm : Form
    {
        private readonly Config cfg;
        private readonly Engine engine;
        private readonly TextBox logBox;
        private readonly Label memLabel;
        private readonly Panel memBar;
        private readonly Panel memFill;
        private readonly Button btnBoost, btnIdle, btnRestore, btnReport, btnTrim, btnConfig, btnUpdate;
        private readonly CheckBox chkSentry;
        private readonly Label sentryLabel;
        private readonly Label updateLabel;
        private readonly System.Windows.Forms.Timer timer;
        private Sentry sentry;
        private NotifyIcon tray;
        private bool reallyExit;
        private bool startHidden;
        private bool watchMode;

        private static readonly Color Bg = Color.FromArgb(18, 18, 22);
        private static readonly Color Fg = Color.FromArgb(225, 225, 232);
        private static readonly Color Dim = Color.FromArgb(120, 120, 132);

        public MainForm(Config c)
        {
            cfg = c;
            engine = new Engine(cfg, AppendLog);

            Text = "IDLE MASTER";
            Size = new Size(700, 706);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9f);

            Label title = new Label();
            title.Text = "IDLE MASTER";
            title.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(120, 200, 255);
            title.SetBounds(20, 14, 400, 36);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Sunshine + Tailscale stay up. Everything else is negotiable.   v" + App.Version;
            sub.ForeColor = Dim;
            sub.SetBounds(22, 50, 500, 20);
            Controls.Add(sub);

            memLabel = new Label();
            memLabel.SetBounds(22, 78, 640, 20);
            memLabel.ForeColor = Fg;
            Controls.Add(memLabel);

            memBar = new Panel();
            memBar.SetBounds(22, 100, 640, 14);
            memBar.BackColor = Color.FromArgb(38, 38, 46);
            memBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(memBar);

            memFill = new Panel();
            memFill.SetBounds(0, 0, 0, 14);
            memFill.BackColor = Color.FromArgb(90, 190, 120);
            memBar.Controls.Add(memFill);

            btnBoost = BigButton("BOOST NOW",
                "Kill the background junk. Desktop stays usable.",
                Color.FromArgb(28, 92, 58), 22, 130);
            btnBoost.Click += delegate { Run("boost"); };

            btnIdle = BigButton("ABSOLUTE IDLE",
                "Strip to Windows vitals + Sunshine + Tailscale. For sleep.",
                Color.FromArgb(110, 40, 40), 22, 218);
            btnIdle.Click += delegate { ConfirmIdle(); };

            btnRestore = SmallButton("Restore desktop", 22, 306);
            btnRestore.Click += delegate { Run("restore"); };
            btnReport = SmallButton("What's eating RAM?", 182, 306);
            btnReport.Click += delegate { Run("report"); };
            btnTrim = SmallButton("Trim RAM now", 342, 306);
            btnTrim.Click += delegate { Run("trim"); };
            btnConfig = SmallButton("Settings", 502, 306);
            btnConfig.Click += delegate { EditConfig(); };

            btnUpdate = SmallButton("Check for updates", 22, 342);
            btnUpdate.Click += delegate { CheckUpdates(); };

            updateLabel = new Label();
            updateLabel.SetBounds(182, 348, 480, 20);
            updateLabel.ForeColor = Dim;
            updateLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            updateLabel.Text = "running v" + App.Version + " - " + Updater.Repo;
            Controls.Add(updateLabel);

            chkSentry = new CheckBox();
            chkSentry.Text = "Keep hunting after boost";
            chkSentry.Checked = cfg.Sentry;
            chkSentry.SetBounds(24, 382, 190, 22);
            chkSentry.ForeColor = Fg;
            chkSentry.FlatStyle = FlatStyle.Flat;
            chkSentry.Click += delegate { ToggleSentry(); };
            Controls.Add(chkSentry);

            sentryLabel = new Label();
            sentryLabel.SetBounds(220, 384, 442, 20);
            sentryLabel.ForeColor = Dim;
            sentryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(sentryLabel);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.FromArgb(12, 12, 15);
            logBox.ForeColor = Color.FromArgb(180, 220, 190);
            logBox.Font = new Font("Consolas", 9f);
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.SetBounds(22, 412, 640, 212);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(logBox);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { UpdateMemory(); UpdateSentry(); };
            timer.Start();
            UpdateMemory();
            UpdateSentry();

            AppendLog("Ready. Config: " + Config.Path_);
            StateFile st = StateFile.Load();
            if (st.Mode.Length > 0)
                AppendLog("Note: last run was '" + st.Mode + "' and has not been restored yet ("
                    + st.StoppedServices.Count + " services still stopped).");

            if (cfg.Tray) BuildTray();

            // A mode was run earlier and never restored - pick the watch back up.
            if (st.Mode.Length > 0 && st.SentryArmed && cfg.Sentry && !Sentry.IsRunningSomewhere())
                StartSentry(st.Mode);

            FormClosing += OnClosing;
        }

        // Started by --watch: no window, just the tray icon and the sentry.
        public void HideOnStart(string enforce)
        {
            startHidden = true;
            watchMode = true;
            WindowState = FormWindowState.Minimized;
            // Touching Handle builds the window now, on this thread, so the sentry
            // has something to marshal its dialogs onto before anything is shown.
            IntPtr forced = Handle;
            GC.KeepAlive(forced);
            if (enforce.Length > 0) StartSentry(enforce);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Shield;
            tray.Text = "Idle Master";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open Idle Master", null, delegate { ShowWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Boost now", null, delegate { ShowWindow(); Run("boost"); });
            menu.Items.Add("Absolute idle", null, delegate { ShowWindow(); ConfirmIdle(); });
            menu.Items.Add("Restore desktop", null, delegate { ShowWindow(); Run("restore"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Stop hunting", null, delegate
            {
                chkSentry.Checked = false;
                StopSentry();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Check for updates", null, delegate { ShowWindow(); CheckUpdates(); });
            menu.Items.Add("Exit", null, delegate { reallyExit = true; Close(); });
            tray.ContextMenuStrip = menu;
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // Closing the window while the sentry is up would let RAM drift back, so
        // it goes to the tray instead. Exit from the tray menu when you mean it.
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            bool hunting = sentry != null && sentry.Alive;
            if (!reallyExit && cfg.Tray && hunting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    tray.ShowBalloonTip(4000, "Still hunting",
                        "Idle Master is holding " + sentry.Mode.ToUpperInvariant()
                        + " in the tray. Right-click the icon to stop it.", ToolTipIcon.Info);
                }
                catch (Exception) { }
                return;
            }
            StopSentry(false);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        }

        private void StartSentry(string mode)
        {
            if (sentry != null && sentry.Alive) return;
            sentry = new Sentry(cfg, engine, AppendLog, mode);
            sentry.Ask = AskOnUiThread;
            if (!sentry.Start()) sentry = null;
            UpdateSentry();
        }

        // Called from the sentry's thread; blocks it until you answer or it times
        // out. That is fine - the next sweep is 20 seconds away anyway.
        private Verdict AskOnUiThread(Question q)
        {
            if (InvokeRequired)
            {
                try { return (Verdict)Invoke((Func<Question, Verdict>)AskOnUiThread, q); }
                catch (Exception) { return Verdict.Keep; }
            }
            using (AskForm f = new AskForm(q, cfg.AskTimeoutSeconds))
            {
                f.ShowDialog();
                return f.Choice;
            }
        }

        // disarm=false is "the window is closing" - the watch should resume next launch.
        private void StopSentry() { StopSentry(true); }

        private void StopSentry(bool disarm)
        {
            if (sentry != null)
            {
                sentry.Stop();
                sentry = null;
            }
            if (disarm)
            {
                StateFile st = StateFile.Load();
                if (st.Mode.Length > 0 && st.SentryArmed) { st.SentryArmed = false; st.Save(); }
            }
            UpdateSentry();
        }

        private void ToggleSentry()
        {
            if (chkSentry.Checked)
            {
                StateFile st = StateFile.Load();
                if (st.Mode.Length == 0)
                {
                    AppendLog("Sentry armed. It starts hunting as soon as you run a mode.");
                    UpdateSentry();
                    return;
                }
                StartSentry(st.Mode);
            }
            else StopSentry();
        }

        private void UpdateSentry()
        {
            bool on = sentry != null && sentry.Alive;
            if (!on && sentry != null)
            {
                sentry = null;
                // --unwatch (or Restore from elsewhere) killed the watch, and a tray
                // app with nothing to do is just a stray icon.
                if (watchMode) { reallyExit = true; Close(); return; }
            }

            if (on)
            {
                string txt = "hunting " + sentry.Mode.ToUpperInvariant() + " - "
                    + sentry.Reaped + " reaped, " + Engine.Size(sentry.Reclaimed) + " held off";
                if (sentry.Restopped > 0) txt += ", " + sentry.Restopped + " services re-stopped";
                sentryLabel.Text = txt;
                sentryLabel.ForeColor = Color.FromArgb(120, 200, 255);
            }
            else if (chkSentry.Checked)
            {
                sentryLabel.Text = "armed - starts with the next boost or idle";
                sentryLabel.ForeColor = Dim;
            }
            else
            {
                sentryLabel.Text = "off - RAM will drift back up on its own";
                sentryLabel.ForeColor = Dim;
            }
        }

        // Asks GitHub, then hands the decision to you. Nothing downloads until you
        // say so, and the installer that arrives is the one you publish.
        private void CheckUpdates()
        {
            btnUpdate.Enabled = false;
            Status("asking GitHub...", Dim);
            AppendLog("Checking " + Updater.Repo + " for releases newer than " + App.Version + "...");

            Thread t = new Thread(delegate()
            {
                Updater.Release r = null;
                string failure = null;
                try { r = Updater.Latest(); }
                catch (Exception ex) { failure = ex.Message.Split('\n')[0]; }

                try { BeginInvoke((Action)delegate { Finish(r, failure); }); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Finish(Updater.Release r, string failure)
        {
            btnUpdate.Enabled = true;

            if (failure != null)
            {
                Status("update check failed", Color.FromArgb(220, 140, 80));
                AppendLog("! update check failed: " + failure);
                return;
            }
            if (r.Tag.Length == 0)
            {
                Status("no releases published yet", Dim);
                return;
            }
            if (!r.Newer)
            {
                Status("v" + App.Version + " is the newest (" + r.Tag + " published)", Dim);
                AppendLog("Already on the newest release.");
                return;
            }
            if (r.Url.Length == 0)
            {
                Status(r.Tag + " is out, but has no installer attached", Color.FromArgb(220, 140, 80));
                AppendLog("! " + r.Tag + " has no IdleMasterSetup.exe asset - update it by hand.");
                return;
            }

            Status(r.Tag + " is available", Color.FromArgb(120, 200, 255));
            if (MessageBox.Show(this,
                r.Tag + " is out - you are on " + App.Version + "."
                + "\n\nDownload it and update this copy in " + App.Dir + "?"
                + "\n\nYour idlemaster.ini is kept exactly as it is. Idle Master will close "
                + "while the installer replaces it.",
                "Idle Master", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
            {
                AppendLog("Update available (" + r.Tag + ") - not installed, your call.");
                return;
            }

            try
            {
                AppendLog("Downloading " + r.Tag + "...");
                Status("downloading " + r.Tag + "...", Color.FromArgb(120, 200, 255));
                string setup = Updater.Fetch(r);
                AppendLog("Handing over to " + setup);

                // Point the installer at THIS copy, so updating a portable exe
                // updates it where it stands instead of installing a second one.
                Process.Start(new ProcessStartInfo(setup, "--dir \"" + App.Dir + "\"")
                { UseShellExecute = true });

                reallyExit = true;
                Close();
            }
            catch (Exception ex)
            {
                Status("update failed", Color.FromArgb(220, 140, 80));
                AppendLog("! update failed: " + ex.Message.Split('\n')[0]);
            }
        }

        private void Status(string text, Color color)
        {
            updateLabel.Text = "v" + App.Version + "  -  " + text;
            updateLabel.ForeColor = color;
        }

        // The lists and switches, edited in place. The running sentry picks up the
        // new config immediately - no restart, no text editor.
        private void EditConfig()
        {
            using (ConfigForm f = new ConfigForm())
            {
                f.ShowDialog(this);
                if (!f.Saved) return;
            }
            try
            {
                cfg.CopyFrom(Config.Load());
                chkSentry.Checked = cfg.Sentry;
                AppendLog("Config saved. " + cfg.Protect.Count + " protected names, "
                    + cfg.BoostKill.Count + " on the boost list, "
                    + cfg.IdleKill.Count + " more on the idle list.");
                if (sentry != null && sentry.Alive)
                    AppendLog("The sentry is using the new lists from its next sweep.");
            }
            catch (Exception ex) { AppendLog("! could not reload the config: " + ex.Message); }
        }

        private Button BigButton(string text, string sub, Color color, int x, int y)
        {
            Button b = new Button();
            b.Text = text + "\n" + sub;
            b.SetBounds(x, y, 640, 76);
            b.BackColor = color;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(b);
            return b;
        }

        private Button SmallButton(string text, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, 152, 30);
            b.BackColor = Color.FromArgb(42, 42, 52);
            b.ForeColor = Fg;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        private void ConfirmIdle()
        {
            string msg = "This closes your browsers, Claude, and everything else you have open."
                + (cfg.KillExplorer ? "\n\nIt also closes the Windows shell (taskbar and desktop disappear)." : "")
                + "\n\nSunshine and Tailscale stay up, so you can still reach this machine."
                + "\n\nTo get the desktop back: press Ctrl+Shift+Esc, then File > Run new task,"
                + " and run this exe again -> Restore desktop."
                + "\n\nGo idle now?";
            if (MessageBox.Show(this, msg, "Absolute Idle",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Run("idle");
        }

        private void Run(string what)
        {
            SetBusy(true);
            // The sentry has to stand down before restore starts putting things back,
            // otherwise it shoots them again on the next sweep.
            if (what == "restore") StopSentry();

            Thread t = new Thread(delegate()
            {
                try
                {
                    if (what == "boost") engine.RunBoost();
                    else if (what == "idle") engine.RunIdle();
                    else if (what == "restore") engine.RunRestore();
                    else if (what == "report") engine.Report();
                    else if (what == "trim") engine.TrimAll();
                }
                catch (Exception ex) { AppendLog("!! " + ex.ToString()); }
                finally
                {
                    BeginInvoke((Action)delegate
                    {
                        SetBusy(false);
                        if ((what == "boost" || what == "idle") && chkSentry.Checked)
                            StartSentry(what);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetBusy(bool busy)
        {
            btnBoost.Enabled = btnIdle.Enabled = btnRestore.Enabled =
                btnReport.Enabled = btnTrim.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        // Everything the engine and the sentry say arrives here, from any thread.
        // The file write happens on the UI hop only - doing it before the hop as
        // well is what put every line in the log twice.
        private void AppendLog(string line)
        {
            if (logBox.InvokeRequired)
            {
                try { logBox.BeginInvoke((Action<string>)AppendLog, line); }
                catch (Exception) { App.FileLog(line); }
                return;
            }
            App.FileLog(line);
            logBox.AppendText(line + Environment.NewLine);
        }

        private void UpdateMemory()
        {
            ulong total, free;
            Engine.ReadMemory(out total, out free);
            ulong used = total - free;
            double pct = total == 0 ? 0 : (double)used / total;
            memLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "RAM  {0:0.0} GB used  /  {1:0.0} GB   -   {2:0.0} GB free",
                used / 1024.0, total / 1024.0, free / 1024.0);
            memFill.Width = (int)(memBar.ClientSize.Width * pct);
            memFill.BackColor = pct > 0.85 ? Color.FromArgb(220, 80, 80)
                : pct > 0.6 ? Color.FromArgb(220, 180, 70)
                : Color.FromArgb(90, 190, 120);
        }
    }
}

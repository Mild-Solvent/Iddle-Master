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
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

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
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
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
Docker Desktop
com.docker.*
vpnkit*
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
vmms
WSLService
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

        // A single silent sweep, used by the sentry. Returns what it killed instead
        // of logging it, so the caller decides how noisy to be. 'skip' holds names
        // the sentry has given up on (respawn backoff); 'protectPid' is spared.
        public List<KillHit> Hunt(List<string> patterns, ICollection<string> skip, int protectPid)
        {
            List<KillHit> hits = new List<KillHit>();
            if (patterns.Count == 0) return hits;
            int me = Process.GetCurrentProcess().Id;

            Process[] all = Process.GetProcesses();
            foreach (Process p in all)
            {
                try
                {
                    string name;
                    long ws;
                    try { name = p.ProcessName; ws = p.WorkingSet64; }
                    catch (Exception) { continue; }

                    if (p.Id == me || p.Id == protectPid) continue;
                    if (skip != null && skip.Contains(name.ToLowerInvariant())) continue;
                    if (IsProtectedProcess(name)) continue;

                    bool wanted = false;
                    foreach (string pat in patterns)
                        if (Match(pat, name)) { wanted = true; break; }
                    if (!wanted) continue;

                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                        hits.Add(new KillHit(name, ws));
                    }
                    catch (Exception) { /* already gone, or refuses - the sweep retries later */ }
                }
                finally { try { p.Dispose(); } catch (Exception) { } }
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
        private Mutex instance;
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

        public static bool IsRunningSomewhere()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(OnlyOne, out m)) return false;
                m.Close();
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool Start()
        {
            if (Alive) return true;
            try
            {
                bool mine;
                instance = new Mutex(true, OnlyOne, out mine);
                if (!mine)
                {
                    instance.Close();
                    instance = null;
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
            if (!Alive) { Release(); return; }
            stopping = true;
            try { if (stopFlag != null) stopFlag.Set(); }
            catch (Exception) { }
            try { thread.Join(4000); }
            catch (Exception) { }
            Release();
            log("[sentry] off watch. " + Reaped + " processes reaped, "
                + Engine.Size(Reclaimed) + " kept out of RAM.");
        }

        private void Release()
        {
            try { if (instance != null) { instance.ReleaseMutex(); instance.Close(); } }
            catch (Exception) { }
            instance = null;
            try { if (stopFlag != null) { stopFlag.Reset(); stopFlag.Close(); } }
            catch (Exception) { }
            stopFlag = null;
        }

        public void Join() { try { if (thread != null) thread.Join(); } catch (Exception) { } }

        private void Loop()
        {
            List<string> patterns = engine.PatternsFor(mode);
            List<string> services = engine.ServicesFor(mode);

            DateTime nextService = DateTime.Now.AddMinutes(cfg.SentryServiceMinutes);
            DateTime nextTrim = DateTime.Now.AddMinutes(cfg.SentryTrimMinutes);
            DateTime nextGuard = DateTime.Now.AddMinutes(cfg.SentryGuardMinutes);

            while (!stopping)
            {
                try
                {
                    SweepProcesses(patterns);

                    if (DateTime.Now >= nextService)
                    {
                        SweepServices(services);
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
        }

        private void SweepProcesses(List<string> patterns)
        {
            ExpireBackoff();

            HashSet<string> skip = new HashSet<string>();
            foreach (KeyValuePair<string, DateTime> kv in cooling) skip.Add(kv.Key);

            int spare = 0;
            if (mode == "boost" && cfg.SentrySkipForeground) spare = Native.ForegroundPid();

            List<KillHit> hits = engine.Hunt(patterns, skip, spare);
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

                Sentry sentry = new Sentry(cfg, eng, logger, enforce);
                if (!sentry.Start()) return 1;
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    sentry.Stop();
                };
                sentry.Join();
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

    // ------------------------------------------------------------------- gui

    internal sealed class MainForm : Form
    {
        private readonly Config cfg;
        private readonly Engine engine;
        private readonly TextBox logBox;
        private readonly Label memLabel;
        private readonly Panel memBar;
        private readonly Panel memFill;
        private readonly Button btnBoost, btnIdle, btnRestore, btnReport, btnTrim, btnConfig;
        private readonly CheckBox chkSentry;
        private readonly Label sentryLabel;
        private readonly System.Windows.Forms.Timer timer;
        private Sentry sentry;

        private static readonly Color Bg = Color.FromArgb(18, 18, 22);
        private static readonly Color Fg = Color.FromArgb(225, 225, 232);
        private static readonly Color Dim = Color.FromArgb(120, 120, 132);

        public MainForm(Config c)
        {
            cfg = c;
            engine = new Engine(cfg, AppendLog);

            Text = "IDLE MASTER";
            Size = new Size(700, 668);
            MinimumSize = new Size(560, 480);
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
            sub.Text = "Sunshine + Tailscale stay up. Everything else is negotiable.";
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
            btnConfig = SmallButton("Edit config", 502, 306);
            btnConfig.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Config.Path_ + "\"")); }
                catch (Exception ex) { AppendLog("! " + ex.Message); }
            };

            chkSentry = new CheckBox();
            chkSentry.Text = "Keep hunting after boost";
            chkSentry.Checked = cfg.Sentry;
            chkSentry.SetBounds(24, 346, 190, 22);
            chkSentry.ForeColor = Fg;
            chkSentry.FlatStyle = FlatStyle.Flat;
            chkSentry.Click += delegate { ToggleSentry(); };
            Controls.Add(chkSentry);

            sentryLabel = new Label();
            sentryLabel.SetBounds(220, 348, 442, 20);
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
            logBox.SetBounds(22, 376, 640, 214);
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

            // A mode was run earlier and never restored - pick the watch back up.
            if (st.Mode.Length > 0 && st.SentryArmed && cfg.Sentry && !Sentry.IsRunningSomewhere())
                StartSentry(st.Mode);

            FormClosing += delegate { StopSentry(false); };
        }

        private void StartSentry(string mode)
        {
            if (sentry != null && sentry.Alive) return;
            sentry = new Sentry(cfg, engine, AppendLog, mode);
            if (!sentry.Start()) sentry = null;
            UpdateSentry();
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
            if (!on && chkSentry.Checked && sentry != null) { sentry = null; }

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

        private void AppendLog(string line)
        {
            App.FileLog(line);
            if (logBox.InvokeRequired)
            {
                try { logBox.BeginInvoke((Action<string>)AppendLog, line); }
                catch (Exception) { }
                return;
            }
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

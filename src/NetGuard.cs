// IDLE MASTER - the network guard.
//
// The sentry guards the RAM. This guards the way back in: a machine that is
// only ever reached over Sunshine-through-Tailscale is useless the moment its
// Wi-Fi drops, its DHCP lease goes stale, tailscaled stops, or Sunshine stays
// alive without listening. The guard measures those four things on a timer,
// in order - link, internet, Tailscale, Sunshine - and repairs the first one
// that is broken with an escalating ladder, then measures again. Quiet while
// everything is fine; every fix is a line in the log.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace IdleMaster
{
    // ------------------------------------------------------------ wlanapi

    // The native WLAN API, thinly wrapped. netsh would do the same jobs but
    // prints in the user's language and, since 24H2, refuses to name networks
    // without location permission; the API answers in structs and needs only
    // the elevation the app already has. Every call here swallows its own
    // failure and returns "don't know", so a driver without an answer cannot
    // take the guard down with it.
    internal static class Wlan
    {
        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ieData, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanConnect(IntPtr handle, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")]
        private static extern uint WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, uint size, IntPtr data, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr p);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid Guid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_PROFILE_INFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
            public DOT11_SSID Ssid;
            public int BssType;
            public uint NumberOfBssids;
            public int Connectable;
            public uint NotConnectableReason;
            public uint NumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] PhyTypes;
            public int MorePhyTypes;
            public uint SignalQuality;
            public int SecurityEnabled;
            public int AuthAlgorithm;
            public int CipherAlgorithm;
            public uint Flags;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_CONNECTION_PARAMETERS
        {
            public int Mode;
            public IntPtr Profile;
            public IntPtr Ssid;
            public IntPtr BssidList;
            public int BssType;
            public uint Flags;
        }

        // The head of WLAN_CONNECTION_ATTRIBUTES - everything up to and including
        // the SSID. The real struct goes on (security attributes); reading only
        // the front of a bigger buffer is fine.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_HEAD
        {
            public int State;
            public int Mode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
            public DOT11_SSID Ssid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_PHY_RADIO_STATE
        {
            public uint PhyIndex;
            public int Software;     // dot11_radio_state: 0 unknown, 1 on, 2 off
            public int Hardware;
        }

        private const int OpAutoconf = 1;
        private const int OpRadioState = 4;
        private const int OpCurrentConnection = 7;

        // WLAN_INTERFACE_STATE
        public const int NotReady = 0, Connected = 1, Disconnected = 4;

        public sealed class Interface
        {
            public Guid Guid;
            public string Description = "";
            public int State = -1;
        }

        public sealed class Network
        {
            public string Profile = "";
            public string Ssid = "";
            public int Signal;
            public bool Connected;
            public bool HasProfile;
        }

        private static IntPtr Open()
        {
            try
            {
                uint v;
                IntPtr h;
                if (WlanOpenHandle(2, IntPtr.Zero, out v, out h) != 0) return IntPtr.Zero;
                return h;
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        private static void Close(IntPtr h)
        {
            try { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
            catch (Exception) { }
        }

        // The Wi-Fi adapters Windows knows about. A disabled adapter is not in
        // here at all, which is itself information.
        public static List<Interface> Interfaces()
        {
            List<Interface> found = new List<Interface>();
            IntPtr h = Open();
            if (h == IntPtr.Zero) return found;
            IntPtr list = IntPtr.Zero;
            try
            {
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return found;
                int n = Marshal.ReadInt32(list);
                int size = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
                for (int i = 0; i < n; i++)
                {
                    IntPtr at = new IntPtr(list.ToInt64() + 8 + (long)i * size);
                    WLAN_INTERFACE_INFO info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(at, typeof(WLAN_INTERFACE_INFO));
                    Interface w = new Interface();
                    w.Guid = info.Guid;
                    w.Description = info.Description ?? "";
                    w.State = info.State;
                    found.Add(w);
                }
            }
            catch (Exception) { }
            finally
            {
                try { if (list != IntPtr.Zero) WlanFreeMemory(list); } catch (Exception) { }
                Close(h);
            }
            return found;
        }

        public static Interface First()
        {
            List<Interface> all = Interfaces();
            return all.Count > 0 ? all[0] : null;
        }

        // Fresh state for one adapter, for polling after a connect.
        public static int State(Guid g)
        {
            foreach (Interface w in Interfaces())
                if (w.Guid == g) return w.State;
            return -1;
        }

        // Saved profiles, in Windows' own preference order.
        public static List<string> Profiles(Guid g)
        {
            List<string> names = new List<string>();
            IntPtr h = Open();
            if (h == IntPtr.Zero) return names;
            IntPtr list = IntPtr.Zero;
            try
            {
                if (WlanGetProfileList(h, ref g, IntPtr.Zero, out list) != 0) return names;
                int n = Marshal.ReadInt32(list);
                int size = Marshal.SizeOf(typeof(WLAN_PROFILE_INFO));
                for (int i = 0; i < n; i++)
                {
                    IntPtr at = new IntPtr(list.ToInt64() + 8 + (long)i * size);
                    WLAN_PROFILE_INFO p = (WLAN_PROFILE_INFO)Marshal.PtrToStructure(at, typeof(WLAN_PROFILE_INFO));
                    if (!string.IsNullOrEmpty(p.Name)) names.Add(p.Name);
                }
            }
            catch (Exception) { }
            finally
            {
                try { if (list != IntPtr.Zero) WlanFreeMemory(list); } catch (Exception) { }
                Close(h);
            }
            return names;
        }

        // What is in the air right now. 'scan' asks the radio to look again and
        // waits for the answer; without it you get whatever Windows saw last.
        public static List<Network> Visible(Guid g, bool scan)
        {
            List<Network> found = new List<Network>();
            IntPtr h = Open();
            if (h == IntPtr.Zero) return found;
            IntPtr list = IntPtr.Zero;
            try
            {
                if (scan)
                {
                    try
                    {
                        if (WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0) Thread.Sleep(4500);
                    }
                    catch (Exception) { }
                }
                if (WlanGetAvailableNetworkList(h, ref g, 0, IntPtr.Zero, out list) != 0) return found;
                int n = Marshal.ReadInt32(list);
                int size = Marshal.SizeOf(typeof(WLAN_AVAILABLE_NETWORK));
                for (int i = 0; i < n; i++)
                {
                    IntPtr at = new IntPtr(list.ToInt64() + 8 + (long)i * size);
                    WLAN_AVAILABLE_NETWORK a = (WLAN_AVAILABLE_NETWORK)Marshal.PtrToStructure(at, typeof(WLAN_AVAILABLE_NETWORK));
                    Network w = new Network();
                    w.Profile = a.ProfileName ?? "";
                    w.Ssid = SsidText(a.Ssid);
                    w.Signal = (int)a.SignalQuality;
                    w.Connected = (a.Flags & 1) != 0;
                    w.HasProfile = (a.Flags & 2) != 0 || w.Profile.Length > 0;
                    found.Add(w);
                }
            }
            catch (Exception) { }
            finally
            {
                try { if (list != IntPtr.Zero) WlanFreeMemory(list); } catch (Exception) { }
                Close(h);
            }
            return found;
        }

        private static string SsidText(DOT11_SSID s)
        {
            try
            {
                if (s.Ssid == null || s.Length == 0 || s.Length > 32) return "";
                return Encoding.UTF8.GetString(s.Ssid, 0, (int)s.Length);
            }
            catch (Exception) { return ""; }
        }

        // Profile and SSID of the current connection, or "" when not connected
        // (or when Windows will not say - unelevated, without location rights).
        public static string CurrentProfile(Guid g, out string ssid)
        {
            ssid = "";
            IntPtr h = Open();
            if (h == IntPtr.Zero) return "";
            IntPtr data = IntPtr.Zero;
            try
            {
                uint size;
                if (WlanQueryInterface(h, ref g, OpCurrentConnection, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0)
                    return "";
                if (data == IntPtr.Zero || size < Marshal.SizeOf(typeof(WLAN_CONNECTION_HEAD))) return "";
                WLAN_CONNECTION_HEAD c = (WLAN_CONNECTION_HEAD)Marshal.PtrToStructure(data, typeof(WLAN_CONNECTION_HEAD));
                if (c.State != Connected) return "";
                ssid = SsidText(c.Ssid);
                return c.ProfileName ?? "";
            }
            catch (Exception) { return ""; }
            finally
            {
                try { if (data != IntPtr.Zero) WlanFreeMemory(data); } catch (Exception) { }
                Close(h);
            }
        }

        // Radio: soft-off is the airplane-mode switch and can be flipped back;
        // hard-off is a key or a slider and cannot. Returns false if unknown.
        public static bool RadioState(Guid g, out bool softwareOff, out bool hardwareOff)
        {
            softwareOff = false; hardwareOff = false;
            IntPtr h = Open();
            if (h == IntPtr.Zero) return false;
            IntPtr data = IntPtr.Zero;
            try
            {
                uint size;
                if (WlanQueryInterface(h, ref g, OpRadioState, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0)
                    return false;
                if (data == IntPtr.Zero || size < 16) return false;
                int phys = Marshal.ReadInt32(data);
                if (phys <= 0) return false;
                // Every PHY must be on for the radio to count as on.
                for (int i = 0; i < phys && i < 64; i++)
                {
                    int sw = Marshal.ReadInt32(data, 4 + i * 12 + 4);
                    int hw = Marshal.ReadInt32(data, 4 + i * 12 + 8);
                    if (sw == 2) softwareOff = true;
                    if (hw == 2) hardwareOff = true;
                }
                return true;
            }
            catch (Exception) { return false; }
            finally
            {
                try { if (data != IntPtr.Zero) WlanFreeMemory(data); } catch (Exception) { }
                Close(h);
            }
        }

        public static bool RadioOn(Guid g)
        {
            IntPtr h = Open();
            if (h == IntPtr.Zero) return false;
            IntPtr mem = IntPtr.Zero;
            try
            {
                WLAN_PHY_RADIO_STATE st = new WLAN_PHY_RADIO_STATE();
                st.PhyIndex = 0;
                st.Software = 1;
                st.Hardware = 1;
                mem = Marshal.AllocHGlobal(12);
                Marshal.StructureToPtr(st, mem, false);
                return WlanSetInterface(h, ref g, OpRadioState, 12, mem, IntPtr.Zero) == 0;
            }
            catch (Exception) { return false; }
            finally
            {
                try { if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem); } catch (Exception) { }
                Close(h);
            }
        }

        // Auto-connect to saved networks. Switched off, Windows sits disconnected
        // forever; nothing else in here would explain that.
        public static bool AutoconfEnabled(Guid g, out bool known)
        {
            known = false;
            IntPtr h = Open();
            if (h == IntPtr.Zero) return true;
            IntPtr data = IntPtr.Zero;
            try
            {
                uint size;
                if (WlanQueryInterface(h, ref g, OpAutoconf, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0)
                    return true;
                if (data == IntPtr.Zero || size < 4) return true;
                known = true;
                return Marshal.ReadInt32(data) != 0;
            }
            catch (Exception) { return true; }
            finally
            {
                try { if (data != IntPtr.Zero) WlanFreeMemory(data); } catch (Exception) { }
                Close(h);
            }
        }

        public static bool EnableAutoconf(Guid g)
        {
            IntPtr h = Open();
            if (h == IntPtr.Zero) return false;
            IntPtr mem = IntPtr.Zero;
            try
            {
                mem = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(mem, 1);
                return WlanSetInterface(h, ref g, OpAutoconf, 4, mem, IntPtr.Zero) == 0;
            }
            catch (Exception) { return false; }
            finally
            {
                try { if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem); } catch (Exception) { }
                Close(h);
            }
        }

        // Starts a connection to a saved profile. Asynchronous: poll State().
        public static bool Connect(Guid g, string profile)
        {
            IntPtr h = Open();
            if (h == IntPtr.Zero) return false;
            IntPtr name = IntPtr.Zero;
            try
            {
                name = Marshal.StringToHGlobalUni(profile);
                WLAN_CONNECTION_PARAMETERS p = new WLAN_CONNECTION_PARAMETERS();
                p.Mode = 0;                 // wlan_connection_mode_profile
                p.Profile = name;
                p.Ssid = IntPtr.Zero;
                p.BssidList = IntPtr.Zero;
                p.BssType = 3;              // dot11_BSS_type_any
                p.Flags = 0;
                return WlanConnect(h, ref g, ref p, IntPtr.Zero) == 0;
            }
            catch (Exception) { return false; }
            finally
            {
                try { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); } catch (Exception) { }
                Close(h);
            }
        }

        public static bool Disconnect(Guid g)
        {
            IntPtr h = Open();
            if (h == IntPtr.Zero) return false;
            try { return WlanDisconnect(h, ref g, IntPtr.Zero) == 0; }
            catch (Exception) { return false; }
            finally { Close(h); }
        }
    }

    // ------------------------------------------------------------- report

    // One measurement: what is up, what is not, and what the guard did about it.
    internal sealed class NetReport
    {
        public DateTime When = DateTime.Now;

        // link: a physical adapter that is up and has a default gateway
        public bool LinkUp;
        public bool LinkIsWifi;
        public string LinkName = "";          // adapter name, "Wi-Fi" / "Ethernet"

        // the Wi-Fi adapter, whether or not it carries the link
        public bool WifiPresent;
        public bool WifiEnumerated;           // the WLAN API sees it (disabled adapters vanish)
        public Guid WifiGuid;
        public string WifiAdapter = "";       // adapter name for netsh / PowerShell
        public int WifiState = -1;            // Wlan.Connected etc.
        public string WifiProfile = "";
        public string WifiSsid = "";
        public bool RadioSoftOff, RadioHardOff;

        public bool Internet;                 // something on the internet answered
        public bool Dns;                      // ...and names resolve

        public bool TailscaleInstalled;
        public bool TailscaleService;
        public string TailscaleState = "";    // BackendState: Running, Stopped, NeedsLogin, NoState
        public string TailscaleIp = "";
        public bool TailscaleAdapter;
        public bool TailscaleOnline;
        public bool NeedsLogin;

        public bool SunshineInstalled;
        public bool SunshineService;
        public bool SunshineListening;

        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Fixes = new List<string>();

        public bool TailscaleOk
        {
            get
            {
                if (!TailscaleInstalled) return true;
                return TailscaleService && TailscaleState == "Running" && TailscaleAdapter && TailscaleIp.Length > 0;
            }
        }

        public bool SunshineOk
        {
            get { return !SunshineInstalled || SunshineListening; }
        }

        public bool Healthy { get { return LinkUp && Internet && TailscaleOk && SunshineOk; } }

        // "Wi-Fi 'HomeNet'" / "Ethernet" / "no link"
        public string LinkText
        {
            get
            {
                if (!LinkUp) return "no link";
                if (LinkIsWifi)
                    return "Wi-Fi" + (WifiProfile.Length > 0 ? " '" + WifiProfile + "'" : WifiSsid.Length > 0 ? " '" + WifiSsid + "'" : "");
                return LinkName.Length > 0 ? LinkName : "Ethernet";
            }
        }

        public string TailscaleText
        {
            get
            {
                if (!TailscaleInstalled) return "Tailscale not installed";
                if (!TailscaleService) return "Tailscale service stopped";
                if (NeedsLogin) return "Tailscale needs a login";
                if (TailscaleState != "Running") return "Tailscale " + (TailscaleState.Length > 0 ? TailscaleState : "not answering");
                if (!TailscaleAdapter) return "Tailscale adapter down";
                if (TailscaleIp.Length == 0) return "Tailscale has no address";
                return "Tailscale " + TailscaleIp;      // the address is the proof it is reachable
            }
        }

        public string SunshineText
        {
            get
            {
                if (!SunshineInstalled) return "Sunshine not installed";
                if (!SunshineService) return "Sunshine service stopped";
                return SunshineListening ? "Sunshine up" : "Sunshine not listening";
            }
        }

        // One line for the window.
        public string Summary()
        {
            if (Healthy) return LinkText + ", " + TailscaleText + ", " + SunshineText;
            return string.Join(", ", Problems.ToArray());
        }

        // The Wi-Fi adapter's own state, in words, whether or not it carries the link.
        public string WifiText()
        {
            if (!WifiEnumerated) return "adapter disabled or missing";
            if (RadioHardOff) return "radio switched off in hardware";
            if (RadioSoftOff) return "radio off";
            switch (WifiState)
            {
                case Wlan.Connected: return "connected" + (WifiProfile.Length > 0 ? " to '" + WifiProfile + "'" : "");
                case Wlan.Disconnected: return "disconnected";
                case Wlan.NotReady: return "not ready";
                default: return "state " + WifiState;
            }
        }

        // The four-line picture, for the log and for the guard's page.
        public List<string> Lines()
        {
            List<string> l = new List<string>();
            l.Add((LinkUp ? "+" : "!") + " link       " + LinkText
                + (WifiPresent && !LinkIsWifi ? "  (Wi-Fi " + WifiText() + ")" : ""));
            l.Add((Internet ? "+" : "!") + " internet   " + (Internet ? "reachable" : "NOT reachable")
                + (Internet && !Dns ? ", but DNS is not resolving" : ""));
            l.Add((TailscaleOk ? "+" : "!") + " " + TailscaleText
                + (TailscaleInstalled && TailscaleState == "Running" ? (TailscaleOnline ? ", online" : ", not online yet") : ""));
            l.Add((SunshineOk ? "+" : "!") + " " + SunshineText);
            return l;
        }
    }

    // -------------------------------------------------------------- guard

    internal sealed class NetGuard
    {
        private const string OnlyOne = "Global\\IdleMasterNetworkGuard";

        private readonly Config cfg;
        private readonly Engine engine;
        private readonly Action<string> log;

        private Thread thread;
        private Semaphore slot;
        private bool holding;
        private readonly object gate = new object();
        private readonly AutoResetEvent poke = new AutoResetEvent(false);
        private volatile bool stopping;
        private volatile bool loudNext;

        // read by the UI
        public NetReport Last;
        public DateTime Since;
        public DateTime LastCheck;
        public int Checks;
        public int FixCount;
        public int Attempt;                     // consecutive failed checks; drives escalation
        public string Refused = "";             // why Start() said no, for the label
        public bool Alive { get { return thread != null && thread.IsAlive; } }
        public bool Busy;                       // a repair pass is running right now

        private DateTime downSince = DateTime.MinValue;
        private DateTime lastDisrupt = DateTime.MinValue;     // last time a working link was deliberately dropped
        private DateTime lastBounce = DateTime.MinValue;      // last adapter disable/enable
        private int sunshineStrikes;
        private bool loginNagged;
        private readonly HashSet<string> missingProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string tailscaleExe;

        public NetGuard(Config c, Engine e, Action<string> logger)
        {
            cfg = c; engine = e; log = logger;
        }

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
            Refused = "";
            try
            {
                bool fresh;
                slot = new Semaphore(1, 1, OnlyOne, out fresh);
                holding = slot.WaitOne(0);
                if (!holding)
                {
                    slot.Close();
                    slot = null;
                    Refused = "another Idle Master is already guarding the connection";
                    log("[guard] " + Refused + " - not starting a second guard");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Refused = "could not claim the guard: " + ex.Message.Split('\n')[0];
                log("[guard] " + Refused);
                return false;
            }

            stopping = false;
            Since = DateTime.Now;
            Checks = 0; FixCount = 0; Attempt = 0;
            Last = null;
            downSince = DateTime.MinValue;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "network guard";
            thread.Start();
            log("[guard] guarding the connection - link, internet, Tailscale, Sunshine every "
                + cfg.NetworkGuardSeconds + " s.");
            return true;
        }

        public void Stop()
        {
            stopping = true;
            try { poke.Set(); } catch (Exception) { }
            if (Alive)
            {
                try { thread.Join(3000); } catch (Exception) { }
            }
            ReleaseSlot();
            log("[guard] guard off. " + Checks + " checks, " + FixCount + " fixes.");
        }

        // Run a check right now, and say what was found even if all is well.
        public void CheckNow()
        {
            loudNext = true;
            try { poke.Set(); } catch (Exception) { }
        }

        private void ReleaseSlot()
        {
            lock (gate)
            {
                if (!holding || slot == null) return;
                try { slot.Release(); } catch (Exception) { }
                try { slot.Close(); } catch (Exception) { }
                holding = false;
                slot = null;
            }
        }

        private void Loop()
        {
            // The first check comes quickly: if we are starting at logon on a
            // machine that lost its network overnight, now is the moment.
            int wait = 5000;
            if (cfg.NetworkGuardKeepWifiAwake)
            {
                try { KeepWifiAwake(); } catch (Exception) { }
            }
            while (!stopping)
            {
                try { poke.WaitOne(wait); } catch (Exception) { break; }
                if (stopping) break;
                bool loud = loudNext;
                loudNext = false;
                try
                {
                    Busy = true;
                    Check(true, loud);
                }
                catch (Exception ex)
                {
                    log("[guard] check failed: " + ex.Message.Split('\n')[0]);
                }
                finally { Busy = false; }
                wait = Math.Max(15, cfg.NetworkGuardSeconds) * 1000;
            }
            ReleaseSlot();
        }

        // ---- one check

        // Measure; if something is wrong and 'repair' is on, walk the ladder and
        // measure again. Returns the last measurement. 'loud' prints the whole
        // picture even when it is fine - the button and --network want that; the
        // timer only wants to hear about trouble. The remote-desktop app watch
        // rides along on every check.
        public NetReport Check(bool repair, bool loud)
        {
            NetReport r = CheckNet(repair, loud);
            try { WatchApps(repair, loud); }
            catch (Exception ex) { log("[guard] remote app watch failed: " + ex.Message.Split('\n')[0]); }
            return r;
        }

        private NetReport CheckNet(bool repair, bool loud)
        {
            NetReport r = Measure();
            Checks++;
            LastCheck = DateTime.Now;

            if (r.Healthy)
            {
                if (downSince != DateTime.MinValue)
                {
                    log("[guard] connection is back on its own after "
                        + Minutes(DateTime.Now - downSince) + " - " + r.Summary());
                    downSince = DateTime.MinValue;
                }
                Attempt = 0;
                sunshineStrikes = 0;
                loginNagged = false;
                Last = r;
                if (loud) Print(r, "all good");
                return r;
            }

            // Trouble. Say so once per outage, not once per minute.
            bool first = downSince == DateTime.MinValue;
            if (first) downSince = DateTime.Now;
            Attempt++;
            if (loud || first || Attempt <= 3 || Attempt % 10 == 0)
                log("[guard] trouble" + (Attempt > 1 ? " (try " + Attempt + ")" : "") + ": "
                    + string.Join("; ", r.Problems.ToArray()));
            Last = r;
            if (!repair)
            {
                if (loud) Print(r, "not repaired (report only)");
                return r;
            }

            // Six tries at full tilt, then one repair pass in five: the measuring
            // stays every minute, the fixing stops thrashing.
            if (!loud && Attempt > 6 && Attempt % 5 != 0) return r;

            NetReport after = Repair(r);
            Last = after;
            if (after.Healthy)
            {
                log("[guard] connection back after " + Minutes(DateTime.Now - downSince)
                    + " - " + (after.Fixes.Count > 0 ? string.Join("; ", after.Fixes.ToArray()) : "it recovered while being looked at")
                    + ". Now: " + after.Summary());
                downSince = DateTime.MinValue;
                Attempt = 0;
                if (loud) Print(after, "fixed");
            }
            else
            {
                if (loud || Attempt <= 3 || Attempt % 10 == 0)
                    log("[guard] still not right: " + string.Join("; ", after.Problems.ToArray())
                        + (after.Fixes.Count > 0 ? "  (tried: " + string.Join("; ", after.Fixes.ToArray()) + ")" : "")
                        + (Attempt >= 6 ? " - checking every " + cfg.NetworkGuardSeconds + " s, repairing every 5th check"
                                        : " - again in " + cfg.NetworkGuardSeconds + " s"));
                if (loud) Print(after, "still broken");
            }
            return after;
        }

        private static string Minutes(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return ((int)t.TotalSeconds) + " s";
            if (t.TotalHours < 1) return ((int)t.TotalMinutes) + " min";
            return t.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " h";
        }

        private void Print(NetReport r, string verdict)
        {
            log("-- network guard: " + verdict);
            foreach (string line in r.Lines()) log("   " + line);
            foreach (string f in r.Fixes) log("   * " + f);
        }

        private static string WifiStateText(NetReport r) { return r.WifiText(); }

        // ---- measuring

        public NetReport Measure()
        {
            NetReport r = new NetReport();

            // 1. the link: a real adapter, up, with somewhere to send packets
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!Physical(ni)) continue;
                    bool wifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    if (wifi && !r.WifiPresent)
                    {
                        r.WifiPresent = true;
                        r.WifiAdapter = ni.Name;
                    }
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (!HasGateway(ni)) continue;
                    // Prefer a wire when both carry a gateway - it is the one
                    // that will not wander off.
                    if (r.LinkUp && r.LinkIsWifi == false) continue;
                    r.LinkUp = true;
                    r.LinkIsWifi = wifi;
                    r.LinkName = ni.Name;
                }
            }
            catch (Exception) { }

            // 2. the Wi-Fi adapter in detail, whether or not it carries the link
            try
            {
                Wlan.Interface w = Wlan.First();
                if (w != null)
                {
                    r.WifiPresent = true;
                    r.WifiEnumerated = true;
                    r.WifiGuid = w.Guid;
                    r.WifiState = w.State;
                    if (r.WifiAdapter.Length == 0) r.WifiAdapter = AdapterNameFor(w.Description);
                    bool soft, hard;
                    if (Wlan.RadioState(w.Guid, out soft, out hard)) { r.RadioSoftOff = soft; r.RadioHardOff = hard; }
                    // Naming the network we are on is, to Windows, telling the
                    // app where you are - it prompts for location. Only if asked.
                    if (w.State == Wlan.Connected && cfg.NetworkGuardScan)
                    {
                        string ssid;
                        r.WifiProfile = Wlan.CurrentProfile(w.Guid, out ssid);
                        r.WifiSsid = ssid;
                    }
                }
            }
            catch (Exception) { }

            // 3. the internet: the control plane Tailscale itself needs, then two
            //    anycast addresses that are never down, to tell DNS trouble from
            //    no-route-at-all.
            if (r.LinkUp)
            {
                IPAddress cp = Resolve("controlplane.tailscale.com", 4000);
                r.Dns = cp != null;
                if (cp != null && Tcp(cp, 443, 4000)) r.Internet = true;
                else if (Tcp(IPAddress.Parse("1.1.1.1"), 443, 3000) || Tcp(IPAddress.Parse("8.8.8.8"), 443, 3000))
                    r.Internet = true;
                else if (Ping_("1.1.1.1") || Ping_("8.8.8.8"))
                    r.Internet = true;
            }

            // 4. Tailscale
            r.TailscaleInstalled = Engine.ServiceExists("Tailscale");
            if (r.TailscaleInstalled)
            {
                r.TailscaleService = Engine.ServiceRunning("Tailscale");
                string adapterIp = "";
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0 &&
                            ni.Name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        r.TailscaleAdapter = true;
                        foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                            if (u.Address.AddressFamily == AddressFamily.InterNetwork) { adapterIp = u.Address.ToString(); break; }
                    }
                }
                catch (Exception) { }
                if (r.TailscaleService)
                {
                    bool answered = TailscaleStatus(r);
                    // No CLI to ask (odd install, or it is not where the service
                    // is): judge by the adapter alone rather than cry wolf.
                    if (!answered)
                    {
                        r.TailscaleState = r.TailscaleAdapter && adapterIp.Length > 0 ? "Running" : "";
                        r.TailscaleIp = adapterIp;
                        r.TailscaleOnline = r.TailscaleAdapter;
                    }
                    // Tailscale talking to its control plane IS the internet
                    // working, whatever a firewalled probe says - and it is the
                    // way in, so nothing below may break a link it is using.
                    else if (r.TailscaleOnline && r.TailscaleState == "Running" && r.LinkUp) r.Internet = true;
                }
            }

            // 5. Sunshine
            r.SunshineInstalled = Engine.ServiceExists("SunshineService");
            if (r.SunshineInstalled)
            {
                r.SunshineService = Engine.ServiceRunning("SunshineService");
                r.SunshineListening = SunshineListening();
            }

            // the verdict, in words
            if (!r.LinkUp)
            {
                if (!r.WifiPresent) r.Problems.Add("no network link (no adapter is up with a gateway)");
                else if (!r.WifiEnumerated) r.Problems.Add("no link; the Wi-Fi adapter is disabled or missing");
                else if (r.RadioHardOff) r.Problems.Add("no link; Wi-Fi radio is switched off in hardware");
                else if (r.RadioSoftOff) r.Problems.Add("no link; Wi-Fi radio is off");
                else if (r.WifiState == Wlan.Connected) r.Problems.Add("Wi-Fi is associated but has no gateway yet");
                else r.Problems.Add("no link; Wi-Fi is " + WifiStateText(r));
            }
            else if (!r.Internet)
                r.Problems.Add("link up (" + r.LinkText + ") but the internet does not answer" + (r.Dns ? "" : " and DNS fails"));
            if (r.TailscaleInstalled && !r.TailscaleOk) r.Problems.Add(r.TailscaleText);
            if (r.SunshineInstalled && !r.SunshineOk) r.Problems.Add(r.SunshineText);
            return r;
        }

        // Adapters that could plausibly be the way out. Tunnels and virtual
        // switches are up and have addresses and would fool a naive check.
        private static readonly string[] VirtualWords = new string[]
        {
            "Tailscale", "Hyper-V", "vEthernet", "VMware", "VirtualBox", "WSL", "Loopback",
            "TAP-", "Wintun", "Bluetooth", "Npcap", "WAN Miniport", "Virtual", "Tunnel",
            "NordLynx", "Proton", "ZeroTier", "Docker", "Radmin", "Hamachi", "Microsoft Wi-Fi Direct",
        };

        private static bool Physical(NetworkInterface ni)
        {
            NetworkInterfaceType t = ni.NetworkInterfaceType;
            if (t != NetworkInterfaceType.Ethernet && t != NetworkInterfaceType.Wireless80211
                && t != NetworkInterfaceType.GigabitEthernet && t != NetworkInterfaceType.FastEthernetT
                && t != NetworkInterfaceType.FastEthernetFx) return false;
            string d = ni.Description + " " + ni.Name;
            foreach (string w in VirtualWords)
                if (d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static bool HasGateway(NetworkInterface ni)
        {
            try
            {
                foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                {
                    if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (g.Address.Equals(IPAddress.Any)) continue;
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // The WLAN API names the adapter by its driver description; PowerShell
        // and netsh want the connection name ("Wi-Fi").
        private static string AdapterNameFor(string description)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (ni.Description.Equals(description, StringComparison.OrdinalIgnoreCase)) return ni.Name;
            }
            catch (Exception) { }
            return "Wi-Fi";
        }

        private static IPAddress Resolve(string host, int ms)
        {
            try
            {
                IAsyncResult ar = Dns.BeginGetHostAddresses(host, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ms)) return null;
                IPAddress[] all = Dns.EndGetHostAddresses(ar);
                foreach (IPAddress a in all)
                    if (a.AddressFamily == AddressFamily.InterNetwork) return a;
                return all.Length > 0 ? all[0] : null;
            }
            catch (Exception) { return null; }
        }

        private static bool Tcp(IPAddress ip, int port, int ms)
        {
            TcpClient c = null;
            try
            {
                c = new TcpClient(ip.AddressFamily);
                IAsyncResult ar = c.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ms)) return false;
                c.EndConnect(ar);
                return c.Connected;
            }
            catch (Exception) { return false; }
            finally { try { if (c != null) c.Close(); } catch (Exception) { } }
        }

        private static bool Ping_(string ip)
        {
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply rep = p.Send(IPAddress.Parse(ip), 2500);
                    return rep != null && rep.Status == IPStatus.Success;
                }
            }
            catch (Exception) { return false; }
        }

        // Dropping a link that carries traffic is a last resort: only when the
        // way in (Tailscale) is already gone, and then at most every 15 minutes,
        // so a probe that is merely firewalled cannot keep kicking a working
        // connection out from under you.
        private bool MayDisrupt(NetReport now)
        {
            if (now.TailscaleInstalled && now.TailscaleOk) return false;
            return (DateTime.Now - lastDisrupt).TotalMinutes >= 15;
        }

        // A driver reset is slow and noisy; once every 10 minutes is plenty.
        private bool MayBounce()
        {
            return (DateTime.Now - lastBounce).TotalMinutes >= 10;
        }

        private static bool SunshineListening()
        {
            try
            {
                IPEndPoint[] eps = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (IPEndPoint ep in eps)
                    if (ep.Port == 47984 || ep.Port == 47989 || ep.Port == 47990 || ep.Port == 48010)
                        return true;
            }
            catch (Exception) { }
            return false;
        }

        private string TailscaleExe()
        {
            if (tailscaleExe != null) return tailscaleExe;
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale\\tailscale.exe");
            if (!File.Exists(exe))
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\Tailscale"))
                    {
                        string img = k == null ? null : k.GetValue("ImagePath") as string;
                        if (img != null)
                        {
                            img = img.Trim().Trim('"');
                            string dir = Path.GetDirectoryName(img);
                            if (dir != null && File.Exists(Path.Combine(dir, "tailscale.exe")))
                                exe = Path.Combine(dir, "tailscale.exe");
                        }
                    }
                }
                catch (Exception) { }
            }
            if (!File.Exists(exe)) exe = "tailscale.exe";     // maybe on PATH; maybe not
            tailscaleExe = exe;
            return exe;
        }

        // Asks the CLI. False = the CLI could not be run at all, so the caller
        // should judge by other means; true = it answered, good news or bad.
        // A CLI that is there but hangs or errors counts as an answer ("NoState"):
        // the daemon is in trouble and a restart later is the right call.
        private bool TailscaleStatus(NetReport r)
        {
            string json;
            int rc = Exec(TailscaleExe(), "status --json --peers=false", 12000, out json);
            if (rc == -2) { r.TailscaleState = ""; return false; }
            if (json == null || json.IndexOf("BackendState", StringComparison.Ordinal) < 0)
            {
                r.TailscaleState = "";
                return true;
            }
            Match m = Regex.Match(json, "\"BackendState\"\\s*:\\s*\"([A-Za-z]+)\"");
            r.TailscaleState = m.Success ? m.Groups[1].Value : "";
            m = Regex.Match(json, "\"TailscaleIPs\"\\s*:\\s*\\[\\s*\"([0-9.]+)\"");
            r.TailscaleIp = m.Success ? m.Groups[1].Value : "";
            m = Regex.Match(json, "\"AuthURL\"\\s*:\\s*\"([^\"]+)\"");
            r.NeedsLogin = m.Success || r.TailscaleState == "NeedsLogin";
            m = Regex.Match(json, "\"Self\"\\s*:\\s*\\{[^}]*?\"Online\"\\s*:\\s*(true|false)");
            r.TailscaleOnline = m.Success && m.Groups[1].Value == "true";
            return true;
        }

        // ---- repairing

        // The ladder. Each rung does one thing, re-measures, and stops the moment
        // the picture is healthy. How far up it goes depends on Attempt: the
        // first check after a drop tries the gentle things, and every check
        // after that reaches one rung higher.
        private NetReport Repair(NetReport r)
        {
            List<string> done = new List<string>();
            NetReport now = r;

            // Services first: cheap, and on this machine the likeliest cause -
            // idle mode stopped something, or it died.
            if (!now.LinkUp || !now.Internet)
            {
                if (now.WifiPresent) Ensure("WlanSvc", "WLAN AutoConfig", done);
                Ensure("Dhcp", "DHCP client", done);
                Ensure("Dnscache", "DNS client", done);
                Ensure("NlaSvc", "Network Location Awareness", done);
                if (done.Count > 0) { Thread.Sleep(3000); now = Again(now, done); if (now.Healthy) return now; }
            }

            // No link at all: Wi-Fi first (a laptop's only way out, usually),
            // then whatever is wired.
            if (!now.LinkUp)
            {
                if (now.WifiPresent && cfg.NetworkGuardWifi) now = RepairWifi(now, done);
                if (now.Healthy) return now;
                if (!now.LinkUp) now = RepairWire(now, done);
                if (now.Healthy) return now;
            }

            // Link, but the internet does not answer.
            if (now.LinkUp && !now.Internet)
            {
                now = RepairInternet(now, done);
                if (now.Healthy) return now;
            }

            // Tailscale - only worth touching once the internet is there.
            if (now.Internet && now.TailscaleInstalled && !now.TailscaleOk)
            {
                now = RepairTailscale(now, done);
                if (now.Healthy) return now;
            }

            // Sunshine - local, independent of everything above.
            if (now.SunshineInstalled && !now.SunshineOk)
                now = RepairSunshine(now, done);

            return now;
        }

        private NetReport Again(NetReport prev, List<string> done)
        {
            NetReport n = Measure();
            n.Fixes.AddRange(done);
            return n;
        }

        private bool Ensure(string service, string what, List<string> done)
        {
            if (!Engine.ServiceExists(service)) return false;
            if (Engine.ServiceRunning(service)) return false;
            bool ok = engine.EnsureService(service, false);
            done.Add((ok ? "restarted " : "could not restart ") + what + " (" + service + ")");
            if (ok) FixCount++;
            return ok;
        }

        private NetReport RepairWifi(NetReport r, List<string> done)
        {
            NetReport now = r;

            // The adapter is switched off at the device level: nothing in the
            // WLAN API can see it. PowerShell can turn it back on.
            if (!now.WifiEnumerated)
            {
                if (Attempt >= 1)
                {
                    string outp;
                    int rc = Exec("powershell.exe",
                        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Disabled' } | Enable-NetAdapter -Confirm:$false\"",
                        40000, out outp);
                    done.Add(rc == 0 ? "re-enabled the disabled network adapter(s)" : "could not re-enable the adapter (" + First(outp) + ")");
                    if (rc == 0) FixCount++;
                    Thread.Sleep(6000);
                    now = Again(now, done);
                }
                return now;
            }

            Guid g = now.WifiGuid;

            if (now.RadioHardOff)
            {
                done.Add("Wi-Fi radio is switched off in hardware - a key or switch; nothing to do from here");
                return now;
            }
            if (now.RadioSoftOff)
            {
                bool ok = Wlan.RadioOn(g);
                done.Add(ok ? "switched the Wi-Fi radio back on" : "could not switch the Wi-Fi radio on");
                if (ok) FixCount++;
                Thread.Sleep(4000);
                now = Again(now, done);
                if (now.LinkUp) return now;
            }

            bool known;
            if (!Wlan.AutoconfEnabled(g, out known) && known)
            {
                bool ok = Wlan.EnableAutoconf(g);
                done.Add(ok ? "Wi-Fi auto-connect was off - turned it back on" : "could not turn Wi-Fi auto-connect on");
                if (ok) FixCount++;
                Thread.Sleep(4000);
                now = Again(now, done);
                if (now.LinkUp) return now;
            }

            // Associated but no address: DHCP is probably still working on it.
            // Give it one check; after that, renew, then reconnect.
            if (now.WifiState == Wlan.Connected)
            {
                if (Attempt == 1)
                {
                    Thread.Sleep(8000);
                    now = Again(now, done);
                    if (now.LinkUp) return now;
                }
                if (Attempt >= 2) { Renew(now, done); now = Again(now, done); if (now.LinkUp) return now; }
                if (Attempt >= 3)
                {
                    Wlan.Disconnect(g);
                    done.Add("dropped the stale Wi-Fi association");
                    Thread.Sleep(2000);
                }
            }

            // Reconnect to something we know.
            now = ConnectKnown(now, done);
            if (now.LinkUp) return now;

            // Still nothing: bounce the adapter, then try again.
            if (Attempt >= 3 && MayBounce())
            {
                Bounce(now.WifiAdapter, done);
                now = Again(now, done);
                if (now.LinkUp) return now;
                if (now.WifiEnumerated) now = ConnectKnown(now, done);
            }
            return now;
        }

        // The list of networks to try, best first: your [network.wifi] order for
        // the ones in the air, then anything else in the air we have a profile
        // for by signal, then the rest of [network.wifi] (hidden SSIDs do not
        // show in a scan), then every saved profile if the scan told us nothing.
        // Without NetworkGuardScan there is no "in the air" - a scan is a location
        // request to Windows, and it asks - so it is [network.wifi], then every
        // saved profile in Windows' own order. Slower, never a prompt.
        internal List<string> Candidates(Guid g)
        {
            List<string> profiles = Wlan.Profiles(g);
            List<Wlan.Network> air = cfg.NetworkGuardScan ? Wlan.Visible(g, true) : new List<Wlan.Network>();
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<string> preferred = new List<string>();
            foreach (string want in cfg.NetworkWifi)
            {
                bool hit = false;
                foreach (string p in profiles)
                    if (Engine.Match(want, p)) { hit = true; if (!preferred.Contains(p)) preferred.Add(p); }
                if (!hit && missingProfiles.Add(want))
                    log("[guard] '" + want + "' in [network.wifi] is not a saved Wi-Fi profile on this machine"
                        + " - connect to it once by hand so Windows keeps the password, then the guard can use it");
            }

            Dictionary<string, int> visible = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Wlan.Network n in air)
            {
                string name = n.Profile.Length > 0 ? n.Profile : n.Ssid;
                if (name.Length == 0) continue;
                bool saved = false;
                foreach (string p in profiles) if (p.Equals(name, StringComparison.OrdinalIgnoreCase)) { saved = true; break; }
                if (!saved) continue;
                if (!visible.ContainsKey(name) || visible[name] < n.Signal) visible[name] = n.Signal;
            }

            foreach (string p in preferred) if (visible.ContainsKey(p) && seen.Add(p)) result.Add(p);
            List<KeyValuePair<string, int>> bySignal = new List<KeyValuePair<string, int>>(visible);
            bySignal.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            foreach (KeyValuePair<string, int> kv in bySignal) if (seen.Add(kv.Key)) result.Add(kv.Key);
            foreach (string p in preferred) if (seen.Add(p)) result.Add(p);
            if (visible.Count == 0)
                foreach (string p in profiles) if (seen.Add(p)) result.Add(p);
            return result;
        }

        private NetReport ConnectKnown(NetReport r, List<string> done)
        {
            NetReport now = r;
            Guid g = now.WifiGuid;
            List<string> cands = Candidates(g);
            if (cands.Count == 0)
            {
                done.Add("no saved Wi-Fi network to connect to" + (cfg.NetworkWifi.Count > 0 ? "" : " - name one in [network.wifi], or connect once by hand"));
                return now;
            }

            // Up to three per pass, and a later pass starts further down the
            // list, so a stubborn favourite cannot hide the rest forever.
            int perPass = Math.Min(3, cands.Count);
            int start = ((Math.Max(1, Attempt) - 1) * perPass) % cands.Count;
            for (int i = 0; i < perPass; i++)
            {
                if (stopping) break;
                string p = cands[(start + i) % cands.Count];
                if (!Wlan.Connect(g, p))
                {
                    done.Add("Wi-Fi connect to '" + p + "' was refused");
                    continue;
                }
                bool assoc = false;
                int patience = cfg.NetworkGuardScan ? 15 : 10;
                for (int t = 0; t < patience && !stopping; t++)
                {
                    Thread.Sleep(1000);
                    if (Wlan.State(g) == Wlan.Connected) { assoc = true; break; }
                }
                if (!assoc) { done.Add("Wi-Fi '" + p + "' did not answer"); continue; }
                // Associated; now an address and a gateway.
                for (int t = 0; t < 12 && !stopping; t++)
                {
                    Thread.Sleep(1000);
                    now = Again(now, done);
                    if (now.LinkUp) break;
                }
                if (!now.LinkUp) { done.Add("Wi-Fi '" + p + "' connected but gave no address"); continue; }
                if (!now.Internet)
                {
                    // one more look - DNS and routes settle a beat after the lease
                    Thread.Sleep(3000);
                    now = Again(now, done);
                }
                FixCount++;
                done.Add("reconnected Wi-Fi to '" + p + "'" + (now.Internet ? "" : " (no internet through it yet)"));
                if (now.Internet) return now;
                // it gave a link but no internet: keep trying others on the
                // next pass rather than the same dead one.
            }
            return now;
        }

        private NetReport RepairWire(NetReport r, List<string> done)
        {
            NetReport now = r;
            if (Attempt >= 1) { Renew(now, done); now = Again(now, done); if (now.LinkUp) return now; }
            if (Attempt >= 2)
            {
                string outp;
                int rc = Exec("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Disabled' } | Enable-NetAdapter -Confirm:$false\"",
                    40000, out outp);
                if (rc == 0) { done.Add("re-enabled any disabled network adapter"); FixCount++; }
                Thread.Sleep(6000);
                now = Again(now, done);
                if (now.LinkUp) return now;
            }
            if (Attempt >= 3 && MayBounce())
            {
                Bounce("", done);
                now = Again(now, done);
            }
            return now;
        }

        private NetReport RepairInternet(NetReport r, List<string> done)
        {
            NetReport now = r;

            // Rung 1: the lease and the resolver cache.
            if (Attempt >= 1)
            {
                Flush(done);
                if (!now.Internet) Renew(now, done);
                Thread.Sleep(2000);
                now = Again(now, done);
                if (now.Internet) return now;
            }

            // Rung 2: services a mode stopped that the network may lean on -
            // NordVPN's kill switch with its service down is the famous one.
            if (Attempt >= 2)
            {
                bool any = false;
                try
                {
                    StateFile st = StateFile.Load();
                    foreach (string s in st.StoppedServices)
                    {
                        foreach (string net in NetServices)
                        {
                            if (!s.Equals(net, StringComparison.OrdinalIgnoreCase)) continue;
                            if (Engine.ServiceRunning(s)) continue;
                            if (engine.EnsureService(s, false)) { done.Add("restarted " + s + " (a mode had stopped it; the network may need it)"); FixCount++; any = true; }
                        }
                    }
                }
                catch (Exception) { }
                if (any)
                {
                    Thread.Sleep(5000);
                    now = Again(now, done);
                    if (now.Internet) return now;
                }
            }

            // Rung 3: reconnect the link itself. This drops a link that is up,
            // so only when the way in is already lost (see MayDisrupt).
            if (Attempt >= 3 && MayDisrupt(now))
            {
                lastDisrupt = DateTime.Now;
                if (now.LinkIsWifi && now.WifiEnumerated && cfg.NetworkGuardWifi)
                {
                    Wlan.Disconnect(now.WifiGuid);
                    done.Add("dropped the Wi-Fi connection to rebuild it");
                    Thread.Sleep(2000);
                    now = ConnectKnown(now, done);
                    if (now.Internet) return now;
                }
                else if (MayBounce())
                {
                    Bounce(now.LinkName, done);
                    now = Again(now, done);
                    if (now.Internet) return now;
                }

                // Rung 4, same pass: bounce the Wi-Fi adapter too, then reconnect.
                if (Attempt >= 4 && now.LinkIsWifi && cfg.NetworkGuardWifi && MayBounce())
                {
                    Bounce(now.WifiAdapter, done);
                    now = Again(now, done);
                    if (!now.Internet && now.WifiEnumerated && !now.LinkUp) now = ConnectKnown(now, done);
                }
            }
            else if (Attempt >= 3)
                done.Add(now.TailscaleOk
                    ? "not touching the link - Tailscale is up through it, so the probe is the odd one out"
                    : "link already rebuilt in the last 15 min - leaving it alone for now");
            return now;
        }

        // Services idle mode may have stopped that the machine's connectivity
        // can depend on. Only ones in idlemaster.state get restarted - i.e.
        // only ones WE stopped.
        private static readonly string[] NetServices = new string[]
        {
            "nordvpn-service", "RasMan", "SstpSvc", "IKEEXT", "PolicyAgent", "WinHttpAutoProxySvc",
            "WlanSvc", "Dhcp", "Dnscache", "NlaSvc", "netprofm", "Wcmsvc", "nsi", "iphlpsvc", "SharedAccess",
        };

        private NetReport RepairTailscale(NetReport r, List<string> done)
        {
            NetReport now = r;
            if (!now.TailscaleService)
            {
                Ensure("Tailscale", "Tailscale", done);
                Thread.Sleep(6000);
                now = Again(now, done);
                if (now.TailscaleOk) return now;
            }

            if (now.NeedsLogin)
            {
                if (!loginNagged)
                {
                    log("[guard] !! Tailscale says it needs a login (its key expired or it was logged out)."
                        + " Nothing here can type that for you: run 'tailscale up' or use the tray app at the keyboard.");
                    loginNagged = true;
                }
                done.Add("Tailscale needs a login - cannot be fixed from here");
                return now;
            }

            // Stopped and NoState both mean the daemon is up with nobody
            // having said "connect" - NoState is also where a fresh service
            // start lands. Restarting the service again never leaves NoState
            // (two days of field log prove that). What leaves it, on a
            // Windows box, is the tray app: tailscale-ipn is what tells the
            // daemon which profile to connect, and idle's own kill list takes
            // it down as a "tray icon nobody can see". Starting it is exactly
            // what typing "tailscale" after the Windows key does - so do
            // that first, then say 'tailscale up' ourselves.
            if (now.TailscaleState == "Stopped" || now.TailscaleState == "NoState")
            {
                if (StartTailscaleGui(done))
                {
                    Thread.Sleep(10000);
                    now = Again(now, done);
                    if (now.TailscaleOk) return now;
                }
                TailscaleUp(done, "(it was " + now.TailscaleState + ")");
                Thread.Sleep(5000);
                now = Again(now, done);
                if (now.TailscaleOk) return now;
            }

            // Running without an adapter/address, or not answering at all:
            // restart the daemon. Gently on the first try, firmly after.
            if (Attempt >= 2 || now.TailscaleState.Length == 0 || now.TailscaleState == "NoState")
            {
                bool ok = engine.RestartService("Tailscale");
                done.Add(ok ? "restarted the Tailscale service" : "could not restart the Tailscale service");
                if (ok) FixCount++;
                Thread.Sleep(8000);
                now = Again(now, done);
                // A restarted daemon reports Stopped or NoState until someone
                // says "connect" - say it, whichever of the two it landed in.
                if (!now.TailscaleOk && (now.TailscaleState == "Stopped"
                    || now.TailscaleState == "NoState" || now.TailscaleState.Length == 0))
                {
                    StartTailscaleGui(done);
                    TailscaleUp(done, "after the restart");
                    Thread.Sleep(8000);
                    now = Again(now, done);
                }
            }
            return now;
        }

        // What typing "tailscale" after the Windows key does: start the tray
        // app. On Windows that app is the thing that hands the daemon a
        // profile to connect with, which is why starting it cures NoState
        // when nothing else does. Already running = nothing to do (a second
        // copy just exits anyway).
        //
        // Found the same way the CLI is: next to tailscale.exe, then the
        // service's own ImagePath, then the usual install root, then PATH -
        // so a machine that put Tailscale somewhere else is still covered.
        private bool StartTailscaleGui(List<string> done)
        {
            try
            {
                if (Process.GetProcessesByName("tailscale-ipn").Length > 0) return false;
            }
            catch (Exception) { }

            string exe = TailscaleGuiExe();
            if (exe == null) { done.Add("no Tailscale tray app to start"); return false; }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;    // it is a tray app; no window on your screen
                Process.Start(psi);
                done.Add("started the Tailscale tray app");
                FixCount++;
                return true;
            }
            catch (Exception ex)
            {
                done.Add("could not start the Tailscale tray app: " + First(ex.Message));
                return false;
            }
        }

        private string tailscaleGui;

        private string TailscaleGuiExe()
        {
            if (tailscaleGui != null) return tailscaleGui.Length == 0 ? null : tailscaleGui;

            List<string> tries = new List<string>();
            try
            {
                string cli = TailscaleExe();
                string dir = Path.GetDirectoryName(cli);
                if (!string.IsNullOrEmpty(dir)) tries.Add(Path.Combine(dir, "tailscale-ipn.exe"));
            }
            catch (Exception) { }
            tries.Add(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "Tailscale\\tailscale-ipn.exe"));
            tries.Add(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86), "Tailscale\\tailscale-ipn.exe"));

            foreach (string t in tries)
            {
                try { if (t.Length > 0 && File.Exists(t)) { tailscaleGui = t; return t; } }
                catch (Exception) { }
            }

            // Last resort: whatever a copy that is running right now was
            // started from - covers installs in a place nobody guessed.
            try
            {
                Process[] live = Process.GetProcessesByName("tailscale-ipn");
                if (live.Length > 0)
                {
                    string path = live[0].MainModule.FileName;
                    if (File.Exists(path)) { tailscaleGui = path; return path; }
                }
            }
            catch (Exception) { }

            tailscaleGui = "";
            return null;
        }

        // 'tailscale up' with the stored prefs - connects, changes nothing.
        // Newer CLIs take --timeout so a wedged daemon cannot hang the guard's
        // thread; older ones do not know the flag, so a refusal falls back to
        // the bare command (Exec's own clock still bounds that one).
        private bool TailscaleUp(List<string> done, string why)
        {
            string outp;
            int rc = Exec(TailscaleExe(), "up --timeout=40s", 55000, out outp);
            if (rc != 0) rc = Exec(TailscaleExe(), "up", 45000, out outp);
            if (rc == 0) { done.Add("ran 'tailscale up' " + why); FixCount++; return true; }
            done.Add("'tailscale up' " + why + " failed: " + First(outp));
            return false;
        }

        private NetReport RepairSunshine(NetReport r, List<string> done)
        {
            NetReport now = r;
            if (!now.SunshineService)
            {
                Ensure("SunshineService", "Sunshine", done);
                Thread.Sleep(5000);
                now = Again(now, done);
                sunshineStrikes = 0;
                return now;
            }
            // Running but deaf. Once might be a restart in progress; twice in a
            // row is a hung service.
            sunshineStrikes++;
            if (sunshineStrikes >= 2)
            {
                bool ok = engine.RestartService("SunshineService");
                done.Add(ok ? "restarted Sunshine (it was running but not listening)" : "could not restart Sunshine");
                if (ok) FixCount++;
                sunshineStrikes = 0;
                Thread.Sleep(6000);
                now = Again(now, done);
            }
            else done.Add("Sunshine is running but not listening - giving it one more check before restarting it");
            return now;
        }

        // ---- the remote-desktop watch: user-picked apps that must stay connected

        // What the pages read: one line per app, and whether all of them look
        // the way the calibration says they should.
        public string[] AppLines = new string[0];
        public bool AppsOk = true;

        private readonly Dictionary<string, DateTime> appFixAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> appSaid =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Every [remote.apps] entry, measured against its calibrated state:
        // running, and still listening on the ports it had when calibrated.
        // Anything that drifted gets put back - service restarted, or the exe
        // relaunched - at most once per app per two minutes.
        public void WatchApps(bool repair, bool loud)
        {
            List<RemoteApp> apps = RemoteApps.Load(cfg);
            if (apps.Count == 0) { AppLines = new string[0]; AppsOk = true; return; }

            Dictionary<int, List<int>> listening = RemoteApps.ListeningByPid();
            List<string> lines = new List<string>();
            bool allOk = true;

            foreach (RemoteApp a in apps)
            {
                if (stopping) break;
                string exeSeen;
                List<int> pids = RemoteApps.PidsMatching(a.Name, out exeSeen);
                List<int> ports = new List<int>();
                foreach (int pid in pids)
                {
                    List<int> l;
                    if (!listening.TryGetValue(pid, out l)) continue;
                    foreach (int p in l) if (!ports.Contains(p)) ports.Add(p);
                }

                bool running = pids.Count > 0;
                List<int> missing = new List<int>();
                if (a.Calibrated)
                    foreach (int p in a.Ports) if (!ports.Contains(p)) missing.Add(p);
                bool ok = running && missing.Count == 0;

                string state;
                if (!running)
                    state = "NOT running";
                else if (missing.Count > 0)
                    state = "running, but calibrated port" + (missing.Count == 1 ? " " : "s ")
                        + Ports_(missing) + (missing.Count == 1 ? " is" : " are") + " gone";
                else
                    state = "connected" + (ports.Count > 0 ? ", listening on " + Ports_(ports) : "")
                        + (a.Calibrated ? "" : "  (not calibrated yet)");

                string fixed_ = "";
                if (!ok)
                {
                    allOk = false;
                    DateTime lastFix;
                    bool may = !appFixAt.TryGetValue(a.Name, out lastFix)
                        || (DateTime.Now - lastFix).TotalMinutes >= 2;
                    if (repair && may)
                    {
                        appFixAt[a.Name] = DateTime.Now;
                        fixed_ = FixApp(a, running, pids);
                    }
                }

                // Say it once per change of picture, not once per minute.
                string said;
                bool news = !appSaid.TryGetValue(a.Name, out said) || said != state;
                appSaid[a.Name] = state;
                if (fixed_.Length > 0)
                    log("[guard] " + a.Name + ": " + state + " - " + fixed_);
                else if (news && !ok)
                    log("[guard] " + a.Name + ": " + state);
                else if (news && ok && said != null)
                    log("[guard] " + a.Name + " is back - " + state);

                lines.Add((ok ? "+ " : "! ") + Pad(a.Name) + " " + state
                    + (fixed_.Length > 0 ? "  * " + fixed_ : ""));
            }

            AppLines = lines.ToArray();
            AppsOk = allOk;
            if (loud)
            {
                log("-- remote desktop apps");
                foreach (string l in lines) log("   " + l);
            }
        }

        private static string Pad(string s) { return s.Length >= 14 ? s : s.PadRight(14); }

        private static string Ports_(List<int> ports)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int p in ports)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append(p.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // Put one drifted app back: its service if it has one, else the exe the
        // calibration remembered. "Running but deaf" means kill it first - a
        // relaunch on top of a wedged copy just fails quietly.
        private string FixApp(RemoteApp a, bool running, List<int> pids)
        {
            if (a.Service.Length > 0 && Engine.ServiceExists(a.Service))
            {
                bool ok = running ? engine.RestartService(a.Service) : engine.EnsureService(a.Service, false);
                if (ok) { FixCount++; return (running ? "restarted" : "started") + " the " + a.Service + " service"; }
                return "could not " + (running ? "restart" : "start") + " the " + a.Service + " service";
            }

            if (a.Exe.Length > 0 && File.Exists(a.Exe))
            {
                if (running)
                {
                    foreach (int pid in pids)
                    {
                        try
                        {
                            using (Process p = Process.GetProcessById(pid))
                            {
                                if (engine.IsProtectedProcess(p.ProcessName)) continue;
                                p.Kill();
                                p.WaitForExit(3000);
                            }
                        }
                        catch (Exception) { }
                    }
                    Thread.Sleep(1500);
                }
                try
                {
                    Process.Start(new ProcessStartInfo(a.Exe) { UseShellExecute = true });
                    FixCount++;
                    return (running ? "relaunched " : "launched ") + a.Exe;
                }
                catch (Exception ex)
                {
                    return "could not launch " + a.Exe + " (" + ex.Message.Split('\n')[0] + ")";
                }
            }

            return "no service or calibrated exe to start it with - open 'Remote desktop setup' and Calibrate while it is running";
        }

        // ---- the tools the rungs use

        private void Flush(List<string> done)
        {
            string outp;
            if (Exec("ipconfig.exe", "/flushdns", 15000, out outp) == 0) done.Add("flushed the DNS cache");
        }

        private void Renew(NetReport r, List<string> done)
        {
            string outp;
            int rc = Exec("ipconfig.exe", "/renew", 60000, out outp);
            done.Add(rc == 0 ? "renewed the DHCP lease" : "DHCP renew did not complete (" + First(outp) + ")");
            if (rc == 0) FixCount++;
        }

        // Disable + enable the adapter: the driver reset that fixes the
        // "connected, no traffic" state nothing else reaches. An empty name
        // means every physical wired adapter.
        private void Bounce(string adapter, List<string> done)
        {
            lastBounce = DateTime.Now;
            string cmd = adapter.Length > 0
                ? "Restart-NetAdapter -Name '" + adapter.Replace("'", "''") + "' -Confirm:$false"
                : "Get-NetAdapter -Physical | Where-Object { $_.MediaType -ne 'Native 802.11' } | Restart-NetAdapter -Confirm:$false";
            string outp;
            int rc = Exec("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd + "\"", 60000, out outp);
            done.Add(rc == 0
                ? "bounced the " + (adapter.Length > 0 ? adapter : "wired") + " adapter (disable + enable)"
                : "could not bounce the adapter (" + First(outp) + ")");
            if (rc == 0) FixCount++;
            Thread.Sleep(8000);
        }

        // Best effort, once per start: tell Windows not to put the Wi-Fi adapter
        // to sleep. Two knobs - the power plan's wireless setting and the
        // device's own "allow the computer to turn off this device". Either may
        // not exist on a given driver; silence is the right answer then.
        private void KeepWifiAwake()
        {
            const string sub = "19cbb8fa-5279-412e-9e1b-c3a4a4b51b0e";    // Wireless Adapter Settings
            const string key = "12bbebe6-58d6-4636-95bb-3217ef867c1a";    // Power Saving Mode
            string outp;
            if (Exec("powercfg.exe", "/query SCHEME_CURRENT " + sub + " " + key, 10000, out outp) == 0)
            {
                MatchCollection hex = Regex.Matches(outp, "0x[0-9a-fA-F]{8}");
                bool allMax = hex.Count >= 2;
                foreach (Match m in hex)
                    if (m.Value.ToLowerInvariant() != "0x00000000") allMax = false;
                if (!allMax)
                {
                    bool ok = Exec("powercfg.exe", "/setacvalueindex SCHEME_CURRENT " + sub + " " + key + " 0", 10000, out outp) == 0;
                    ok &= Exec("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT " + sub + " " + key + " 0", 10000, out outp) == 0;
                    ok &= Exec("powercfg.exe", "/setactive SCHEME_CURRENT", 10000, out outp) == 0;
                    if (ok) log("[guard] Wi-Fi power saving set to maximum performance in the power plan (NetworkGuardKeepWifiAwake)");
                }
            }

            NetReport r = Measure();
            if (!r.WifiPresent || r.WifiAdapter.Length == 0) return;
            string name = r.WifiAdapter.Replace("'", "''");
            string q = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"(Get-NetAdapterPowerManagement -Name '"
                + name + "' -ErrorAction Stop).AllowComputerToTurnOffDevice\"";
            if (Exec("powershell.exe", q, 30000, out outp) != 0) return;
            if (outp.IndexOf("Enabled", StringComparison.OrdinalIgnoreCase) < 0) return;
            string set = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Set-NetAdapterPowerManagement -Name '"
                + name + "' -AllowComputerToTurnOffDevice Disabled -NoRestart -ErrorAction Stop\"";
            if (Exec("powershell.exe", set, 30000, out outp) == 0)
                log("[guard] told Windows it may not switch off the " + r.WifiAdapter
                    + " adapter to save power (takes effect at the next adapter restart)");
        }

        private static string First(string s)
        {
            if (s == null) return "";
            foreach (string line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 120 ? t.Substring(0, 120) : t;
            }
            return "";
        }

        // Run a tool without a window, capture what it says, give up after a
        // while. -1 = timed out, -2 = could not start.
        internal static int Exec(string exe, string args, int timeoutMs, out string output)
        {
            output = "";
            StringBuilder sb = new StringBuilder();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        lock (sb) output = sb.ToString();
                        return -1;
                    }
                    p.WaitForExit();
                    lock (sb) output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -2;
            }
        }
    }

    // -------------------------------------------------------- remote apps

    // One app the remote-desktop watch keeps connected, and what "connected"
    // looked like the last time the user pressed Calibrate: the exe to relaunch
    // it with, the service that owns it (if any), and the TCP ports it was
    // listening on. No calibration = "just keep it running".
    internal sealed class RemoteApp
    {
        public string Name = "";
        public string Exe = "";
        public string Service = "";
        public readonly List<int> Ports = new List<int>();
        public bool Calibrated;
    }

    internal static class RemoteApps
    {
        // process name pattern | what it is | the service behind it ("" = none).
        // The pick dialog offers these first, detected ones marked - but any
        // process on the machine can be chosen instead.
        public static readonly string[][] Common = new string[][]
        {
            new string[] { "sunshine",      "Sunshine game-stream host",  "SunshineService" },
            new string[] { "tailscaled",    "Tailscale daemon",           "Tailscale" },
            new string[] { "tailscale-ipn", "Tailscale tray app",         "" },
            new string[] { "parsecd",       "Parsec host",                "Parsec" },
            new string[] { "TeamViewer",    "TeamViewer",                 "TeamViewer" },
            new string[] { "AnyDesk",       "AnyDesk",                    "AnyDesk" },
            new string[] { "RustDesk",      "RustDesk",                   "RustDesk" },
            new string[] { "remoting_host", "Chrome Remote Desktop",      "chromoting" },
            new string[] { "tvnserver",     "TightVNC server",            "tvnserver" },
            new string[] { "winvnc",        "UltraVNC server",            "uvnc_service" },
            new string[] { "vncserver",     "RealVNC server",             "vncserver" },
            new string[] { "nxservice64",   "NoMachine",                  "nxservice" },
            new string[] { "moonlight",     "Moonlight (client side)",    "" },
        };

        public static bool Detected(string process, string service)
        {
            try { if (Process.GetProcessesByName(process).Length > 0) return true; }
            catch (Exception) { }
            return service.Length > 0 && Engine.ServiceExists(service);
        }

        // The service behind a name: the Common table first, then a service
        // that simply shares the process name.
        public static string ServiceFor(string name)
        {
            foreach (string[] c in Common)
                if (Engine.Match(c[0], name) || Engine.Match(name, c[0]))
                    return c[2];
            return Engine.ServiceExists(name) ? name : "";
        }

        // Calibrations live next to the ini in their own file - they are machine
        // state, not configuration, and the windows must not clobber them.
        private static string CalibPath { get { return Path.Combine(App.Dir, "idlemaster.remote"); } }

        // One RemoteApp per enabled [remote.apps] entry, calibration merged in.
        public static List<RemoteApp> Load(Config cfg)
        {
            Dictionary<string, RemoteApp> calib = ReadCalib();
            List<RemoteApp> apps = new List<RemoteApp>();
            foreach (string name in cfg.RemoteApps)
            {
                RemoteApp a;
                if (calib.TryGetValue(name.ToLowerInvariant(), out a))
                    apps.Add(a);
                else
                {
                    a = new RemoteApp();
                    a.Name = name;
                    a.Service = ServiceFor(name);
                    apps.Add(a);
                }
            }
            return apps;
        }

        private static Dictionary<string, RemoteApp> ReadCalib()
        {
            Dictionary<string, RemoteApp> map =
                new Dictionary<string, RemoteApp>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CalibPath)) return map;
                foreach (string line in File.ReadAllLines(CalibPath))
                {
                    string[] p = line.Split('|');
                    if (p.Length < 5 || p[0] != "app") continue;
                    RemoteApp a = new RemoteApp();
                    a.Name = p[1];
                    a.Exe = p[2];
                    a.Service = p[3];
                    foreach (string port in p[4].Split(','))
                    {
                        int n;
                        if (int.TryParse(port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            a.Ports.Add(n);
                    }
                    a.Calibrated = true;
                    if (a.Name.Length > 0) map[a.Name.ToLowerInvariant()] = a;
                }
            }
            catch (Exception) { }
            return map;
        }

        // "This is what connected looks like": snapshot every named app that is
        // running right now - exe, service, listening ports - and remember it.
        // Apps not running are left uncalibrated (and said so), because a
        // snapshot of a dead app would teach the guard nothing.
        public static void Calibrate(List<string> names, Action<string> log)
        {
            Dictionary<string, RemoteApp> calib = ReadCalib();
            Dictionary<int, List<int>> listening = ListeningByPid();
            int done = 0;

            foreach (string name in names)
            {
                string exe;
                List<int> pids = PidsMatching(name, out exe);
                if (pids.Count == 0)
                {
                    log("[calibrate] " + name + " is not running - start and connect it, then calibrate again");
                    continue;
                }
                RemoteApp a = new RemoteApp();
                a.Name = name;
                a.Exe = exe ?? "";
                a.Service = ServiceFor(name);
                foreach (int pid in pids)
                {
                    List<int> l;
                    if (!listening.TryGetValue(pid, out l)) continue;
                    foreach (int p in l) if (!a.Ports.Contains(p)) a.Ports.Add(p);
                }
                a.Ports.Sort();
                a.Calibrated = true;
                calib[name.ToLowerInvariant()] = a;
                done++;
                log("[calibrate] " + name + ": " + pids.Count + " process" + (pids.Count == 1 ? "" : "es")
                    + (a.Service.Length > 0 ? ", service " + a.Service : "")
                    + (a.Ports.Count > 0 ? ", listening on " + PortsText(a.Ports) : ", no listening ports")
                    + (a.Exe.Length > 0 ? "" : ", exe path not readable"));
            }

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# what 'connected' looks like, written by Calibrate - delete to forget");
                foreach (KeyValuePair<string, RemoteApp> kv in calib)
                {
                    RemoteApp a = kv.Value;
                    sb.AppendLine("app|" + a.Name + "|" + a.Exe + "|" + a.Service + "|" + PortsText(a.Ports));
                }
                File.WriteAllText(CalibPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                log("[calibrate] ! could not save the calibration: " + ex.Message.Split('\n')[0]);
                return;
            }
            log("[calibrate] " + done + " app" + (done == 1 ? "" : "s") + " calibrated - the guard now"
                + " holds them to this picture and reconnects whatever drifts.");
        }

        private static string PortsText(List<int> ports)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int p in ports)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append(p.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // Pids whose process name matches the pattern, and the first exe path
        // we are allowed to read (the relaunch handle).
        public static List<int> PidsMatching(string pattern, out string exe)
        {
            exe = "";
            List<int> pids = new List<int>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string name;
                    int pid;
                    try { name = p.ProcessName; pid = p.Id; }
                    catch (Exception) { continue; }
                    if (!Engine.Match(pattern, name)) continue;
                    pids.Add(pid);
                    if (exe.Length == 0)
                    {
                        try { exe = p.MainModule.FileName; }
                        catch (Exception) { }
                    }
                }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            return pids;
        }

        // ---- who is listening on what

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort,
            int family, int tableClass, uint reserved);

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        // Listening TCP ports per owning pid, straight from iphlpapi - the only
        // way to tie a port to a process without shelling out to netstat.
        public static Dictionary<int, List<int>> ListeningByPid()
        {
            Dictionary<int, List<int>> map = new Dictionary<int, List<int>>();
            IntPtr table = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (size <= 0) return map;
                table = Marshal.AllocHGlobal(size);
                if (GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                    return map;
                int n = Marshal.ReadInt32(table);
                for (int i = 0; i < n; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                    long row = table.ToInt64() + 4 + (long)i * 24;
                    int portRaw = Marshal.ReadInt32(new IntPtr(row + 8));
                    int pid = Marshal.ReadInt32(new IntPtr(row + 20));
                    int port = ((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF);
                    List<int> l;
                    if (!map.TryGetValue(pid, out l)) { l = new List<int>(); map[pid] = l; }
                    if (!l.Contains(port)) l.Add(port);
                }
            }
            catch (Exception) { }
            finally
            {
                try { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); } catch (Exception) { }
            }
            return map;
        }
    }
}

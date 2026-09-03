// IDLE MASTER - the VPN handshake.
//
// A VPN service is not like the other services on the lists. Stopping NordVPN
// while its tunnel is connected does not hand you back to Wi-Fi: NordLynx stays
// up, keeps the default route it won on metric (5, against Wi-Fi's 35), and
// every packet leaves through a pipe with nothing on the other end. The link is
// fine, the router answers, and nothing past it does - "link up (Wi-Fi) but the
// internet does not answer and DNS fails", which is the guard describing a
// problem it did not cause and cannot fix from the Wi-Fi side. That is not the
// kill switch. It is the default route pointing at a corpse.
//
// So the tunnel is asked to hang up FIRST, through the app's own CLI, and the
// service is only stopped once the route is back on a physical adapter. If the
// tunnel will not come down, the service stays up: 87 MB is not worth the
// machine's connection, and on a box you only reach over Tailscale it is not
// worth the trip home either.
//
// Nothing here ever touches Tailscale. Its adapter is a tunnel too, and it is
// the way back in - the tables below name their adapters explicitly for exactly
// that reason.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace IdleMaster
{
    // One VPN we know how to hang up politely.
    internal sealed class VpnApp
    {
        public readonly string Service;      // the name that appears on the service lists
        public readonly string Ui;           // the desktop process that owns the CLI, "" if none
        public readonly string[] Exe;        // where that CLI lives; first one on disk wins
        public readonly string Arg;          // what to pass it to disconnect
        public readonly string[] Adapters;   // words that name ITS tunnel adapters, nobody else's

        public VpnApp(string service, string ui, string[] exe, string arg, string[] adapters)
        {
            Service = service; Ui = ui; Exe = exe; Arg = arg; Adapters = adapters;
        }
    }

    internal static class Vpn
    {
        // Adding one is a row here. The adapter words must be specific to the
        // product: "Tunnel" on its own would match Tailscale and this would
        // start hanging up the way home.
        private static readonly VpnApp[] Known = new VpnApp[]
        {
            new VpnApp("nordvpn-service", "NordVPN",
                new string[]
                {
                    @"%ProgramFiles%\NordVPN\NordVPN.exe",
                    @"%ProgramFiles(x86)%\NordVPN\NordVPN.exe",
                },
                "-d",
                new string[] { "NordLynx", "TAP-NordVPN", "OpenVPN Data Channel Offload" }),
        };

        public static VpnApp For(string service)
        {
            foreach (VpnApp v in Known)
                if (string.Equals(v.Service, service, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        // Is this VPN's own tunnel adapter up right now?
        public static bool TunnelUp(VpnApp v)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string d = ni.Description + " " + ni.Name;
                    foreach (string w in v.Adapters)
                        if (d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // The fingerprint of our own mess: the service down but its tunnel still
        // up, i.e. the machine is routing into nothing. The one condition under
        // which the network guard is allowed to start a service back up that you
        // are otherwise meant to start by hand.
        public static bool StrandedTunnel(string service)
        {
            VpnApp v = For(service);
            if (v == null) return false;
            if (Engine.ServiceRunning(service)) return false;
            return TunnelUp(v);
        }

        // Hang the tunnel up and wait for the route to come off it. True when
        // there is nothing left in the way of stopping the service - including
        // the easy case where no tunnel was up to begin with.
        public static bool StandDown(VpnApp v, Action<string> log)
        {
            if (!TunnelUp(v)) return true;

            // The CLI is a thin front end for the desktop app: with the app
            // already running it forwards the argument to that instance and no
            // window appears, which is the only way we are willing to call it.
            // With the app gone there is nothing to forward to and starting it
            // would put a window on a screen nobody is watching - so we stop,
            // and the service keeps the connection it is holding up.
            if (v.Ui.Length > 0 && Process.GetProcessesByName(v.Ui).Length == 0)
            {
                log("   . " + v.Service + ": tunnel up but " + v.Ui + " is not running to hang it up"
                    + " - leaving the service alone (stopping it now would take the network with it)");
                return false;
            }

            string exe = FindExe(v);
            if (exe == null)
            {
                log("   . " + v.Service + ": tunnel up and no CLI found to disconnect it"
                    + " - leaving the service alone");
                return false;
            }

            log("   - " + v.Service + ": tunnel is up - asking " + v.Ui + " to disconnect first");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, v.Arg);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                using (Process p = Process.Start(psi))
                    if (p != null) p.WaitForExit(8000);
            }
            catch (Exception ex)
            {
                log("   ! " + v.Service + ": could not run the disconnect (" + ex.GetType().Name + ")"
                    + " - leaving the service alone");
                return false;
            }

            // The adapter does not vanish the moment the CLI returns; the route
            // comes off it a beat later. Poll rather than guess.
            for (int i = 0; i < 24; i++)
            {
                if (!TunnelUp(v))
                {
                    log("   + " + v.Service + ": tunnel down - the default route is back on the link");
                    return true;
                }
                Thread.Sleep(500);
            }

            log("   ! " + v.Service + ": the tunnel is still up 12 s after the disconnect"
                + " - leaving the service alone rather than stranding the route on it");
            return false;
        }

        // Called at the top of a pass, before anything is killed: the CLI needs
        // the desktop app alive to forward to, and the boost list kills it.
        public static void StandDownFor(Engine engine, IEnumerable<string> services, Action<string> log)
        {
            List<VpnApp> due = new List<VpnApp>();
            foreach (string s in services)
            {
                VpnApp v = For(s);
                if (v == null) continue;
                if (engine != null && engine.IsProtectedService(s)) continue;
                if (!Engine.ServiceRunning(s)) continue;
                if (!TunnelUp(v)) continue;
                if (!due.Contains(v)) due.Add(v);
            }
            if (due.Count == 0) return;

            log("-- hanging up the VPN before anything else");
            foreach (VpnApp v in due) StandDown(v, log);
        }

        // ---- the residue a stopped VPN leaves behind
        //
        // StandDown covers the case where the tunnel is still UP. This is the
        // other one, and it is worse because the machine looks fine: stop
        // NordVPN after it has been connected and the adapter stays up holding
        //
        //   0.0.0.0/1    via the tunnel gateway   metric 25
        //   128.0.0.0/1  via the tunnel gateway   metric 25
        //   0.0.0.0/0    via the real router      metric 35
        //
        // Those two halves cover the whole address space between them and are
        // MORE SPECIFIC than the real default route, so they win no matter what
        // the metric says - and they point at a gateway that died with the
        // tunnel. Its DNS servers (reachable only through that tunnel) are left
        // on the adapter too. Ping still answers, because ICMP to the router is
        // a directly-connected route, so the machine reads as online while DNS
        // and TCP go nowhere. Starting the VPN again "fixes" it by restoring
        // the gateway those routes point at, which is exactly how someone ends
        // up believing they can never turn the VPN off.
        //
        // Everything here is gated on NO service of this VPN running: every one
        // of these is legitimate while it is connected, and the app rebuilds all
        // of it on its next connect. Nothing is removed that is not both on this
        // VPN's own adapter and pointing into a tunnel that is gone.
        public static List<string> ClearResidue(Action<string> log, bool dryRun)
        {
            List<string> did = new List<string>();
            foreach (VpnApp v in Known)
            {
                // The gate. Any of its services alive means it is in charge of
                // its own configuration and this must keep its hands off.
                if (Engine.ServiceRunning(v.Service)) continue;

                List<int> idx = new List<int>();
                List<string> alias = new List<string>();
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        string d = ni.Description + " " + ni.Name;
                        bool mine = false;
                        foreach (string w in v.Adapters)
                            if (d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) mine = true;
                        if (!mine) continue;
                        try
                        {
                            IPInterfaceProperties ip = ni.GetIPProperties();
                            IPv4InterfaceProperties v4 = ip.GetIPv4Properties();
                            if (v4 != null) { idx.Add(v4.Index); alias.Add(ni.Name); }
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                if (idx.Count == 0) continue;

                // The split default, if it is still there. Matched by interface
                // index rather than by name, because netsh reports the index and
                // an adapter alias can contain anything at all.
                string routes;
                NetGuard.Exec("netsh", "interface ipv4 show route", 8000, out routes);
                foreach (string half in new string[] { "0.0.0.0/1", "128.0.0.0/1" })
                {
                    foreach (string raw in routes.Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.IndexOf(half, StringComparison.Ordinal) < 0)
                            continue;

                        int on = IndexOn(line, half, idx);
                        if (on < 0) continue;

                        string what = half + " on " + v.Service + "'s adapter (interface " + on + ")";
                        if (dryRun) { did.Add("WOULD DROP " + what); continue; }

                        string outp;
                        int rc = NetGuard.Exec("netsh",
                            "interface ipv4 delete route prefix=" + half + " interface=" + on,
                            8000, out outp);
                        if (rc == 0) { did.Add("dropped " + what); if (log != null) log("   + dropped the dead " + what); }
                        else if (log != null) log("   ! could not drop " + what + " (netsh rc=" + rc + ")");
                    }
                }

                // The DNS servers, which only ever resolved through the tunnel.
                for (int i = 0; i < idx.Count; i++)
                {
                    bool any = false;
                    try
                    {
                        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.Name != alias[i]) continue;
                            foreach (IPAddress a in ni.GetIPProperties().DnsAddresses)
                                if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                    any = true;
                        }
                    }
                    catch (Exception) { }
                    if (!any) continue;

                    string what2 = "DNS servers on " + alias[i];
                    if (dryRun) { did.Add("WOULD RESET " + what2); continue; }

                    string outp;
                    int rc = NetGuard.Exec("netsh",
                        "interface ipv4 set dnsservers name=\"" + alias[i] + "\" source=dhcp",
                        8000, out outp);
                    if (rc == 0) { did.Add("reset " + what2); if (log != null) log("   + reset the " + what2); }
                    else if (log != null) log("   ! could not reset the " + what2 + " (netsh rc=" + rc + ")");
                }
            }

            if (did.Count > 0 && !dryRun)
            {
                string outp;
                NetGuard.Exec("ipconfig", "/flushdns", 8000, out outp);
                did.Add("flushed the DNS cache");
            }
            return did;
        }

        // The interface index a netsh route line belongs to, if it is one of
        // ours. netsh prints
        //
        //   Publish  Type    Met  Prefix       Idx  Gateway/Interface Name
        //   No       System  256  10.5.0.0/16   24  NordLynx
        //
        // so the index is the token immediately after the prefix. It is found
        // that way rather than by scanning the line for any number we happen to
        // recognise: Met is a bare number too, and a metric of 25 beside an
        // interface index of 25 would have matched the wrong thing and deleted
        // a route on somebody else's adapter. Anchoring on the prefix also
        // survives a localised header, which counting columns would not.
        private static int IndexOn(string line, string prefix, List<int> mine)
        {
            string[] tok = line.Split(new char[] { ' ', '	' },
                               StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < tok.Length; i++)
            {
                if (!string.Equals(tok[i], prefix, StringComparison.Ordinal)) continue;
                int n;
                if (int.TryParse(tok[i + 1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out n)
                    && mine.Contains(n)) return n;
            }
            return -1;
        }

        private static string FindExe(VpnApp v)
        {
            foreach (string raw in v.Exe)
            {
                try
                {
                    string p = Environment.ExpandEnvironmentVariables(raw);
                    if (File.Exists(p)) return p;
                }
                catch (Exception) { }
            }
            // Fall back to wherever the running app actually lives - a portable
            // or relocated install still answers its own CLI.
            if (v.Ui.Length > 0)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(v.Ui))
                    {
                        using (p)
                        {
                            string path = Native.ImagePath(p.Id);
                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}

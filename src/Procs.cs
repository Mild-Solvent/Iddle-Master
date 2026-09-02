// IDLE MASTER - the census behind the task manager.
//
// Process.GetProcesses() knows how much RAM something holds and nothing else:
// asking it for CPU time opens a handle per process, and half of them refuse.
// The kernel already keeps every number Task Manager shows in one table, so
// this asks for that table instead - one NtQuerySystemInformation call returns
// name, pid, parent, threads, handles, CPU time, I/O bytes and working set for
// everything on the machine, including the processes a handle would be denied
// on. Two of those tables a couple of seconds apart are what turns counters
// into rates: CPU % and disk B/s are deltas, never a single reading.
//
// The layout below is the documented x64 SYSTEM_PROCESS_INFORMATION. It is
// read field by field at explicit offsets rather than marshalled as a struct,
// because the offsets are the part that has to be right and this way they are
// visible. If the call ever fails, Fallback() reads the same list the slow way
// and the window carries on without CPU or disk figures.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleMaster
{
    // One process as the kernel just described it. Raw counters only - the
    // rates are worked out by ProcSampler, which is the only thing that knows
    // how long ago the previous table was read.
    internal sealed class ProcInfo
    {
        public int Pid;
        public int Parent;
        public string Name;         // no ".exe" - matches Process.ProcessName
        public long WorkingSet;     // bytes resident
        public long Private;        // bytes committed and not shared
        public long CpuTicks;       // kernel + user, in 100 ns units
        public long IoBytes;        // read + write + other transfer, cumulative
        public int Threads;
        public int Handles;
        public int Session;
        public DateTime Started;
    }

    internal static class ProcQuery
    {
        private const int SystemProcessInformation = 5;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        // Field offsets into one SYSTEM_PROCESS_INFORMATION entry, x64.
        private const int OffNext = 0x00;
        private const int OffThreads = 0x04;
        private const int OffCreate = 0x20;
        private const int OffUser = 0x28;
        private const int OffKernel = 0x30;
        private const int OffNameLen = 0x38;
        private const int OffNameBuf = 0x40;
        private const int OffPid = 0x50;
        private const int OffParent = 0x58;
        private const int OffHandles = 0x60;
        private const int OffSession = 0x64;
        private const int OffWorkingSet = 0x90;
        private const int OffPrivate = 0xC8;
        private const int OffReadBytes = 0xE8;
        private const int OffWriteBytes = 0xF0;
        private const int OffOtherBytes = 0xF8;
        private const int EntryMin = 0x100;

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(
            int infoClass, IntPtr buffer, int length, out int needed);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr h, uint flags, StringBuilder name, ref int size);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // True once the kernel table has answered at least once, so callers can
        // tell "0 %" from "we never got a reading".
        public static bool Detailed { get; private set; }

        public static List<ProcInfo> All()
        {
            List<ProcInfo> got = FromKernel();
            if (got != null && got.Count > 0) { Detailed = true; return got; }
            return Fallback();
        }

        private static List<ProcInfo> FromKernel()
        {
            IntPtr buf = IntPtr.Zero;
            int size = 512 * 1024;
            try
            {
                // The table grows between the size question and the read, so
                // ask with a generous buffer and widen on mismatch rather than
                // trusting the first answer.
                for (int tries = 0; tries < 6; tries++)
                {
                    buf = Marshal.AllocHGlobal(size);
                    int needed;
                    uint rc = NtQuerySystemInformation(
                        SystemProcessInformation, buf, size, out needed);
                    if (rc == 0) return Walk(buf, size);

                    Marshal.FreeHGlobal(buf);
                    buf = IntPtr.Zero;
                    if (rc != STATUS_INFO_LENGTH_MISMATCH) return null;
                    size = Math.Max(needed + 64 * 1024, size * 2);
                }
                return null;
            }
            catch (Exception) { return null; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        private static List<ProcInfo> Walk(IntPtr buf, int size)
        {
            List<ProcInfo> list = new List<ProcInfo>(400);
            long at = 0;
            while (at >= 0 && at + EntryMin <= size)
            {
                IntPtr e = (IntPtr)(buf.ToInt64() + at);

                ProcInfo p = new ProcInfo();
                p.Pid = (int)Marshal.ReadInt64(e, OffPid);
                p.Parent = (int)Marshal.ReadInt64(e, OffParent);
                p.Threads = Marshal.ReadInt32(e, OffThreads);
                p.Handles = Marshal.ReadInt32(e, OffHandles);
                p.Session = Marshal.ReadInt32(e, OffSession);
                p.WorkingSet = Marshal.ReadInt64(e, OffWorkingSet);
                p.Private = Marshal.ReadInt64(e, OffPrivate);
                p.CpuTicks = Marshal.ReadInt64(e, OffUser) + Marshal.ReadInt64(e, OffKernel);
                p.IoBytes = Marshal.ReadInt64(e, OffReadBytes)
                          + Marshal.ReadInt64(e, OffWriteBytes)
                          + Marshal.ReadInt64(e, OffOtherBytes);
                p.Name = ReadName(e);
                try
                {
                    long ft = Marshal.ReadInt64(e, OffCreate);
                    p.Started = ft > 0 ? DateTime.FromFileTime(ft) : DateTime.MinValue;
                }
                catch (Exception) { p.Started = DateTime.MinValue; }

                // Pid 0 is the idle process: it is the machine doing nothing,
                // not something you could ever close.
                if (p.Pid != 0 && p.Name.Length > 0) list.Add(p);

                int next = Marshal.ReadInt32(e, OffNext);
                if (next <= 0) break;
                at += next;
            }
            return list;
        }

        private static string ReadName(IntPtr e)
        {
            try
            {
                int len = (ushort)Marshal.ReadInt16(e, OffNameLen);
                IntPtr p = Marshal.ReadIntPtr(e, OffNameBuf);
                if (p == IntPtr.Zero || len <= 0) return "";
                string s = Marshal.PtrToStringUni(p, len / 2);
                return Strip(s);
            }
            catch (Exception) { return ""; }
        }

        // ProcessName has no extension and every kill list in this app is
        // written that way, so the census speaks the same dialect.
        private static string Strip(string s)
        {
            if (s == null) return "";
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - 4);
            return s;
        }

        // No CPU or I/O here - nothing is pretending otherwise, the columns
        // just read "-" until the kernel table works again.
        private static List<ProcInfo> Fallback()
        {
            List<ProcInfo> list = new List<ProcInfo>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    ProcInfo i = new ProcInfo();
                    i.Pid = p.Id;
                    i.Name = p.ProcessName;
                    i.WorkingSet = p.WorkingSet64;
                    i.Private = p.PrivateMemorySize64;
                    i.Threads = p.Threads.Count;
                    i.CpuTicks = -1;
                    i.IoBytes = -1;
                    list.Add(i);
                }
                catch (Exception) { }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            return list;
        }

        // ---- the exe behind a pid, and what its version resource calls it

        private static readonly Dictionary<int, string> pathCache = new Dictionary<int, string>();
        private static readonly Dictionary<string, string> descCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string PathOf(int pid)
        {
            string hit;
            lock (pathCache) if (pathCache.TryGetValue(pid, out hit)) return hit;

            string path = "";
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h != IntPtr.Zero)
                {
                    int cap = 1024;
                    StringBuilder sb = new StringBuilder(cap);
                    if (QueryFullProcessImageName(h, 0, sb, ref cap)) path = sb.ToString();
                }
            }
            catch (Exception) { }
            finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch (Exception) { } }

            lock (pathCache)
            {
                // A pid is reused eventually; the cache is small and cheap to
                // rebuild, so it is emptied wholesale rather than aged.
                if (pathCache.Count > 4000) pathCache.Clear();
                pathCache[pid] = path;
            }
            return path;
        }

        // "Google Chrome" instead of "chrome" - the description is what makes a
        // list of exe names readable. Keyed by path, not pid: fifty Chrome
        // helpers read the version resource once between them.
        public static string DescriptionOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string hit;
            lock (descCache) if (descCache.TryGetValue(path, out hit)) return hit;

            string desc = "";
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
                desc = v.FileDescription;
                if (string.IsNullOrEmpty(desc)) desc = v.ProductName;
                if (string.IsNullOrEmpty(desc)) desc = v.CompanyName;
                if (desc != null) desc = desc.Trim();
            }
            catch (Exception) { }
            if (desc == null) desc = "";

            lock (descCache) descCache[path] = desc;
            return desc;
        }

        public static string CompanyOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return (FileVersionInfo.GetVersionInfo(path).CompanyName ?? "").Trim(); }
            catch (Exception) { return ""; }
        }
    }

    // Two censuses and the clock between them. Everything the window shows as a
    // rate is worked out here; everything it shows as a total comes straight
    // off the newer census.
    internal sealed class ProcSampler
    {
        private Dictionary<int, long> lastCpu = new Dictionary<int, long>();
        private Dictionary<int, long> lastIo = new Dictionary<int, long>();
        private long lastTicks;                 // Stopwatch ticks of the last census
        private readonly int cores = Math.Max(1, Environment.ProcessorCount);

        // Set by the last Sample(): the whole machine's CPU, summed from the
        // same deltas the rows are drawn from, so the header and the list can
        // never disagree.
        public double TotalCpu;
        public long TotalBytes;
        public double TotalIo;
        public int ProcessCount;
        public bool HaveRates;

        // Reads the counters and nothing else, so the very first Sample() a
        // couple of seconds later already has two readings to subtract. Without
        // it the window opens showing "-" in every CPU and disk cell until the
        // second tick, which reads as broken rather than as "not yet". One
        // syscall, no file reads - safe to call while a window is opening.
        public void Prime()
        {
            try
            {
                List<ProcInfo> now = ProcQuery.All();
                foreach (ProcInfo p in now)
                {
                    if (p.CpuTicks >= 0) lastCpu[p.Pid] = p.CpuTicks;
                    if (p.IoBytes >= 0) lastIo[p.Pid] = p.IoBytes;
                }
                lastTicks = Stopwatch.GetTimestamp();
            }
            catch (Exception) { }
        }

        // byPid = one row per process; otherwise identical names are folded
        // into one row, which is how the rest of this app thinks about apps.
        public List<ProcRow> Sample(Engine tagger, bool byPid)
        {
            List<ProcInfo> now = ProcQuery.All();
            long ticks = Stopwatch.GetTimestamp();
            double seconds = lastTicks == 0
                ? 0 : (ticks - lastTicks) / (double)Stopwatch.Frequency;
            HaveRates = seconds > 0.05 && ProcQuery.Detailed;

            HashSet<int> windows = WindowPids();

            Dictionary<int, long> cpuNow = new Dictionary<int, long>(now.Count);
            Dictionary<int, long> ioNow = new Dictionary<int, long>(now.Count);

            Dictionary<string, ProcRow> byKey = new Dictionary<string, ProcRow>();
            List<ProcRow> rows = new List<ProcRow>(now.Count);

            TotalCpu = 0; TotalBytes = 0; TotalIo = 0; ProcessCount = now.Count;

            foreach (ProcInfo p in now)
            {
                if (p.CpuTicks >= 0) cpuNow[p.Pid] = p.CpuTicks;
                if (p.IoBytes >= 0) ioNow[p.Pid] = p.IoBytes;

                double cpu = 0;
                double io = 0;
                if (HaveRates)
                {
                    long was;
                    if (p.CpuTicks >= 0 && lastCpu.TryGetValue(p.Pid, out was) && p.CpuTicks >= was)
                        cpu = (p.CpuTicks - was) / 1e7 / seconds / cores * 100.0;
                    if (p.IoBytes >= 0 && lastIo.TryGetValue(p.Pid, out was) && p.IoBytes >= was)
                        io = (p.IoBytes - was) / seconds;
                }

                TotalCpu += cpu;
                TotalBytes += p.WorkingSet;
                TotalIo += io;

                string key = byPid
                    ? p.Name.ToLowerInvariant() + "#" + p.Pid.ToString(CultureInfo.InvariantCulture)
                    : p.Name.ToLowerInvariant();

                ProcRow r;
                if (!byKey.TryGetValue(key, out r))
                {
                    r = new ProcRow();
                    r.Name = p.Name;
                    r.RowKey = key;
                    r.Pid = byPid ? p.Pid : 0;
                    r.Started = p.Started;
                    byKey[key] = r;
                    rows.Add(r);
                }

                r.Count++;
                r.Bytes += p.WorkingSet;
                r.PrivateBytes += p.Private;
                r.Cpu += cpu;
                r.Disk += io;
                r.Threads += p.Threads;
                r.Handles += p.Handles;
                r.Pids.Add(p.Pid);
                if (windows.Contains(p.Pid)) r.HasWindow = true;
                if (p.Started != DateTime.MinValue && (r.Started == DateTime.MinValue || p.Started < r.Started))
                    r.Started = p.Started;
            }

            lastCpu = cpuNow;
            lastIo = ioNow;
            lastTicks = ticks;

            if (tagger != null)
                foreach (ProcRow r in rows) r.Tag = tagger.TagOf(r.Name);

            // The description costs a file read, so it is only ever resolved
            // once per exe and cached across every sample after that.
            foreach (ProcRow r in rows)
            {
                if (r.Pids.Count == 0) continue;
                r.Path = ProcQuery.PathOf(r.Pids[0]);
                r.Desc = ProcQuery.DescriptionOf(r.Path);
            }

            rows.Sort(delegate(ProcRow a, ProcRow b) { return b.Bytes.CompareTo(a.Bytes); });
            return rows;
        }

        // A visible, uncloaked top-level window means "the user has this open",
        // which is exactly the thing the boost sweep refuses to close. Same
        // rule as WindowGuard, minus the ancestor walk: this is a label, not a
        // decision, and it says which process actually owns the window.
        private static HashSet<int> WindowPids()
        {
            HashSet<int> set = new HashSet<int>();
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr _)
                {
                    try
                    {
                        if (!Native.IsWindowVisible(h)) return true;
                        int cloaked;
                        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED,
                                out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                        uint pid;
                        Native.GetWindowThreadProcessId(h, out pid);
                        if (pid != 0) set.Add((int)pid);
                    }
                    catch (Exception) { }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return set;
        }

        // ---- shared formatting, so every column speaks the same way

        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return "-";
            if (bytesPerSecond < 1024 * 1024)
                return (bytesPerSecond / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
            return (bytesPerSecond / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
        }

        public static string Percent(double v)
        {
            if (v < 0.05) return "-";
            return v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        }

        public static string Age(DateTime started)
        {
            if (started == DateTime.MinValue) return "-";
            TimeSpan t = DateTime.Now - started;
            if (t.TotalSeconds < 0) return "-";
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + "s";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }
    }
}

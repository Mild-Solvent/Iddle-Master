// IDLE MASTER - the disk mapper. WizTree's trick, done honestly: NTFS keeps
// every file's name, parent and size in one contiguous table (the MFT), so
// instead of asking the filesystem about a million files one at a time, read
// the table itself in a handful of big sequential reads and rebuild the whole
// tree in memory. A full drive in seconds, sizes included.
//
// Raw volume reads need the elevation the app already carries. Anything that
// stops the MFT path - a FAT USB stick, a denied handle, a parse whose total
// does not add up against the drive's own used-space number - drops to a
// parallel FindFirstFile walk that produces the same tree, just slower.
//
// Read-only, all of it: nothing in this file writes, deletes or changes one
// byte on disk. Cleanup.cs decides what the tree MEANS; Ui.cs shows it.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IdleMaster
{
    // ----------------------------------------------------------------- tree

    // One scanned volume, as flat arrays indexed by node id - a million nodes
    // as objects would cost more in headers than in data. Files carry their
    // own size; directories carry the cumulative size of everything below.
    internal sealed class DiskTree
    {
        public const byte FlagDir = 1;
        public const byte FlagReparse = 2;

        public string Root;         // "C:\"
        public bool FromMft;        // true = table read, false = walked
        public int RootNode = -1;
        public int NodeCount;       // valid nodes (slots with Name == null are holes)

        public string[] Name;
        public int[] Parent;
        public long[] Bytes;        // file: own size / dir: subtree total
        public int[] Items;         // dir: files+folders below (recursive)
        public byte[] Flags;
        public int[] FirstChild;
        public int[] NextSibling;

        public bool IsDir(int i) { return (Flags[i] & FlagDir) != 0; }
        public bool IsReparse(int i) { return (Flags[i] & FlagReparse) != 0; }

        public string PathOf(int i)
        {
            if (i == RootNode) return Root;
            List<string> parts = new List<string>();
            int guard = 0;
            while (i >= 0 && i != RootNode && guard++ < 512)
            {
                parts.Add(Name[i]);
                i = Parent[i];
            }
            System.Text.StringBuilder b = new System.Text.StringBuilder(Root);
            for (int k = parts.Count - 1; k >= 0; k--)
            {
                if (b[b.Length - 1] != '\\') b.Append('\\');
                b.Append(parts[k]);
            }
            return b.ToString();
        }

        // Path -> node id, or -1. Case-insensitive, linear per level - this is
        // called dozens of times per scan, not millions.
        public int Lookup(string path)
        {
            if (path == null || RootNode < 0) return -1;
            if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return -1;
            string rest = path.Substring(Root.Length).Trim('\\');
            int at = RootNode;
            if (rest.Length == 0) return at;
            foreach (string seg in rest.Split('\\'))
            {
                int found = -1;
                for (int c = FirstChild[at]; c >= 0; c = NextSibling[c])
                {
                    if (string.Equals(Name[c], seg, StringComparison.OrdinalIgnoreCase))
                    { found = c; break; }
                }
                if (found < 0) return -1;
                at = found;
            }
            return at;
        }

        // Children of one node, biggest first - what the tree view shows.
        public List<int> ChildrenBySize(int i)
        {
            List<int> kids = new List<int>();
            for (int c = FirstChild[i]; c >= 0; c = NextSibling[c]) kids.Add(c);
            long[] size = Bytes;
            kids.Sort(delegate(int a, int b) { return size[b].CompareTo(size[a]); });
            return kids;
        }

        // After a successful delete: unlink the node and subtract its weight
        // up the ancestor chain, so the window keeps telling the truth
        // without a rescan.
        public void Deduct(int i)
        {
            if (i < 0 || i == RootNode) return;
            long gone = Bytes[i];
            int count = (IsDir(i) ? Items[i] : 0) + 1;
            int p = Parent[i];

            if (FirstChild[p] == i) FirstChild[p] = NextSibling[i];
            else
            {
                for (int c = FirstChild[p]; c >= 0; c = NextSibling[c])
                    if (NextSibling[c] == i) { NextSibling[c] = NextSibling[i]; break; }
            }

            int guard = 0;
            while (p >= 0 && guard++ < 512)
            {
                Bytes[p] -= gone;
                Items[p] -= count;
                if (p == RootNode) break;
                p = Parent[p];
            }
            Name[i] = null;
        }

        // Wire up sibling chains and roll sizes and counts up to the root.
        // Shared by both scanners - they only differ in how nodes arrive.
        public void Finish()
        {
            int n = Name.Length;
            FirstChild = new int[n];
            NextSibling = new int[n];
            for (int i = 0; i < n; i++) { FirstChild[i] = -1; NextSibling[i] = -1; }

            // Orphans (parent missing or not a folder) hang off the root so
            // no byte silently vanishes from the totals.
            for (int i = 0; i < n; i++)
            {
                if (Name[i] == null || i == RootNode) continue;
                int p = Parent[i];
                if (p < 0 || p >= n || p == i || Name[p] == null || (Flags[p] & FlagDir) == 0)
                    Parent[i] = RootNode;
            }
            for (int i = 0; i < n; i++)
            {
                if (Name[i] == null || i == RootNode) continue;
                int p = Parent[i];
                NextSibling[i] = FirstChild[p];
                FirstChild[p] = i;
            }

            for (int i = 0; i < n; i++)
            {
                if (Name[i] == null || i == RootNode) continue;
                long own = IsDir(i) ? 0 : Bytes[i];
                int p = Parent[i];
                int guard = 0;
                while (p >= 0 && guard++ < 512)
                {
                    if (own != 0) Bytes[p] += own;
                    Items[p] += 1;      // dirs count too - "n items" means entries
                    if (p == RootNode) break;
                    p = Parent[p];
                }
            }

            NodeCount = 0;
            for (int i = 0; i < n; i++) if (Name[i] != null) NodeCount++;
        }
    }

    // -------------------------------------------------------------- scanner

    internal static class DiskScanner
    {
        // The one entry point: MFT when the volume allows it and the numbers
        // check out, the walker otherwise. 'progress' gets where-are-we lines,
        // 'cancelled' is polled so a Stop press actually stops.
        public static DiskTree ScanDrive(DriveInfo drive, Action<string> progress,
                                         Func<bool> cancelled)
        {
            string root = drive.RootDirectory.FullName;   // "C:\"
            bool ntfs = false;
            try { ntfs = "NTFS".Equals(drive.DriveFormat, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { }

            if (ntfs)
            {
                DiskTree t = null;
                try { t = MftRead(root, progress, cancelled); }
                catch (Exception) { t = null; }
                if (cancelled()) return t;
                if (t != null && Plausible(t, drive, progress)) return t;
                if (t != null) progress(root + " table read did not add up - walking instead");
            }
            return Walk(root, progress, cancelled);
        }

        // The MFT total may legitimately EXCEED used space (sparse, compressed
        // files count logical bytes). Falling far short means the parse missed
        // real data - that is the one result not worth trusting.
        private static bool Plausible(DiskTree t, DriveInfo drive, Action<string> progress)
        {
            try
            {
                if (t.RootNode < 0 || t.NodeCount < 16) return false;
                long used = drive.TotalSize - drive.TotalFreeSpace;
                long seen = t.Bytes[t.RootNode];
                if (used > 1024L * 1024 * 1024 && seen * 2 < used) return false;
                return true;
            }
            catch (Exception) { return true; }
        }

        // ---- path one: read the file table

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_ALL = 0x7;    // read | write | delete
        private const uint OPEN_EXISTING = 3;
        private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access,
            uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code,
            IntPtr inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

        private static DiskTree MftRead(string root, Action<string> progress,
                                        Func<bool> cancelled)
        {
            string volume = @"\\.\" + root.TrimEnd('\\');           // "\\.\C:"
            using (SafeFileHandle h = CreateFile(volume, GENERIC_READ, FILE_SHARE_ALL,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;

                byte[] vd = new byte[128];
                int got;
                if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0,
                    vd, vd.Length, out got, IntPtr.Zero) || got < 96) return null;

                int bytesPerSector = BitConverter.ToInt32(vd, 40);
                int bytesPerCluster = BitConverter.ToInt32(vd, 44);
                int recSize = BitConverter.ToInt32(vd, 48);
                long mftValid = BitConverter.ToInt64(vd, 56);
                long mftStartLcn = BitConverter.ToInt64(vd, 64);
                if (recSize <= 0 || recSize > 8192 || bytesPerCluster <= 0
                    || bytesPerSector <= 0 || mftValid <= 0) return null;

                using (FileStream fs = new FileStream(h, FileAccess.Read, 1, false))
                {
                    // Record 0 describes the MFT itself: its $DATA runs say
                    // where on disk the rest of the table lives. Both sizes
                    // are powers of two, so the larger is aligned for both.
                    int r0 = Math.Max(recSize, bytesPerCluster);
                    byte[] rec0 = new byte[r0];
                    fs.Seek(mftStartLcn * bytesPerCluster, SeekOrigin.Begin);
                    if (fs.Read(rec0, 0, r0) != r0) return null;
                    if (!Fixup(rec0, 0, recSize, bytesPerSector)) return null;

                    List<long[]> runs = MftRuns(rec0, recSize);     // [lcn, clusters]
                    if (runs == null || runs.Count == 0) return null;

                    long totalRecords = mftValid / recSize;
                    if (totalRecords < 16 || totalRecords > 400 * 1000 * 1000) return null;

                    DiskTree t = new DiskTree();
                    t.Root = root;
                    t.FromMft = true;
                    int n = (int)totalRecords;
                    t.Name = new string[n];
                    t.Parent = new int[n];
                    t.Bytes = new long[n];
                    t.Items = new int[n];
                    t.Flags = new byte[n];

                    ParseTable(fs, runs, bytesPerCluster, bytesPerSector, recSize,
                        mftValid, t, progress, cancelled);
                    if (cancelled()) return null;

                    if (t.Name[5] == null) return null;             // record 5 = the root dir
                    t.Name[5] = root;
                    t.RootNode = 5;
                    t.Flags[5] |= DiskTree.FlagDir;

                    progress(root + " table read - " + t.NodeCount.ToString("N0")
                        + " entries, wiring the tree...");
                    t.Finish();
                    return t;
                }
            }
        }

        // The multi-sector fixup: NTFS stamps the last two bytes of each
        // sector with a sequence number and parks the real bytes in the
        // header. Undo that or every record lies at its sector seams.
        private static bool Fixup(byte[] buf, int off, int recSize, int bytesPerSector)
        {
            if (buf[off] != 'F' || buf[off + 1] != 'I' || buf[off + 2] != 'L'
                || buf[off + 3] != 'E') return false;
            int usaOff = BitConverter.ToUInt16(buf, off + 4);
            int usaCount = BitConverter.ToUInt16(buf, off + 6);
            if (usaOff <= 0 || usaCount <= 1
                || usaOff + usaCount * 2 > recSize
                || (usaCount - 1) * bytesPerSector > recSize) return false;

            ushort usn = BitConverter.ToUInt16(buf, off + usaOff);
            for (int k = 1; k < usaCount; k++)
            {
                int end = off + k * bytesPerSector - 2;
                if (BitConverter.ToUInt16(buf, end) != usn) return false;   // torn record
                buf[end] = buf[off + usaOff + k * 2];
                buf[end + 1] = buf[off + usaOff + k * 2 + 1];
            }
            return true;
        }

        // Decode record 0's non-resident $DATA run list -> the disk extents
        // holding the table. An $ATTRIBUTE_LIST here (an MFT fragmented into
        // extension records) is rare enough to punt to the validity check.
        private static List<long[]> MftRuns(byte[] rec, int recSize)
        {
            int at = BitConverter.ToUInt16(rec, 20);
            while (at + 8 <= recSize)
            {
                uint type = BitConverter.ToUInt32(rec, at);
                if (type == 0xFFFFFFFF) break;
                int len = (int)BitConverter.ToUInt32(rec, at + 4);
                if (len <= 0 || at + len > recSize) break;
                if (type == 0x80 && rec[at + 8] == 1)               // non-resident $DATA
                {
                    int mp = BitConverter.ToUInt16(rec, at + 32);
                    return DecodeRuns(rec, at + mp, at + len);
                }
                at += len;
            }
            return null;
        }

        private static List<long[]> DecodeRuns(byte[] buf, int at, int end)
        {
            List<long[]> runs = new List<long[]>();
            long lcn = 0;
            while (at < end)
            {
                byte head = buf[at++];
                if (head == 0) break;
                int lenLen = head & 0xF, offLen = head >> 4;
                if (lenLen == 0 || at + lenLen + offLen > end) break;

                long count = 0;
                for (int i = 0; i < lenLen; i++) count |= (long)buf[at + i] << (8 * i);
                at += lenLen;

                if (offLen == 0) continue;                          // sparse run - no disk home
                long delta = 0;
                for (int i = 0; i < offLen; i++) delta |= (long)buf[at + i] << (8 * i);
                if ((buf[at + offLen - 1] & 0x80) != 0)             // sign-extend
                    delta -= 1L << (8 * offLen);
                at += offLen;

                lcn += delta;
                if (count > 0) runs.Add(new long[] { lcn, count });
            }
            return runs;
        }

        // Stream the whole table through an 8 MB window and parse every record.
        // Sequential volume reads are what disks are best at - this loop IS
        // the "entire C in seconds" part.
        private static void ParseTable(FileStream fs, List<long[]> runs, int bytesPerCluster,
            int bytesPerSector, int recSize, long mftValid, DiskTree t,
            Action<string> progress, Func<bool> cancelled)
        {
            // Volume handles insist on sector-aligned reads, so the window and
            // every read stay cluster-multiples; the valid-length cutoff is
            // applied when parsing, not when reading.
            int window = 8 * 1024 * 1024;
            if (window < bytesPerCluster) window = bytesPerCluster;
            window = window / bytesPerCluster * bytesPerCluster;
            byte[] buf = new byte[window];
            long consumed = 0;          // bytes of the table handled so far
            int recNo = 0;
            long lastTold = 0;

            foreach (long[] run in runs)
            {
                long diskPos = run[0] * bytesPerCluster;
                long left = run[1] * bytesPerCluster;   // always a cluster multiple
                while (left > 0 && consumed < mftValid)
                {
                    if (cancelled()) return;
                    int want = (int)Math.Min(buf.Length, left);

                    fs.Seek(diskPos, SeekOrigin.Begin);
                    int read = 0;
                    while (read < want)
                    {
                        int r = fs.Read(buf, read, want - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    if (read < want) return;                        // truncated - validity check decides

                    long parseTo = Math.Min(read, mftValid - consumed);
                    for (int off = 0; off + recSize <= parseTo; off += recSize, recNo++)
                    {
                        if (recNo >= t.Name.Length) return;
                        ParseRecord(buf, off, recSize, bytesPerSector, recNo, t);
                    }

                    diskPos += read; left -= read; consumed += read;
                    if (consumed - lastTold > 64 * 1024 * 1024)
                    {
                        lastTold = consumed;
                        progress(t.Root + " reading the file table... "
                            + recNo.ToString("N0") + " entries");
                    }
                }
            }
        }

        private static void ParseRecord(byte[] buf, int off, int recSize,
                                        int bytesPerSector, int recNo, DiskTree t)
        {
            if (!Fixup(buf, off, recSize, bytesPerSector)) return;

            int flags = BitConverter.ToUInt16(buf, off + 22);
            if ((flags & 0x1) == 0) return;                         // not in use
            bool isDir = (flags & 0x2) != 0;

            long baseRec = (long)(BitConverter.ToUInt64(buf, off + 32) & 0xFFFFFFFFFFFFUL);
            bool extension = baseRec != 0 && baseRec != recNo;
            int sizeInto = extension ? (int)baseRec : recNo;
            if (sizeInto < 0 || sizeInto >= t.Bytes.Length) return;

            int at = off + BitConverter.ToUInt16(buf, off + 20);
            int end = off + recSize;
            string name = null;
            byte nameSpaceGot = 99;
            long parent = -1;
            long bytes = 0;
            bool reparse = false;

            while (at + 8 <= end)
            {
                uint type = BitConverter.ToUInt32(buf, at);
                if (type == 0xFFFFFFFF) break;
                int alen = (int)BitConverter.ToUInt32(buf, at + 4);
                if (alen <= 0 || at + alen > end) break;

                if (type == 0x30 && buf[at + 8] == 0 && !extension)     // $FILE_NAME, resident
                {
                    int v = at + BitConverter.ToUInt16(buf, at + 20);
                    if (v + 66 <= end)
                    {
                        byte ns = buf[v + 65];                      // 2 = DOS-only alias
                        int nlen = buf[v + 64];
                        if (ns != 2 && ns < nameSpaceGot && v + 66 + nlen * 2 <= end)
                        {
                            name = System.Text.Encoding.Unicode.GetString(buf, v + 66, nlen * 2);
                            parent = (long)(BitConverter.ToUInt64(buf, v) & 0xFFFFFFFFFFFFUL);
                            nameSpaceGot = ns;
                        }
                    }
                }
                else if (type == 0x80 && buf[at + 9] == 0)          // unnamed $DATA - the file body
                {
                    if (buf[at + 8] == 1)                           // non-resident
                    {
                        long lowVcn = BitConverter.ToInt64(buf, at + 16);
                        if (lowVcn == 0 && at + 56 <= end)
                            bytes += BitConverter.ToInt64(buf, at + 48);
                    }
                    else
                        bytes += BitConverter.ToUInt32(buf, at + 16);
                }
                else if (type == 0xC0) reparse = true;              // $REPARSE_POINT

                at += alen;
            }

            if (bytes != 0) t.Bytes[sizeInto] += bytes;
            if (extension) return;                                  // name lives in the base record

            if (name == null || recNo == 5) { if (recNo == 5) t.Flags[5] |= DiskTree.FlagDir; return; }
            t.Name[recNo] = name;
            t.Parent[recNo] = (int)parent;
            byte f = 0;
            if (isDir) f |= DiskTree.FlagDir;
            if (reparse) f |= DiskTree.FlagReparse;
            t.Flags[recNo] = f;
        }

        // ---- path two: walk it

        private const int MAX_PATH_LONG = 4;        // \\?\ prefix handles deep trees

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string fileName, int infoLevel,
            out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr h);

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const int FindExInfoBasic = 1;              // skip short names - faster
        private const int FIND_FIRST_EX_LARGE_FETCH = 2;
        private const uint ATTR_DIRECTORY = 0x10;
        private const uint ATTR_REPARSE = 0x400;

        // One FindFirstFileEx pass per directory hands back name and size
        // together - no second stat per file, which is where the old scanner
        // lost its life. Several workers walk in parallel; the lists are the
        // only shared state and one lock guards them.
        private static DiskTree Walk(string root, Action<string> progress,
                                     Func<bool> cancelled)
        {
            progress(root + " walking the long way (no file table here)...");

            List<string> name = new List<string>(1 << 16);
            List<int> parent = new List<int>(1 << 16);
            List<long> bytes = new List<long>(1 << 16);
            List<byte> flags = new List<byte>(1 << 16);
            Stack<int> pending = new Stack<int>();      // dir node ids to enumerate
            object gate = new object();
            int outstanding = 1;                         // dirs queued or in flight
            int done = 0;
            ManualResetEvent finished = new ManualResetEvent(false);

            name.Add(root); parent.Add(-1); bytes.Add(0); flags.Add(DiskTree.FlagDir);
            pending.Push(0);

            int workers = Math.Min(Environment.ProcessorCount, 8);
            for (int w = 0; w < workers; w++)
            {
                Thread th = new Thread(delegate()
                {
                    WIN32_FIND_DATA fd;
                    List<object[]> batch = new List<object[]>();    // name, size, flags
                    while (true)
                    {
                        int dir = -1;
                        string dirPath = null;
                        lock (gate)
                        {
                            if (pending.Count > 0)
                            {
                                dir = pending.Pop();
                                dirPath = PathOfLists(name, parent, dir, root);
                            }
                        }
                        if (dir < 0)
                        {
                            if (Thread.VolatileRead(ref outstanding) == 0) return;
                            Thread.Sleep(1);
                            continue;
                        }
                        if (cancelled())
                        {
                            if (Interlocked.Decrement(ref outstanding) == 0) finished.Set();
                            continue;
                        }

                        batch.Clear();
                        IntPtr h = FindFirstFileExW(@"\\?\" + dirPath.TrimEnd('\\') + @"\*",
                            FindExInfoBasic, out fd, 0, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
                        if (h != INVALID_HANDLE)
                        {
                            do
                            {
                                string fn = fd.cFileName;
                                if (fn == "." || fn == "..") continue;
                                long sz = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                                byte fl = 0;
                                if ((fd.dwFileAttributes & ATTR_DIRECTORY) != 0)
                                { fl |= DiskTree.FlagDir; sz = 0; }
                                if ((fd.dwFileAttributes & ATTR_REPARSE) != 0)
                                    fl |= DiskTree.FlagReparse;
                                batch.Add(new object[] { fn, sz, fl });
                            } while (FindNextFileW(h, out fd));
                            FindClose(h);
                        }

                        lock (gate)
                        {
                            foreach (object[] e in batch)
                            {
                                byte fl = (byte)e[2];
                                int id = name.Count;
                                name.Add((string)e[0]);
                                parent.Add(dir);
                                bytes.Add((long)e[1]);
                                flags.Add(fl);
                                // never through a junction - that is how a walk
                                // loops forever and counts one file twice
                                if ((fl & DiskTree.FlagDir) != 0
                                    && (fl & DiskTree.FlagReparse) == 0)
                                {
                                    Interlocked.Increment(ref outstanding);
                                    pending.Push(id);
                                }
                            }
                        }

                        int d = Interlocked.Increment(ref done);
                        if ((d & 0x3FF) == 0)
                            progress(root + " walking... " + name.Count.ToString("N0") + " entries");
                        if (Interlocked.Decrement(ref outstanding) == 0) finished.Set();
                    }
                });
                th.IsBackground = true;
                th.Start();
            }

            finished.WaitOne();
            // let the workers notice and drain; the lists stop changing once
            // outstanding hits zero because only workers add to them
            Thread.Sleep(30);

            lock (gate)
            {
                DiskTree t = new DiskTree();
                t.Root = root;
                t.FromMft = false;
                t.RootNode = 0;
                t.Name = name.ToArray();
                t.Parent = parent.ToArray();
                t.Bytes = bytes.ToArray();
                t.Items = new int[name.Count];
                t.Flags = flags.ToArray();
                progress(root + " walked - " + t.Name.Length.ToString("N0")
                    + " entries, wiring the tree...");
                t.Finish();
                return t;
            }
        }

        private static string PathOfLists(List<string> name, List<int> parent,
                                          int i, string root)
        {
            if (i == 0) return root;
            List<string> parts = new List<string>();
            int guard = 0;
            while (i > 0 && guard++ < 512) { parts.Add(name[i]); i = parent[i]; }
            System.Text.StringBuilder b = new System.Text.StringBuilder(root);
            for (int k = parts.Count - 1; k >= 0; k--)
            {
                if (b[b.Length - 1] != '\\') b.Append('\\');
                b.Append(parts[k]);
            }
            return b.ToString();
        }
    }
}

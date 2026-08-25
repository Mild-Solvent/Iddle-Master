// SoloInstance.cs - one Idle Master per machine.
//
//   SoloInstance : a machine-wide slot every long-lived copy (window, --watch,
//                  --guard, --startup) must claim before it builds a tray icon.
//                  A second launch does not claim it - it pokes the first copy,
//                  whose window comes to the front, and bows out. That is how
//                  two tray icons stop happening.
//   LogTail      : when a sentry in ANOTHER process already has the watch, the
//                  window does not start a second one - it says "sentry found"
//                  and follows that sentry's log (the shared idlemaster.log)
//                  live in its own console box.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IdleMaster
{
    internal static class SoloInstance
    {
        // Global\ = the whole machine, every desktop and session - the same
        // scope the sentry's own slot uses.
        private const string SlotName = "Global\\IdleMasterOneInstance";
        private const string ShowEventName = "Global\\IdleMasterShowYourself";

        private static Mutex slot;              // held for the life of the process
        private static Thread watcher;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr h, int cmd);
        private const int SW_RESTORE = 9;

        // Take the machine-wide slot. True = this process is the Idle Master.
        // A mutex rather than a semaphore: if the holder dies without cleaning
        // up, Windows hands the (abandoned) mutex to the next taker instead of
        // leaving the slot wedged.
        public static bool Claim()
        {
            try
            {
                bool fresh;
                slot = new Mutex(false, SlotName, out fresh);
                try { return slot.WaitOne(0); }
                catch (AbandonedMutexException) { return true; }   // previous holder crashed - slot is ours
            }
            catch (Exception) { return true; }   // cannot even ask - do not refuse to run over it
        }

        // Called by the copy that lost: wake the one that is running and get
        // its window on top, then exit. The event is what really shows the
        // window (the running copy calls its own Show); the Win32 calls only
        // hand it the right to take the foreground, which a background process
        // is otherwise not allowed to grab.
        public static void PokeRunning()
        {
            IntPtr h = IntPtr.Zero;
            try { h = FindWindowW(null, "IDLE MASTER"); } catch (Exception) { }
            if (h != IntPtr.Zero)
            {
                try
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    AllowSetForegroundWindow((int)pid);
                }
                catch (Exception) { }
            }

            try
            {
                bool fresh;
                using (EventWaitHandle e = new EventWaitHandle(
                    false, EventResetMode.AutoReset, ShowEventName, out fresh))
                    e.Set();
            }
            catch (Exception) { }

            if (h != IntPtr.Zero)
            {
                try { ShowWindowAsync(h, SW_RESTORE); SetForegroundWindow(h); }
                catch (Exception) { }
            }
        }

        // The running copy parks a thread on the event; every later launch of
        // the exe fires onShow once. The thread is background, so it never
        // holds the process open.
        public static void WatchForShow(Action onShow)
        {
            if (watcher != null) return;
            EventWaitHandle flag;
            try
            {
                bool fresh;
                flag = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out fresh);
            }
            catch (Exception) { return; }

            watcher = new Thread(delegate()
            {
                while (true)
                {
                    try { flag.WaitOne(); }
                    catch (Exception) { return; }
                    try { onShow(); } catch (Exception) { }
                }
            });
            watcher.IsBackground = true;
            watcher.Start();
        }
    }

    // Follows a log file another process is writing, feeding each new complete
    // line to a sink. Lines this process wrote itself (both copies share
    // idlemaster.log) are announced via NoteLocal and skipped, so nothing is
    // printed twice. Built and polled on the UI thread - no locking.
    internal sealed class LogTail
    {
        private readonly string path;
        private readonly Action<string> emit;
        private readonly System.Windows.Forms.Timer timer;
        private readonly List<string> local = new List<string>();
        private long pos;
        private string carry = "";

        public LogTail(string file, Action<string> sink)
        {
            path = file;
            emit = sink;
            try { pos = new FileInfo(path).Length; }
            catch (Exception) { pos = 0; }
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        public void NoteLocal(string line)
        {
            local.Add(line);
            if (local.Count > 200) local.RemoveAt(0);
        }

        public void Stop()
        {
            timer.Stop();
            timer.Dispose();
        }

        private void Poll()
        {
            try
            {
                using (FileStream f = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (f.Length < pos) { pos = 0; carry = ""; }   // log was truncated
                    if (f.Length == pos) return;
                    f.Seek(pos, SeekOrigin.Begin);
                    int want = (int)Math.Min(f.Length - pos, 65536);
                    byte[] buf = new byte[want];
                    int got = f.Read(buf, 0, want);
                    if (got <= 0) return;
                    pos += got;

                    string text = carry + Encoding.UTF8.GetString(buf, 0, got);
                    string[] lines = text.Split('\n');
                    carry = lines[lines.Length - 1];   // no newline yet - keep for next poll
                    for (int i = 0; i < lines.Length - 1; i++)
                        Emit(lines[i].TrimEnd('\r'));
                }
            }
            catch (FileNotFoundException) { pos = 0; carry = ""; }
            catch (Exception) { }
        }

        private void Emit(string line)
        {
            if (line.Length == 0) return;
            // FileLog stamps "yyyy-MM-dd HH:mm:ss " on every line - take it off.
            string body = line;
            if (line.Length > 20 && line[4] == '-' && line[7] == '-'
                && line[10] == ' ' && line[13] == ':' && line[16] == ':')
                body = line.Substring(20);

            int mine = local.IndexOf(body);
            if (mine >= 0) { local.RemoveAt(mine); return; }   // our own write echoed back
            emit(body);
        }
    }
}

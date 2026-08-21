// IDLE MASTER - debloat, the engine side. Windows ships with apps nobody asked
// for, and OEMs pile more on top. This file finds them; DebloatForm (Ui.cs)
// owns the checkboxes. Same split as disk cleanup: this owns the facts.
//
//   The scan asks Windows itself (Get-AppxPackage) what is installed, tags
//   what a known-bloat table recognises, and lists the rest so nothing hides.
//   Known junk arrives pre-checked; everything with a defensible reason to
//   exist arrives unchecked with a note saying what it is.
//
// Removal is NOT the Recycle Bin: an app removed here is gone until you
// reinstall it from the Microsoft Store. That is why the confirm dialog says
// so in as many words, why [debloat.protect] wins over every list, and why
// the Store, winget and the codec packs are protected in CODE - no ini edit
// may shoot the reinstall path on a machine you only reach remotely.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace IdleMaster
{
    // ------------------------------------------------------------- findings

    // One installed Store app. Package is the stable name the protect list and
    // the removal act on; FullNames are the staged versions actually removed.
    internal sealed class DebloatItem
    {
        public string Name;                 // "Solitaire Collection"
        public string Package;              // "Microsoft.MicrosoftSolitaireCollection"
        public readonly List<string> FullNames = new List<string>();
        public string Category;             // the group in the window
        public long Bytes;                  // best effort - WindowsApps guards its folders
        public bool Safe;                   // true = known bloat, arrives pre-checked
        public bool Provisioned;            // Windows re-installs it for every new account
        public string ProvisionedName = ""; // what Remove-AppxProvisionedPackage wants
        public string Note = "";
        public string Where = "";           // install location, for the tooltip

        public string Key { get { return Package.ToLowerInvariant(); } }
    }

    // -------------------------------------------------------------- scanner

    internal sealed class DebloatScanner
    {
        private readonly Config cfg;
        private volatile bool cancel;

        public DebloatScanner(Config c) { cfg = c; }

        public void Cancel() { cancel = true; }
        public bool Cancelled { get { return cancel; } }

        // Never listed, whatever any list says - in code so no ini edit can
        // point the remover at the machine's own lifelines. Losing the Store
        // or winget means losing the way to undo a removal; losing a codec
        // pack means a stream you cannot debug from bed.
        private static readonly string[] NeverTouch = new string[]
        {
            "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp",
            "Microsoft.DesktopAppInstaller", "Microsoft.Winget.*",
            "Microsoft.Services.Store*",
            "Microsoft.WindowsTerminal",
            "Microsoft.SecHealthUI", "Microsoft.Windows.SecHealthUI",
            "Microsoft.MicrosoftEdge*",                 // Windows refuses anyway
            "MicrosoftCorporationII.WinAppRuntime*", "Microsoft.WindowsAppRuntime*",
            "MicrosoftCorporationII.WindowsSubsystemForLinux",  // Docker rides it
            "Microsoft.LanguageExperiencePack*", "Microsoft.Ink.Handwriting*",
            "Microsoft.*VideoExtension*", "Microsoft.*ImageExtension*",
            "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions",
            "Microsoft.UI.Xaml*", "Microsoft.NET.*", "Microsoft.VCLibs*",
            "NVDisplay.*", "NVIDIACorp.NVIDIAControlPanel",     // the display stack
        };

        // The known-bloat table: package pattern, friendly name, category,
        // pre-checked ("1") or review ("0"), note. Order matters only for the
        // first match. Everything installed that is not here and not protected
        // still shows up, under "Everything else".
        private const string Junk = "Preinstalled junk";
        private const string Sponsor = "Sponsored apps";
        private const string Xbox = "Xbox & gaming";
        private const string Extras = "Microsoft extras";
        private const string Rest = "Everything else";

        // Fixed order, "obviously junk" first - the window and the CLI report
        // both read the table top-down from "tick it" to "your call".
        public static readonly string[] CategoryOrder = new string[]
        {
            Junk, Sponsor, Xbox, Extras, Rest,
        };

        public static int Rank(string category)
        {
            for (int i = 0; i < CategoryOrder.Length; i++)
                if (CategoryOrder[i] == category) return i;
            return CategoryOrder.Length;
        }

        private static readonly string[][] Known = new string[][]
        {
            // ---- the classics: shipped by Windows, wanted by nobody
            new string[] { "Microsoft.3DBuilder",           "3D Builder",            Junk, "1", "" },
            new string[] { "Microsoft.Microsoft3DViewer",   "3D Viewer",             Junk, "1", "" },
            new string[] { "Microsoft.Print3D",             "Print 3D",              Junk, "1", "" },
            new string[] { "Microsoft.BingNews",            "News",                  Junk, "1", "" },
            new string[] { "Microsoft.BingWeather",         "Weather",               Junk, "1", "" },
            new string[] { "Microsoft.BingFinance",         "Money",                 Junk, "1", "" },
            new string[] { "Microsoft.BingSports",          "Sports",                Junk, "1", "" },
            new string[] { "Microsoft.BingSearch",          "Bing Search",           Junk, "1", "" },
            new string[] { "Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", Junk, "1", "" },
            new string[] { "Microsoft.MicrosoftOfficeHub",  "Microsoft 365 hub",     Junk, "1", "an ad for Office, not Office itself" },
            new string[] { "Microsoft.GetHelp",             "Get Help",              Junk, "1", "" },
            new string[] { "Microsoft.Getstarted",          "Tips",                  Junk, "1", "" },
            new string[] { "Microsoft.WindowsFeedbackHub",  "Feedback Hub",          Junk, "1", "" },
            new string[] { "Microsoft.People",              "People",                Junk, "1", "" },
            new string[] { "Microsoft.Wallet",              "Wallet",                Junk, "1", "" },
            new string[] { "Microsoft.Messaging",           "Messaging",             Junk, "1", "" },
            new string[] { "Microsoft.OneConnect",          "Mobile Plans",          Junk, "1", "" },
            new string[] { "Microsoft.MixedReality.Portal", "Mixed Reality Portal",  Junk, "1", "" },
            new string[] { "Microsoft.549981C3F5F10",       "Cortana",               Junk, "1", "" },
            new string[] { "Microsoft.Windows.DevHome",     "Dev Home",              Junk, "1", "Microsoft itself has retired it" },
            new string[] { "Microsoft.PowerAutomateDesktop","Power Automate",        Junk, "1", "" },
            new string[] { "MicrosoftTeams",                "Teams (personal)",      Junk, "1", "the consumer chat pinned to Win11, not work Teams" },
            new string[] { "MSTeams",                       "Teams (personal)",      Junk, "1", "the consumer chat pinned to Win11, not work Teams" },
            new string[] { "Microsoft.SkypeApp",            "Skype",                 Junk, "1", "" },
            new string[] { "Clipchamp.Clipchamp",           "Clipchamp",             Junk, "1", "" },
            new string[] { "Microsoft.Copilot",             "Copilot",               Junk, "1", "the rebuild kit runs RemoveWindowsAI; this is the same idea, one app at a time" },
            new string[] { "Microsoft.Windows.Ai.Copilot*", "Copilot provider",      Junk, "1", "" },

            // ---- force-installed third-party. The games are junk by any
            // definition; the media apps might be YOURS, so they only get
            // listed, never pre-checked.
            new string[] { "king.com.*",                    "King games",            Sponsor, "1", "Candy Crush and friends" },
            new string[] { "*CandyCrush*",                  "Candy Crush",           Sponsor, "1", "" },
            new string[] { "*BubbleWitch*",                 "Bubble Witch",          Sponsor, "1", "" },
            new string[] { "*MarchofEmpires*",              "March of Empires",      Sponsor, "1", "" },
            new string[] { "*HiddenCity*",                  "Hidden City",           Sponsor, "1", "" },
            new string[] { "*RoyalRevolt*",                 "Royal Revolt",          Sponsor, "1", "" },
            new string[] { "*.McAfee*",                     "McAfee stub",           Sponsor, "1", "the trial nag, not protection" },
            new string[] { "7EE7776C.LinkedInforWindows",   "LinkedIn",              Sponsor, "1", "the preinstalled stub - the site works without it" },
            new string[] { "SpotifyAB.SpotifyMusic",        "Spotify",               Sponsor, "0", "preinstalled as a stub on many machines - but maybe you use it" },
            new string[] { "*.Netflix",                     "Netflix",               Sponsor, "0", "" },
            new string[] { "Disney.*",                      "Disney+",               Sponsor, "0", "" },
            new string[] { "AmazonVideo.PrimeVideo",        "Prime Video",           Sponsor, "0", "" },
            new string[] { "BytedancePte.Ltd.TikTok",       "TikTok",                Sponsor, "0", "" },
            new string[] { "Facebook.Instagram*",           "Instagram",             Sponsor, "0", "" },
            new string[] { "Facebook.Facebook*",            "Facebook",              Sponsor, "0", "" },
            new string[] { "9E2F88E3.Twitter",              "Twitter",               Sponsor, "0", "" },

            // ---- Xbox. Never pre-checked: Game Bar owns game capture and the
            // identity provider owns Minecraft / Game Pass sign-ins. Removing
            // these on a gaming machine is the NvContainer mistake again.
            new string[] { "Microsoft.GamingApp",           "Xbox",                  Xbox, "0", "the Xbox app itself" },
            new string[] { "Microsoft.XboxApp",             "Xbox Console Companion",Xbox, "0", "the old Xbox app" },
            new string[] { "Microsoft.XboxGamingOverlay",   "Game Bar",              Xbox, "0", "game capture and overlays live here" },
            new string[] { "Microsoft.XboxGameOverlay",     "Game Bar plugin",       Xbox, "0", "" },
            new string[] { "Microsoft.Xbox.TCUI",           "Xbox TCUI",             Xbox, "0", "in-game invites and account UI use it" },
            new string[] { "Microsoft.XboxIdentityProvider","Xbox sign-in",          Xbox, "0", "Minecraft and PC Game Pass logins go through it" },
            new string[] { "Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text", Xbox, "0", "" },
            new string[] { "Microsoft.GamingServices",      "Gaming Services",       Xbox, "0", "PC Game Pass installs need it" },

            // ---- Microsoft apps someone might genuinely use. Listed so the
            // table is complete, never pre-checked, each with its alibi.
            new string[] { "Microsoft.WindowsMaps",         "Maps",                  Extras, "0", "" },
            new string[] { "Microsoft.WindowsAlarms",       "Clock",                 Extras, "0", "alarms and the focus timer" },
            new string[] { "Microsoft.WindowsCamera",       "Camera",                Extras, "0", "" },
            new string[] { "Microsoft.WindowsSoundRecorder","Sound Recorder",        Extras, "0", "" },
            new string[] { "Microsoft.MicrosoftStickyNotes","Sticky Notes",          Extras, "0", "" },
            new string[] { "Microsoft.Windows.Photos",      "Photos",                Extras, "0", "the default image viewer - removing it leaves you with none" },
            new string[] { "Microsoft.ScreenSketch",        "Snipping Tool",         Extras, "0", "genuinely useful" },
            new string[] { "Microsoft.Paint",               "Paint",                 Extras, "0", "" },
            new string[] { "Microsoft.WindowsNotepad",      "Notepad",               Extras, "0", "" },
            new string[] { "Microsoft.WindowsCalculator",   "Calculator",            Extras, "0", "" },
            new string[] { "Microsoft.YourPhone",           "Phone Link",            Extras, "0", "" },
            new string[] { "Microsoft.ZuneMusic",           "Media Player",          Extras, "0", "the default music player" },
            new string[] { "Microsoft.ZuneVideo",           "Movies & TV",           Extras, "0", "" },
            new string[] { "Microsoft.OutlookForWindows",   "Outlook (new)",         Extras, "0", "" },
            new string[] { "Microsoft.Todos",               "To Do",                 Extras, "0", "" },
            new string[] { "Microsoft.Office.OneNote",      "OneNote (Store)",       Extras, "0", "" },
            new string[] { "MicrosoftCorporationII.QuickAssist", "Quick Assist",     Extras, "0", "remote help - one more way back into this machine" },
        };

        // The whole scan. 'progress' gets a short where-are-we line, 'found'
        // gets each finding as it lands - the form marshals both to its thread.
        public List<DebloatItem> Scan(Action<string> progress, Action<DebloatItem> found)
        {
            List<DebloatItem> all = new List<DebloatItem>();

            progress("asking Windows what is installed...");
            List<string[]> raw = ListPackages();
            if (cancel) { progress("scan cancelled"); return all; }

            progress("asking which come back for new accounts...");
            Dictionary<string, string> provisioned = ListProvisioned();

            // -AllUsers can stage several versions of one package; the window
            // wants one row per app, with every staged version behind it.
            Dictionary<string, DebloatItem> byName = new Dictionary<string, DebloatItem>();
            foreach (string[] p in raw)
            {
                if (cancel) break;
                string name = p[0], full = p[1], where = p[2];
                bool nonRemovable = p[3].Equals("True", StringComparison.OrdinalIgnoreCase);
                bool framework = p[4].Equals("True", StringComparison.OrdinalIgnoreCase);
                if (name.Length == 0 || full.Length == 0) continue;
                if (nonRemovable || framework) continue;    // Windows itself says no
                if (OnNeverTouch(name)) continue;
                if (IsProtectedPackage(name)) continue;

                DebloatItem it;
                if (byName.TryGetValue(name.ToLowerInvariant(), out it))
                {
                    it.FullNames.Add(full);
                    continue;
                }

                it = new DebloatItem();
                it.Package = name;
                it.FullNames.Add(full);
                it.Where = where;
                Classify(it);

                string prov;
                if (provisioned.TryGetValue(name.ToLowerInvariant(), out prov))
                {
                    it.Provisioned = true;
                    it.ProvisionedName = prov;
                }
                byName[name.ToLowerInvariant()] = it;
            }

            // Weighing comes last, and is best effort: WindowsApps folders
            // guard themselves even against administrators. A row with "?"
            // for a size is still a row worth deciding about.
            foreach (DebloatItem it in byName.Values)
            {
                if (cancel) break;
                progress("weighing " + it.Name + "...");
                it.Bytes = Weigh(it.Where);
                all.Add(it);
                found(it);
            }

            progress(cancel ? "scan cancelled" : "scan finished");
            return all;
        }

        // The protect list wins - checked before a finding is shown AND again
        // before every removal. Package names, '*' works.
        public bool IsProtectedPackage(string package)
        {
            foreach (string p in cfg.DebloatProtect)
            {
                if (p.Length == 0) continue;
                if (Engine.Match(p, package)) return true;
            }
            return false;
        }

        private static bool OnNeverTouch(string package)
        {
            foreach (string pat in NeverTouch)
                if (Engine.Match(pat, package)) return true;
            return false;
        }

        private static void Classify(DebloatItem it)
        {
            foreach (string[] k in Known)
            {
                if (!Engine.Match(k[0], it.Package)) continue;
                it.Name = k[1];
                it.Category = k[2];
                it.Safe = k[3] == "1";
                it.Note = k[4];
                return;
            }
            // Not in the table: shown so nothing hides, decided by you.
            it.Name = Pretty(it.Package);
            it.Category = Rest;
            it.Safe = false;
        }

        // "5319275A.WhatsAppDesktop" -> "WhatsAppDesktop": the vendor prefix
        // is noise in a column that already shows the full package name.
        private static string Pretty(string package)
        {
            int dot = package.IndexOf('.');
            if (dot > 0 && dot < package.Length - 1) return package.Substring(dot + 1);
            return package;
        }

        private long Weigh(string where)
        {
            if (where == null || where.Length == 0) return 0;
            long total = 0;
            Stack<string> work = new Stack<string>();
            work.Push(where);
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
                        try
                        {
                            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch (Exception) { continue; }
                        work.Push(d);
                    }
                }
                catch (Exception) { }
            }
            return total;
        }

        // ---- asking Windows

        // [ Name, PackageFullName, InstallLocation, NonRemovable, IsFramework ]
        // per staged package. -AllUsers needs elevation (which the app has);
        // the fallback inside the command keeps a bare test run working too.
        private List<string[]> ListPackages()
        {
            string cmd =
                "$p = Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue; "
                + "if (-not $p) { $p = Get-AppxPackage }; "
                + "$p | ForEach-Object { '{0}|{1}|{2}|{3}|{4}' -f "
                + "$_.Name, $_.PackageFullName, $_.InstallLocation, $_.NonRemovable, $_.IsFramework }";

            List<string[]> list = new List<string[]>();
            foreach (string line in DebloatActions.RunPowerShell(cmd, 120000))
            {
                string[] p = line.Split('|');
                if (p.Length < 5) continue;
                list.Add(new string[] { p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim() });
            }
            return list;
        }

        // package name (lowercased) -> provisioned package name. Empty when
        // not elevated or the DISM module is sulking - the scan still works,
        // the rows just cannot say "comes back for new accounts".
        private Dictionary<string, string> ListProvisioned()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            string cmd =
                "Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue "
                + "| ForEach-Object { '{0}|{1}' -f $_.DisplayName, $_.PackageName }";
            foreach (string line in DebloatActions.RunPowerShell(cmd, 120000))
            {
                string[] p = line.Split('|');
                if (p.Length < 2) continue;
                string name = p[0].Trim();
                if (name.Length == 0) continue;
                map[name.ToLowerInvariant()] = p[1].Trim();
            }
            return map;
        }
    }

    // -------------------------------------------------------------- actions

    internal static class DebloatActions
    {
        // Uninstalls one app, every staged version, for every account. When
        // 'deprovision' is on and Windows keeps a machine copy for new
        // accounts, that goes too - otherwise a feature update or a new user
        // quietly brings the app back. The protect list is checked AGAIN here:
        // the list may have changed since the scan filled the table.
        public static bool Remove(DebloatItem it, bool deprovision,
                                  DebloatScanner guard, Action<string> log)
        {
            if (guard.IsProtectedPackage(it.Package))
            {
                log("   . skipped (protected): " + it.Package);
                return false;
            }

            bool removed = false;
            foreach (string full in it.FullNames)
            {
                string cmd =
                    "try { "
                    + "try { Remove-AppxPackage -Package '" + full + "' -AllUsers -ErrorAction Stop } "
                    + "catch { Remove-AppxPackage -Package '" + full + "' -ErrorAction Stop }; "
                    + "exit 0 } catch { $_.Exception.Message; exit 1 }";
                List<string> outp;
                int rc = RunPowerShellRc(cmd, 120000, out outp);
                if (rc == 0) { removed = true; continue; }
                log("   ! could not remove " + it.Name + " (" + full + ")"
                    + (outp.Count > 0 ? ": " + outp[0] : ""));
            }

            if (removed && deprovision && it.Provisioned && it.ProvisionedName.Length > 0)
            {
                string cmd =
                    "try { Remove-AppxProvisionedPackage -Online -PackageName '"
                    + it.ProvisionedName + "' -ErrorAction Stop | Out-Null; exit 0 } "
                    + "catch { $_.Exception.Message; exit 1 }";
                List<string> outp;
                int rc = RunPowerShellRc(cmd, 120000, out outp);
                if (rc != 0)
                    log("   ! removed for current accounts, but the machine copy refused to go"
                        + (outp.Count > 0 ? ": " + outp[0] : ""));
            }

            if (removed)
                log("   x " + it.Name + " (" + it.Package + ") uninstalled"
                    + (deprovision && it.Provisioned ? ", machine copy too" : ""));
            return removed;
        }

        // ---- PowerShell plumbing. The Appx cmdlets have no Win32 twin, so
        // this is the honest way in. -NoProfile keeps someone's profile
        // script out of an elevated process.

        public static List<string> RunPowerShell(string command, int timeoutMs)
        {
            List<string> outp;
            RunPowerShellRc(command, timeoutMs, out outp);
            return outp;
        }

        private static int RunPowerShellRc(string command, int timeoutMs, out List<string> output)
        {
            output = new List<string>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.SystemDirectory,
                    @"WindowsPowerShell\v1.0\powershell.exe");
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \""
                    + command.Replace("\"", "\\\"") + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;

                using (Process p = Process.Start(psi))
                {
                    List<string> lines = output;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null && e.Data.Trim().Length > 0)
                            lock (lines) lines.Add(e.Data.Trim());
                    };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return 1;
                    }
                    p.WaitForExit();    // flush the async readers
                    return p.ExitCode;
                }
            }
            catch (Exception) { return 1; }
        }
    }
}

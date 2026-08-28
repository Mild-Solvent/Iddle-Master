<img src="docs/icon.png" width="72" align="right" alt="Idle Master">

# Idle Master

A single small exe for a machine that has to stay awake so Sunshine can stream it,
but shouldn't be burning 11 GB of RAM doing nothing at 3am.

Two buttons:

| | what it does | what survives |
|---|---|---|
| **BOOST NOW** | kills background clutter so you can actually work | your desktop, browser, everything you opened on purpose |
| **ABSOLUTE IDLE** | strips the machine to Windows vitals | Sunshine, Tailscale, Defender, networking — nothing else |

Plus **Restore desktop**, which puts back exactly what the last run took away.

A boost is only a snapshot, so there is also a **sentry**: after a mode runs it
keeps sweeping the same lists in the background, and RAM stays where you put it
instead of drifting back up. See [Keeping it clean](#keeping-it-clean).

And because a machine you only reach over Sunshine-through-Tailscale is a brick
the moment its Wi-Fi drops, there is a **network guard**: whenever Idle Master is
running it checks the link, the internet, Tailscale and Sunshine every minute and
puts back whatever fell over — reconnecting Wi-Fi to a known network included.
See [Network guard](#network-guard).

## Install

Download **`IdleMasterSetup.exe`** from
[Releases](https://github.com/Mild-Solvent/Iddle-Master/releases) and run it.
One file, ~330 KB, the app is carried inside it.

It installs to `%LOCALAPPDATA%\Programs\IdleMaster` — your own profile, so
installing needs no administrator (the app elevates itself when it runs, which is
where admin is actually needed). It makes a Start Menu shortcut, registers in
Windows' *Installed apps* so you can uninstall normally, and can register the
logon task for you.

**Updating is the same file.** Run a newer setup and it replaces the exe in
place, keeping `idlemaster.ini` exactly as you left it.

Or let the app do it. It **asks GitHub on its own** — a minute after start, then
every `UpdateCheckHours` (6; 0 turns it off) — and if there is something newer
you get a tray toast, the button turns into **Update to vX**, and the tray menu
grows an *Update now*. **One click** downloads that release's installer, hands
over to it silently, pointed at the folder this copy is running from (so a
portable copy updates where it stands), and Idle Master comes back on its own
with your `idlemaster.ini` untouched. **Check for updates** still asks right now
and tells you where you stand — *v0.6.1 is the newest*, or *v0.7.0 is available*.
Nothing is downloaded until you click.

Silent, for scripts:

```
IdleMasterSetup.exe --silent            install or update, no window
IdleMasterSetup.exe --silent --dir D:\Apps\IdleMaster
IdleMasterSetup.exe --silent --desktop  ...and a desktop shortcut too
IdleMasterSetup.exe --silent --relaunch ...and start Idle Master when done (the one-click update uses this)
IdleMasterSetup.exe --uninstall         removes it, keeps your config
```

## Build

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the .NET Framework compiler that ships inside Windows. No SDK,
no NuGet, no internet. Outputs `dist\IdleMasterRebuild.exe` (goes inside every
backup kit), `dist\IdleMaster.exe` (the app, with the rebuild exe and the icon
embedded) and `dist\IdleMasterSetup.exe` (the app embedded in the installer).

## Run

Right-click → **Run as administrator** (it asks anyway — stopping services needs it).

```bash
dist\IdleMaster.exe --report
```

Command line, for scheduling:

```
IdleMaster.exe               open the window
IdleMaster.exe --boost       boost mode, no UI
IdleMaster.exe --idle        absolute idle, no UI
IdleMaster.exe --restore     undo (also stops the sentry)
IdleMaster.exe --report      print what's eating RAM and what each mode would do
IdleMaster.exe --boost --watch   boost, then keep hunting until told to stop
IdleMaster.exe --watch       take up the watch for whichever mode ran last
IdleMaster.exe --unwatch     stop the sentry
IdleMaster.exe --debloat-report  list the preinstalled Store apps and which are known bloat
IdleMaster.exe --network      check link + internet + Tailscale + Sunshine now, fix what is down (exit 0 = all up)
IdleMaster.exe --guard       sit in the tray running only the network guard
IdleMaster.exe --installtask run the sentry at every logon (--removetask undoes it)
IdleMaster.exe --installtask --guard   ...or only the network guard at logon
```

Run `--report` first. It tags every process and service with the mode that would
close it, so you see the plan before anything dies.

## What it found on this machine

Measured, not guessed — 15.9 GB total, 11.4 GB in use at the time of the scan:

| | RAM | closed by |
|---|---|---|
| Brave (28 processes) | 5.0 GB | idle |
| Claude desktop (17 processes) | 3.0 GB | idle |
| NordVPN UI + service + threat protection + updater | 760 MB | UI in boost, services in idle |
| msedgewebview2 (widget/tray hosts, 17 processes) | 540 MB | boost |
| Razer Cortex + Central + CefSharp + GameManagerService3 | 305 MB | boost |
| Windows shell (explorer, start menu, search host, text input) | 490 MB | idle (recycled, not left dead) |
| NVIDIA overlay / ShadowPlay | 110 MB | boost |
| Lenovo Vantage + add-ins | 90 MB | boost |
| Tailscale tray icon (the daemon is untouched) | 68 MB | idle |
| Windows Search indexer | 60 MB | boost |

Rough expectation: **boost frees ~1.5–2 GB** without touching your work,
**absolute idle lands around 1.5–2 GB total in use** — that's Windows kernel,
Defender, Sunshine and Tailscale, and there is no honest way below that.

## Keeping it clean

Killing 2 GB once is easy; the problem is twenty minutes later, when WebView2 has
respawned for a tray icon, `WSearch` trigger-started itself and Razer's launcher
service came back. The **sentry** is a background thread that re-applies the
*same lists* on a timer:

| every | it does |
|---|---|
| 20 s | sweeps processes against the active mode's kill lists |
| 5 min | re-stops services from those lists that restarted themselves |
| 10 min | trims working sets and purges the standby list again |
| 5 min | checks Sunshine + Tailscale, restarts them if they died |

It enforces **whichever mode ran last** — boost after Boost Now, the full idle
list after Absolute Idle — and it stands down the instant you hit Restore. In the
window it's the *Sentry: keep hunting* checkbox, with a live count of what it
has reaped; on the command line it's `--watch` / `--unwatch`.

### It asks before killing anything you started

On its first sweep the sentry takes a census. Everything running *then* that
matches a list is the junk the mode was aimed at, and dies without a word.
Anything that appears **after** that is something you deliberately started, so it
gets a toast in the corner instead:

> **Docker Desktop** just started — 4 processes, 512 MB.
> It is on your BOOST kill list, so the sentry is about to close it.
> `Keep it` · `Always keep` · `Trash once` · `Always trash`

The toast shows the app's own icon, the description and company from the exe, and
the path — so *Update.exe, 300 MB* reads as *Discord Inc.* before you decide.

- **Keep it** — left alone for 30 minutes, then asked again.
- **Always keep** — written into `[protect]`, remembered forever.
- **Trash once** — closed now, nothing written; if it comes back you are asked again.
- **Always trash** — closed now and every time it returns (an unlisted name goes
  into `[boost.kill]`, so the lists learn from what you actually do).

No answer in 47 seconds means whatever `AskTimeoutAction` says — *trash once* by
default, or `keep` / `always` if you prefer; the toast's last line tells you which.

Set `AskAboveMb` and it also asks about newcomers that are on no list at all but
bigger than that (250 MB by default). Idle mode never asks: nobody is there.

### Three brakes

Because "kill it every 20 seconds forever" is a good way to make a machine
miserable, three more things hold it back:

**It stands down when you come back.** Absolute Idle is built on *nobody is
watching*. Idle mode spares neither the window in front nor the ones you have
open, and Overclocked spares nothing at all — right for an empty chair, plainly
wrong for an occupied one. A watch left enforcing idle rules while you are at the
keyboard reaps every app you open inside 20 seconds, until the respawn backoff
below gives up on it and it finally sticks: the app appears to need launching
five times before it lives. That is not a mode, that's a fight.

So the watch follows the room. Any keyboard or mouse input and it drops to
**boost rules** — front window spared, open windows spared, newcomers asked about
— with Overclocked suspended, and says so in the log. Go quiet for
`SentryAwaySeconds` (120) and the idle rules come back on their own. It never
ends the watch and never restarts a service: that is still Restore's job.
Streamed input counts as present, so a human on the far end of Sunshine is a
human. `SentryStandDown=0` restores the old fight-you-forever behaviour.

**Respawn backoff.** If one process name comes back `SentryRespawnLimit` times
(6 by default), the sentry stops fighting it for 30 minutes and writes a line
saying so. Something on the machine clearly wants that process alive, and an
endless kill/respawn loop burns more CPU than the process ever cost you in RAM.
After the backoff it puts the name back on the list and tries again. It is a
last resort, not a normal outcome: the usual reason a name used to hit the limit
was Idle Master killing an Electron app's helpers before its main process, and
[that no longer happens](#the-safety-story).

**Foreground guard.** In boost mode it never kills the process that owns the
window you're currently looking at — if you deliberately open Steam, it survives
as long as it's in front. Idle mode ignores this, because nobody is there.

Everything it does goes to `idlemaster.log`, and only when it actually acts:
a quiet night leaves no lines at all.

Two things worth knowing before you leave it on:

- The sentry **is** the thing that stops you re-opening apps on the kill list.
  If you boost and then want Discord back, uncheck the box (or `--unwatch`)
  first, otherwise it dies within 20 seconds.
- Only one sentry runs at a time — a second one refuses the watch and says so.
  Closing the window stops the thread but leaves the watch *armed*, so opening
  Idle Master again picks it back up where it left off.

### Or just run it again, on a clock

The sentry sweeps; the **repeat loop** re-runs the whole thing. It rides on the
BOOST NOW button itself: the **refresh arrow at its left edge**, with the
interval in the middle of the ring. Click the arrow — not the rest of the
button, which still boosts — and a small menu opens: on/off, a spinner for any
number of minutes, and the usual 5 / 10 / 15 / 30 / 60 / 120. Pick one and the
window clicks that button for you on that interval — the same lists, the same
asking, the sentry re-armed after each pass.

The ring is the countdown: it fills as the next boost comes round, and hovering
the arrow says how long is left (*next boost in 4:32*). A run that is still
going just pushes the clock: the loop never stacks two boosts.

It belongs to the window, so closing Idle Master ends it, and the interval is
remembered in `RepeatBoostMinutes` — set it there (or in Settings > Advanced) and
the loop arms itself the next time the app opens, first run one interval away.
The tray menu carries the same toggle for when the window is hidden.

That is a different knob from `SentryFullPassMinutes` below: the sentry's full
pass happens inside the watch, quietly; the repeat loop is the button being
pressed again.

**Sentry lists & timers** (button on the sentry row, tray menu) is the sentry's
own page: the active mode's kill list and service list as checklists — add from
what is running, type a name, remove, untick to comment out — plus every timer,
and two *boost again* knobs: `SentryFullPassMinutes` repeats a *whole* pass
(services, trim, purge, stream check) every N minutes on top of the 20-second
sweep, and `BoostWhenFreeBelowMb` does one the moment free RAM drops under a line.

`--installtask` registers a logon scheduled task (`IdleMaster Sentry`, highest
privileges) so the watch survives a reboot. It is off unless you ask for it, and
`--removetask` deletes it.

## Network guard

The sentry guards the RAM; the network guard guards the way back in. A headless
laptop that is only ever reached over Sunshine-through-Tailscale is useless the
moment its Wi-Fi drops, its DHCP lease goes stale, `tailscaled` stops, or Sunshine
sits alive without listening — and nothing on the machine will notice except you,
from somewhere else, too late.

So whenever Idle Master is running — window, tray, `--watch` or `--guard` — the
guard checks, every `NetworkGuardSeconds` (60), in this order, and repairs the
first thing that is wrong:

| it checks | healthy means | if not, it |
|---|---|---|
| **link** | a real adapter is up with a default gateway | restarts WLAN AutoConfig / DHCP / DNS if they died; switches the Wi-Fi radio back on if software turned it off; turns Wi-Fi auto-connect back on; **reconnects to a known network** — `[network.wifi]` first, then every other saved profile (in range first if you let it scan); renews DHCP; re-enables a disabled adapter; as a last resort bounces the adapter |
| **internet** | `controlplane.tailscale.com` answers on 443 (or 1.1.1.1 / 8.8.8.8 do), or Tailscale itself says it is online | flushes DNS, renews the lease; restarts network services a mode stopped (NordVPN's service with its kill switch armed is the famous one); rebuilds the Wi-Fi connection; bounces the adapter |
| **Tailscale** | service running, `BackendState: Running`, an address, adapter up | restarts the service; runs `tailscale up` if it was Stopped; a needed login is said loudly once — nothing can type that for you |
| **Sunshine** | service running and listening on its ports | restarts the service; up-but-deaf twice in a row gets restarted too |

Each check that still finds trouble reaches one rung higher up that ladder. After
six in a row it keeps *measuring* every minute but only *repairs* every fifth
one, so a router that is genuinely off does not get the adapter bounced all night.
The moment a check comes back clean it says how long the outage was and what
fixed it, and goes quiet again.

**Two rules it will not break.** It never drops a link that Tailscale is still
up through — an internet probe can be firewalled; your session cannot be argued
with. And it never rebuilds a working link more than once in 15 minutes, or
bounces an adapter more than once in 10.

`NetworkGuardKeepWifiAwake` (on) also tells the power plan and the adapter not to
switch the Wi-Fi off to save energy — the usual reason a headless laptop falls
off the network at 3am. Best effort; silent when the driver has no such knob.

It uses the Windows WLAN API directly, not `netsh`, so it works in any language.
It can only join networks Windows already has a profile for — connect once by
hand and it can reconnect forever; it cannot type a password. By default it
**never scans** and never asks which network it is on: Windows 11 counts both as
*location* and would prompt you to allow it — so reconnects go by `[network.wifi]`
order, then Windows' own saved order, and the status line just says *Wi-Fi*.
`NetworkGuardScan=1` turns the scan on (in-range networks first, strongest first,
the name shown) and Windows asks for location permission once.

Quiet while all is well. Every fix is one line in `idlemaster.log`, every outage
one *trouble* line and one *back after* line (all prefixed `[guard]`). In the window it is the **Network
guard** button, which turns red while the guard is fighting something and opens
its own page: the four-line picture of what it last saw, *Check now*, its
switches (`NetworkGuard` is the whole feature — the check inside a run *and* the
standing watch), the check interval, and the `[network.wifi]` list with a picker
of the saved networks. The tray has *Network guard...* and *Check the connection
now*. Closing the window while it guards hides to the tray. `--network` does one
check-and-fix from a script, `--guard` sits in the tray running only the guard,
and `--installtask --guard` makes that happen at every logon.

## Disk cleanup

RAM comes back on its own; disk junk sits there until somebody weighs it. **Disk
cleanup** reads each NTFS drive's own file table (the MFT) raw — the WizTree
trick, and the app's elevation is exactly what raw volume reads need — so the
whole of `C:` is sized in seconds, then sorted into a tree you can judge:

- **Known junk spots** — temp folders, browser caches, crash dumps, update
  leftovers, thumbnail caches. Pre-ticked, because the answer is known.
- **Old installers**, **possible leftovers** (folders no installed program
  claims), **big folders** — listed, never pre-ticked. Your call.
- **Disk map** — the whole drive at the bottom, browsable, biggest first.

Every row opens into what is actually *inside* it, biggest first, each line with
its size, its share-of-parent bar, and an **owner** column that names the app
(or the person, or Windows) the bytes belong to — so you can tell somebody's
droppings from a load-bearing part of an app before you tick it.

Filters up top: minimum size, safe/review, type-to-filter by name. **ticked
only** flips the tree into a flat review list of exactly what Clean will take,
full paths shown — untick a row there and it leaves the plan. **Clean checked**
sends everything to the Recycle Bin — nested ticks collapse into their parent,
one shell call, one undo — and the tree updates without a rescan. The bin row
itself is the one *permanent* action, says so in as many words, and goes last.

`[cleanup.protect]` wins over everything, and the deep guardrails are in code:
the map refuses to tick `\Windows`, a whole drive, a whole profile, the pagefile
and its friends, however hard you click. A drive that is not NTFS — or a table
read whose total does not add up against the drive's own used-space number —
falls back to a parallel walk that builds the same tree, just slower.

## Debloat

The sentry fights the junk that runs; **Debloat** removes the junk that is merely
*installed* — the Store apps Windows and the OEM shipped that you never asked
for. Same shape as Disk cleanup: **Scan** fills a table, you tick, **Remove
checked** acts. Nothing is removed until you press the button.

The scan asks Windows itself (`Get-AppxPackage`) and shows *every* removable
app, so nothing hides:

- **Preinstalled junk** — News, Weather, Solitaire, Get Help, Tips, Feedback
  Hub, Cortana, Copilot, consumer Teams, the Office ad... arrives **pre-ticked**.
- **Sponsored apps** — the force-installed third-party stuff. Candy Crush and
  the McAfee nag arrive ticked; Spotify, Netflix and friends are only *listed*,
  because they might be yours.
- **Xbox & gaming** — never pre-ticked: Game Bar owns game capture, and the
  Xbox identity provider owns Minecraft / Game Pass sign-ins.
- **Microsoft extras** — Photos, Snipping Tool, Calculator, Phone Link...
  listed with a note saying what each one is, decided by you.
- **Everything else** — whatever else is installed and removable.

Two things to know before pressing the button:

- **This is not the Recycle Bin.** A removed app is gone until you reinstall it
  from the Microsoft Store; the confirm dialog says so in as many words. That is
  also why the Store, winget, the Terminal, WSL, Edge and the codec packs are
  protected **in code** — no ini edit can shoot the reinstall path on a machine
  you only reach remotely.
- **Also drop the machine copy** (on by default) removes the *provisioned* copy
  too, so a feature update or a new account does not quietly bring the app
  back. Rows that would come back are marked in their tooltip.

Right-click offers *Remove just this one*, *Copy package name*, and *Never
suggest this app*, which writes the package into `[debloat.protect]` — the same
protect-list-wins design everything else here uses. `--debloat-report` prints
the same table from a script and never removes anything.

## Backup kit

For the day you reinstall Windows. **Backup kit** (main window, tray menu) writes
**one zip** that can put a fresh install back the way this one is:

- **Apps** — it asks winget what is installed; everything with a winget or Store
  package is listed with its id and pre-ticked (Windows' own bits start off).
  Things winget cannot reinstall are listed greyed, so you know what to fetch by
  hand.
- **Files and folders** — Documents, Desktop, Pictures and `.ssh` start ticked,
  Downloads/Videos/Music are listed but off, and *Add folder / Add file* take
  anything else. Sizes count in the background; junctions are skipped.
- **On the new machine, also:** install Idle Master with this `idlemaster.ini`,
  run [zoicware/RemoveWindowsAI](https://github.com/zoicware/RemoveWindowsAI)
  (Copilot, Recall, the lot — non-interactive, all options), apply a
  [Chris Titus WinUtil](https://christitus.com/win) preset without clicking
  (Standard / Minimal / Advanced), and leave WinUtil open at the end for anything
  more you want from it.

The zip holds `IdleMasterRebuild.exe`, a plain-text `rebuild.ini` with what you
picked, an `apps.json` in `winget import` format, `files\`, your `idlemaster.ini`,
and — when the app finds its own installer next to it — `IdleMasterSetup.exe` of
the same version. Building a kit changes nothing on this machine.

On the fresh Windows: extract the *whole* zip, run `IdleMasterRebuild.exe` (asks
for administrator), tick, **Rebuild**, read the log. Files go back where they
were (the old profile path is remapped to the new account) or into
`Desktop\Restored files`; existing files are left alone unless you say
otherwise. Apps go through `winget install` one at a time, so one failure costs
one app. Without a bundled installer, the Idle Master step downloads the latest
release. `--auto` starts with whatever the ini says ticked and no click.

## The safety story

This is a tool that kills processes on a machine you can only reach remotely, so
most of the code is about not stranding you.

**Protected list wins over everything.** `[protect]` and `[protect.services]` in
the ini are checked before any kill or stop, even if something is also listed in
a kill list. Sunshine, tailscaled, Defender, lsass, the audio stack, the NVIDIA
*display* container, Docker and its WSL/Hyper-V backend, and the exe itself are
all in there. Docker is protected on purpose: containers you left running matter
more than the RAM the backend costs. Take it out of `[protect]` if you disagree.

**`[protect.tree]` spares an app *and* its helpers.** A name-matched list cannot
save a WebView2 or Electron app: WhatsApp, Discord and their family do the real
work in child processes called `msedgewebview2`, and that name is on the boost
list because most of the time it genuinely is junk. Spare only the parent and you
get the worst outcome — a tray icon still sitting there, with nothing behind it,
quietly not receiving your messages. Name the app in `[protect.tree]` (it ships
with `WhatsApp*`) and everything underneath it is off limits too. It is a
separate list from `[protect]` on purpose: that one holds `svchost`, `services`
and `explorer`, and every process on the machine descends from one of those, so
walking ancestors against it would spare the entire session.

**`[protect.path]` tells apart two programs with the same name.** Every other
list matches a process *name*, and a name is not always an app. `claude` is the
3 GB Electron desktop app, which belongs on the idle list — and it is also the
Claude Code CLI, which is the work you left running in a terminal. Same name,
same kill list, and the list only ever meant the first one. Put a path here
(full paths, `*` works, a path covers everything under it — the same spelling
`[cleanup.protect]` uses) and any process launched from there is off limits
whatever it is called. It ships with `*\claude-code\*`; `node`, `python` and
`java` are the same shape of problem if you ever list them.

**Families are ended from the root down.** A Chromium or Electron app is one
main process and a dozen helpers that *all carry the same name*, and the kernel
does not list them parent-first — on this machine Claude's main process sat
sixth of seventeen. Kill a helper first and the main process notices the hole
and spawns a replacement, which is reaped on the next sweep, until the respawn
backoff below decides something wants it alive and gives up for half an hour —
leaving the app running with half its helpers dead. Idle Master now sorts a
family by depth and ends the root first; Chromium keeps its children in a job
object, so they go down with it and there is nothing left to respawn anything.

**Close first, terminate second.** An app with a window on screen is asked to
shut itself down and given `CloseGraceMs` (3 s by default) to do it; only what is
still standing afterwards is terminated. This is what stops Electron apps coming
back broken the next morning: terminated outright they never release the
singleton lock in their own profile, and the next launch refuses to start and
says another copy is already running. Set `CloseGraceMs=0` for the old
terminate-outright behaviour. The Windows shell is exempt — Absolute Idle
*terminates* explorer on purpose, because asking it politely is what leaves a
taskbar whose Start button answers nothing.

**Network guard.** Before finishing (and mid-run, in idle mode) it verifies that
`SunshineService` and `Tailscale` are running, that something is listening on a
Sunshine port, and that the Tailscale adapter is up. If a service died as
collateral damage, it restarts it and says so loudly in the log. That is the
check *inside* a run; the same [network guard](#network-guard) also stands
there the whole time Idle Master is running.

**Everything is reversible.** Services are *stopped*, never disabled, so a reboot
returns the machine to normal on its own. Each run writes `idlemaster.state` with
the exact list of what it stopped, and Restore walks that list backwards. The
sentry appends to the same file, so anything it re-stops later still gets undone.

**Restore disarms the hunter first.** Before it restarts a single service it
signals the sentry to stand down — otherwise the sentry would shoot everything
Restore just brought back, on its next sweep.

**The shell is recycled, not decapitated.** Absolute idle terminates
`explorer.exe` — and Winlogon rebuilds the session on its own, the way it always
does when the shell dies. The desktop, taskbar and Start menu come back within a
few seconds, fresh, and a few hundred MB lighter than the shell that had been
running all day. That is the point: the shell is the biggest thing on the box
that cannot be trimmed, only replaced, so absolute idle is a restart of the
session without a restart of the machine.

The screen flashes black while that happens, and open File Explorer windows do
not survive it. Nothing else changes — your apps are already closed by then, and
Sunshine and Tailscale are untouched.

Two things make this safe rather than scary, and both are in code, not in a list:

- The whole shell family — `explorer`, `sihost`, `StartMenuExperienceHost`,
  `ShellExperienceHost`, `ShellHost`, `SearchHost`, `TextInputHost` — is
  protected from every sweep. The sentry cannot hunt the rebuild Winlogon just
  started, and killing those hosts one by one is what used to leave a taskbar
  whose Start button answered nothing. They come back with the session or not at
  all: launched by path, `StartMenuExperienceHost.exe` fails its own stack check.
- The run waits for the rebuild and says what it found — desktop and Start back,
  desktop only, or nothing — and starts the shell by hand if Winlogon did not.

If the rebuild ever fails to arrive, Task Manager is hosted by winlogon, not
explorer, so the way back still works with no shell running:

> **Ctrl+Shift+Esc** → File → Run new task → browse to `IdleMaster.exe` → **Restore desktop**

Set `KillExplorer=0` in the ini and idle mode leaves the shell alone.

## Tuning it

The fastest way is **What's eating RAM?**, which opens the Master's own little
task manager: a live table that refreshes every two seconds, tags each row with
the list it is on
(`BOOST` / `IDLE` / `KEEP`), and a right-click offers *End it now*, *Close on
every boost*, *Also close on absolute idle*, or *Never touch* — each choice is
written straight into the ini, dated, and picked up by a running sentry on its
next sweep.

**Settings** opens the switches most people touch — sentry on/off, ask-before-
kill, tray, sweep interval, emergency trim — in plain words. **Advanced
settings...** behind it is the whole config: every switch as a checkbox, every
interval as a number, and each list as a checklist you can add to from what is
running right now — pick processes sorted by how much RAM they are costing, or
services by display name. Unchecking an entry comments it out instead of
deleting it, so the suggestions that ship commented-out are visible and one
click from being live. Saving re-reads the config immediately.

It edits `idlemaster.ini` line by line, so every comment in it survives. You can
still edit the file by hand if you prefer — plain text, `*` wildcards, `#`
comments out a line.

**Windows Search is the one you'll notice.** `WSearch` is in `[boost.services]`
and `SearchIndexer` in `[idle.kill]`, and stopped, the Start menu can't resolve
its own shortcuts — type *powershell* and you get *"the item you selected is
unavailable. It might have been moved, renamed, or removed."* Answer **Cancel**
there, never Remove: that deletes a Start entry over a service that was only
paused.

After a plain **boost** it heals itself — `WSearch` is Automatic (Delayed) and
trigger-started, and `[boost.kill]` never touches `SearchIndexer`, so the index
is intact when Windows brings the service back a few minutes later. Under a
**watch** it doesn't heal: the sentry re-stops the service every
`SentryServiceMinutes`, and an **Absolute Idle** watch also kills `SearchIndexer`
every 20 seconds, so the index can never finish rebuilding either. That is the
difference between *search is off for a bit* and *search is off*. Comment out
`WSearch`, `SearchIndexer`, `SearchProtocolHost` and `SearchFilterHost` if you'd
rather have a working Start menu than the ~100 MB.

Two entries are deliberately left commented out because they're the ones that
could bite you from bed:

- `NvContainerLocalSystem` — hosts the NVIDIA App. Some drivers get unhappy about
  NVENC when it's gone, and a black stream is exactly what you can't debug asleep.
  Test a stream without it before enabling.
- `powershell` / `WindowsTerminal` in `[idle.kill]` — enable only if you never
  leave a long job running overnight. Note that the terminal being spared does
  not automatically spare what is *running in* it: a CLI is a process of its own
  with a name of its own. `[protect.path]` is how you spare one by where it was
  installed; that is why the Claude Code CLI ships in there.

`nordvpn-service` **is** stopped in idle mode. If you have NordVPN's kill switch
armed, killing the service can take the whole network with it — the network guard
will catch that and scream in the log, but test it once while you're awake.

## Files

```
src/IdleMaster.cs         engine, config, sentry, updater, CLI entry point
src/NetGuard.cs             the network guard: WLAN API, measuring, the repair ladder
src/Ui.cs                 every window: main, task manager, settings, ask toast
src/Cleanup.cs            the disk cleanup scanner: junk spots, leftovers, owners
src/DiskScan.cs           the disk mapper: raw MFT reader + parallel-walk fallback
src/Debloat.cs            the debloater: Store-app inventory, known-bloat table, removal
src/Backup.cs             the backup kit: app inventory, zip writer, window
src/Rebuild.cs            the standalone exe that ships inside a kit
src/Theme.cs              the dark palette, fonts, and control styling
src/Setup.cs              the installer; carries the app as an embedded resource
src/app.manifest          requireAdministrator + per-monitor DPI
src/idlemaster.ico        the icon; make-icon.ps1 draws it
build.ps1                 builds all three exes
dist/IdleMasterRebuild.exe  goes inside every backup kit
dist/IdleMaster.exe       the app
dist/IdleMasterSetup.exe  the thing you publish and people download
dist/idlemaster.ini       the lists; edit in Settings or by hand
dist/idlemaster.log       append-only record of every run
dist/idlemaster.state     written by boost/idle, consumed by restore
```

## License

[FSL-1.1-MIT](LICENSE.md) — the Functional Source License, with an MIT future
grant. Copyright 2026 Mild-Solvent.

In plain words, and the file is what actually counts:

- **Use it however you like.** At home, at work, on one machine or five hundred.
  Fork it, patch it, redistribute it, build on it. No fee, no permission needed.
- **Keep my name on it.** Redistribute it and the copyright notice goes with it.
- **Don't sell my tool as your product.** The one forbidden thing is a *competing
  use* — packaging this into a commercial product or service that substitutes for
  it, or does substantially the same thing. If you want to do that, ask me; I'm
  reachable through GitHub and open to it.
- **It becomes MIT in two years.** Every release picks up a plain MIT license on
  the second anniversary of its publication, automatically and irrevocably. The
  restriction is a head start, not a permanent enclosure.

This is source-available rather than OSI open source, and that is deliberate: the
Windows utility world has a long history of free tools being rewrapped in an
installer with a "Pro" tier attached. Everything else — reading, learning from,
improving, and using this — is wide open.

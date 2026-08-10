# Idle Master

A single 50 KB exe for a machine that has to stay awake so Sunshine can stream it,
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

## Build

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the .NET Framework compiler that ships inside Windows. No SDK,
no NuGet, no internet. Output: `dist\IdleMaster.exe`.

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
IdleMaster.exe --installtask run the sentry at every logon (--removetask undoes it)
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
| Windows shell (explorer, start menu, search host, text input) | 490 MB | idle |
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
window it's the *Keep hunting after boost* checkbox, with a live count of what it
has reaped; on the command line it's `--watch` / `--unwatch`.

Because "kill it every 20 seconds forever" is a good way to make a machine
miserable, two things hold it back:

**Respawn backoff.** If one process name comes back `SentryRespawnLimit` times
(6 by default), the sentry stops fighting it for 30 minutes and writes a line
saying so. Something on the machine clearly wants that process alive, and an
endless kill/respawn loop burns more CPU than the process ever cost you in RAM.
After the backoff it puts the name back on the list and tries again.

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

`--installtask` registers a logon scheduled task (`IdleMaster Sentry`, highest
privileges) so the watch survives a reboot. It is off unless you ask for it, and
`--removetask` deletes it.

## The safety story

This is a tool that kills processes on a machine you can only reach remotely, so
most of the code is about not stranding you.

**Protected list wins over everything.** `[protect]` and `[protect.services]` in
the ini are checked before any kill or stop, even if something is also listed in
a kill list. Sunshine, tailscaled, Defender, lsass, the audio stack, the NVIDIA
*display* container, and the exe itself are all in there.

**Network guard.** Before finishing (and mid-run, in idle mode) it verifies that
`SunshineService` and `Tailscale` are running, that something is listening on a
Sunshine port, and that the Tailscale adapter is up. If a service died as
collateral damage, it restarts it and says so loudly in the log.

**Everything is reversible.** Services are *stopped*, never disabled, so a reboot
returns the machine to normal on its own. Each run writes `idlemaster.state` with
the exact list of what it stopped, and Restore walks that list backwards. The
sentry appends to the same file, so anything it re-stops later still gets undone.

**Restore disarms the hunter first.** Before it restarts a single service it
signals the sentry to stand down — otherwise the sentry would shoot everything
Restore just brought back, on its next sweep.

**The shell is the one scary part.** Absolute idle closes `explorer.exe` — that's
~490 MB with the start menu and search host that follow it. Your taskbar and
desktop wallpaper disappear. If you stream in and get a blank screen:

> **Ctrl+Shift+Esc** → File → Run new task → browse to `IdleMaster.exe` → **Restore desktop**

Task Manager is handled by winlogon, not explorer, so that shortcut works with no
shell running. If you'd rather not risk it, set `KillExplorer=0` in the ini and
idle mode leaves the desktop alone.

## Tuning it

Everything lives in `dist\idlemaster.ini`, next to the exe. Plain text, `*`
wildcards, `#` comments out a line. The exe re-reads it on every launch — no
rebuild needed.

Two entries are deliberately left commented out because they're the ones that
could bite you from bed:

- `NvContainerLocalSystem` — hosts the NVIDIA App. Some drivers get unhappy about
  NVENC when it's gone, and a black stream is exactly what you can't debug asleep.
  Test a stream without it before enabling.
- `powershell` / `WindowsTerminal` in `[idle.kill]` — enable only if you never
  leave a long job running overnight.

`nordvpn-service` **is** stopped in idle mode. If you have NordVPN's kill switch
armed, killing the service can take the whole network with it — the network guard
will catch that and scream in the log, but test it once while you're awake.

## Files

```
src/IdleMaster.cs     everything: engine, sentry, config parser, CLI, WinForms UI
src/app.manifest      requireAdministrator + per-monitor DPI
build.ps1             one-line build
dist/IdleMaster.exe   the thing
dist/idlemaster.ini   the lists, edit freely
dist/idlemaster.log   append-only record of every run
dist/idlemaster.state written by boost/idle, consumed by restore
```

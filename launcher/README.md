# VSpeedLauncher — Persistent JVM Daemon

Native Windows launcher that turns Minecraft startup from "wait 88 seconds"
into "click and play in ~3 seconds" — without the JVM eating 12 GB of RAM
when you're not playing.

## How it works in one paragraph

The launcher spawns PrismLauncher with `-Dvspeed.daemon=true` injected via
`JAVA_TOOL_OPTIONS`.  Inside the JVM, the `vspeed-loader` mod sees that flag,
and after `FMLLoadCompleteEvent` it connects to a named pipe
(`\\.\pipe\vspeed-daemon`) and sends `READY pid=<n> instance=<id>`.  The
launcher reads that, calls **`NtSuspendProcess`** on the JVM, then
**`EmptyWorkingSet`** — Windows then pages all 12 GB of heap out to
`pagefile.sys`.  Resident memory drops from 12 GB → ~50 MB.

To play again, click "Wake up".  The launcher calls `NtResumeProcess`, the
threads start running, and Windows faults pages back from disk on the first
access.  Total wake time: 2-5 seconds (depending on SSD speed).

Nothing exotic: no CRaC, no WSL, no patched JVM.  Just two undocumented-but-
stable-since-XP `ntdll` calls and one well-documented `psapi` call.  The
launcher is a stock .NET 8 WPF app.

## Build

Prerequisites:
* Windows 11 (or Windows 10 1809+)
* .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)

From this directory:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Output: `bin/Release/net8.0-windows/win-x64/publish/VSpeedLauncher.exe`
(~155 MB single-file exe with the full .NET runtime embedded — no install
needed on target machines).

## Run

1. Double-click `VSpeedLauncher.exe`.  First launch shows the window; on
   subsequent runs it stays in the tray until you click the icon.
2. **Settings → PrismLauncher.exe** — point at your `prismlauncher.exe`
   (auto-detected from `%LOCALAPPDATA%\Programs\PrismLauncher\`).
3. Click **Play** next to ATM10.  The first run takes the usual ~68 s (CDS).
   When the mod reports READY, the launcher auto-hibernates: tray
   notification says "ATM10 hibernated, 47 MB resident".
4. Next time you want to play: click **Wake up** (or use the tray menu).
   ~3 seconds later the game window is alive.

## Mod-side requirement

Your ATM10 instance must have `vspeed-loader` installed (this repo's parent
project).  The mod reads `-Dvspeed.daemon=true` and signals readiness.  If
the mod is missing, the launcher will keep waiting for READY and eventually
mark the instance as "Crashed".

The mod is harmless in non-daemon launches: without the property set, it
runs in normal mode and the named pipe code never runs.

## Architecture map

```
launcher/VSpeedLauncher/
├── App.xaml(.cs)              ─ application bootstrapper, wires up DI-like singletons
├── app.manifest               ─ asInvoker (no UAC), Common Controls v6
│
├── Core/
│   ├── ConfigStore.cs         ─ %LOCALAPPDATA%\VSpeedLauncher\config.json
│   ├── InstanceManager.cs     ─ launch / hibernate / wake state machine
│   ├── Logger.cs              ─ append-only file logger
│   ├── PipeServer.cs          ─ named-pipe listener for the mod's READY signal
│   └── ProcessHibernator.cs   ─ the actual Win32 magic (NtSuspendProcess + EmptyWorkingSet)
│
└── UI/
    ├── MainWindow.xaml(.cs)   ─ Steam-style instance list
    ├── SettingsWindow.xaml(.cs)
    └── TrayIcon.cs            ─ WinForms NotifyIcon (WPF has no native tray API)
```

## Why these particular Win32 calls

`NtSuspendProcess` (ntdll) atomically suspends every thread in a process
kernel-side.  Doing this from user-mode (CreateToolhelp32Snapshot +
SuspendThread per thread) has a race: a new thread can be created between
the snapshot and the iteration, and you'd miss it.

`EmptyWorkingSet` (psapi) tells the memory manager to evict every page that
isn't actively pinned.  Pages stay committed (process VAS unchanged), they
just live in `pagefile.sys` instead of RAM.  Combined with the suspend (no
new accesses) this leaves the process at ~50 MB resident.

`NtResumeProcess` (ntdll) wakes everything.  First memory access per page
triggers a page-fault which the OS resolves by reading from `pagefile.sys`.
SSDs do this at 500-3000 MB/s so faulting 3 GB back in takes 1-6 seconds
worst case — usually much less because not all pages are touched immediately.

## What this does NOT do (yet)

* **No multi-instance hibernation across reboots.**  After a Windows
  reboot, all hibernated JVMs are gone.  pagefile.sys does not survive
  reboot.  First launch after reboot takes the cold-boot time.
* **No window restore optimisation.**  Wake currently just resumes threads
  — you may briefly see the game catching up on missed frames before it
  becomes interactive.  Future work: hide window before hibernate,
  ShowWindow(SW_RESTORE) on wake.
* **No memory ballooning detection.**  If you hibernate two 12 GB instances
  on a 16 GB machine, both heaps land in pagefile.sys (~24 GB pagefile use).
  That's fine if your pagefile is sized accordingly, but the launcher
  doesn't warn you about it.

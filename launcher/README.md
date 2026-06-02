# Cryo Launcher

A standalone Windows Minecraft modpack launcher with a built-in optimization
engine ("VSpeed"). Native WPF host + WebView2 rendering a React UI, a CmlLib.Core
launch engine, encrypted Microsoft auth, per-instance Java auto-detection, and
Velopack auto-updates. **No PrismLauncher dependency.**

> Looking for how the code is laid out and the project conventions? See
> [`CLAUDE.md`](./CLAUDE.md). It also carries a changelog of recent fixes.

## What it does

- **Install & play modpacks** from Modrinth and CurseForge, or create your own
  instance. Loaders supported: NeoForge, Forge, Fabric, Quilt, and vanilla.
- **Self-contained launch engine** — [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core)
  installs the version, libraries, and assets and builds the JVM command. The
  correct Mojang JRE is downloaded automatically per Minecraft version.
- **Microsoft sign-in** — tokens are encrypted at rest with **Windows DPAPI**
  (current-user scope); the launcher never stores them in plaintext and never
  sees your Microsoft password.
- **Faster boot (VSpeed)** — on Java 19+ the engine adds AppCDS
  (`-XX:+AutoCreateSharedArchive`) so subsequent launches reuse a class archive.
- **Polished React UI** — Library, per-instance Overview with a live boot
  timeline + benchmark, Performance, Mods, Worlds, Servers, and Settings; a
  Dashboard, an AI Assistant, and a Logs viewer.
- **Tuning that won't foot-gun you** — RAM sliders capped to your machine's
  physical memory, JVM-argument presets (Balanced G1GC / Low-pause ZGC / Aikar),
  and Java auto-detect with a picker of every JRE found on the system.
- **Auto-updates** via [Velopack](https://github.com/velopack/velopack) from
  GitHub Releases — testers get delta updates on launch.

## Requirements

- Windows 10 (1809+) or Windows 11, x64.
- WebView2 Runtime (preinstalled on Windows 11; otherwise install the Evergreen
  runtime from Microsoft).
- The shipped build is **self-contained** — the .NET 8 runtime is embedded, so
  end users don't need to install anything else.

## Build & run (development)

Prerequisite: **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`).

```powershell
# from this directory
dotnet build VSpeedLauncher/VSpeedLauncher.csproj -c Release
```

Run `VSpeedLauncher/bin/Release/net8.0-windows/win-x64/VSpeedLauncher.exe`.
Stop any running instance before rebuilding — a running exe locks the output file.

The UI (`VSpeedLauncher/WebUI/`) is **served from the source folder in dev builds**,
so editing the `.jsx` files and relaunching is enough — no rebuild needed for
front-end changes. Only C# changes require `dotnet build`.

## Build a release (installer + auto-update feed)

Prerequisite: the `vpk` CLI (`dotnet tool install -g vpk --version 1.1.1`).

1. Bump `<Version>` in `VSpeedLauncher/VSpeedLauncher.csproj`.
2. Run the build script:
   ```powershell
   ./build-release.ps1            # or: ./build-release.ps1 -Version 1.2.3
   ```
   It publishes a self-contained `win-x64` build and packs it with Velopack into
   `Releases/` (`Cryo-win-Setup.exe`, full/delta `.nupkg`, and the update feed).
3. Publish to GitHub Releases so installed apps auto-update (the script prints the
   exact `vpk upload github …` command).

`Cryo-win-Setup.exe` is the installer you hand to users for a first install.

## Architecture map

```
launcher/
├── build-release.ps1             ─ publish + Velopack pack
└── VSpeedLauncher/
    ├── Program.cs                ─ entry point (Velopack runs before WPF)
    ├── App.xaml(.cs)             ─ WPF bootstrap; hosts the WebView2
    ├── Core/
    │   ├── CryoBridge.cs         ─ JS↔C# bridge + most logic: launch, account,
    │   │                           modpack install, Java detection, instance cfg
    │   ├── LauncherCore.cs       ─ CmlLib.Core wrapper (install loaders, launch)
    │   ├── MicrosoftAccount.cs   ─ MSA login; DPAPI-encrypted token store
    │   ├── LogReader.cs          ─ parses latest.log / cryo-engine.log
    │   ├── InstanceMetaReader.cs ─ reads instance.cfg / mmc-pack.json
    │   └── ConfigStore.cs        ─ %LocalAppData%\VSpeedLauncher\config.json
    └── WebUI/                    ─ React (Babel standalone, no JSX) served at
        │                           https://cryo.local/ via a virtual-host mapping
        ├── Cryo Launcher.html    ─ script load order
        └── src/
            ├── app.jsx           ─ window shell + titlebar account chip
            ├── ui.jsx            ─ shared components (Btn, Icon, Select, …)
            ├── store.jsx         ─ the api.* bridge wrappers
            ├── library.jsx       ─ instance grid + create/import
            ├── instance-tabs.jsx ─ Overview / Performance / Mods / Settings
            ├── modrinth.jsx      ─ Modrinth + CurseForge browser
            ├── settings.jsx, dashboard.jsx, logs.jsx, assistant.jsx
```

## Data locations

- Game runtime (shared): `%LocalAppData%\VSpeedLauncher\game\`
  (libraries, versions, assets, downloaded Mojang JREs under `runtime\`).
- Instances (shared with PrismLauncher's layout):
  `%APPDATA%\PrismLauncher\instances\<id>\` — `instance.cfg`, `mmc-pack.json`,
  and the per-instance `minecraft\` (mods, config, saves, logs).
- Encrypted account tokens: `%LocalAppData%\VSpeedLauncher\auth\accounts.bin`
  (Windows DPAPI, current user only).
- App config: `%LocalAppData%\VSpeedLauncher\config.json`.

## Java handling

The engine maps each Minecraft version to the Java major it needs
(`≤1.16→8`, `1.17→16`, `1.18–1.20.4→17`, `1.20.5+/1.21→21`) and picks Java in this
order: a user-set path in `instance.cfg` (if it exists) → a bundled JRE matching
that major → otherwise CmlLib downloads the right one. AppCDS is only added on
Java 19+ (it aborts older JVMs). Settings → Java offers an **Auto-detect** button
and a picker listing every JRE found on the machine.

# CLAUDE.md — Cryo Launcher

> Project context for Claude Code sessions. **Read this first.**
> When you fix or add something, append it to the **Changelog** at the bottom so
> the next session (and other people) know what changed and why.

## What Cryo is (current state)

- A standalone **Windows Minecraft modpack launcher**. WPF host (.NET 8,
  `net8.0-windows`, self-contained `win-x64`) + **WebView2** rendering a **React**
  UI. Product name "Cryo"; the optimization engine is branded "VSpeed".
- **No PrismLauncher dependency** for launching anymore. Note: some C# from the old
  Prism-daemon design is still present but **unused for Cryo-native instances** —
  `PipeServer`, `ProcessHibernator`, and `InstanceManager.LaunchAsync`'s Prism path.
  The `vspeed-loader` mod + Gradle project were **removed from the repo in v1.0.6**
  (recoverable via git history); the repo is now just `launcher/` + `docs/`.
- Launch engine: **CmlLib.Core 4.0.6** installs versions/loaders/assets and
  builds the JVM command. Mojang JREs auto-download to
  `%LocalAppData%\VSpeedLauncher\game\runtime\windows-x64\<component>\bin\`.
- Auto-update: **Velopack 1.1.1** + `vpk` CLI 1.1.1 → GitHub Releases
  (repo `github.com/xponer/vspeed-cryoLauncher`).
- Data dir is shared with Prism: instances live under
  `%APPDATA%\PrismLauncher\instances\<id>\` (`instance.cfg`, `mmc-pack.json`,
  `minecraft/`). `mmc-pack.json` `net.minecraft` component = the MC version.

## ⚠️ Hard rules (do NOT violate)

1. **NEVER `git commit` or `git push` without the user explicitly asking.**
   The working tree intentionally carries uncommitted changes. Ask every time.
2. **Security (token theft prevention):** Microsoft/Minecraft tokens are
   encrypted at rest with **Windows DPAPI (CurrentUser scope)** at
   `%LocalAppData%\VSpeedLauncher\auth\accounts.bin` — decryptable only by this
   Windows user on this PC, never plaintext. The launcher never sees the MS
   password. AI / CurseForge / Discord API keys are **never echoed to the UI** —
   only booleans (`aiHasKey` / `curseHasKey` / `discordHasId`).
3. **Work one task at a time and announce each briefly** (user preference).

## Build / Run

- Dev build: `dotnet build VSpeedLauncher/VSpeedLauncher.csproj -c Release`
  → `VSpeedLauncher/bin/Release/net8.0-windows/win-x64/VSpeedLauncher.exe`.
  **Stop any running instance first** — it locks the exe (MSB3027 otherwise).
- **Dev builds serve the WebUI from the SOURCE folder** (`VSpeedLauncher/WebUI/`).
  So **JS/JSX changes apply on relaunch — no rebuild needed.** Only **C# changes**
  require `dotnet build`.
- Release: `./build-release.ps1` (publishes self-contained, then `vpk pack` into
  `Releases/`). Bump `<Version>` in `VSpeedLauncher/VSpeedLauncher.csproj` first.
  Publishing to GitHub is a separate step — **only when the user authorizes it.**

## UI conventions (`WebUI/src/*.jsx`)

- React 18 via **Babel standalone**, **NO JSX** — everything is
  `React.createElement(...)`.
- Scripts are plain (non-module): a top-level `function Foo(){}` in any `.jsx` is
  a **global** usable from other files. Load order is in `Cryo Launcher.html`;
  `ui.jsx` (shared: `Btn`, `Icon`, `Select`, `TextInput`, `Slider`, `SkinHead`…)
  loads before the feature files.
- **Never define a component inside another component's render.** Babel/React
  sees a new type each render and **remounts** the subtree — this caused the
  "Java-path input loses focus on every keystroke" bug (`Section` was nested
  inside `SettingsTab`). Define components at module scope.
- Bridge: `store.jsx` `api.<method>()` → `CryoBridge.DispatchAsync` switch. To add
  a call, add it in **both** places. C#→JS events: `Push(event, payload)` in
  CryoBridge → `window` event listeners in the UI.

## Engine launch & Java (the part that breaks most often)

- Engine path runs when `Source=="cryo" && LoggedIn &&
  GetStoredEngineVersion(id)!=null` → `CryoBridge.LaunchWithEngine`.
- **Java major per MC version** (`JavaMajorForMc`): `≤1.16→8`, `1.17→16`,
  `1.18–1.20.4→17`, `1.20.5+ / 1.21→21`.
- **AppCDS** (`-XX:+AutoCreateSharedArchive`) is **Java 19+ ONLY**. Adding it on
  Java 8/16/17 → "Unrecognized VM option" → instant JVM abort (exit 1, *no*
  Minecraft log). Always gate on `JavaMajorForMc(meta.Mc) >= 19`.
- **Java selection** in `LaunchWithEngine`: (1) user override from `instance.cfg`
  `JavaPath` *if the file exists*; (2) else the bundled JRE matching the required
  major (`ResolveBundledJava`); (3) else `null` → CmlLib downloads it.
- **Early-crash logs:** the JVM's stdout/stderr is captured to
  `<gameDir>/logs/cryo-engine.log` (`LauncherCore.InstallAndLaunchAsync`'s
  `stdoutLog`). `LogReader` reads the freshest of `latest.log` / `cryo-engine.log`.
- Mojang JRE components → major: `jre-legacy=8`, `java-runtime-alpha=16`,
  `java-runtime-beta/gamma=17`, `java-runtime-delta=21`. Version/vendor are read
  from each JRE's `release` file (`JAVA_VERSION` / `IMPLEMENTOR`).

## Key files (current architecture)

- `Core/CryoBridge.cs` — JS↔C# bridge + most logic: dispatch switch, engine
  launch, MS account, modpack install, Java detection, instance.cfg, system info.
- `Core/LauncherCore.cs` — CmlLib.Core wrapper (install loaders,
  `InstallAndLaunchAsync`, stdout capture).
- `Core/LogReader.cs`, `Core/InstanceMetaReader.cs`,
  `Core/MicrosoftAccount.cs` (DPAPI), `Core/ConfigStore.cs`.
- `WebUI/src/`: `app.jsx` (shell + titlebar `AccountChip`), `ui.jsx` (shared
  components), `instance-tabs.jsx` (instance Settings/Performance/Mods),
  `settings.jsx` (global settings + account card), `store.jsx` (bridge),
  `library.jsx`, `modrinth.jsx`, `logs.jsx`, `dashboard.jsx`, `assistant.jsx`.

## Changelog

> Newest at the top. Mark whether something is **released** (in a GitHub
> Velopack release that testers auto-update to) or **unreleased** (only in the
> local working tree / dev build).

### v1.0.9 — released (GitHub) — startup splash, health check, screenshots, auto-backup
- **Startup splash (no white/black flash)** — an in-page HTML/CSS splash (snowflake + a
  GPU-composited spinner) paints instantly and fades only once the initial data has loaded
  (`store.jsx` `__cryoHideSplash`, fired when the bridge goes idle). A dark WPF `InitCover`
  covers the WebView2 init phase, and `WebView.DefaultBackgroundColor` is dark. This replaced
  earlier janky/flickery attempts (SVG rotation on the Babel-busy main thread; the WPF↔WebView
  compositing gap when a WPF splash faded before the app composited).
- **Instance Health check** — per-instance VSpeed diagnostics (Java↔MC version, RAM↔mod count,
  AppCDS status, disk space, mods) → a 0–100 score + per-check tips. `CryoBridge.GetHealth`;
  `HealthCard` shown at the top of the Performance tab.
- **Screenshots gallery** — a new instance tab browsing `minecraft/screenshots`, served via a
  `cryo-shots.local` virtual-host mapping; click to open, button to delete.
  `GetScreenshots` / `OpenScreenshot` / `DeleteScreenshot`.
- **Auto-backup before launch (opt-in)** — a Settings toggle; `LaunchInstance` zips the worlds
  (`AutoBackupWorlds`, keeps the last 5) before launching. `config.AutoBackupBeforeLaunch`.
- (A batch **Update all** for mods already existed in the Mods tab.)
- **Discord Rich Presence: zero-setup** — the app embeds a public Rich Presence app ID
  (`DiscordRpc.EmbeddedClientId`); `UpdateDiscordPresence` always uses it. Settings → Discord is
  now just an on/off toggle (the Application-ID input + save were removed).
- **Configurable instance locations (multi-folder)** — the old "PrismLauncher path" setting is
  gone. Config holds `InstanceRoots` (Prism-style data dirs, each with an `instances/` subfolder);
  every `InstanceEntry` is tagged with its `DataDir`. Discovery scans all roots; a back-compat
  migration (`ConfigStore.MigrateInstanceRoots`) seeds the list from the legacy `PrismDataDir`.
  All ~40 instance-path lookups in `CryoBridge` now go through a new `InstanceDataDir(id)` helper
  (only the field/assignment/fallback still name `_prismDataDir`).
  - **Settings → Instances** — add/remove locations (native folder picker), **Set primary**
    (new installs default there), **Open** in Explorer; shows per-location instance counts.
  - **Install picker** — installing a modpack with >1 location asks **"Install where?"**;
    `installModrinth/CurseForgeModpack` + `createInstance` now take a `targetRoot`
    (`CreateInstanceFolder`/`RegisterInstance`/`FinishModpack` thread it through).
  - **Move instance** — the library card menu gains **Move → <location>** (same-volume rename;
    cross-volume copy+delete; refuses while running). Bridge `moveInstance` + `moveProgress`/`moveDone`.
  - New bridge methods: `getInstanceRoots / addInstanceRoot / removeInstanceRoot / setPrimaryRoot /
    pickFolder / openPath / moveInstance` (+ store.jsx wrappers, `cfg.instances` i18n).
- ⚠️ Large change to core instance-path handling — **builds clean + JS passes `node --check`, but
  is NOT yet GUI-tested**. Verify instances still launch and mods/worlds resolve before releasing.

### v1.0.8 — released (GitHub) — maximized-window fix
- **Maximize no longer slides under the taskbar** — a `WindowStyle=None` window maximizes
  over the *whole monitor* by default, so the launcher's bottom edge (status bar / content)
  was covered by the Windows taskbar. `MainWindow` now hooks `WM_GETMINMAXINFO` and clamps
  the maximized rect to the monitor **work area** (handles a taskbar on any edge and
  secondary monitors). `MainWindow.xaml.cs`.

### v1.0.7 — released (GitHub) — UX fixes from tester feedback
- **Dialogs no longer close on drag-release** — modal backdrops (New instance, Delete,
  command palette) closed on `onClick` (= mouse-up), so pressing inside and releasing over
  the backdrop (dragging a slider, selecting text) dismissed them. Now they close only on a
  `mousedown` that *starts* on the backdrop (`onMouseDown` + `e.target===e.currentTarget`).
- **Menu reliably closes on outside click** — the `Menu` popover's outside-close used a
  one-shot `{ once: true }` mousedown listener that could be consumed without closing;
  replaced with a normal listener removed on cleanup (now matches `Select`).
- **RAM sliders capped to installed RAM** — removed the hardcoded `max: 32768` from the
  New-instance dialog, the global default (Settings), and the profile editor; all now cap at
  the machine's physical RAM via a shared `useSysRamMb(api)` hook in `ui.jsx` (the
  per-instance Settings slider already did this). Values clamp down on smaller machines.
- **Portable build surfaced** — every release already ships `Cryo-win-Portable.zip` (extract
  anywhere = choose your own folder); documented in the README. Installer stays **Velopack**
  (per-user `%LocalAppData%\Cryo`, seamless auto-update) — Velopack has no install-dir picker
  by design; the portable zip is the "custom location" path.

### v1.0.6 — released (GitHub) — tray polish + repo cleanup
- **Tray icon fixed** — the system-tray icon now uses the app's own `cryo.ico` (loaded
  from the embedded WPF resource, multi-resolution) instead of the generic
  `SystemIcons.Application` fallback. `TrayIcon.LoadIcon()`.
- **Themed tray menu** — the WinForms context menu is now dark (`CryoColors`
  `ProfessionalColorTable` + `CryoRenderer`), with a branded header (icon + version),
  anti-aliased status-dot icons per instance (green/amber/blue/red/gray), and drawn
  glyphs (play / stop / folder / update / quit). Applied via `ToolStripManager.Renderer`
  so submenus are themed too.
- **More tray actions** — added **Open game folder**, **Check for updates** (Velopack
  `UpdateService`; confirms, downloads, restarts), per-instance **Open folder**, and
  **Stop all (N)** (shown only when something is running). Dropped the dead
  **Hibernate/Wake** items (that architecture is retired). `TrayIcon` now takes a
  `ConfigStore` (for `PrismDataDir`).
- **Auto-update URL fix (important)** — `UpdateService.RepoUrl` still pointed at the old
  repo name `vspeed-atm10`; corrected to `vspeed-cryoLauncher`. Auto-update was only
  working through GitHub's rename redirect — now it's canonical. Also fixed stale
  `vspeed-atm10` references in `settings.jsx` (About links), `build-release.ps1`, docs.
- **Repo cleanup (max hygiene)** — removed the retired VSpeed *mod* project:
  `vspeed-loader/`, `vspeed-agent/`, `vspeed_agent.jar`, the Gradle wrapper +
  `build.gradle`/`settings.gradle`/`gradle.properties`, and `scripts/vspeed_test.py`
  (recoverable via git history). The repo is now just `launcher/` + `docs/`.
- **Root `README.md` rewritten** — it described the dead Prism/mod architecture and
  pointed at the old repo; it now accurately describes the standalone launcher.
  `.gitignore` trimmed accordingly.
- ⚠️ The tray changes **build cleanly** but were **not GUI-tested** this session — eyeball
  the icon + menu before relying on them (the logic is straightforward).

### v1.0.5 — released (GitHub) — features
- **Mod dependency auto-resolver** — installing a Modrinth mod also pulls its required
  dependencies (`DownloadModrinthMod` / `downloadModrinthMod`), recursively + de-duped
  (depth ≤5, ≤60 deps); the toast shows "+N dependencies". CurseForge mods install as one file.
- **Servers: Join + live status** — the Servers tab gained a **Join** button that launches
  straight into a server: `--quickPlayMultiplayer <ip>` on MC 1.20+, else `--server`/`--port`
  (`BuildJoinGameArgs`), threaded through `LaunchInstance`/`LaunchWithEngine` →
  `InstallAndLaunchAsync(extraGameArgs:)`. (Live ping / MOTD / player count already shown.)
- **Modpack update** — `cryo-pack.json` records the install source; `getModpackInfo` checks
  for a newer version; **Update** re-installs the latest, MOVING old mods to `mods.bak-<ts>`
  (reversible) and leaving `saves/` untouched. New "Modpack" card in instance Settings.
- **App icon** — `VSpeedLauncher/cryo.ico` (snowflake) as the exe `<ApplicationIcon>`, WPF
  `Window.Icon`, and the Velopack installer/shortcut icon.
- **Landing page** — `docs/` static site on GitHub Pages
  (https://xponer.github.io/vspeed-cryoLauncher/); `og:image` PNG for link previews.

### v1.0.4 — CurseForge "works-for-everyone" groundwork + One-click Optimize
- **One-click Optimize** (instance Settings → Memory allocation) — a button that
  picks Xmx from the pack's mod count and the machine's physical RAM (leaves OS
  headroom, capped at 16 GB), sets Xms=Xmx, and applies a JVM preset by Java major
  (Aikar/G1GC by default; ZGC for Java 21 + ≥10 GB heap). `recommendRamMb` logic
  verified via node across scenarios; GUI click-test pending (computer-use was
  disconnected this session).
- **CurseForge embedded-key mechanism** — CurseForge can work for ALL users with
  no per-user key: drop a key into `VSpeedLauncher/curseforge.key` (gitignored) and
  the build embeds it (csproj `AssemblyMetadata` → `CurseForgeClient.DefaultApiKey`).
  Falls back to a user-entered key (Settings); the key never enters the public repo
  (only the built binary). **No key is embedded yet** — needs a free key from
  console.curseforge.com — so CurseForge still asks for one until provided.
  `getConfig` now also returns `curseEnabled` (user OR embedded key) for UI gating.

### v1.0.3 — released (GitHub)
- **Java-path input focus bug fixed** — moved `Section` out of `SettingsTab`'s
  render to module scope (`instance-tabs.jsx`); typing in the Java path field no
  longer drops focus per keystroke. *Verified live.*
- **Engine Java selection** — `LaunchWithEngine` now honors a user-set
  `JavaPath` (when the file exists) and otherwise auto-selects the bundled JRE
  matching the MC's required major (`ResolveBundledJava`), falling back to
  CmlLib's download. Hardens modded packs whose JSON lacks `javaVersion`.
- **Account avatar fixed** — `crafatar.com` was down (HTTP 521), blanking all
  avatars. Added shared `SkinHead` component (`ui.jsx`) with fallback chain
  **mc-heads → minotar → crafatar**; used in the titlebar and the account card.
  *Verified live (skin head shows).*
- **Settings → Java: Auto-detect button + detected-Java picker** — new bridge
  `detectJavas(id)` scans Cryo bundled runtimes, Prism's `java/`, common vendor
  dirs (Oracle/Adoptium/Microsoft/Zulu/Corretto/Liberica/Semeru), `JAVA_HOME`,
  `PATH`; reads version/vendor from each `release` file. UI: an "Auto-detect"
  button (fills the recommended bundled JRE + toast) and a dropdown of all found
  Javas (recommended major starred ★). Slash-normalized matching so the picker
  reflects the configured path. *Verified live.*
- **Memory-allocation Max slider capped to physical RAM** — new bridge
  `getSystemRam()` (Win32 `GlobalMemoryStatusEx`) replaces the hardcoded 64 GB
  cap; the slider now maxes at the machine's installed RAM and a caption shows
  the total. *Verified live (showed 31.5 GB on a 32 GB PC).*

### v1.0.2 — released (GitHub) — Java-detection crash fix
- Gated AppCDS to Java 19+ so packs on Java 8/16/17 (MC ≤1.20.4, e.g. Fabric
  1.18.2, Forge 1.12.2) launch instead of instant-crashing on an "Unrecognized
  VM option".
- Capture the JVM's stdout/stderr to `cryo-engine.log`; `LogReader` falls back to
  it when `latest.log` is absent (so early crashes are no longer "invisible").
- NOTE: v1.0.0–v1.0.2 were released as binaries + GitHub tags only; their source
  was never committed (no-commit rule). The first source commit is **v1.0.3**, so
  it actually contains the v1.0.1/v1.0.2 fixes too. The older tags predate it.

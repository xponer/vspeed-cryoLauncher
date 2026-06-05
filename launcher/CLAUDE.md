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

## UI conventions (`WebUI/src/*.js`)

- React 18 (**production, vendored** at `WebUI/vendor/react*.production.min.js`),
  **NO JSX** — everything is `React.createElement(...)`. There is **no Babel** any
  more: the files are plain JS loaded as `<script defer src="src/*.js">` (unreleased).
- Scripts are plain (non-module): a top-level `function Foo(){}` in any `.js` is
  a **global** usable from other files. Load order is in `Cryo Launcher.html`;
  `ui.js` (shared: `Btn`, `Icon`, `Select`, `TextInput`, `Slider`, `SkinHead`…)
  loads before the feature files.
- **Cross-file shared bindings must NOT collide.** Because every file shares one
  global lexical scope, a top-level `const`/`let` with the same name in two files
  throws `Identifier 'X' has already been declared` (Babel used to hide this by
  downleveling `const`→`var`). So: **alias per-file** (`const { useApp: useAppMR } =
  window.CryoStore`) **or** use `var` for a genuinely shared binding (e.g.
  `var { useApp } = window.CryoStore`). `function` redeclaration is fine.
- **Never define a component inside another component's render.** React sees a new
  type each render and **remounts** the subtree — this caused the "Java-path input
  loses focus on every keystroke" bug (`Section` was nested inside `SettingsTab`).
  Define components at module scope.
- Bridge: `store.js` `api.<method>()` → `CryoBridge.DispatchAsync` switch. To add
  a call, add it in **both** places. C#→JS events: `Push(event, payload)` in
  CryoBridge → `window` event listeners in the UI.
- **Page-side errors go to `launcher.log`**: `MainWindow` enables CDP `Log`/`Runtime`
  and logs error-level `WebLog:` (CSP/MIME/network) + `WebError:` (uncaught JS). If the
  UI silently fails to render, check the log first.

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

### v1.0.14 — released (GitHub) — host a dedicated server for any pack
- **Host server tab** (instance → "Host server") — run a real dedicated Minecraft server for a
  modpack, set up in one click from the pack's OWN mods + config. New `Core/CryoBridge.Servers.cs`
  (CryoBridge is now `partial`). Servers live under `%LocalAppData%\VSpeedLauncher\servers\<id>\`,
  one per instance; metadata in `cryo-server.json`.
- **One-click setup** — copies the instance's enabled mod jars + `config`/`defaultconfigs`/`kubejs`/
  `scripts`, then installs the loader's server: **NeoForge** (official `--installServer`), **Fabric**
  (fabric-server-launch), **Vanilla** (Mojang server.jar). Forge/Quilt show "coming soon"
  (`IsLoaderServerSupported`). Writes `server.properties` + JVM heap (`user_jvm_args.txt` for NeoForge).
- **Live console with commands** — process launched with redirected stdin/stdout; a console
  ring-buffer the UI polls (~1.2s). States stopped/installing/starting/running/stopping/crashed;
  crash detection only on early non-zero exit. Type commands → stdin; **Stop** sends `stop` then
  kills after 20s. EULA gate (writes `eula.txt` only after the user accepts in-app).
- **Filtered console** (`ServerConsole`) — same toolkit as the Logs screen: parses each line into
  time/level/thread/source, 6-level chips with counts, Errors-only, thread + source filters
  (click-to-filter), regex search, per-segment auto-hued colouring. Reuses Logs globals
  (`LEVEL_META`/`LEVELS`/`hueFor`/`highlight`).
- **Full settings editor** (`ServerSettings`) — a Console/Settings switch; the Settings view is a
  complete `server.properties` editor: ~45 standard keys with proper widgets (toggles, difficulty/
  gamemode/world-type dropdowns, numeric + text), grouped (World/Players/Mobs & view/Server &
  network/Resource pack), a filter box, and an **"Other / advanced"** section for any non-schema
  keys in the file — so everything is editable in-app, no file editing. Backend `getServerProperties`/
  `saveServerProperties` preserve comments + key order, sync the cached port, refuse while running.
- New `server` + `terminal` icons (`ui.js`). 12 new bridge methods + store wrappers. Builds clean
  (0/0); UI verified to load + the tab/console/settings render with zero page errors. ⚠️ A real
  end-to-end server *boot* (NeoForge `--installServer` + first start) was **not** run this session
  (no UI automation) — exercise Set up → Accept EULA → Start on a NeoForge pack before relying on it.

### v1.0.13 — released (GitHub) — manual tags, notes & colours for mods and packs
- **Mod tags + notes** (instance → Mods tab) — assign your own labels (`optimization`, `visuals`,
  `create addon`, …) and a free-text note to any mod. A tag button per row opens an inline editor;
  tags render as coloured chips; a tag-filter row (live counts, AND) + search-matches-tags. Stored
  in `cryo-modmeta.json` next to the instance, **keyed by base filename** so tags/notes survive
  enable/disable (which renames the jar). Bridge: `setModTags`/`setModNote`; `getMods` returns them.
- **Pack (instance) tags + notes** (Library → card "⋮" → Tags & note) — the same for whole packs.
  Tags show as chips on cards/rows, note preview on cards; a **library-wide tag-filter row** (counts,
  AND, Clear). Stored in `cryo-instance.json` next to the instance (travels with move/duplicate).
  Bridge: `setInstanceTags`/`setInstanceNote`; `getInstances`/`getInstance` now return tags+note.
- **Tag colours** — a native colour swatch on each tag chip. Mod-tag colours are per-instance
  (`cryo-tagcolors.json`); pack-tag colours are library-wide (`instance-tagcolors.json` — a tag name
  is shared across packs). Unset → a stable auto-hue, rendered via `color-mix` (readable on dark).
  Bridge: `getTagColors`/`setTagColor`, `getInstanceTagColors`/`setInstanceTagColor` (hex-validated).
- **Quick-add tag pool** — the pack dialog and the mod editor suggest tags already created elsewhere,
  so you pick from the pool instead of retyping a tag letter-by-letter on every instance.
- **Fix: tags silently lost** — typing a tag then clicking Save / clicking away **without pressing
  Enter** dropped the text (only the note saved → `tags:[]`). Pending input is now flushed into a chip
  on **Save** and on **blur**, in both the pack dialog and the mod editor. Root-caused & verified
  end-to-end **live via computer-use** (tag persists; chip + filter + colour all render).
- New `tag` + `stickyNote` icons (`ui.js`). Chip helpers (`modTagChip`/`modTagHue`/`tagEffectiveHex`/
  `hslToHex`) live in `instance-tabs.js` and are reused as globals from `library.js`. Build clean (0/0).

### v1.0.12 — released (GitHub) — live colour-coded log console
- **Live log tail** — the Logs screen now polls `getLogs` every ~1.2s (while viewing & not
  paused), so you can watch the game boot in real time. A "LIVE" indicator; autoscroll follows
  the tail and pauses when you scroll up to read (resumes at the bottom). `onScroll` manages it.
- **Auto-selects the launching instance** — on the `loading`/`waking` state transition the Logs
  view switches to that instance; opening Logs after launching from anywhere (incl. tray) defaults
  to it (`window.__cryoLastLaunched`, set by a global listener in `app.js` + the LogsScreen).
- **Console-style view** — renders the original log line verbatim (new `LogReader`/`LogEntry`
  `Raw` field), monospace, **wrapping fully** (no more truncation/ellipsis — that was the "broken"
  look), capped to the last 1200 lines for snappiness.
- **Fully colour-coded debug console** — per-segment colouring: time (dim), level (level colour),
  **thread** and **mod source** each auto-hued to a stable unique colour (`hueFor`), message tinted
  by level. Filters: 6 levels (TRACE…FATAL) with **live counts**, **"Errors only"** quick toggle,
  **thread** + **source** dropdown filters, **click a thread/source in any line to filter to it**,
  regex/text search, next-error jump (ERROR+FATAL). 
- **Fix: false "crash"** — (1) the engine exit handler marked Crashed on *any* JVM exit while in
  `Loading` (the standalone state never leaves Loading — no READY pipe), so a normal close read as a
  crash. Now only a non-zero exit *during startup* (<60s) is a crash; clean exit / long session /
  user-stop → Stopped. (2) The Logs crash banner triggered on *any* stack trace (modded startup is
  full of benign ones) — now only on real markers (Crash Report header, JVM fatal, `Exception in
  thread "main"`, server-start failure).

### v1.0.11 — released (GitHub) — mod workflow, filters & polish
- **In-instance "Add mods" tab** — search Modrinth + CurseForge from inside an instance,
  auto-filtered to its loader + MC, one-click install (with deps) straight in — no "which
  instance?" picker. `InstanceModBrowser` (modrinth.js) reusing `ModCard`; new instance tab
  (instance.js). Mod count + Mods tab refresh after install (`refreshMods`).
- **VSpeed Performance pack (unique)** — one click installs curated FPS/memory mods for the
  loader (Fabric/Quilt: Sodium, Lithium, FerriteCore, ModernFix, EntityCulling, Dynamic FPS,
  ImmediatelyFast, Krypton; Forge/NeoForge: Embeddium, FerriteCore, ModernFix, Saturn, Canary).
  Incompatible slugs skipped, required deps pulled one level. `CryoBridge.InstallPerformancePack`
  → `perfPackProgress`/`perfPackDone`; accent banner at the top of the Add-mods tab.
- **"Installed ✓" badges** — both mod browsers flag mods already present, hash-matched to
  Modrinth project ids (`GetInstalledModIdsAsync`, reuses the CheckUpdates hashing). CurseForge-only
  mods simply won't match (no false positives). ("Update all" already existed in the Mods tab.)
- **Add local .jar mods** — the Mods tab gains an "Add .jar" button (native picker → `AddLocalMods`,
  any size) and a drag-and-drop zone (reads bytes → base64 → `AddLocalModData`; ≤100 MB, .jar only,
  path-guarded). `UseShellExecute`-free; the NavigationStarting guard also blocks file-drop navigation.
- **Visual polish** — ambient aurora background (accent-tinted radial glows over `--bg-0`, retints
  per theme), accent-coloured scrollbar thumb on hover.
- **Ambient snowfall (unique)** — a quiet on-brand touch behind the content (`SnowField` in app.js,
  z-index 1 so it shows through the frosted glass), hidden when the animations toggle is off.
- **Mod browser filters** — both browsers (global + in-instance) gained a **Sort** (Relevance /
  Downloads / Updated → Modrinth `index` + CurseForge `sortField`) and a **Category** selector
  (Modrinth slugs; separate mod vs modpack lists; resets on Mods↔Modpacks; a Clear button).
  Applies to mods AND modpacks. CurseForge keeps sort only — CF category ids are deliberately not
  hardcoded (to avoid silently-wrong filtering), so the category selector shows for Modrinth source.
- **Fix: Health-check score unreadable** — the ring's inner disc was `var(--panel)` (translucent),
  so the green ring bled through and the green number blended in. Disc → `--panel-solid` (opaque),
  number → `--text` (theme-contrast); the status colour stays on the ring (both themes verified).
- **Fix: mod count now updates live** — adding a local `.jar` or toggling a mod in the Mods tab
  only updated the local list; the header/tab count (`instance.mods`) stayed stale. `ModsTab` now
  fires `onModsChanged` (→ `refreshMods`) after add/toggle so the count refreshes immediately.
  (Installs from the Add-mods tab / Performance pack already refreshed it.)
- All build clean (0 warn / 0 err) + load clean (verified via `WebError`/`WebLog` diagnostics);
  **not GUI click-tested** (no UI automation this session) — eyeball the mod install/perf-pack flows.

### v1.0.10 — released (GitHub) — quality pass (UI polish · clean code · security · perf)
> A staged, four-part quality pass: **UI polish → clean code → security hardening →
> performance**. Released as v1.0.10 (commit 16adb94).
- **Step 4 — Performance / lightweight (drop runtime Babel).** The biggest startup cost was
  **Babel-standalone**: it downloaded ~3 MB from a CDN and recompiled all 15 UI files on the
  main thread at every launch (the startup lag/jank). But the files contain **no JSX** (all
  `React.createElement`), so Babel was pure overhead. Removed it entirely:
  - **No Babel; files load as plain `<script defer src="src/*.js">`** (renamed `.jsx` → `.js`,
    since they were never JSX). `defer` = parallel download, order preserved.
  - **React 18.3.1 production, vendored locally** (`WebUI/vendor/react*.production.min.js`,
    ~139 KB total) — replaces the dev builds loaded from unpkg. No CDN for scripts → faster
    cold start, works offline, and enables a strict `script-src 'self'`.
  - **CSP tightened to `script-src 'self'`** (dropped `'unsafe-inline'`, `'unsafe-eval'`, and
    `https://unpkg.com` — all only needed for runtime Babel). This completes the step-3 CSP.
  - **Fix (Babel-masked bug):** as native classic scripts, a duplicate top-level `const` across
    files throws `Identifier 'useApp' has already been declared` (Babel hid this via `const`→`var`).
    `useApp` was declared un-aliased in 6 files → changed those to `var { useApp } = window.CryoStore`
    (assistant/modrinth already aliased it). See the new "cross-file shared bindings" UI convention.
  - **Page-error diagnostics → `launcher.log`** (`MainWindow`, CDP `Log`/`Runtime`): error-level
    `WebLog:` (CSP/MIME/network refusals) + `WebError:` (uncaught JS). This is how the `useApp`
    crash was pinpointed; kept (filtered to errors) as a permanent support aid.
  - **Inline SVG favicon** (brand snowflake, data: URI) — removes the `/favicon.ico` 404.
  - Verified live: bridge fires (23 calls), `WebError: 0`, CSP refusals `0`, log clean.
  - ⚠️ Remaining CDN dep: the **Geist fonts** still load from jsdelivr (style/font-src). They work
    (fallback to system fonts offline) but trigger WebView2 tracking-prevention notices; vendoring
    them would allow dropping jsdelivr from the CSP too — left as optional follow-up.
- **Step 3 — Security hardening (data/personal).** Verified token/key handling is sound
  (MS/Minecraft tokens stay DPAPI-encrypted `accounts.bin`; AI/CurseForge keys + Discord ID
  never leave C# — `getConfig` returns only `aiHasKey`/`curseHasKey`/`discordHasId` booleans;
  AI calls run in C#, the key never reaches JS). New hardening:
  - **Log PII scrubbing** — `Logger` now collapses the user-profile path to `~` on every line
    (both slash styles), so a shared log can't leak the Windows account name / home layout.
  - **Bridge path-traversal guards** (treat renderer messages as untrusted, defence-in-depth):
    `IsSafeSegment` (no separators/`..`/illegal chars) + `IsContained` (resolved path stays in
    its base). Applied to `SetModEnabled` (the real hole — `file` was combined unchecked),
    `DeleteScreenshot`/`OpenScreenshot`, `OpenFolder`, `MoveInstance` (id), and `setModEnabled`.
  - **`OpenPath` lock-down** — rejects UNC paths (`\\host` → NTLM-leak vector) and only opens
    folders inside a launcher-managed base (`IsAllowedOpenBase`: configured instance roots +
    Cryo's app-data dir). The Settings "Open" button still works (it passes a configured root).
  - **Content-Security-Policy** — added a strict CSP `<meta>` to `Cryo Launcher.html`:
    `default-src 'none'`; scripts limited to self + unpkg (React/Babel); styles/fonts to self +
    jsdelivr; `img-src 'self' data: https:`; `connect-src 'self'`; iframes/objects/workers/forms/
    `<base>` all denied. `script-src` still needs `'unsafe-inline'`/`'unsafe-eval'` ONLY because
    JSX is compiled in-browser by Babel — **step 4 removes both** once JSX is precompiled (`'self'`).
  - Verified live via the launcher log: bridge calls fire (CSP didn't break Babel/React render)
    and the "Serving UI from source" path now prints `~\…` (redaction working).
- **Step 2 — Clean code.** Removed the dead **hibernate/wake** user feature (it depended on
  `InstanceState.Ready`, only ever set by the retired vspeed-loader pipe, so it was inert):
  dropped the `hibernateInstance`/`wakeInstance` bridge arms + methods, the two `store.jsx`
  wrappers, and the `hibernateBtn`/`wakeBtn` in `instance.jsx` (running state is now just a
  "running" badge). `InstanceManager`/`ProcessHibernator` are kept intact — `Terminate` still
  backs Stop/Kill, and the Prism `LaunchAsync` path still serves non-cryo instances.
- **Step 1 — UI polish.** `styles.css`: `accent-color`, removed tap-highlight, `scrollbar-gutter:
  stable` (no layout shift), `text-wrap: balance/pretty` for headings/paragraphs. `ScreenshotsTab`
  loading state now uses the shared `Spinner`.
  - A `prefers-reduced-motion` block was added then **removed**: it froze the loading spinners
    when the OS "show animations" toggle is off, and it duplicated the launcher's own animation
    toggle (`data-anim="off"`). Motion is now governed solely by that in-app setting, which never
    stops the functional spinners.

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

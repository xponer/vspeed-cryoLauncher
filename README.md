# ❄️ Cryo Launcher

A fast, modern Minecraft modpack launcher for Windows — create instances, install modpacks from **Modrinth & CurseForge**, manage mods, and launch **without PrismLauncher**. Includes a built-in AI assistant, server browser, world backups, and the **VSpeed** startup-optimization engine.

## ⬇️ Download (Windows)

**[→ Download the latest installer (Cryo-win-Setup.exe)](https://github.com/xponer/vspeed-atm10/releases/latest)**

1. Download `Cryo-win-Setup.exe` from the latest release.
2. Run it — installs per-user (no admin needed), adds a Desktop + Start-menu shortcut.
3. Windows SmartScreen may warn on first run (the app isn't code-signed yet) — click **More info → Run anyway**.
4. The launcher **auto-updates** from GitHub: new releases download in the background and apply on restart.

> First public test build — expect rough edges. Please report anything you hit. 🙏

## 🐞 Found a bug?

Use the in-app button **Settings → About → Report a bug**, or open one here:

**[→ Report a bug](https://github.com/xponer/vspeed-atm10/issues/new/choose)**

Please include your launcher version (Settings → About) and, if relevant, the launcher log (Settings → Self-Check → Open launcher log).

## ✨ Features

- **Create instances natively** — NeoForge / Forge / Fabric / Quilt / Vanilla, no Prism required
- **Install modpacks** from Modrinth (`.mrpack`) and CurseForge in one click
- **Mod browser** with version picker, SHA-512-verified downloads, and one-click updates
- **Dependency check** + duplicate-mod scanner before you launch
- **AI assistant** — diagnoses crashes, mod conflicts, and lag (bring your own free NVIDIA key)
- **Server browser** with live ping, **world backups**, **boot waterfall**, launch **profiles**
- **VSpeed engine** — AppCDS-based startup optimization (details below)
- **Discord Rich Presence**, auto-update, light/dark themes

---

# VSpeed — Dynamic AppCDS for NeoForge Modpacks

> **~13% faster startup** on All the Mods 10 (481 mods, 12 GB RAM) using Java's built-in Class Data Sharing.

No patches. No bytecode hacks. Just the JVM loading pre-shared class metadata instead of parsing 566 MB of JAR files from scratch every launch.

---

## Benchmark results (ATM10, 481 mods)

| Run | Time | vs Baseline |
|-----|------|-------------|
| Baseline (no CDS) | 79 s | — |
| **AppCDS enabled** | **68 s** | **−11 s (−13.9%)** |

*Hardware: Intel Core i5-12500H, 16 GB RAM, Windows 11. Results vary by disk speed and mod count.*

---

## How it works

Java 21 has [Application Class-Data Sharing (AppCDS)](https://docs.oracle.com/en/java/se/21/docs/specs/man/java.html#application-class-data-sharing) — it can snapshot every class loaded during a run into a binary archive. On subsequent launches the JVM maps that archive directly into memory, skipping JAR parsing, bytecode verification, and a big chunk of linking work.

The tricky part with NeoForge: mods are loaded by FML's custom `TransformingClassLoader`, not the standard classpath. Static AppCDS (the official tutorial approach) only captures ~5 000 bootstrap classes and gives ~3% gain. We need **Dynamic** AppCDS, which snapshots all classloaders at runtime.

```
Normal launch:
  JVM → parse 481 JARs → verify bytecode → link classes → run  (~79s)

With VSpeed archive:
  JVM → mmap 566 MB archive → classes already verified & linked → run  (~68s)
```

**The profiling pipeline:**

```
1. Launch ATM10 with -XX:+RecordDynamicDumpInfo
      ↓ game loads normally
2. jcmd <pid> VM.cds dynamic_dump ~/atm10_cds.jsa
      ↓ JVM writes 566 MB archive while game is still alive
3. Kill game
      ↓
4. All future launches: -Xshare:auto -XX:SharedArchiveFile=~/atm10_cds.jsa
```

> **Why not `-XX:ArchiveClassesAtExit`?**  
> On Oracle JDK 21.0.10 + 481 Mixin-transformed mods the JVM crashes inside `halt0` every time it tries to write the archive during shutdown (null pointer in the CDS writer iterating over Create mod's class metadata). Using `jcmd` while the game is running avoids the shutdown path entirely.

---

## Requirements

| Requirement | Version |
|-------------|---------|
| OS | Windows (tested on Windows 11) |
| Java | **Oracle JDK 21** (specifically tested on 21.0.10) |
| NeoForge | 1.21.1 |
| Launcher | PrismLauncher |
| Python | 3.9+ |
| Python package | `psutil` (auto-installed) |

---

## Installation

### 1. Build the mod

```bash
git clone https://github.com/xponer/vspeed-atm10.git
cd vspeed-atm10
.\gradlew.bat build
```

Output: `build/libs/vspeed-v2-1.0-SNAPSHOT.jar`

### 2. Configure the benchmark script

Open `scripts/vspeed_test.py` and edit the paths at the top:

```python
# ── Config ────────────────────────────────────────────────────────────
INSTANCE_ROOT = Path(r"C:\Users\YOUR_NAME\AppData\Roaming\PrismLauncher\instances\All the Mods 10 - ATM10")
PRISM_EXE     = Path(r"C:\Users\YOUR_NAME\AppData\Local\Programs\PrismLauncher\prismlauncher.exe")
```

Also check that the archive output paths (no spaces allowed — JVM arg limitation):

```python
CDS_ARCHIVE = Path.home() / "atm10_cds.jsa"   # e.g. C:\Users\you\atm10_cds.jsa
```

### 3. Deploy the mod

Copy `build/libs/vspeed-v2-1.0-SNAPSHOT.jar` into your ATM10 instance's `minecraft/mods/` folder.

---

## Usage

### First run — profile + benchmark

```bash
cd vspeed-atm10
python scripts\vspeed_test.py
```

This runs 4 phases automatically:

| Phase | What happens |
|-------|-------------|
| **0 — Profiling** | Game launches, loads fully, Python calls `jcmd VM.cds dynamic_dump`, game is killed. Archive created (~566 MB). Takes ~300 s. |
| **1 — Baseline** | Game launches with `-Xshare:off`. Measures stock startup time. |
| **2 — AppCDS only** | Game launches with the new archive. Measures CDS gain. |
| **3 — AppCDS + mod active** | Same as Phase 2 with all VSpeed mixins enabled (sanity check). |

Example output:
```
RESULTS
  Baseline                    : 79s
  AppCDS only                 : 69s   -10s (12.7% faster)
  AppCDS + vspeed mod         : 68s   -11s (13.9% faster)
  Archive : 566 MB
```

### Permanent setup (no benchmark, just faster launches)

After the profiling run, add these JVM args to your ATM10 instance in PrismLauncher:

```
-Xshare:auto -XX:SharedArchiveFile=C:/Users/YOUR_NAME/atm10_cds.jsa
```

*PrismLauncher → right-click instance → Edit → Settings → Java → JVM arguments*

### Re-profiling (after mod updates)

The CDS archive is keyed to the exact set of classes. When you add, remove, or update mods the old archive is silently ignored by the JVM. Re-profile:

```bash
python scripts\vspeed_test.py --skip-build --reprofile
```

### Skip Gradle build (already built)

```bash
python scripts\vspeed_test.py --skip-build
```

---

## Project structure

```
vspeed-atm10/
├── vspeed-loader/          # NeoForge 1.21.1 mod (Java)
│   └── src/main/java/dev/vspeed/
│       ├── VSpeedMod.java              # FMLLoadCompleteEvent hook, writes signal file
│       └── mixin/
│           ├── VSpeedMixinPlugin.java  # Conditional mixin loader
│           ├── ParallelReloadMixin.java    # no-op (benchmarked, no gain)
│           └── ParallelModelBakingMixin.java  # no-op (unsafe, reverted)
├── vspeed-agent/           # Java agent (class loading instrumentation, experimental)
├── scripts/
│   └── vspeed_test.py      # Full benchmark pipeline
├── build.gradle
└── settings.gradle
```

---

## Adapting to other modpacks

VSpeed works with any NeoForge 1.21.1 modpack on PrismLauncher. Changes needed in `vspeed_test.py`:

```python
INSTANCE_ROOT = Path(r"...\instances\Your Modpack Name")
PRISM_EXE     = Path(r"...\prismlauncher.exe")
CDS_ARCHIVE   = Path.home() / "yourpack_cds.jsa"   # pick any path without spaces
```

The mod JAR itself is universal — it only writes a `vspeed_signal.done` file when the game finishes loading and exits if `-Dvspeed.benchmark=true` is set.

---

## Known limitations

- **Windows only** — tested on Windows 11. Linux/macOS would need path changes and `javaw` → `java` process detection tweaks.
- **Oracle JDK 21** — `VM.cds dynamic_dump` command name is Oracle-specific. OpenJDK / Temurin uses `VM.dynamicdump` instead. The script uses the Oracle variant.
- **Archive is machine-specific** — the `.jsa` file is not portable between machines or Java versions.
- **Profiling run is slow** — `-XX:+RecordDynamicDumpInfo` adds ~3× overhead during profiling. This is a one-time cost.

---

## License

MIT

#!/usr/bin/env python3
"""
VSpeed Benchmark — AppCDS for ATM10.

Phase 0  Profiling: game loads with -XX:DumpLoadedClassList, script kills it,
                    then runs 'java -Xshare:dump' to build the archive.
                    Does NOT require graceful JVM exit.
Phase 1  Baseline:  -Xshare:off
Phase 2  Optimized: -Xshare:auto + archive

Usage:
  python vspeed_test.py
  python vspeed_test.py --reprofile   # delete archive and redo profiling
  python vspeed_test.py --skip-build  # skip Gradle
"""

import argparse
import os
import re
import shlex
import shutil
import stat
import subprocess
import sys
import threading
import time
from pathlib import Path

# ── Config ────────────────────────────────────────────────────────────────────

INSTANCE_ROOT = Path(r"C:\Users\xponer\AppData\Roaming\PrismLauncher\instances\All the Mods 10 - ATM10")
PRISM_EXE     = Path(r"C:\Users\xponer\AppData\Local\Programs\PrismLauncher\prismlauncher.exe")
PROJECT_ROOT  = Path(__file__).parent.parent

MINECRAFT_DIR = INSTANCE_ROOT / "minecraft"
INSTANCE_CFG  = INSTANCE_ROOT / "instance.cfg"
LATEST_LOG    = MINECRAFT_DIR / "logs" / "latest.log"
SIGNAL_FILE   = MINECRAFT_DIR / "vspeed_signal.done"

# No spaces in these paths — JVM arg parsing breaks on spaces
CDS_ARCHIVE            = Path.home() / "atm10_cds.jsa"
CDS_CLASSLIST          = Path.home() / "atm10_classlist.txt"
CDS_CLASSLIST_FILTERED = Path.home() / "atm10_classlist.filtered.txt"
CDS_CLASSPATH_FILE     = Path.home() / "atm10_classpath.txt"   # saved classpath for dump retries

MOD_SRC    = PROJECT_ROOT / "build" / "libs" / "vspeed-v2-1.0-SNAPSHOT.jar"
MOD_DEPLOY = MINECRAFT_DIR / "mods" / "vspeed-v2-1.0-SNAPSHOT.jar"

JFR_OUTPUT = Path.home() / "atm10_startup.jfr"   # Java Flight Recorder output

# JEP 483 AOT (JDK 24+) — Ahead-of-Time Class Loading & Linking
# Workflow: record → create → run  (3 steps, like CDS but handles custom classloaders)
AOT_CONFIG = Path.home() / "atm10_aot.aotconf"  # written by -XX:AOTMode=record
AOT_CACHE  = Path.home() / "atm10_aot.aot"       # written by -XX:AOTMode=create

# G1GC tuning — for 12 GB heap, heavy-allocation startup (481 mods)
# JFR showed 15 pauses / 4.04s total / 959ms max during ATM10 startup.
# Root cause: default IHOP=45% lets heap fill up before concurrent GC starts → Full GC.
#
# Only product-tier flags here — G1NewSizePercent / G1MaxNewSizePercent / SurvivorRatio
# are diagnostic flags in Oracle JDK 21 and require -XX:+UnlockDiagnosticVMOptions;
# without it the JVM prints "Unrecognized VM option" and exits before loading anything.
G1GC_FLAGS = [
    "-XX:MaxGCPauseMillis=200",               # G1 targets ≤200ms stop-the-world pauses
    "-XX:G1HeapRegionSize=16m",               # 16m regions for 12 GB heap (default 8m)
    "-XX:InitiatingHeapOccupancyPercent=15",  # start concurrent GC at 15% full (default 45%)
]

PRISM_LOG_DIR = INSTANCE_ROOT.parent.parent / "logs"   # PrismLauncher session logs

GAME_TIMEOUT             = 600   # 10 min — max wait for game to load
DUMP_TIMEOUT             = 900   # 15 min — static dump fallback timeout
PROFILE_SHUTDOWN_TIMEOUT = 600   # 10 min — graceful JVM exit during Dynamic CDS profiling

# ── Dependencies ──────────────────────────────────────────────────────────────

try:
    import psutil
except ImportError:
    subprocess.run([sys.executable, "-m", "pip", "install", "psutil"], check=True)
    import psutil

# ── Terminal ──────────────────────────────────────────────────────────────────

_C = {"g": "\033[92m", "y": "\033[93m", "r": "\033[91m", "c": "\033[96m", "w": "\033[97m"}
_R = "\033[0m"

def col(text, color):
    return f"{_C[color]}{text}{_R}"

def log(msg, color="w"):
    print(col(msg, color), flush=True)

def progress(msg):
    print(f"  {col(msg, 'y')}   ", end="\r", flush=True)

# ── Process management ────────────────────────────────────────────────────────

def kill_java_and_prism():
    killed = set()
    for proc in psutil.process_iter(["pid", "name"]):
        try:
            n = proc.info["name"].lower()
            if "java" in n or "prism" in n:
                proc.kill()
                killed.add(proc.info["name"])
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    if killed:
        time.sleep(2)
        log(f"  Killed: {', '.join(sorted(killed))}", "y")


def wait_for_java_exit(timeout_secs):
    """
    Block until all javaw.exe processes have terminated or the timeout expires.
    Used during Dynamic CDS profiling: the JVM writes the archive only after all
    Java shutdown hooks have run (NeoForge mod shutdown hooks, then JVM cleanup).
    Returns True if clean exit, False if timed out.
    """
    start = time.time()
    while True:
        alive = False
        for proc in psutil.process_iter(["name"]):
            try:
                if "javaw" in proc.info["name"].lower():
                    alive = True
                    break
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        if not alive:
            print()
            return True
        elapsed = int(time.time() - start)
        if elapsed >= timeout_secs:
            print()
            return False
        progress(f"⏱  {elapsed}s — waiting for JVM to exit (NeoForge shutdown + archive write)...")

# ── Capture Java classpath from live process ──────────────────────────────────

def _expand_argfile(path_str):
    """Expand @argfile into a list of args."""
    try:
        content = Path(path_str).read_text(encoding="utf-8", errors="ignore")
        result = []
        for line in content.splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                try:
                    result.extend(shlex.split(line, posix=False))
                except ValueError:
                    result.append(line)
        return result
    except Exception:
        return []

def capture_java_info():
    """
    Find running Minecraft Java process, return (java_exe, classpath).
    Handles @argfile expansion (PrismLauncher uses it when cmdline is very long).
    """
    for proc in psutil.process_iter(["name", "cmdline", "exe"]):
        try:
            if "java" not in proc.info.get("name", "").lower():
                continue
            raw = [str(a) for a in (proc.info.get("cmdline") or [])]
            if not raw:
                continue

            # Expand @argfiles
            expanded = []
            for arg in raw:
                if arg.startswith("@"):
                    expanded.extend(_expand_argfile(arg[1:]))
                else:
                    expanded.append(arg)

            joined = " ".join(expanded).lower()
            if not any(kw in joined for kw in ("net.minecraft", "cpw.mods", "neoforged", "fml")):
                continue

            java_exe = proc.info.get("exe") or raw[0]

            # Find classpath
            for i, arg in enumerate(expanded):
                if arg in ("-cp", "-classpath") and i + 1 < len(expanded):
                    return java_exe, expanded[i + 1]
            for arg in expanded:
                if arg.startswith("-cp="):
                    return java_exe, arg[4:]
                if arg.startswith("-classpath="):
                    return java_exe, arg[11:]
        except (psutil.NoSuchProcess, psutil.AccessDenied, Exception):
            pass
    return None, None

# ── instance.cfg ──────────────────────────────────────────────────────────────

_ORIGINAL_CFG: str | None = None
_OUR_KEYS = ("JvmArgs=", "JavaArgs=", "OverrideJavaArgs=", "OverrideJava=")


def _parse_ini_sections(text):
    """Parse Qt INI text into [(section_name_or_None, [lines]), ...]."""
    sections = []
    current_name = None
    current_lines = []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]") and len(stripped) > 2:
            sections.append((current_name, current_lines))
            current_name = stripped[1:-1]
            current_lines = []
        else:
            current_lines.append(line)
    sections.append((current_name, current_lines))
    return sections


def _rebuild_ini(sections):
    """Reconstruct INI text from parsed section list."""
    out = []
    for name, lines in sections:
        if name is not None:
            out.append(f"[{name}]")
        out.extend(lines)
    return "\n".join(out) + "\n"


def set_jvm_args(args):
    """Inject JVM args into the [General] section of instance.cfg.

    Saves a snapshot of the original file the first time it is called so that
    reset_jvm_args() can restore it exactly, preserving any args the user set
    via the PrismLauncher GUI.
    """
    global _ORIGINAL_CFG
    if not INSTANCE_CFG.exists():
        raise FileNotFoundError(f"instance.cfg not found: {INSTANCE_CFG}")

    original = INSTANCE_CFG.read_text(encoding="utf-8")
    if _ORIGINAL_CFG is None:
        _ORIGINAL_CFG = original  # save once; restored verbatim by reset_jvm_args()

    sections = _parse_ini_sections(original)
    modified = False
    for i, (name, lines) in enumerate(sections):
        if name == "General":
            # Drop any keys we own, then re-add them with new values
            new_lines = [l for l in lines if not any(l.startswith(k) for k in _OUR_KEYS)]
            new_lines.append("OverrideJavaArgs=true")
            if args:
                new_lines.append("JvmArgs=" + " ".join(args))
            sections[i] = (name, new_lines)
            modified = True
            break

    if not modified:
        raise ValueError("instance.cfg has no [General] section — unexpected format")

    INSTANCE_CFG.write_text(_rebuild_ini(sections), encoding="utf-8", newline="\n")


def reset_jvm_args():
    """Restore instance.cfg to its original state.

    If a snapshot was saved by set_jvm_args(), it is written back verbatim.
    Otherwise (startup cleanup after a crash) our keys are scrubbed from
    [General] without touching anything else.
    """
    global _ORIGINAL_CFG
    if _ORIGINAL_CFG is not None:
        INSTANCE_CFG.write_text(_ORIGINAL_CFG, encoding="utf-8", newline="\n")
        _ORIGINAL_CFG = None
    elif INSTANCE_CFG.exists():
        # No snapshot — just scrub any keys we may have left from a previous crash
        text = INSTANCE_CFG.read_text(encoding="utf-8")
        sections = _parse_ini_sections(text)
        for i, (name, lines) in enumerate(sections):
            if name == "General":
                sections[i] = (name, [l for l in lines
                                      if not any(l.startswith(k) for k in _OUR_KEYS)])
                break
        INSTANCE_CFG.write_text(_rebuild_ini(sections), encoding="utf-8", newline="\n")

# ── Log monitor ───────────────────────────────────────────────────────────────

def _log_monitor(stop, results):
    pos = 0
    while not stop.is_set():
        if LATEST_LOG.exists():
            try:
                # If the file shrank (Minecraft recreated it for a new run), reset.
                cur_size = LATEST_LOG.stat().st_size
                if cur_size < pos:
                    pos = 0
                with open(LATEST_LOG, "r", encoding="utf-8", errors="ignore") as f:
                    f.seek(pos)
                    for line in f:
                        m = re.search(r"STARTUP_TIME_SECONDS: (\d+)", line)
                        if m:
                            results["log_time"] = int(m.group(1))
                        # Phase timing: "[vspeed-Metrics] PHASE_COMMON_SETUP:  8423 ms"
                        mp = re.search(r"PHASE_(\w+):\s+(\d+)\s*ms", line)
                        if mp:
                            results.setdefault("phases", {})[mp.group(1)] = int(mp.group(2))
                        if any(t in line for t in ("[VSpeed", "[vspeed")):
                            results.setdefault("output", []).append(line.strip())
                    pos = f.tell()
            except OSError:
                pass
        time.sleep(0.3)

def _show_crash_info(mon):
    """Extract and display the crash reason from Minecraft / PrismLauncher logs."""
    time.sleep(0.5)   # let the log file finish flushing

    def _print_excerpt(header, lines):
        log(f"\n  ── {header} {'─'*(46-len(header))}", "r")
        for ln in lines[:15]:
            log(f"    {ln.rstrip()}", "r")

    # 1. Check Minecraft latest.log — present if FML got far enough to open it
    if LATEST_LOG.exists() and LATEST_LOG.stat().st_size > 0:
        try:
            all_lines = LATEST_LOG.read_text(encoding="utf-8", errors="ignore").splitlines()
            exc_lines = []
            capturing = False
            for ln in all_lines:
                if not capturing and any(k in ln for k in
                        ("Exception", "FATAL", "Caused by:", "ERROR in thread")):
                    capturing = True
                if capturing:
                    exc_lines.append(ln)
                    if len(exc_lines) >= 15:
                        break
            if exc_lines:
                _print_excerpt("latest.log", exc_lines)
                return
        except OSError:
            pass

    # 2. Fall back to most-recent PrismLauncher-N.log
    if PRISM_LOG_DIR.exists():
        logs = sorted(PRISM_LOG_DIR.glob("PrismLauncher-*.log"),
                      key=lambda p: p.stat().st_mtime, reverse=True)
        for plog in logs[:1]:
            try:
                all_lines = plog.read_text(encoding="utf-8", errors="ignore").splitlines()
                err_lines = [ln for ln in all_lines if any(k in ln for k in (
                    "Exception", "Caused by:", "ERROR", "Exiting with",
                    "InaccessibleObjectException", "IllegalStateException",
                    "exit code",
                ))]
                if err_lines:
                    _print_excerpt(plog.name, err_lines[-15:])
            except OSError:
                pass


def wait_for_signal(timeout_secs):
    """Returns (startup_seconds, monitor_results).
    time = -1  → timeout
    time = -2  → JVM crashed before writing the signal file
    """
    results = {}
    stop = threading.Event()
    mon = threading.Thread(target=_log_monitor, args=(stop, results), daemon=True)
    mon.start()
    start = time.time()
    t = -1
    mc_pid  = None
    mc_proc = None

    try:
        while True:
            elapsed = int(time.time() - start)
            progress(f"⏱  {elapsed}s — waiting for game to load...")

            # ── Success: signal file or log message ──────────────────────────
            if SIGNAL_FILE.exists():
                try:
                    t = int(SIGNAL_FILE.read_text().strip())
                    print()
                    break
                except (ValueError, OSError):
                    pass
            if "log_time" in results:
                t = results["log_time"]
                print()
                break

            # ── Crash detection ───────────────────────────────────────────────
            # Step 1: latch onto the JVM PID once it appears
            if mc_pid is None:
                new_pid = _get_minecraft_pid()
                if new_pid:
                    mc_pid = new_pid
                    try:
                        mc_proc = psutil.Process(mc_pid)
                    except psutil.NoSuchProcess:
                        mc_pid = None   # gone already — will re-detect next loop

            # Step 2: check if the latched process is still alive
            elif mc_proc is not None:
                try:
                    alive = mc_proc.is_running() and mc_proc.status() != psutil.STATUS_ZOMBIE
                except psutil.NoSuchProcess:
                    alive = False

                if not alive:
                    print()
                    results["crashed"] = True
                    results["crash_pid"] = mc_pid
                    t = -2
                    break

            # ── Timeout ───────────────────────────────────────────────────────
            if elapsed >= timeout_secs:
                print()
                log("  Timed out.", "r")
                break

            time.sleep(0.5)
    finally:
        stop.set()
        mon.join(timeout=2)
    return t, results

# ── Common helpers ────────────────────────────────────────────────────────────

def before_run(jvm_args):
    kill_java_and_prism()
    if SIGNAL_FILE.exists():
        SIGNAL_FILE.unlink()
    if LATEST_LOG.exists():
        LATEST_LOG.write_text("", encoding="utf-8")
    set_jvm_args(jvm_args)

def after_run():
    reset_jvm_args()
    if SIGNAL_FILE.exists():
        SIGNAL_FILE.unlink()

def launch_prism():
    subprocess.Popen(
        [str(PRISM_EXE), "--launch", "All the Mods 10 - ATM10"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

# ── Classlist normaliser ──────────────────────────────────────────────────────

def filter_classlist(classlist_path, filtered_path, classpath_str):
    """
    Convert a JDK 21 rich classlist (from -XX:DumpLoadedClassList) to the
    simple one-name-per-line format that -XX:SharedClassListFile also accepts.

    JDK 21 DumpLoadedClassList writes:
        io/example/Foo id: 897 super: 896 source: foo.jar
    The 'super: N' field is a numeric back-reference.  If any entry whose id is
    referenced as a super is absent or reordered, the JVM aborts with
    "Super class id N is not yet loaded".  You CANNOT safely remove individual
    lines from this format.

    The simple format has no numeric references — the JVM just tries to load
    each named class from the classpath and silently skips those it cannot find.
    That handles mod classes (loaded by FML's classloader, not on -cp) without
    errors and without needing to scan classpath JARs.

    *classpath_str* is accepted for API compatibility but is not used.
    Returns (n_unique, 0).
    """
    lines = classlist_path.read_text(encoding="utf-8").splitlines()
    seen: set[str] = set()
    out: list[str] = []
    for line in lines:
        s = line.strip()
        # Skip blank lines, comments, and JVM directives (@lambda-form-invoker,
        # @lambda-proxy, @app, etc.).  These directives carry required arguments
        # that we'd lose when stripping metadata; without them the parser aborts.
        # They're JVM internals — dropping them doesn't prevent archive creation.
        if not s or s.startswith("#") or s.startswith("@"):
            continue
        class_name = s.split()[0]   # strip id:/super:/source: metadata
        if class_name not in seen:
            seen.add(class_name)
            out.append(class_name)

    filtered_path.write_text("\n".join(out) + "\n", encoding="utf-8")
    return len(out), 0


# ── Phase 0: Profiling + Dump ─────────────────────────────────────────────────

def phase_profile():
    """
    Dynamic CDS profiling via `jcmd VM.dynamicdump`.

    Why not -XX:ArchiveClassesAtExit?
      On this configuration (HotSpot 21.0.10 + 481 mods + Mixin transforms) the JVM
      crashes with EXCEPTION_ACCESS_VIOLATION inside jvm.dll halt0 every time it tries
      to write the archive during shutdown.  The archive is never created.

    This approach instead:
      1. Launches the game with only -Dvspeed.cds.profiling=true (no ArchiveClassesAtExit).
         VSpeedMod writes the signal file but does NOT call exit().
      2. After the signal, Python calls `jcmd <pid> VM.dynamicdump <archive>` while the
         JVM is still alive.  This writes the archive without touching the shutdown path.
      3. After jcmd returns, Python force-kills the JVM.
    """
    JCMD_DUMP_TIMEOUT = 300   # max seconds for jcmd VM.dynamicdump to complete

    log("\n" + "─" * 58, "c")
    log("  PHASE 0 — PROFILING  (jcmd VM.dynamicdump)", "c")
    log("─" * 58, "c")
    log(f"  Archive   : {CDS_ARCHIVE}")

    before_run([
        "-Dvspeed.cds.profiling=true",
        "-XX:+RecordDynamicDumpInfo",   # required for: jcmd VM.cds dynamic_dump
    ])

    log("\n  Launching Prism → ATM10...")
    log("  Python will jcmd-dump after load — do NOT close manually.", "y")
    launch_prism()

    t, mon = wait_for_signal(GAME_TIMEOUT)
    after_run()   # restore instance.cfg; JVM keeps running

    if t < 0:
        log("  Game never signalled — is vspeed mod in the mods folder?", "r")
        kill_java_and_prism()
        return False

    log(f"  Game loaded in {t}s.", "g")

    # ── Find JVM PID ─────────────────────────────────────────────────────────
    pid = _get_minecraft_pid()
    if pid is None:
        log("  Could not find Minecraft JVM PID.", "r")
        kill_java_and_prism()
        return False
    log(f"  Minecraft JVM PID: {pid}", "g")

    # ── Find jcmd ────────────────────────────────────────────────────────────
    java_exe = _java_exe_from_cfg()
    try:
        jcmd_exe = _jcmd_from_java(java_exe)
    except FileNotFoundError as e:
        log(f"  {e}", "r")
        kill_java_and_prism()
        return False
    log(f"  jcmd: {jcmd_exe}", "g")

    # ── Run jcmd VM.cds dynamic_dump ─────────────────────────────────────────
    # Oracle JDK 21 uses "VM.cds dynamic_dump <file>" (not "VM.dynamicdump")
    archive_arg = str(CDS_ARCHIVE).replace("\\", "/")
    log(f"\n  Running: jcmd {pid} VM.cds dynamic_dump {archive_arg}", "c")
    log(f"  (timeout {JCMD_DUMP_TIMEOUT}s — writing ~500 MB archive...)", "y")

    try:
        r = subprocess.run(
            [jcmd_exe, str(pid), "VM.cds", "dynamic_dump", archive_arg],
            capture_output=True, text=True,
            timeout=JCMD_DUMP_TIMEOUT,
        )
        out = (r.stdout + r.stderr).strip()
        if out:
            log(f"  jcmd output: {out}", "w")
        if r.returncode != 0:
            log(f"  jcmd returned exit code {r.returncode}", "y")
    except subprocess.TimeoutExpired:
        log(f"  jcmd timed out after {JCMD_DUMP_TIMEOUT}s.", "r")
        kill_java_and_prism()
        return False
    except Exception as e:
        log(f"  jcmd failed: {e}", "r")
        kill_java_and_prism()
        return False

    # ── Kill JVM ─────────────────────────────────────────────────────────────
    log("\n  Killing JVM...", "c")
    kill_java_and_prism()

    # ── Validate archive ──────────────────────────────────────────────────────
    if not CDS_ARCHIVE.exists():
        log("  Archive not created.", "r")
        log("  Possible causes:", "r")
        log("    • VM.dynamicdump not supported by this JDK (check jcmd output above)", "r")
        log("    • JVM crashed or exited before jcmd ran", "r")
        log("    • Archive path has spaces or special characters", "r")
        return False

    size_mb = CDS_ARCHIVE.stat().st_size / (1024 * 1024)
    if size_mb < 5:
        log(f"  Archive too small ({size_mb:.1f} MB) — dump may be incomplete.", "r")
        CDS_ARCHIVE.unlink()
        return False

    log(f"  Archive   : {size_mb:.0f} MB  ✓  (jcmd VM.dynamicdump)", "g")
    return True

# ── Phase 0b (standalone): Dump only — reuse existing classlist ──────────────

def _java_exe_from_cfg():
    """Read JavaPath from instance.cfg [General] without modifying anything."""
    if not INSTANCE_CFG.exists():
        return None
    sections = _parse_ini_sections(INSTANCE_CFG.read_text(encoding="utf-8"))
    for name, lines in sections:
        if name == "General":
            for line in lines:
                if line.startswith("JavaPath="):
                    return line[9:].strip()
    return None


def _get_minecraft_pid():
    """Return PID of the running Minecraft JVM process, or None."""
    for proc in psutil.process_iter(["pid", "name", "cmdline"]):
        try:
            if "java" not in proc.info.get("name", "").lower():
                continue
            raw = [str(a) for a in (proc.info.get("cmdline") or [])]
            expanded = []
            for arg in raw:
                if arg.startswith("@"):
                    expanded.extend(_expand_argfile(arg[1:]))
                else:
                    expanded.append(arg)
            joined = " ".join(expanded).lower()
            if any(kw in joined for kw in ("net.minecraft", "cpw.mods", "neoforged", "fml")):
                return proc.info["pid"]
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    return None


def _jcmd_from_java(java_exe):
    """Derive jcmd.exe path from java.exe path. Raises FileNotFoundError if not found."""
    if not java_exe:
        raise FileNotFoundError("java_exe is None — JavaPath missing from instance.cfg")
    for name in ("jcmd.exe", "jcmd"):
        p = Path(java_exe).parent / name
        if p.exists():
            return str(p)
    raise FileNotFoundError(f"jcmd not found alongside {java_exe}")


def phase_dump_only():
    """
    Run only the classlist-filter + dump step, reusing the existing classlist
    and the classpath saved by the previous profiling run.
    Called when classlist + classpath file exist but the archive is missing
    (e.g. after a previous dump timeout).
    """
    log("\n" + "─" * 58, "c")
    log("  PHASE 0b — DUMP RETRY  (classlist already exists)", "c")
    log("─" * 58, "c")

    java_exe = _java_exe_from_cfg()
    if not java_exe:
        log("  Could not find JavaPath in instance.cfg.", "r")
        return False
    classpath = CDS_CLASSPATH_FILE.read_text(encoding="utf-8").strip()

    log(f"  Classlist : {CDS_CLASSLIST.stat().st_size // 1024} KB", "w")
    log(f"  Java      : {java_exe}", "w")
    log(f"  CP entries: {classpath.count(';') + 1}", "w")

    log("  Normalising classlist (stripping id/super/source metadata)...", "c")
    n_kept, _ = filter_classlist(CDS_CLASSLIST, CDS_CLASSLIST_FILTERED, classpath)
    log(f"  {n_kept} unique class names → simplified classlist", "g")

    log(f"\n  Running java -Xshare:dump  (timeout {DUMP_TIMEOUT}s)...", "c")
    java_console = str(Path(java_exe).parent / "java.exe")
    if not Path(java_console).exists():
        java_console = java_exe
    archive_arg  = str(CDS_ARCHIVE).replace("\\", "/")
    filtered_arg = str(CDS_CLASSLIST_FILTERED).replace("\\", "/")
    dump_cmd = [
        java_console, "-Xshare:dump",
        f"-XX:SharedClassListFile={filtered_arg}",
        f"-XX:SharedArchiveFile={archive_arg}",
        "-cp", classpath,
    ]
    try:
        r = subprocess.run(dump_cmd, timeout=DUMP_TIMEOUT)
    except subprocess.TimeoutExpired:
        log(f"  Dump timed out after {DUMP_TIMEOUT}s.", "r")
        return False

    if r.returncode != 0:
        log(f"  Dump exited {r.returncode} (non-zero may still produce an archive).", "y")

    if not CDS_ARCHIVE.exists():
        log("  Archive not created.", "r")
        return False

    size_mb = CDS_ARCHIVE.stat().st_size / (1024 * 1024)
    if size_mb < 5:
        log(f"  Archive too small ({size_mb:.1f} MB) — dump failed.", "r")
        CDS_ARCHIVE.unlink()
        return False

    log(f"  Archive   : {size_mb:.0f} MB  ✓", "g")
    return True


# ── JFR profiling ─────────────────────────────────────────────────────────────

def _jfr_exe():
    java_exe = _java_exe_from_cfg()
    if not java_exe:
        raise FileNotFoundError("JavaPath missing from instance.cfg")
    for name in ("jfr.exe", "jfr"):
        p = Path(java_exe).parent / name
        if p.exists():
            return str(p)
    raise FileNotFoundError(f"jfr tool not found alongside {java_exe}")


def analyze_jfr(jfr_path):
    """Parse a JFR recording and print an actionable startup breakdown."""
    try:
        jfr = _jfr_exe()
    except FileNotFoundError as e:
        log(f"  {e}", "r")
        return

    log("\n" + "─" * 58, "c")
    log("  JFR ANALYSIS", "c")
    log("─" * 58, "c")

    # ── 1. GC pauses ─────────────────────────────────────────────────────────
    log("\n  [GC Pauses]", "c")
    r = subprocess.run(
        [jfr, "print", "--events", "jdk.GarbageCollection", str(jfr_path)],
        capture_output=True, text=True, timeout=60,
    )
    gc_pauses = [float(m.group(1))
                 for line in r.stdout.splitlines()
                 for m in [re.search(r'duration\s*=\s*([\d.]+)\s*ms', line)] if m]
    if gc_pauses:
        total_gc_ms = sum(gc_pauses)
        log(f"  Count : {len(gc_pauses)}", "w")
        log(f"  Total : {total_gc_ms / 1000:.2f} s", "w")
        log(f"  Max   : {max(gc_pauses):.0f} ms", "w")
        if total_gc_ms > 5000:
            log("  ⚠  GC is significant — G1GC tuning will reduce pauses", "y")
        else:
            log("  ✓  GC not a bottleneck", "g")
    else:
        log("  No GC events found (or couldn't parse)", "y")

    # ── 2. Class loading breakdown ────────────────────────────────────────────
    log("\n  [Class Loading — top packages]", "c")
    pkg_counts: dict[str, int] = {}
    total_classes = 0
    proc = subprocess.Popen(
        [jfr, "print", "--events", "jdk.ClassLoad", str(jfr_path)],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
    )
    for line in proc.stdout:
        m = re.search(r'loadedClass\s*=\s*([\w$./\[\]]+)', line)
        if m:
            cls = m.group(1).replace("/", ".")
            total_classes += 1
            parts = cls.split(".")
            # group by first 2 segments (e.g. "net.minecraft", "com.simibubi")
            pkg = ".".join(parts[:2]) if len(parts) >= 2 else parts[0]
            pkg_counts[pkg] = pkg_counts.get(pkg, 0) + 1
    proc.wait()

    log(f"  Total loaded : {total_classes:,}", "w")
    if total_classes == 0:
        log("  ✓  0 ClassLoad events — AppCDS is loading classes from the archive", "g")
        log("     (JVM skips ClassLoad instrumentation for CDS-mapped classes)", "w")
    elif pkg_counts:
        top = sorted(pkg_counts.items(), key=lambda x: -x[1])[:20]
        max_count = top[0][1] if top else 1
        for pkg, count in top:
            bar = "█" * max(1, round(count / max_count * 30))
            log(f"  {pkg:<38} {count:>6}  {bar}", "w")

    # ── 3. CPU load ───────────────────────────────────────────────────────────
    log("\n  [CPU Load during startup]", "c")
    r = subprocess.run(
        [jfr, "print", "--events", "jdk.CPULoad", str(jfr_path)],
        capture_output=True, text=True, timeout=60,
    )
    # jfr print outputs CPU values already in % form (e.g. "jvmUser = 22.39 %")
    # Do NOT multiply by 100 — the regex captures the numeric part only.
    cpu_vals = [float(m.group(1))
                for line in r.stdout.splitlines()
                for m in [re.search(r'jvmUser\s*=\s*([\d.]+)', line)] if m]
    if cpu_vals:
        avg_cpu = sum(cpu_vals) / len(cpu_vals)
        max_cpu = max(cpu_vals)
        log(f"  Avg JVM CPU : {avg_cpu:.1f}%", "w")
        log(f"  Max JVM CPU : {max_cpu:.1f}%", "w")
        if avg_cpu > 60:
            log("  → CPU-bound startup: class init/JIT dominant", "y")
            log("    Best next step: JDK 24 + Project Leyden (AOT class linking)", "y")
        else:
            log("  → I/O-bound startup: disk reads dominant", "y")
            log("    Best next step: RAM disk or prefetch script", "y")

    # ── 4. Thread contention ──────────────────────────────────────────────────
    log("\n  [Monitor Waits > 10ms]", "c")
    r = subprocess.run(
        [jfr, "print", "--events", "jdk.JavaMonitorWait", str(jfr_path)],
        capture_output=True, text=True, timeout=60,
    )
    long_waits = [float(m.group(1))
                  for line in r.stdout.splitlines()
                  for m in [re.search(r'duration\s*=\s*([\d.]+)\s*ms', line)] if m
                  if float(m.group(1)) > 10]
    if long_waits:
        log(f"  {len(long_waits)} waits > 10ms  (total {sum(long_waits) / 1000:.1f} s)", "y")
        if sum(long_waits) > 5000:
            log("  ⚠  Heavy thread contention — sequential bottleneck in mod init", "r")
    else:
        log("  ✓  No heavy contention", "g")

    # ── 5. Open in JMC ────────────────────────────────────────────────────────
    java_exe = _java_exe_from_cfg()
    jmc = Path(java_exe).parent / "jmc.exe" if java_exe else None
    log(f"\n  JFR file saved: {jfr_path}", "g")
    if jmc and jmc.exists():
        log(f"  Open in JMC  : {jmc}", "w")
    else:
        log("  Open in JMC  : https://www.oracle.com/java/technologies/jdk-mission-control.html", "w")


def phase_jfr():
    """
    Attach JFR via jcmd shortly after game launch (avoids JVM-arg parsing issues
    with -XX:StartFlightRecording's commas/equals signs in PrismLauncher).
    Misses only the first ~3s of startup; captures all of mod init + resource load.
    """
    JFR_ATTACH_DELAY = 3    # seconds after launch before jcmd JFR.start
    JFR_DUMP_TIMEOUT = 120

    if JFR_OUTPUT.exists():
        try:
            os.chmod(JFR_OUTPUT, stat.S_IWRITE | stat.S_IREAD)
            JFR_OUTPUT.unlink()
        except OSError:
            pass

    log("\n" + "─" * 58, "c")
    log("  JFR PROFILING  (startup breakdown)", "c")
    log("─" * 58, "c")
    log(f"  Output: {JFR_OUTPUT}")

    # Use CDS archive if available so profiling reflects real-world conditions
    jvm_args = []
    if CDS_ARCHIVE.exists():
        jvm_args += ["-Xshare:auto", f"-XX:SharedArchiveFile={str(CDS_ARCHIVE).replace(chr(92), '/')}"]
        log("  (CDS archive active — profiling reflects optimised launch)", "y")
    else:
        jvm_args.append("-Xshare:off")
        log("  (no CDS archive — profiling reflects baseline launch)", "y")

    before_run(jvm_args)
    log("\n  Launching game...")
    launch_prism()

    # Poll for JVM PID — PrismLauncher takes a variable amount of time to spawn javaw
    java_exe = _java_exe_from_cfg()
    jcmd_exe = None
    pid = None
    try:
        jcmd_exe = _jcmd_from_java(java_exe)
    except FileNotFoundError as e:
        log(f"  {e}", "r")

    if jcmd_exe:
        log("  Polling for JVM PID (up to 60s)...", "y")
        for _ in range(120):          # 120 × 0.5s = 60s max
            pid = _get_minecraft_pid()
            if pid:
                break
            time.sleep(0.5)

        if pid:
            log(f"  JVM found: PID {pid}", "g")
            r = subprocess.run(
                [jcmd_exe, str(pid), "JFR.start", "name=startup", "settings=profile"],
                capture_output=True, text=True, timeout=30,
            )
            out = (r.stdout + r.stderr).strip()
            log(f"  JFR.start → {out}", "g" if "Started" in out else "y")
        else:
            log("  JVM not found after 60s.", "r")

    # Wait for game to finish loading
    t, mon = wait_for_signal(GAME_TIMEOUT)
    after_run()

    if t < 0:
        log("  Game never signalled.", "r")
        kill_java_and_prism()
        return False

    log(f"  Game loaded in {t}s.", "g")

    # Dump JFR
    if pid and jcmd_exe:
        jfr_path_arg = str(JFR_OUTPUT).replace("\\", "/")
        log("\n  Dumping JFR...", "c")
        r = subprocess.run(
            [jcmd_exe, str(pid), "JFR.dump", "name=startup", f"filename={jfr_path_arg}"],
            capture_output=True, text=True, timeout=JFR_DUMP_TIMEOUT,
        )
        out = (r.stdout + r.stderr).strip()
        if out:
            log(f"  {out}", "g" if r.returncode == 0 else "y")

    kill_java_and_prism()

    if not JFR_OUTPUT.exists() or JFR_OUTPUT.stat().st_size < 1024:
        log("  JFR file not created or empty.", "r")
        return False

    size_mb = JFR_OUTPUT.stat().st_size / (1024 * 1024)
    log(f"\n  JFR file: {size_mb:.1f} MB  ✓", "g")

    analyze_jfr(JFR_OUTPUT)
    return True


# ── OS cache prefetch ─────────────────────────────────────────────────────────

def prefetch_to_os_cache():
    """
    Read every JAR in mods/ and libraries/ into the Windows OS file cache.

    JFR showed 23.5% avg CPU during a 72s startup — the JVM is blocking on
    disk reads ~76% of the time.  A single sequential pass through all JARs
    before launch converts those random-read stalls into OS-cache hits.

    Returns (elapsed_seconds, total_mb).
    """
    targets = [MINECRAFT_DIR / "mods", MINECRAFT_DIR / "libraries"]
    total_bytes = 0
    total_files = 0
    t0 = time.time()

    for base in targets:
        if not base.exists():
            continue
        for jar in base.rglob("*.jar"):
            try:
                with open(jar, "rb") as fh:
                    while fh.read(1 << 20):   # 1 MB chunks; GC frees each one
                        pass
                total_bytes += jar.stat().st_size
                total_files += 1
            except OSError:
                pass
            if total_files % 25 == 0:
                progress(f"  Prefetching... {total_files} JARs  "
                         f"{total_bytes / (1024 * 1024):.0f} MB")

    elapsed = time.time() - t0
    print()
    log(f"  Prefetched {total_files} JARs → {total_bytes / (1024*1024):.0f} MB "
        f"in {elapsed:.1f}s", "g")
    return elapsed, total_bytes / (1024 * 1024)


# ── JEP 483 AOT (JDK 24+) ────────────────────────────────────────────────────

def phase_aot_profile():
    """
    JDK 24+ JEP 483 — Step 1: record AOT configuration.

    Launches the game with -XX:AOTMode=record.  The JVM writes a small
    metadata file (AOT_CONFIG) that records every class loaded, in order,
    including classes loaded by FML's TransformingClassLoader and Mixin.
    Unlike -XX:ArchiveClassesAtExit, this file is written at JVM shutdown
    and is just metadata (a few MB), so the shutdown-time crash that
    plagued dynamic CDS doesn't apply here.

    After the game signals load-complete we send a graceful terminate
    (not halt) so the JVM runs shutdown hooks and flushes the aotconf.
    """
    AOT_GRACEFUL_TIMEOUT = 60   # seconds to wait for graceful exit after terminate

    log("\n" + "─" * 58, "c")
    log("  PHASE AOT RECORD  (JDK 24+ JEP 483 — step 1 of 2)", "c")
    log("─" * 58, "c")
    log(f"  AOT config: {AOT_CONFIG}", "w")

    config_arg = str(AOT_CONFIG).replace("\\", "/")

    # NOTE: AOTConfiguration is mutually exclusive with -Xshare:* and SharedArchiveFile.
    # The JEP 483 AOT cache supersedes CDS — do NOT combine them.
    before_run([
        "-XX:AOTMode=record",
        f"-XX:AOTConfiguration={config_arg}",
    ])

    log("\n  Launching Prism → ATM10 (AOT recording)...", "c")
    log("  Game will load normally. Python sends graceful shutdown after signal.", "y")
    launch_prism()

    t, mon = wait_for_signal(GAME_TIMEOUT)
    after_run()   # restore instance.cfg; JVM still running

    if t < 0:
        log("  Game never signalled.", "r")
        kill_java_and_prism()
        return False

    log(f"  Game loaded in {t}s. Sending graceful terminate for AOT flush...", "g")

    # Use terminate() (WM_CLOSE / SIGTERM) not kill() — JVM must run shutdown
    # hooks to flush the aotconf file.
    terminated = set()
    for proc in psutil.process_iter(["pid", "name"]):
        try:
            n = proc.info["name"].lower()
            if "javaw" in n or "prism" in n:
                proc.terminate()
                terminated.add(f"{proc.info['name']}:{proc.info['pid']}")
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    if terminated:
        log(f"  Terminated: {', '.join(sorted(terminated))}", "y")

    # Wait for graceful exit (aotconf flush)
    log(f"  Waiting up to {AOT_GRACEFUL_TIMEOUT}s for JVM to write aotconf...", "y")
    clean = wait_for_java_exit(AOT_GRACEFUL_TIMEOUT)
    if not clean:
        log("  JVM did not exit gracefully — force killing.", "y")
        kill_java_and_prism()

    if not AOT_CONFIG.exists():
        log("  AOT config NOT created.", "r")
        log("  → Is this JDK 24?  -XX:AOTMode=record requires JDK 24+.", "r")
        log("    Check: PrismLauncher → ATM10 instance → Java tab → Java path", "y")
        return False

    size_kb = AOT_CONFIG.stat().st_size // 1024
    log(f"  AOT config: {size_kb} KB  ✓", "g")
    return True


def phase_aot_create():
    """
    JDK 24+ JEP 483 — Step 2: create AOT cache via PrismLauncher.

    Launches with -XX:AOTMode=create.  The JVM loads every class recorded
    in the aotconf (including FML/Mixin-transformed ones), pre-links them,
    writes the AOT cache, then exits WITHOUT starting the game or calling
    main().  Takes 30–120 s depending on class count.
    """
    AOT_CREATE_TIMEOUT = 300   # 5 min max

    log("\n" + "─" * 58, "c")
    log("  PHASE AOT CREATE  (JDK 24+ JEP 483 — step 2 of 2)", "c")
    log("─" * 58, "c")
    log(f"  AOT cache:  {AOT_CACHE}", "w")

    if not AOT_CONFIG.exists():
        log("  AOT config missing — run without --skip-aot-profile first.", "r")
        return False

    config_arg = str(AOT_CONFIG).replace("\\", "/")
    cache_arg  = str(AOT_CACHE).replace("\\", "/")

    # NOTE: AOTConfiguration is mutually exclusive with -Xshare:* and SharedArchiveFile.
    before_run([
        "-XX:AOTMode=create",
        f"-XX:AOTConfiguration={config_arg}",
        f"-XX:AOTCache={cache_arg}",
    ])

    log("  Launching Prism → ATM10 (AOT create mode)...", "c")
    log("  JVM will load all recorded classes, write the cache, then exit.", "y")
    launch_prism()

    # Phase 1: wait for javaw to APPEAR (PrismLauncher takes several seconds to spawn it)
    log("  Waiting for JVM to start (up to 60s)...", "y")
    pid = None
    for _ in range(120):      # 120 × 0.5s = 60s max
        pid = _get_minecraft_pid()
        if pid:
            break
        time.sleep(0.5)

    if not pid:
        log("  JVM never started — check PrismLauncher logs.", "r")
        after_run()
        return False

    log(f"  JVM started (PID {pid}).  Waiting up to {AOT_CREATE_TIMEOUT}s "
        f"for cache write + exit...", "g")

    # Phase 2: wait for javaw to EXIT (JVM writes cache then exits automatically)
    clean = wait_for_java_exit(AOT_CREATE_TIMEOUT)
    after_run()

    if not clean:
        log(f"  Timed out after {AOT_CREATE_TIMEOUT}s.", "r")
        kill_java_and_prism()
        return False

    if not AOT_CACHE.exists():
        log("  AOT cache NOT created.", "r")
        log("  Possible causes:", "r")
        log("    • -XX:AOTMode=create requires JDK 24+ (not JDK 21)", "r")
        log("    • aotconf was recorded with a different JDK", "r")
        log("    • classpath mismatch (re-run --reprofile --aot)", "r")
        return False

    size_mb = AOT_CACHE.stat().st_size / (1024 * 1024)
    if size_mb < 1:
        log(f"  AOT cache too small ({size_mb:.1f} MB) — create likely failed.", "r")
        AOT_CACHE.unlink()
        return False

    log(f"  AOT cache: {size_mb:.0f} MB  ✓", "g")
    return True


# ── Phases 1 & 2: Timed runs ─────────────────────────────────────────────────

def phase_timed(label, jvm_args):
    log("\n" + "─" * 58, "c")
    log(f"  {label}", "c")
    log("─" * 58, "c")

    before_run(jvm_args)
    log("  Launching Prism → ATM10...")
    launch_prism()

    t, mon = wait_for_signal(GAME_TIMEOUT)

    kill_java_and_prism()
    after_run()

    if t == -2:
        log("  CRASHED — JVM exited before game finished loading", "r")
        _show_crash_info(mon)
    elif t > 0:
        log(f"  Result: {t}s", "g")
    else:
        log("  FAILED / timed out", "r")

    # ── Phase timing breakdown ────────────────────────────────────────────────
    if t > 0 and mon.get("phases"):
        phases = mon["phases"]
        log("\n  NeoForge phase timing (wall-clock, worst mod per phase):", "c")
        phase_order = [("COMMON_SETUP", "FMLCommonSetupEvent"),
                       ("CLIENT_SETUP", "FMLClientSetupEvent")]
        for key, name in phase_order:
            ms = phases.get(key)
            if ms is not None:
                bar = "█" * min(40, ms // 250)
                log(f"    {name:25s} {ms:5d} ms  {bar}", "c" if ms < 5000 else "r")
        # Any unexpected phases
        for key, ms in phases.items():
            if key not in ("COMMON_SETUP", "CLIENT_SETUP"):
                log(f"    {key:25s} {ms:5d} ms", "c")

    if mon.get("output"):
        log("\n  Mod output:", "y")
        for line in mon["output"][-10:]:
            log(f"    {line}", "y")

    return {"label": label, "time": t, "phases": mon.get("phases", {})}

# ── Build & deploy ────────────────────────────────────────────────────────────

def build():
    log("\n[Build] Compiling...", "c")
    r = subprocess.run(
        [str(PROJECT_ROOT / "gradlew.bat"), "build", "--parallel"],
        cwd=PROJECT_ROOT,
    )
    if r.returncode != 0:
        log("[Build] FAILED", "r"); sys.exit(1)
    log("[Build] OK", "g")

def deploy():
    if not MOD_SRC.exists():
        log(f"[Deploy] Mod JAR missing: {MOD_SRC}", "r"); sys.exit(1)
    (MINECRAFT_DIR / "mods").mkdir(parents=True, exist_ok=True)
    shutil.copy2(MOD_SRC, MOD_DEPLOY)
    log(f"[Deploy] {MOD_DEPLOY.name}", "g")

# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reprofile",  action="store_true", help="Delete archive and redo profiling")
    parser.add_argument("--skip-build", action="store_true", help="Skip Gradle build")
    parser.add_argument("--jfr",        action="store_true", help="Run JFR startup profiling only")
    parser.add_argument("--extended",   action="store_true",
                        help="Run extra phases: G1GC tuning, OS prefetch, and their combination")
    parser.add_argument("--aot",        action="store_true",
                        help="Run JEP 483 AOT pipeline: record → create → benchmark (JDK 24+)")
    args = parser.parse_args()

    log("=" * 58, "c")
    log("  VSpeed Benchmark  —  AppCDS", "c")
    log("=" * 58, "c")

    kill_java_and_prism()  # clear any stale processes / file locks from a previous run
    reset_jvm_args()       # restore instance.cfg if a previous run crashed mid-test

    if not args.skip_build:
        build()
    deploy()

    # ── JFR-only mode ─────────────────────────────────────────────────────────
    if args.jfr:
        phase_jfr()
        return

    # ── AOT mode (JDK 24+ JEP 483) ────────────────────────────────────────────
    if args.aot:
        if AOT_CONFIG.exists() and AOT_CACHE.exists():
            size_mb = AOT_CACHE.stat().st_size / (1024 * 1024)
            log(f"\n[AOT] Cache found: {size_mb:.0f} MB — skipping profiling.", "g")
            log(  "      Use --reprofile to rebuild.", "w")
        elif AOT_CONFIG.exists() and not AOT_CACHE.exists():
            log("\n[AOT] Config found but cache missing — retrying create step...", "y")
            if not phase_aot_create():
                log("\n[AOT] Create failed. Run with --reprofile --aot to start over.", "r")
                sys.exit(1)
            log("\n[Cooldown] 10 seconds...", "y")
            time.sleep(10)
        else:
            log("\n[AOT] No AOT files — running full pipeline (record → create)...", "y")
            if not phase_aot_profile():
                log("\n[AOT] Record failed.  Is this JDK 24?", "r"); sys.exit(1)
            log("\n[Cooldown] 10 seconds...", "y")
            time.sleep(10)
            if not phase_aot_create():
                log("\n[AOT] Create failed.", "r"); sys.exit(1)
            log("\n[Cooldown] 10 seconds...", "y")
            time.sleep(10)

        # Benchmark: CDS baseline vs JEP 483 AOT cache
        # The AOT cache is a superset of CDS — it REPLACES -Xshare:auto + SharedArchiveFile.
        archive_path = str(CDS_ARCHIVE).replace("\\", "/")
        aot_path     = str(AOT_CACHE).replace("\\", "/")

        log("\n[AOT Benchmark] Running CDS baseline (JDK 24, no AOT cache)...", "c")
        cds_run = phase_timed(
            "CDS only (baseline for AOT)",
            ["-Xshare:auto", f"-XX:SharedArchiveFile={archive_path}",
             "-Dvspeed.benchmark=true"],
        )

        log("\n[Cooldown] 10 seconds...", "y")
        time.sleep(10)

        log("\n[AOT Benchmark] Running JEP 483 AOT cache (replaces CDS)...", "c")
        aot_run = phase_timed(
            "JEP 483 AOT cache (no -Xshare flags)",
            [
                f"-XX:AOTCache={aot_path}",
                # AOT pre-initializes UnionFileSystem before ForgeWrapper runs its
                # reflective module-open setup.  Passing this explicitly at JVM
                # startup ensures java.lang.invoke is accessible before <clinit>.
                "--add-opens=java.base/java.lang.invoke=ALL-UNNAMED",
                "-Dvspeed.benchmark=true",
            ],
        )

        bt = cds_run["time"]
        at = aot_run["time"]

        log(f"\n{'='*58}", "c")
        log("  AOT RESULTS  (JDK 24 + JEP 483)", "c")
        log(f"{'='*58}", "c")
        log(f"  {'CDS only':<36}: {bt}s", "w")
        if at > 0 and bt > 0:
            diff = bt - at
            pct  = diff / bt * 100
            sign = f"-{diff}s ({pct:.1f}% faster)" if diff > 0 else (
                   f"+{abs(diff)}s ({abs(pct):.1f}% slower)" if diff < 0 else "no change")
            color = "g" if diff > 0 else ("r" if diff < 0 else "y")
            log(f"  {'CDS + AOT (JEP 483)':<36}: {at}s   {sign}", color)
        elif at <= 0:
            log(f"  {'CDS + AOT (JEP 483)':<36}: FAILED", "r")
        if AOT_CACHE.exists():
            log(f"\n  AOT cache : {AOT_CACHE.stat().st_size/(1024*1024):.0f} MB", "w")
        if CDS_ARCHIVE.exists():
            log(f"  CDS archive: {CDS_ARCHIVE.stat().st_size/(1024*1024):.0f} MB", "w")
        log("=" * 58, "c")
        return

    # Phase 0
    if args.reprofile:
        kill_java_and_prism()   # release any file locks before deleting the archive
        for f in (CDS_ARCHIVE, CDS_CLASSLIST, CDS_CLASSLIST_FILTERED, CDS_CLASSPATH_FILE,
                  AOT_CONFIG, AOT_CACHE):
            if f.exists():
                log(f"\n[CDS] Removing {f.name}...", "y")
                try:
                    # JVM creates CDS archives read-only on Windows — clear that first
                    os.chmod(f, stat.S_IWRITE | stat.S_IREAD)
                    f.unlink()
                except PermissionError as e:
                    log(f"  Cannot delete {f.name}: {e}", "r")
                    log("  Try:  Remove-Item -Force " + str(f), "y")
                    sys.exit(1)

    if CDS_ARCHIVE.exists():
        size_mb = CDS_ARCHIVE.stat().st_size / (1024 * 1024)
        log(f"\n[CDS] Archive found: {size_mb:.0f} MB — skipping profiling.", "g")
        log( "      Use --reprofile to rebuild.", "w")
    elif CDS_CLASSLIST.exists() and CDS_CLASSPATH_FILE.exists():
        # Classlist + classpath exist but archive is missing (e.g. previous dump timeout).
        # Re-run only the dump step — no need to relaunch the game.
        log("\n[CDS] Classlist found but archive missing — retrying dump step...", "y")
        if not phase_dump_only():
            log("\n[CDS] Dump retry failed. Run with --reprofile to start over.", "r")
            sys.exit(1)
        log("\n[Cooldown] 10 seconds...", "y")
        time.sleep(10)
    else:
        log("\n[CDS] No archive — running profiling phase...", "y")
        if not phase_profile():
            log("\n[CDS] Profiling failed.", "r"); sys.exit(1)
        log("\n[Cooldown] 10 seconds...", "y")
        time.sleep(10)

    # Phase 1 — baseline
    baseline = phase_timed(
        "PHASE 1 — BASELINE  (no CDS, no mixins)",
        ["-Xshare:off", "-Dvspeed.benchmark=true"],
    )

    log("\n[Cooldown] 10 seconds...", "y")
    time.sleep(10)

    # Phase 2 — AppCDS only
    archive_path = str(CDS_ARCHIVE).replace("\\", "/")
    cds_only = phase_timed(
        "PHASE 2 — AppCDS only",
        ["-Xshare:auto", f"-XX:SharedArchiveFile={archive_path}", "-Dvspeed.benchmark=true"],
    )

    log("\n[Cooldown] 10 seconds...", "y")
    time.sleep(10)

    # Phase 3 — AppCDS + vspeed mod loaded (no-op mixins, sanity check)
    combined = phase_timed(
        "PHASE 3 — AppCDS + vspeed mod active",
        [
            "-Xshare:auto", f"-XX:SharedArchiveFile={archive_path}",
            "-Dvspeed.benchmark=true",
            "-Dvspeed.optimized=true",   # activates mixins (all no-ops now)
        ],
    )

    # ── Extended phases (--extended) ──────────────────────────────────────────
    gc_phase        = None
    prefetch_phase  = None
    combo_phase     = None
    pre_secs        = 0.0
    pre_mb          = 0.0
    pre_secs2       = 0.0

    if args.extended:
        archive_path = str(CDS_ARCHIVE).replace("\\", "/")

        log("\n[Cooldown] 10 seconds...", "y")
        time.sleep(10)

        # Phase 4 — AppCDS + G1GC tuning
        gc_phase = phase_timed(
            "PHASE 4 — AppCDS + G1GC tuning",
            ["-Xshare:auto", f"-XX:SharedArchiveFile={archive_path}",
             "-Dvspeed.benchmark=true"] + G1GC_FLAGS,
        )

        log("\n[Cooldown] 10 seconds...", "y")
        time.sleep(10)

        # Phase 5 — AppCDS + Prefetch (warm OS file cache before launch)
        log("\n[Prefetch] Warming OS file cache...", "c")
        pre_secs, pre_mb = prefetch_to_os_cache()
        prefetch_phase = phase_timed(
            "PHASE 5 — AppCDS + Prefetch",
            ["-Xshare:auto", f"-XX:SharedArchiveFile={archive_path}",
             "-Dvspeed.benchmark=true"],
        )

        log("\n[Cooldown] 10 seconds...", "y")
        time.sleep(10)

        # Phase 6 — AppCDS + G1GC + Prefetch (best of both)
        log("\n[Prefetch] Warming OS file cache...", "c")
        pre_secs2, _ = prefetch_to_os_cache()
        combo_phase = phase_timed(
            "PHASE 6 — AppCDS + G1GC + Prefetch",
            ["-Xshare:auto", f"-XX:SharedArchiveFile={archive_path}",
             "-Dvspeed.benchmark=true"] + G1GC_FLAGS,
        )

    # ── Results ───────────────────────────────────────────────────────────────
    bt  = baseline["time"]
    ct  = cds_only["time"]
    cmt = combined["time"]

    log(f"\n{'='*58}", "c")
    log("  RESULTS", "c")
    log(f"{'='*58}", "c")

    def result_line(label, t, ref=None, note=""):
        if t <= 0:
            log(f"  {label:<36}: FAILED", "r")
            return
        suffix = f"   {note}" if note else ""
        if ref and ref > 0:
            diff = ref - t
            pct  = diff / ref * 100
            sign = f"-{diff}s ({pct:.1f}% faster)" if diff > 0 else (
                   f"+{abs(diff)}s ({abs(pct):.1f}% slower)" if diff < 0 else "no change")
            color = "g" if diff > 0 else ("r" if diff < 0 else "y")
            log(f"  {label:<36}: {t}s   {sign}{suffix}", color)
        else:
            log(f"  {label:<36}: {t}s{suffix}", "w")

    result_line("Baseline",              bt)
    result_line("AppCDS only",           ct,  bt)
    result_line("AppCDS + vspeed mod",   cmt, bt)

    if ct > 0 and cmt > 0:
        extra = ct - cmt
        if extra < 0:
            log(f"\n  vspeed mod overhead: +{abs(extra)}s vs CDS alone", "r")
        else:
            log("\n  vspeed mod: no overhead  ✓", "g")

    if args.extended:
        log("", "w")
        if gc_phase:
            result_line("AppCDS + G1GC tuning", gc_phase["time"], bt)
        if prefetch_phase:
            result_line("AppCDS + Prefetch",  prefetch_phase["time"], bt,
                        f"(+{pre_secs:.1f}s prefetch → {prefetch_phase['time'] + pre_secs:.0f}s wall)")
        if combo_phase:
            result_line("AppCDS + G1GC + Prefetch", combo_phase["time"], bt,
                        f"(+{pre_secs2:.1f}s prefetch → {combo_phase['time'] + pre_secs2:.0f}s wall)")

        # Best configuration recommendation
        candidates = [r for r in [gc_phase, prefetch_phase, combo_phase] if r and r["time"] > 0]
        if candidates and bt > 0:
            best = min(candidates, key=lambda r: r["time"])
            log(f"\n  Best config: {best['label'].split('—')[1].strip()}", "g")
            total_gain = bt - best["time"]
            log(f"  Total gain : -{total_gain}s ({total_gain/bt*100:.1f}% faster than baseline)", "g")

    if CDS_ARCHIVE.exists():
        log(f"\n  Archive : {CDS_ARCHIVE.stat().st_size/(1024*1024):.0f} MB", "w")
    log("=" * 58, "c")

if __name__ == "__main__":
    main()

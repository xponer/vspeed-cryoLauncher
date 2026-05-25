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
                with open(LATEST_LOG, "r", encoding="utf-8", errors="ignore") as f:
                    f.seek(pos)
                    for line in f:
                        m = re.search(r"STARTUP_TIME_SECONDS: (\d+)", line)
                        if m:
                            results["log_time"] = int(m.group(1))
                        if any(t in line for t in ("[VSpeed", "[vspeed")):
                            results.setdefault("output", []).append(line.strip())
                    pos = f.tell()
            except OSError:
                pass
        time.sleep(0.3)

def wait_for_signal(timeout_secs):
    """Returns (startup_seconds, monitor_results). time=-1 on timeout."""
    results = {}
    stop = threading.Event()
    mon = threading.Thread(target=_log_monitor, args=(stop, results), daemon=True)
    mon.start()
    start = time.time()
    t = -1
    try:
        while True:
            elapsed = int(time.time() - start)
            progress(f"⏱  {elapsed}s — waiting for game to load...")
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
    if pkg_counts:
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
    cpu_vals = [float(m.group(1)) * 100
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
    Launch game with Java Flight Recorder (profile settings), dump JFR via jcmd
    after the game finishes loading, then kill the JVM and analyze.
    """
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

    jfr_path_arg = str(JFR_OUTPUT).replace("\\", "/")

    # Use CDS archive if available so profiling reflects real-world conditions
    jvm_args = [
        f"-XX:StartFlightRecording=name=startup,settings=profile,dumponexit=true,filename={jfr_path_arg}",
    ]
    if CDS_ARCHIVE.exists():
        jvm_args += ["-Xshare:auto", f"-XX:SharedArchiveFile={str(CDS_ARCHIVE).replace(chr(92), '/')}"]
        log("  (using existing CDS archive — profiling reflects optimised launch)", "y")
    else:
        jvm_args.append("-Xshare:off")
        log("  (no CDS archive — profiling reflects baseline launch)", "y")

    before_run(jvm_args)
    log("\n  Launching game with JFR recording...")
    log("  Do NOT close manually — Python will dump and kill after load.", "y")
    launch_prism()

    t, mon = wait_for_signal(GAME_TIMEOUT)
    after_run()

    if t < 0:
        log("  Game never signalled.", "r")
        kill_java_and_prism()
        return False

    log(f"  Game loaded in {t}s.", "g")

    # Dump JFR while JVM is alive (more complete than dumponexit on force-kill)
    pid = _get_minecraft_pid()
    if pid:
        java_exe = _java_exe_from_cfg()
        try:
            jcmd_exe = _jcmd_from_java(java_exe)
            log("\n  Dumping JFR via jcmd...", "c")
            r = subprocess.run(
                [jcmd_exe, str(pid), "JFR.dump",
                 "name=startup", f"filename={jfr_path_arg}"],
                capture_output=True, text=True, timeout=JFR_DUMP_TIMEOUT,
            )
            out = (r.stdout + r.stderr).strip()
            if out:
                log(f"  {out}", "g" if r.returncode == 0 else "y")
        except Exception as e:
            log(f"  jcmd JFR.dump failed ({e}) — relying on dumponexit", "y")

    kill_java_and_prism()

    if not JFR_OUTPUT.exists() or JFR_OUTPUT.stat().st_size < 1024:
        log("  JFR file not created or empty.", "r")
        return False

    size_mb = JFR_OUTPUT.stat().st_size / (1024 * 1024)
    log(f"\n  JFR file: {size_mb:.1f} MB  ✓", "g")

    analyze_jfr(JFR_OUTPUT)
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

    if t > 0:
        log(f"  Result: {t}s", "g")
    else:
        log("  FAILED / timed out", "r")

    if mon.get("output"):
        log("\n  Mod output:", "y")
        for line in mon["output"][-10:]:
            log(f"    {line}", "y")

    return {"label": label, "time": t}

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

    # Phase 0
    if args.reprofile:
        kill_java_and_prism()   # release any file locks before deleting the archive
        for f in (CDS_ARCHIVE, CDS_CLASSLIST, CDS_CLASSLIST_FILTERED, CDS_CLASSPATH_FILE):
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

    # ── Results ───────────────────────────────────────────────────────────────
    bt  = baseline["time"]
    ct  = cds_only["time"]
    cmt = combined["time"]

    log(f"\n{'='*58}", "c")
    log("  RESULTS", "c")
    log(f"{'='*58}", "c")

    def result_line(label, t, ref=None):
        if t <= 0:
            log(f"  {label:<28}: FAILED", "r")
            return
        if ref and ref > 0:
            diff = ref - t
            pct  = diff / ref * 100
            sign = f"-{diff}s ({pct:.1f}% faster)" if diff > 0 else (
                   f"+{abs(diff)}s ({abs(pct):.1f}% slower)" if diff < 0 else "no change")
            color = "g" if diff > 0 else ("r" if diff < 0 else "y")
            log(f"  {label:<28}: {t}s   {sign}", color)
        else:
            log(f"  {label:<28}: {t}s", "w")

    result_line("Baseline",              bt)
    result_line("AppCDS only",           ct,  bt)
    result_line("AppCDS + vspeed mod",   cmt, bt)

    if ct > 0 and cmt > 0:
        extra = ct - cmt
        if extra > 0:
            log(f"\n  vspeed mod overhead     : -{extra}s (unexpected gain)", "g")
        elif extra < 0:
            log(f"\n  vspeed mod overhead     : +{abs(extra)}s regression vs CDS alone", "r")
        else:
            log("\n  vspeed mod: no overhead vs CDS alone  ✓", "g")

    if CDS_ARCHIVE.exists():
        log(f"\n  Archive : {CDS_ARCHIVE.stat().st_size/(1024*1024):.0f} MB", "w")
    log("=" * 58, "c")

if __name__ == "__main__":
    main()

/* ============================================================
   Cryo — Instance Detail tabs: Performance / Mods / Settings
   ============================================================ */
const { useState: tS, useEffect: tE, useMemo: tM, useRef: tRf } = React;

/* ============ BENCHMARK (real data-load measurement) ============ */
function BenchmarkCard({ instance, api, hasBridge, t, fmt }) {
  const LSK = "cryo.bench." + instance.id;
  const [bench, setBench] = tS(() => {
    try { return JSON.parse(localStorage.getItem(LSK) || "{}"); } catch { return {}; }
  });
  const [stats, setStats] = tS(null);
  const [busy, setBusy] = tS(false);

  // ── Auto boot-to-menu benchmark (the one-click automatic test) ──
  const AUTOK = "cryo.bench.auto." + instance.id;
  const [auto, setAuto] = tS(() => { try { return JSON.parse(localStorage.getItem(AUTOK) || "null"); } catch { return null; } });
  const [running, setRunning] = tS(false);
  const [prog, setProg] = tS(null); // { step, totalSteps, message, mode }

  function persist(next) { setBench(next); try { localStorage.setItem(LSK, JSON.stringify(next)); } catch {} }

  async function refresh() {
    if (!hasBridge || !api.getStats) return;
    const s = await api.getStats(instance.id).catch(() => ({ available: false }));
    setStats(s);
    if (s && s.available) {
      // mode "hit" => optimized (cached); "cold" => vanilla (scanned)
      const slot = s.mode === "hit" ? "optimized" : "vanilla";
      const next = { ...bench, [slot]: { totalMs: s.totalMs, totalEntries: s.totalEntries, types: s.types, at: s.updatedAt } };
      persist(next);
    }
  }

  // Auto-refresh the measurement when the instance reaches a ready state
  tE(() => {
    if (!hasBridge) return;
    refresh();
    function onState(e) { if (e.detail.id === instance.id && (e.detail.state === "ready" || e.detail.state === "hibernated")) setTimeout(refresh, 1200); }
    window.addEventListener("cryo:instanceStateChanged", onState);
    return () => window.removeEventListener("cryo:instanceStateChanged", onState);
  }, [hasBridge, instance.id]);

  async function launch(vanilla) {
    if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Preview mode", body: "Launch works in the desktop launcher." }); return; }
    setBusy(true);
    await api.launchInstance(instance.id, vanilla).catch(() => {});
    window.toast({
      tone: vanilla ? "neutral" : "accent",
      icon: vanilla ? "package" : "zap",
      title: vanilla ? "Launching VANILLA (no cache)" : "Launching OPTIMIZED",
      body: "Enter a world once so the data load is measured, then come back.",
    });
    setBusy(false);
  }

  // Live progress from the C# benchmark orchestrator
  tE(() => {
    function onProg(e) {
      const d = (e && e.detail) || {};
      if (d.phase === "done") {
        const res = { bootVanilla: d.bootVanilla, bootOptimized: d.bootOptimized,
                      deltaSeconds: d.deltaSeconds, deltaPercent: d.deltaPercent, at: Date.now() };
        setAuto(res); try { localStorage.setItem(AUTOK, JSON.stringify(res)); } catch {}
        setRunning(false); setProg(null);
        window.toast({ tone: "success", icon: "gauge", title: "Benchmark complete",
          body: (d.deltaSeconds > 0 ? "Optimized boot saved " + d.deltaSeconds + "s (" + d.deltaPercent + "%)"
                                    : "No measurable boot speed-up — see result below") });
      } else if (d.phase === "error") {
        setRunning(false); setProg(null);
        window.toast({ tone: "danger", icon: "alert", title: "Benchmark failed", body: d.message || "" });
      } else if (d.phase === "cancelled") {
        setRunning(false); setProg(null);
        window.toast({ tone: "warn", icon: "info", title: "Benchmark cancelled", body: "" });
      } else {
        setRunning(true);
        setProg({ step: d.step || 0, totalSteps: d.totalSteps || 3, message: d.message || "", mode: d.mode });
      }
    }
    window.addEventListener("cryo:benchmarkProgress", onProg);
    return () => window.removeEventListener("cryo:benchmarkProgress", onProg);
  }, [instance.id]);

  async function startAuto() {
    if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Preview mode", body: "Auto-benchmark runs in the desktop launcher." }); return; }
    const ok = window.confirm(
      "Run the automated boot-to-menu benchmark?\n\n" +
      "Cryo will launch \"" + instance.name + "\" three times (Vanilla → Optimized warm-up → Optimized measured) and close it each time at the main menu.\n\n" +
      "This takes about 5–8 minutes. Don't game on the PC meanwhile.");
    if (!ok) return;
    setRunning(true); setProg({ step: 0, totalSteps: 3, message: "Starting…" });
    const r = await api.startBenchmark(instance.id).catch(e => ({ ok: false, error: String(e) }));
    if (!r || r.ok === false) {
      setRunning(false); setProg(null);
      window.toast({ tone: "danger", icon: "alert", title: "Couldn't start benchmark", body: (r && r.error) || "" });
    }
  }
  async function cancelAuto() { await api.cancelBenchmark().catch(() => {}); }

  const v = bench.vanilla, o = bench.optimized;
  const haveBoth = v && o && v.totalMs > 0 && o.totalMs > 0;
  const savedMs  = haveBoth ? v.totalMs - o.totalMs : 0;
  const savedPct = haveBoth ? Math.round((1 - o.totalMs / v.totalMs) * 100) : 0;
  const maxMs    = haveBoth ? Math.max(v.totalMs, o.totalMs) : 1;

  const bar = (label, ms, color, glow) => React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 8 } },
    React.createElement("span", { style: { width: 78, fontSize: 11.5, color: "var(--text-faint)", fontWeight: 600 } }, label),
    React.createElement("div", { style: { flex: 1, height: 24, borderRadius: 7, background: "var(--panel-2)", overflow: "hidden" } },
      React.createElement("div", { style: { width: Math.max(4, (ms / maxMs) * 100) + "%", height: "100%", background: color, borderRadius: 7, transition: "width .8s var(--ease)", boxShadow: glow ? "0 0 16px var(--acc-glow)" : "none" } })),
    React.createElement("span", { className: "tnum mono", style: { width: 70, textAlign: "right", fontSize: 12.5, fontWeight: 700 } }, (ms / 1000).toFixed(2) + "s"),
  );

  // ── Auto boot-to-menu benchmark card ──
  const maxBoot = auto ? Math.max(auto.bootVanilla || 0, auto.bootOptimized || 0, 1) : 1;
  const autoBar = (label, secs, color, glow) => React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 8 } },
    React.createElement("span", { style: { width: 84, fontSize: 11.5, color: "var(--text-faint)", fontWeight: 600 } }, label),
    React.createElement("div", { style: { flex: 1, height: 24, borderRadius: 7, background: "var(--panel-2)", overflow: "hidden" } },
      React.createElement("div", { style: { width: Math.max(4, ((secs > 0 ? secs : 0) / maxBoot) * 100) + "%", height: "100%", background: color, borderRadius: 7, transition: "width .8s var(--ease)", boxShadow: glow ? "0 0 16px var(--acc-glow)" : "none" } })),
    React.createElement("span", { className: "tnum mono", style: { width: 56, textAlign: "right", fontSize: 12.5, fontWeight: 700 } }, secs > 0 ? secs.toFixed(0) + "s" : "—"),
  );
  const pct = auto ? Math.round(auto.deltaPercent || 0) : 0;
  const autoCard = React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, marginBottom: 6 } },
      React.createElement(Icon, { name: "zap", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Auto-Benchmark — boot to main menu"),
      React.createElement("span", { style: { marginLeft: "auto", fontSize: 10.5, fontWeight: 700, letterSpacing: ".04em", textTransform: "uppercase", color: "var(--acc-text)", background: "var(--acc-soft)", border: "1px solid var(--acc-soft-2)", borderRadius: 999, padding: "3px 9px" } }, "automatic")),
    React.createElement("p", { style: { margin: "0 0 16px", fontSize: 12, color: "var(--text-faint)", lineHeight: 1.5 } },
      "One click runs the whole test: Vanilla baseline → Optimized warm-up (builds the class cache) → Optimized measured. Cryo launches the game, waits for the main menu, records the time and closes it. 3 launches, ~5–8 min, no world entry needed."),

    running
      ? React.createElement("div", null,
          React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 10 } },
            React.createElement("span", { style: { fontSize: 13, fontWeight: 700, color: "var(--acc-text)" } }, prog ? ("Step " + prog.step + "/" + prog.totalSteps) : "…"),
            React.createElement("span", { style: { flex: 1, fontSize: 12, color: "var(--text-dim)" } }, prog ? prog.message : ""),
            React.createElement(Btn, { variant: "outline", size: "sm", onClick: cancelAuto }, "Cancel")),
          React.createElement("div", { style: { height: 8, borderRadius: 6, background: "var(--panel-2)", overflow: "hidden" } },
            React.createElement("div", { style: { height: "100%", width: (prog ? (prog.step / prog.totalSteps) * 100 : 0) + "%", background: "var(--acc-grad)", transition: "width .6s var(--ease)" } })))
      : React.createElement(Btn, { variant: "primary", icon: "gauge", onClick: startAuto }, "Run Auto-Benchmark (3 launches)"),

    auto && !running && React.createElement("div", { style: { marginTop: 16 } },
      (auto.bootVanilla > 0 && auto.bootOptimized > 0)
        ? React.createElement("div", null,
            React.createElement("div", { style: { display: "flex", alignItems: "baseline", gap: 10, marginBottom: 14 } },
              React.createElement("span", { className: "tnum", style: { fontSize: 30, fontWeight: 760, color: auto.deltaSeconds > 0 ? "var(--success)" : "var(--text-dim)" } }, (auto.deltaSeconds > 0 ? "−" : "+") + Math.abs(pct) + "%"),
              React.createElement("span", { style: { fontSize: 13, color: "var(--text-dim)" } }, "boot-to-menu · " + (auto.deltaSeconds > 0 ? ("saved " + auto.deltaSeconds + "s") : "no speed-up"))),
            autoBar("Vanilla", auto.bootVanilla, "var(--text-faint)", false),
            autoBar("Optimized", auto.bootOptimized, "var(--acc-grad)", true),
            React.createElement("div", { style: { fontSize: 11, color: "var(--text-faint)", marginTop: 8 } },
              "boot-to-menu only · AppCDS class cache. Data-load (recipes) is measured separately below."))
        : React.createElement("div", { style: { padding: "14px 16px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 12.5, color: "var(--text-dim)", lineHeight: 1.5 } },
            "Last run incomplete — " + (auto.bootVanilla > 0 ? ("Vanilla " + auto.bootVanilla + "s") : "Vanilla —") + " · " + (auto.bootOptimized > 0 ? ("Optimized " + auto.bootOptimized + "s") : "Optimized —") + ". Run it again.")));

  return React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 18 } },
    autoCard,
    React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, marginBottom: 6 } },
      React.createElement(Icon, { name: "gauge", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Benchmark — data load (world entry)"),
      hasBridge && React.createElement(Btn, { variant: "ghost", size: "sm", icon: "refresh", style: { marginLeft: "auto" }, onClick: refresh }, "Refresh")),
    React.createElement("p", { style: { margin: "0 0 16px", fontSize: 12, color: "var(--text-faint)", lineHeight: 1.5 } },
      "Launch each mode and enter a world once. The mod measures how long recipes + advancements take to load, and Cryo compares them. (This is what VSpeed accelerates — not the boot-to-menu time.)"),

    // launch buttons
    React.createElement("div", { style: { display: "flex", gap: 10, flexWrap: "wrap", marginBottom: 18 } },
      React.createElement(Btn, { variant: "primary", icon: "zap", disabled: busy, onClick: () => launch(false) }, "Launch — Optimized"),
      React.createElement(Btn, { variant: "outline", icon: "package", disabled: busy, onClick: () => launch(true) }, "Launch — Vanilla (no cache)"),
    ),

    // comparison
    haveBoth
      ? React.createElement("div", null,
          React.createElement("div", { style: { display: "flex", alignItems: "baseline", gap: 10, marginBottom: 14 } },
            React.createElement("span", { className: "tnum", style: { fontSize: 30, fontWeight: 760, color: "var(--success)" } }, "−" + savedPct + "%"),
            React.createElement("span", { style: { fontSize: 13, color: "var(--text-dim)" } }, "data load · saved " + (savedMs / 1000).toFixed(2) + "s")),
          bar("Vanilla", v.totalMs, "var(--text-faint)", false),
          bar("VSpeed", o.totalMs, "var(--acc-grad)", true),
          React.createElement("div", { style: { fontSize: 11, color: "var(--text-faint)", marginTop: 8 } },
            (o.totalEntries || 0).toLocaleString() + " entries · cached load " + (o.totalMs) + "ms vs cold scan " + (v.totalMs) + "ms"),
        )
      : React.createElement("div", { style: { padding: "14px 16px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 12.5, color: "var(--text-dim)", lineHeight: 1.5 } },
          v || o
            ? "Recorded " + (o ? "Optimized (" + (o.totalMs/1000).toFixed(2) + "s)" : "") + (v ? (o ? " · " : "") + "Vanilla (" + (v.totalMs/1000).toFixed(2) + "s)" : "") + ". Run the other mode to compare."
            : (stats && stats.available
                ? "Last run: " + stats.mode + " · " + (stats.totalMs/1000).toFixed(2) + "s for " + (stats.totalEntries||0).toLocaleString() + " entries. Run both modes to compare."
                : "No measurement yet. Launch a mode and enter a world once.")),
  ));
}

/* ============ CRYO ENGINE CARD ============ */
function EngineCard({ instance, api, hasBridge }) {
  const [status, setStatus]   = tS(null);  // { installed, versionName, mcVersion, loader, loaderVer, loggedIn, source }
  const [versions, setVers]   = tS([]);
  const [selVer, setSelVer]   = tS("");
  const [prog, setProg]       = tS(null);  // { phase, message, done?, total? }
  const [installing, setInst] = tS(false);
  const [launching, setLaunch]= tS(false);
  const [showVers, setShowV]  = tS(false);
  const [toggling, setTog]    = tS(false);

  // Load status on mount and after install/launch
  async function loadStatus() {
    if (!hasBridge || !api.getEngineStatus) return;
    const s = await api.getEngineStatus(instance.id).catch(() => null);
    if (s) setStatus(s);
  }
  tE(() => { loadStatus(); }, [hasBridge, instance.id]);

  // Event listeners: neoforgeProgress/Done/Error + engineProgress/Error
  tE(() => {
    function onNFProg(e) {
      const d = e.detail || {};
      setProg({ phase: d.phase, message: d.message || d.name || "" , done: d.done, total: d.total });
    }
    function onNFDone(e) {
      const d = e.detail || {};
      setInst(false); setProg(null);
      window.toast({ tone: "success", icon: "zap", title: "NeoForge installed", body: "Version: " + (d.versionName || "") });
      loadStatus();
    }
    function onNFErr(e) {
      const d = e.detail || {};
      setInst(false); setProg(null);
      window.toast({ tone: "danger", icon: "alert", title: "Install failed", body: d.error || "" });
    }
    function onEngProg(e) {
      const d = e.detail || {};
      if (d.phase === "launched") { setLaunch(false); setProg(null); return; }
      setProg({ phase: d.phase, message: d.message || d.name || "" });
    }
    function onEngErr(e) {
      const d = e.detail || {};
      setLaunch(false); setProg(null);
      window.toast({ tone: "danger", icon: "alert", title: "Engine launch failed", body: d.error || "" });
    }
    window.addEventListener("cryo:neoforgeProgress", onNFProg);
    window.addEventListener("cryo:neoforgeDone",     onNFDone);
    window.addEventListener("cryo:neoforgeError",    onNFErr);
    window.addEventListener("cryo:engineProgress",   onEngProg);
    window.addEventListener("cryo:engineError",      onEngErr);
    return () => {
      window.removeEventListener("cryo:neoforgeProgress", onNFProg);
      window.removeEventListener("cryo:neoforgeDone",     onNFDone);
      window.removeEventListener("cryo:neoforgeError",    onNFErr);
      window.removeEventListener("cryo:engineProgress",   onEngProg);
      window.removeEventListener("cryo:engineError",      onEngErr);
    };
  }, []);

  async function loadVersions() {
    if (!hasBridge || !status) return;
    setShowV(true);
    if (versions.length) return;
    const r = await api.getNeoForgeVersions(status.mcVersion || "1.21.1").catch(() => ({ ok: false, versions: [] }));
    if (r && r.versions) setVers(r.versions);
  }

  async function install() {
    if (!hasBridge) return;
    setInst(true); setProg({ phase: "start", message: "Starting…" });
    const r = await api.installNeoForge(instance.id, selVer).catch(e => ({ ok: false, error: String(e) }));
    if (r && r.ok === false) {
      setInst(false); setProg(null);
      window.toast({ tone: "danger", icon: "alert", title: "Couldn't start install", body: r.error || "" });
    }
  }

  async function launchEngine() {
    if (!hasBridge) return;
    setLaunch(true); setProg({ phase: "start", message: "Launching…" });
    const r = await api.launchWithEngine(instance.id).catch(e => ({ ok: false, error: String(e) }));
    if (r && r.ok === false) {
      setLaunch(false); setProg(null);
      window.toast({ tone: "danger", icon: "alert", title: "Launch failed", body: r.error || "" });
    }
  }

  if (!status) return null;

  const busy = installing || launching || toggling;
  const isNeoForge = (status.loader || "").toLowerCase().includes("neoforge");
  const isCryo     = status.source === "cryo";

  return React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    // Header
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 10 } },
      React.createElement("div", { style: { width: 40, height: 40, borderRadius: 12, display: "grid", placeItems: "center", background: "var(--acc-soft)", border: "1px solid var(--acc-soft-2)", color: "var(--acc-text)" } },
        React.createElement(Icon, { name: "cpu", size: 20 })),
      React.createElement("div", { style: { flex: 1 } },
        React.createElement("div", { style: { fontSize: 15, fontWeight: 680 } }, "Cryo Engine"),
        React.createElement("div", { style: { fontSize: 12, color: "var(--text-faint)", marginTop: 1 } },
          "Launch without Prism · CmlLib.Core " + (status.loader || "Unknown") + " " + (status.loaderVer || ""))),
      status.installed
        ? React.createElement(Badge, { tone: "success", dot: true }, "Installed")
        : React.createElement(Badge, { tone: "neutral" }, "Not installed"),
    ),

    // Loader info row
    React.createElement("div", { style: { display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 14 } },
      React.createElement("div", { style: { padding: "5px 11px", borderRadius: 8, background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 11.5, fontWeight: 600, color: "var(--text-dim)" } },
        "MC " + (status.mcVersion || "?")),
      React.createElement("div", { style: { padding: "5px 11px", borderRadius: 8, background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 11.5, fontWeight: 600, color: "var(--text-dim)" } },
        (status.loader || "Unknown") + " " + (status.loaderVer || "")),
      status.installed && React.createElement("div", { style: { padding: "5px 11px", borderRadius: 8, background: "var(--acc-soft)", border: "1px solid var(--acc-soft-2)", fontSize: 11.5, fontWeight: 600, color: "var(--acc-text)" } },
        status.versionName || ""),
    ),

    // Auth warning
    !status.loggedIn && React.createElement("div", { style: { padding: "10px 14px", borderRadius: "var(--r-md)", background: "var(--warn-dim)", border: "1px solid color-mix(in oklab, var(--warn) 30%, transparent)", fontSize: 12.5, color: "var(--warn)", marginBottom: 14, display: "flex", alignItems: "center", gap: 8 } },
      React.createElement(Icon, { name: "info", size: 15 }),
      "Sign in to your Microsoft account first (titlebar chip) to launch via engine."),

    // Not a NeoForge instance warning
    !isNeoForge && status.loader && React.createElement("div", { style: { padding: "10px 14px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 12.5, color: "var(--text-dim)", marginBottom: 14 } },
      "Engine currently supports NeoForge instances. This instance uses " + (status.loader || "Unknown") + "."),

    // Progress bar
    (installing || launching) && prog && React.createElement("div", { style: { marginBottom: 14 } },
      React.createElement("div", { style: { display: "flex", justifyContent: "space-between", marginBottom: 6, fontSize: 12, color: "var(--text-dim)" } },
        React.createElement("span", null, prog.message || "Working…"),
        prog.done != null && prog.total > 0 && React.createElement("span", { className: "tnum" }, prog.done + " / " + prog.total)),
      React.createElement("div", { style: { height: 6, borderRadius: 99, background: "var(--panel-2)", overflow: "hidden" } },
        React.createElement("div", { style: { height: "100%", background: "var(--acc-grad)", borderRadius: 99, width: (prog.total > 0 ? Math.round((prog.done / prog.total) * 100) : 0) + "%", transition: "width .4s", animation: !(prog.total > 0) ? "shimmer 1.4s linear infinite" : "none", backgroundSize: "200% 100%" } }))),

    // Action buttons
    React.createElement("div", { style: { display: "flex", gap: 10, flexWrap: "wrap", alignItems: "center" } },
      // Install / Reinstall
      isNeoForge && React.createElement("div", { style: { display: "flex", gap: 0 } },
        React.createElement(Btn, {
          variant: status.installed ? "outline" : "primary",
          icon: "download", disabled: busy,
          onClick: install,
          style: showVers ? { borderRadius: "var(--r-md) 0 0 var(--r-md)", borderRight: "none" } : {},
        }, status.installed ? "Reinstall Engine" : "Install Engine"),
        React.createElement("button", {
          className: "no-drag",
          disabled: busy,
          onClick: loadVersions,
          style: { padding: "0 10px", borderRadius: "0 var(--r-md) var(--r-md) 0", border: "1px solid var(--border)", background: "var(--panel-2)", color: "var(--text-dim)", fontSize: 12, cursor: "pointer" },
        }, React.createElement(Icon, { name: "chevronDown", size: 14 })),
      ),
      // Version picker (appears when chevron clicked)
      showVers && versions.length > 0 && React.createElement(Select, {
        value: selVer, onChange: setSelVer, size: "sm", width: 200,
        options: [{ value: "", label: "Latest stable" }, ...versions.slice(0, 20).map(v => ({ value: v, label: v }))],
      }),
      // Launch via engine (one-time manual launch)
      status.installed && status.loggedIn && !isCryo && React.createElement(Btn, {
        variant: "outline", icon: launching ? "refresh" : "play",
        iconSpin: launching, disabled: busy,
        onClick: launchEngine,
      }, "Test Launch"),
    ),

    // "Use as Default Engine" — makes every Launch button use the engine
    status.installed && isNeoForge && React.createElement("div", {
      style: { marginTop: 14, padding: "12px 16px", borderRadius: "var(--r-md)", background: isCryo ? "var(--acc-soft)" : "var(--panel-2)", border: "1px solid " + (isCryo ? "var(--acc-soft-2)" : "var(--border)"), display: "flex", alignItems: "center", gap: 12 },
    },
      React.createElement("div", { style: { flex: 1 } },
        React.createElement("div", { style: { fontSize: 13, fontWeight: 680, color: isCryo ? "var(--acc-text)" : "var(--text)" } }, isCryo ? "Engine is the default launcher" : "Use engine as default launcher"),
        React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 2 } },
          isCryo ? "The Launch button uses Cryo Engine (no Prism needed)." : "Enable to make Launch use Cryo Engine instead of Prism.")),
      React.createElement(Toggle, {
        checked: isCryo, disabled: busy || toggling,
        onChange: async (val) => {
          setTog(true);
          const r = await api.setEngineSource(instance.id, val ? "cryo" : "prism").catch(() => null);
          if (r && r.ok) { await loadStatus(); window.toast({ tone: val ? "success" : "neutral", icon: val ? "zap" : "package", title: val ? "Cryo Engine enabled" : "Prism mode restored", body: val ? "Launch button now uses engine directly." : "Launch button now uses Prism." }); }
          setTog(false);
        },
      }),
    ),

    // Footer note
    React.createElement("p", { style: { margin: "12px 0 0", fontSize: 11.5, color: "var(--text-faint)", lineHeight: 1.5 } },
      "Uses the same mods/config/saves folder as Prism but without PrismLauncher itself. NeoForge libraries are downloaded once to the shared Cryo game root."),
  );
}

/* ============ BOOT WATERFALL (derived from log timestamps) ============ */
function BootWaterfall({ instance, api, hasBridge }) {
  const [data, setData]   = tS(null);   // { totalMs, phases, lineCount }
  const [loading, setLd]  = tS(false);
  const [err, setErr]     = tS("");

  async function load() {
    if (!hasBridge) return;
    setLd(true); setErr("");
    const r = await api.getBootTimeline(instance.id).catch(e => ({ ok: false, error: String(e) }));
    setLd(false);
    if (r && r.ok) setData(r); else setErr((r && r.error) || "Failed");
  }

  const PHASE_COLORS = ["#38BDF8", "#6366F1", "#A855F7", "#22D3EE", "#34D399", "#FBBF77", "#FB7185", "#67E8F9"];
  const total = data ? Math.max(1, data.totalMs) : 1;

  return React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, marginBottom: 6, flexWrap: "wrap" } },
      React.createElement(Icon, { name: "activity", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Boot waterfall"),
      data && React.createElement("span", { className: "tnum", style: { fontSize: 12, color: "var(--text-faint)" } }, "total " + (data.totalMs / 1000).toFixed(1) + "s"),
      React.createElement("div", { style: { marginLeft: "auto" } },
        React.createElement(Btn, { variant: "outline", size: "sm", icon: loading ? "refresh" : "activity", iconSpin: loading, disabled: loading, onClick: load }, loading ? "Reading…" : "Analyze last boot"))),
    React.createElement("p", { style: { margin: "0 0 8px", fontSize: 12, color: "var(--text-faint)", lineHeight: 1.5 } },
      "Reconstructed from latest.log timestamps — where startup time actually went, phase by phase."),
    err && React.createElement("div", { style: { fontSize: 12.5, color: "var(--text-dim)", padding: "8px 0" } }, err),
    data && data.phases.length > 0 && React.createElement("div", { style: { marginTop: 10 } },
      // Stacked bar
      React.createElement("div", { style: { display: "flex", height: 26, borderRadius: 7, overflow: "hidden", background: "var(--panel-2)", marginBottom: 14 } },
        data.phases.map((p, i) => p.durationMs > 0 && React.createElement(Tip, { key: i, label: p.name + " · " + (p.durationMs / 1000).toFixed(2) + "s" },
          React.createElement("div", { style: { width: (p.durationMs / total * 100) + "%", height: "100%", background: PHASE_COLORS[i % PHASE_COLORS.length], minWidth: 2 } })))),
      // Phase rows
      React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 6 } },
        data.phases.map((p, i) => React.createElement("div", { key: i, style: { display: "flex", alignItems: "center", gap: 10, fontSize: 12.5 } },
          React.createElement("span", { style: { width: 10, height: 10, borderRadius: 3, background: PHASE_COLORS[i % PHASE_COLORS.length], flexShrink: 0 } }),
          React.createElement("span", { style: { flex: 1, color: "var(--text-dim)" } }, p.name),
          React.createElement("span", { className: "tnum", style: { width: 110, height: 5, borderRadius: 3, background: "var(--panel-2)", overflow: "hidden", position: "relative" } },
            React.createElement("span", { style: { position: "absolute", left: 0, top: 0, bottom: 0, width: (p.durationMs / total * 100) + "%", background: PHASE_COLORS[i % PHASE_COLORS.length] } })),
          React.createElement("span", { className: "tnum", style: { width: 56, textAlign: "right", fontWeight: 700 } }, (p.durationMs / 1000).toFixed(2) + "s"))))),
    data && data.phases.length === 0 && React.createElement("div", { style: { fontSize: 12.5, color: "var(--text-dim)", padding: "8px 0" } },
      "Couldn't identify phase markers in this log. Launch once and try again."));
}

/* ============ PERFORMANCE / VSPEED ============ */
function PerformanceTab({ instance, cache: cache0, t, fmt, api, hasBridge }) {
  const [cache, setCache] = tS(cache0);
  const [enabled, setEnabled] = tS(cache0.enabled);
  const [rebuilding, setRebuilding] = tS(cache0.state === "rebuilding");
  const [profile, setProfile] = tS(false);

  tE(() => { setCache(cache0); setEnabled(cache0.enabled); setRebuilding(cache0.state === "rebuilding"); }, [cache0]);

  async function rebuild() {
    setRebuilding(true);
    window.toast({ tone: "neutral", icon: "refresh", title: "Rebuilding cache…", body: instance.name });
    const res = await api.rebuildCache(instance.id);
    // Reload fresh cache info
    const fresh = await api.getCache(instance.id).catch(() => res);
    setCache(fresh); setRebuilding(false);
    window.toast({ tone: "success", icon: "database", title: "Cache ready",
      body: fmt.bytes(fresh.sizeBytes || 0) });
  }

  const ready = enabled && !rebuilding && cache.sizeBytes > 0;
  const stat = (label, value, mono = false) => React.createElement("div", null,
    React.createElement("div", { style: { fontSize: 11, color: "var(--text-faint)", fontWeight: 600, marginBottom: 4 } }, label),
    React.createElement("div", { className: (mono ? "mono " : "") + "tnum",
      style: { fontSize: 13.5, fontWeight: 600, color: "var(--text)", wordBreak: "break-all" } }, value),
  );

  return React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 18 } },
    // toggle card
    React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
      React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 16 } },
        React.createElement("div", { style: { width: 44, height: 44, borderRadius: 13, display: "grid", placeItems: "center", background: "var(--acc-soft)", border: "1px solid var(--acc-soft-2)", color: "var(--acc-text)" } },
          React.createElement(Icon, { name: "zap", size: 22 })),
        React.createElement("div", { style: { flex: 1 } },
          React.createElement("div", { style: { fontSize: 15, fontWeight: 680 } }, t("perf.toggle")),
          React.createElement("div", { style: { fontSize: 12.5, color: "var(--text-dim)", marginTop: 2 } },
            enabled ? t("perf.toggleOn") : t("perf.toggleOff")),
        ),
        React.createElement(Toggle, { checked: enabled, onChange: setEnabled }),
      ),
    ),

    // benchmark (real measured speed-up + launch modes)
    React.createElement(BenchmarkCard, { instance, api, hasBridge, t, fmt }),

    // cryo standalone engine
    React.createElement(EngineCard, { instance, api, hasBridge }),

    // boot waterfall (where startup time went)
    React.createElement(BootWaterfall, { instance, api, hasBridge }),

    // status + composition
    React.createElement("div", { style: { display: "grid", gridTemplateColumns: "minmax(0,1.6fr) minmax(0,1fr)", gap: 18 }, className: "cryo-perf-grid" },
      React.createElement(Card, { style: { borderRadius: "var(--r-xl)", opacity: enabled ? 1 : 0.55 } },
        React.createElement("div", { style: { display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 16 } },
          React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, t("perf.title")),
          rebuilding
            ? React.createElement(Badge, { tone: "warn", icon: "refresh" }, t("cache.rebuilding"))
            : ready ? React.createElement(Badge, { tone: "success", dot: true }, t("cache.ready"))
              : React.createElement(Badge, { tone: "neutral" }, t("cache.off"))),
        React.createElement("div", { style: { display: "grid", gridTemplateColumns: "1fr 1fr", gap: 18, marginBottom: 18 } },
          stat(t("perf.type"), "JSON / reload-listener"),
          stat(t("perf.size"), rebuilding ? "—" : fmt.bytes(cache.sizeBytes) + " gzip"),
          stat(t("perf.hash"), cache.modsetHash || "—", true),
          stat(t("perf.built"), rebuilding ? t("perf.rebuilding") : fmt.date(cache.builtAt, "en")),
        ),
        React.createElement("div", { className: "hr", style: { marginBottom: 16 } }),
        React.createElement("div", { style: { display: "flex", gap: 10, flexWrap: "wrap" } },
          React.createElement(Btn, { variant: "subtle", icon: "refresh", onClick: rebuild, disabled: rebuilding || !enabled },
            rebuilding ? t("perf.rebuilding") : t("perf.rebuild")),
          React.createElement(Tip, { label: "Enable JFR profiling for the next launch (saves vspeed-boot.jfr)" },
            React.createElement(Btn, {
              variant: profile ? "accentSoft" : "ghost",
              icon: profile ? "check" : "activity",
              onClick: async () => {
                const on = !profile;
                setProfile(on);
                if (api.setProfileNextLaunch) await api.setProfileNextLaunch(instance.id, on).catch(() => {});
                window.toast({
                  tone: on ? "accent" : "neutral",
                  icon: on ? "activity" : "x",
                  title: on ? "JFR profiling enabled" : "JFR profiling disabled",
                  body: on ? "Next launch records vspeed-boot.jfr (instance.cfg updated)" : "Removed from instance.cfg",
                });
              },
            }, t("perf.profile")),
          ),
        ),
        React.createElement("div", { style: { display: "flex", gap: 9, marginTop: 16, alignItems: "flex-start" } },
          React.createElement(Icon, { name: "hash", size: 14, style: { color: "var(--text-faint)", marginTop: 2 } }),
          React.createElement("p", { style: { margin: 0, fontSize: 11.5, color: "var(--text-faint)", lineHeight: 1.5 } },
            t("perf.invalidate"), " ",
            React.createElement("span", { className: "mono", style: { color: "var(--text-dim)" } },
              cache.path || ".vspeed-cache/json/<type>/<hash>.bin")),
        ),
      ),
      React.createElement(Card, { style: { borderRadius: "var(--r-xl)", display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: 16, opacity: enabled && ready ? 1 : 0.55 } },
        React.createElement(Donut, {
          segments: [
            { value: cache.recipes  || 1, color: "var(--acc-2)" },
            { value: cache.advancements || 1, color: "var(--acc-3)" },
          ],
          center: React.createElement("div", null,
            React.createElement("div", { className: "tnum", style: { fontSize: 19, fontWeight: 740 } }, fmt.bytes(cache.sizeBytes)),
            React.createElement("div", { style: { fontSize: 10.5, color: "var(--text-faint)" } }, "gzip cache")),
        }),
        React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 8, width: "100%" } },
          React.createElement(LegendRow, { color: "var(--acc-2)", label: t("perf.recipesWord"), value: fmt.num(cache.recipes) }),
          React.createElement(LegendRow, { color: "var(--acc-3)", label: t("perf.advWord"),     value: fmt.num(cache.advancements) }),
        ),
      ),
    ),

    // honest block
    React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
      React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, marginBottom: 16 } },
        React.createElement(Icon, { name: "shield", size: 17, style: { color: "var(--acc-2)" } }),
        React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, t("perf.honest.title")),
      ),
      React.createElement("div", { style: { display: "grid", gridTemplateColumns: "1fr 1fr", gap: 14 }, className: "cryo-honest-grid" },
        React.createElement(HonestCard, {
          tone: "success", icon: "checkCircle", title: t("perf.honest.yes"),
          body: t("perf.honest.yesBody", { size: fmt.bytes(cache.sizeBytes || 4.8 * 1024 * 1024), cold: cache.worldEntryCold, warm: cache.worldEntryWarm }),
          chip: "−" + Math.round((1 - (cache.worldEntryWarm || 1.8) / (cache.worldEntryCold || 9.4)) * 100) + "%",
        }),
        React.createElement(HonestCard, {
          tone: "neutral", icon: "xCircle", title: t("perf.honest.no"),
          body: t("perf.honest.noBody"), chip: "no change",
        }),
      ),
    ),
  );
}
function LegendRow({ color, label, value }) {
  return React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 8, fontSize: 12.5 } },
    React.createElement("span", { style: { width: 9, height: 9, borderRadius: 3, background: color } }),
    React.createElement("span", { style: { color: "var(--text-dim)", flex: 1 } }, label),
    React.createElement("span", { className: "tnum", style: { fontWeight: 700 } }, value),
  );
}
function HonestCard({ tone, icon, title, body, chip }) {
  const ok = tone === "success";
  return React.createElement("div", {
    style: {
      padding: 16, borderRadius: "var(--r-lg)",
      background: ok ? "var(--success-dim)" : "var(--panel-2)",
      border: "1px solid " + (ok ? "color-mix(in oklab, var(--success) 26%, transparent)" : "var(--border)"),
    },
  },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, marginBottom: 9 } },
      React.createElement(Icon, { name: icon, size: 17, style: { color: ok ? "var(--success)" : "var(--text-faint)" } }),
      React.createElement("span", { style: { fontSize: 13.5, fontWeight: 680, flex: 1 } }, title),
      React.createElement(Badge, { tone: ok ? "success" : "neutral", size: "sm" }, chip),
    ),
    React.createElement("p", { style: { margin: 0, fontSize: 12, color: "var(--text-dim)", lineHeight: 1.55 } }, body),
  );
}

/* ============ DEPENDENCY GRAPH (radial SVG of the problem subgraph) ============ */
function DepGraph({ nodes, edges }) {
  if (!nodes || nodes.length === 0) return null;
  const W = 560, H = Math.min(360, 160 + nodes.length * 18), cx = W / 2, cy = H / 2;
  const R = Math.min(cx, cy) - 70;
  // Place nodes evenly on a circle
  const pos = {};
  nodes.forEach((n, i) => {
    const a = (2 * Math.PI * i) / nodes.length - Math.PI / 2;
    pos[n.id] = { x: cx + R * Math.cos(a), y: cy + R * Math.sin(a) };
  });

  const lines = edges.filter(e => pos[e.from] && pos[e.to]).map((e, i) => {
    const a = pos[e.from], b = pos[e.to];
    return React.createElement("line", { key: "e" + i, x1: a.x, y1: a.y, x2: b.x, y2: b.y,
      stroke: "var(--error)", strokeWidth: 1.5, strokeDasharray: "4 3", opacity: 0.7 });
  });

  const dots = nodes.map((n, i) => {
    const p = pos[n.id];
    const missing = !n.installed;
    const short = n.id.length > 16 ? n.id.slice(0, 15) + "…" : n.id;
    return React.createElement("g", { key: "n" + i },
      React.createElement("circle", { cx: p.x, cy: p.y, r: 7,
        fill: missing ? "var(--error)" : "var(--acc-2)",
        stroke: "var(--bg-1)", strokeWidth: 2 }),
      React.createElement("text", { x: p.x, y: p.y - 12, textAnchor: "middle",
        fontSize: 10.5, fontWeight: 600, fill: missing ? "var(--error)" : "var(--text-dim)" }, short),
      missing && React.createElement("text", { x: p.x, y: p.y + 20, textAnchor: "middle",
        fontSize: 9, fill: "var(--error)" }, "missing"));
  });

  return React.createElement("div", { style: { marginTop: 14, borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", overflow: "hidden" } },
    React.createElement("div", { style: { padding: "8px 12px", fontSize: 11, color: "var(--text-faint)", borderBottom: "1px solid var(--border)" } },
      "Problem subgraph — red = missing/disabled required dependency, dashed = unsatisfied link"),
    React.createElement("svg", { viewBox: "0 0 " + W + " " + H, style: { width: "100%", height: H, display: "block" } },
      ...lines, ...dots));
}

/* ============ MODS ============ */
function ModsTab({ instance, mods: mods0, t, fmt, api, hasBridge, onModsChanged }) {
  const [mods, setMods] = tS(mods0);
  const [q, setQ] = tS("");
  const [filter, setFilter] = tS("all");
  const [dragOver, setDragOver] = tS(false);
  const dragCnt = tRf(0);
  tE(() => setMods(mods0), [mods0]);

  function reloadMods() {
    if (hasBridge) api.getMods(instance.id).then(m => setMods(m || [])).catch(() => {});
  }
  async function pickLocalMods() {
    if (!hasBridge || !api.addLocalMods) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
    const r = await api.addLocalMods(instance.id).catch(e => ({ ok: false, error: String(e) }));
    if (r && r.ok && r.added > 0) { window.toast({ tone: "success", icon: "check", title: "Added " + r.added + " mod" + (r.added === 1 ? "" : "s") }); (onModsChanged || reloadMods)(); }
    else if (r && !r.ok) window.toast({ tone: "danger", icon: "alert", title: "Couldn't add mods", body: r.error || "" });
  }
  async function handleModDrop(e) {
    e.preventDefault(); dragCnt.current = 0; setDragOver(false);
    if (!hasBridge || !api.addLocalModData) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
    const files = Array.from((e.dataTransfer && e.dataTransfer.files) || []).filter(f => /\.jar$/i.test(f.name));
    if (!files.length) { window.toast({ tone: "warn", icon: "info", title: "Drop .jar files", body: "Only Minecraft mod .jar files can be added." }); return; }
    let added = 0;
    for (const f of files) {
      if (f.size > 100 * 1024 * 1024) { window.toast({ tone: "warn", icon: "alert", title: "Too large", body: f.name + " exceeds 100 MB — use the Add .jar button." }); continue; }
      try {
        const b64 = await new Promise((res, rej) => { const rd = new FileReader(); rd.onload = () => res(String(rd.result).split(",")[1] || ""); rd.onerror = rej; rd.readAsDataURL(f); });
        const rr = await api.addLocalModData(instance.id, f.name, b64);
        if (rr && rr.ok) added++; else window.toast({ tone: "danger", icon: "alert", title: "Couldn't add " + f.name, body: (rr && rr.error) || "" });
      } catch (err) { window.toast({ tone: "danger", icon: "alert", title: "Couldn't read " + f.name }); }
    }
    if (added) { window.toast({ tone: "success", icon: "check", title: "Added " + added + " mod" + (added === 1 ? "" : "s") }); (onModsChanged || reloadMods)(); }
  }

  const optimCount = mods.filter(m => m.optimization).length;
  const updateCount = mods.filter(m => m.update).length;
  const enabledCount = mods.filter(m => m.enabled).length;

  const filtered = tM(() => mods.filter(m => {
    if (q && !m.name.toLowerCase().includes(q.toLowerCase())) return false;
    if (filter === "optim")    return m.optimization;
    if (filter === "updates")  return m.update;
    if (filter === "disabled") return !m.enabled;
    return true;
  }), [mods, q, filter]);

  // Real toggle: rename the jar via the bridge (jar <-> jar.disabled)
  async function toggle(m) {
    if (!hasBridge || !api.setModEnabled) {
      setMods(ms => ms.map(x => x.id === m.id ? { ...x, enabled: !x.enabled } : x));
      return;
    }
    const want = !m.enabled;
    const res = await api.setModEnabled(instance.id, m.file, want).catch(e => ({ ok: false, error: e.message }));
    if (res.ok) {
      setMods(ms => ms.map(x => x.id === m.id ? { ...x, enabled: want, file: res.file, id: instance.id + "::" + res.file } : x));
      onModsChanged && onModsChanged();   // keep the header/tab mod count in sync (enabled jars)
    } else {
      window.toast({ tone: "error", icon: "alert", title: "Couldn't toggle mod", body: res.error || "" });
    }
  }

  // ── Conflict scan (structural duplicate detection + AI analysis) ──
  const { navigate: navTo } = window.CryoStore.useApp();
  const [scan, setScan] = tS(null);
  const [scanning, setScanning] = tS(false);
  async function runScan() {
    if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
    setScanning(true);
    const r = await api.scanMods(instance.id).catch(e => ({ ok: false, error: String(e) }));
    setScanning(false);
    if (r && r.ok) { setScan(r); if (!r.duplicates.length) window.toast({ tone: "success", icon: "check", title: "No duplicate mods", body: r.total + " mods scanned" }); }
    else window.toast({ tone: "danger", icon: "alert", title: "Scan failed", body: (r && r.error) || "" });
  }
  async function disableDupes(group) {
    const extras = group.files.slice(1);
    for (const f of extras) await api.setModEnabled(instance.id, f, false).catch(() => {});
    window.toast({ tone: "success", icon: "check", title: "Disabled " + extras.length + " duplicate(s)", body: group.modId });
    setScan(s => s ? { ...s, duplicates: s.duplicates.filter(d => d.modId !== group.modId) } : s);
  }
  function analyzeAI() {
    window.__cryoAssistantPreload = { instanceId: instance.id, attach: { mods: true }, prompt: "Analyze my mod list for duplicate mods, known incompatible combinations, and missing dependencies. List concrete issues and propose disableMod fixes where justified.", autoSend: true };
    navTo("assistant");
  }

  // ── Mod updates (Modrinth hash lookup) ──
  const [updates, setUpdates]   = tS(null);   // null = not checked; [] = none; [...] = available
  const [checkingUpd, setCheck] = tS(false);
  const [updProg, setUpdProg]   = tS("");
  const [updating, setUpdating] = tS({});      // { [currentFile]: true }

  tE(() => {
    function onProg(e) { setUpdProg((e.detail && e.detail.message) || ""); }
    function onDone(e) {
      const d = e.detail || {};
      setCheck(false); setUpdProg("");
      setUpdates(d.updates || []);
      if (d.ok && (!d.updates || !d.updates.length))
        window.toast({ tone: "success", icon: "check", title: "All mods up to date", body: (d.scanned || 0) + " mods checked on Modrinth" });
    }
    function onErr(e) { setCheck(false); setUpdProg(""); window.toast({ tone: "danger", icon: "alert", title: "Update check failed", body: (e.detail && e.detail.error) || "" }); }
    function onUpdDone(e) {
      const d = e.detail || {};
      setUpdating(u => { const n = { ...u }; delete n[d.oldFile]; return n; });
      setUpdates(list => (list || []).filter(x => x.currentFile !== d.oldFile));
      window.toast({ tone: "success", icon: "check", title: "Updated", body: d.newFile || "" });
    }
    function onUpdErr(e) {
      const d = e.detail || {};
      setUpdating(u => { const n = { ...u }; delete n[d.oldFile]; return n; });
      window.toast({ tone: "danger", icon: "alert", title: "Update failed", body: (d.error || "") });
    }
    window.addEventListener("cryo:modUpdatesProgress", onProg);
    window.addEventListener("cryo:modUpdatesDone", onDone);
    window.addEventListener("cryo:modUpdatesError", onErr);
    window.addEventListener("cryo:modUpdateDone", onUpdDone);
    window.addEventListener("cryo:modUpdateError", onUpdErr);
    return () => {
      window.removeEventListener("cryo:modUpdatesProgress", onProg);
      window.removeEventListener("cryo:modUpdatesDone", onDone);
      window.removeEventListener("cryo:modUpdatesError", onErr);
      window.removeEventListener("cryo:modUpdateDone", onUpdDone);
      window.removeEventListener("cryo:modUpdateError", onUpdErr);
    };
  }, []);

  async function checkUpdates() {
    if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
    setCheck(true); setUpdates(null); setUpdProg("Starting…");
    const r = await api.checkModUpdates(instance.id).catch(e => ({ ok: false, error: String(e) }));
    if (r && r.ok === false) { setCheck(false); window.toast({ tone: "danger", icon: "alert", title: "Couldn't start", body: r.error || "" }); }
  }

  // ── Dependency graph analysis ──
  const [graph, setGraph]     = tS(null);   // { nodeCount, edgeCount, issueCount, issues, nodes, edges }
  const [analyzing, setAnalyz] = tS(false);
  async function analyzeDeps() {
    if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
    setAnalyz(true); setGraph(null);
    const r = await api.analyzeModGraph(instance.id).catch(e => ({ ok: false, error: String(e) }));
    setAnalyz(false);
    if (r && r.ok) { setGraph(r); if (!r.issueCount) window.toast({ tone: "success", icon: "check", title: "No dependency problems", body: r.nodeCount + " mods · " + r.edgeCount + " links" }); }
    else window.toast({ tone: "danger", icon: "alert", title: "Analysis failed", body: (r && r.error) || "" });
  }
  const depCard = React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, flexWrap: "wrap" } },
      React.createElement(Icon, { name: "layers2", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Dependency check"),
      graph && React.createElement(Badge, { tone: graph.issueCount ? "danger" : "success", size: "sm" }, graph.issueCount ? (graph.issueCount + " issue" + (graph.issueCount !== 1 ? "s" : "")) : "all satisfied"),
      React.createElement("div", { style: { marginLeft: "auto" } },
        React.createElement(Btn, { variant: "outline", size: "sm", icon: analyzing ? "refresh" : "layers2", iconSpin: analyzing, disabled: analyzing, onClick: analyzeDeps }, analyzing ? "Analyzing…" : "Check dependencies"))),
    React.createElement("p", { style: { margin: "8px 0 0", fontSize: 12, color: "var(--text-faint)", lineHeight: 1.5 } },
      "Reads every mod's metadata and finds required dependencies that are missing or disabled — before you launch."),
    graph && graph.issues.length > 0 && React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 7, marginTop: 12 } },
      graph.issues.map((iss, i) => React.createElement("div", { key: i, style: { display: "flex", alignItems: "flex-start", gap: 9, padding: "9px 12px", borderRadius: "var(--r-md)", background: "var(--error-dim)", border: "1px solid color-mix(in oklab, var(--error) 28%, transparent)" } },
        React.createElement(Icon, { name: iss.type === "disabled" ? "pause" : "alert", size: 14, style: { color: "var(--error)", marginTop: 2, flexShrink: 0 } }),
        React.createElement("div", { style: { flex: 1, minWidth: 0 } },
          React.createElement("div", { style: { fontSize: 12.5, color: "var(--text)", lineHeight: 1.45 } }, iss.message),
          iss.type === "missing" && React.createElement(Btn, { variant: "ghost", size: "sm", icon: "search", style: { marginTop: 4 }, onClick: () => { window.__cryoModSearch = iss.dep; navTo("browse"); } }, "Find on Modrinth"))))),
    graph && graph.issueCount > 0 && React.createElement(DepGraph, { nodes: graph.nodes.filter(n => n.involved), edges: graph.edges.filter(e => !e.ok) }),
    graph && graph.issueCount === 0 && React.createElement("div", { style: { marginTop: 10, fontSize: 12.5, color: "var(--success)" } }, "✓ All required dependencies are present (" + graph.nodeCount + " mods, " + graph.edgeCount + " dependency links)."));
  async function doUpdate(u) {
    setUpdating(s => ({ ...s, [u.currentFile]: true }));
    await api.updateMod(instance.id, u.currentFile, u.url, u.newFilename, u.sha512).catch(() => {});
  }
  async function updateAll() {
    for (const u of (updates || [])) await doUpdate(u);
  }

  const updatesCard = React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, flexWrap: "wrap" } },
      React.createElement(Icon, { name: "download", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Mod updates"),
      React.createElement("div", { style: { marginLeft: "auto", display: "flex", gap: 8 } },
        (updates && updates.length > 0) && React.createElement(Btn, { variant: "primary", size: "sm", icon: "download", onClick: updateAll, disabled: Object.keys(updating).length > 0 }, "Update all (" + updates.length + ")"),
        React.createElement(Btn, { variant: "outline", size: "sm", icon: checkingUpd ? "refresh" : "refresh", iconSpin: checkingUpd, disabled: checkingUpd, onClick: checkUpdates }, checkingUpd ? "Checking…" : "Check for updates"))),
    checkingUpd && React.createElement("div", { style: { marginTop: 10, fontSize: 12, color: "var(--text-dim)" } }, updProg || "Working…"),
    updates && updates.length > 0 && React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 8, marginTop: 12 } },
      updates.map(u => React.createElement("div", { key: u.currentFile, style: { display: "flex", alignItems: "center", gap: 10, padding: "10px 12px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)" } },
        React.createElement("div", { style: { flex: 1, minWidth: 0 } },
          React.createElement("div", { style: { fontSize: 12.5, fontWeight: 600, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, u.currentFile),
          React.createElement("div", { style: { fontSize: 11, color: "var(--success)", marginTop: 2 } }, "→ " + (u.newVersion || u.newFilename))),
        React.createElement(Btn, { variant: "outline", size: "sm", icon: updating[u.currentFile] ? "refresh" : "download", iconSpin: !!updating[u.currentFile], disabled: !!updating[u.currentFile], onClick: () => doUpdate(u) }, "Update")))),
    updates && updates.length === 0 && React.createElement("div", { style: { marginTop: 10, fontSize: 12.5, color: "var(--text-dim)" } }, "All mods are on the latest version available for this MC version + loader."));
  const scanCard = React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, flexWrap: "wrap" } },
      React.createElement(Icon, { name: "package", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Conflict scan"),
      React.createElement("div", { style: { marginLeft: "auto", display: "flex", gap: 8 } },
        React.createElement(Btn, { variant: "outline", size: "sm", icon: scanning ? "refresh" : "gauge", iconSpin: scanning, disabled: scanning, onClick: runScan }, scanning ? "Scanning…" : "Scan duplicates"),
        hasBridge && React.createElement(Btn, { variant: "primary", size: "sm", icon: "sparkles", onClick: analyzeAI }, "Analyze with AI"))),
    scan && (scan.duplicates.length
      ? React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 8, marginTop: 12 } },
          React.createElement("div", { style: { fontSize: 12, color: "var(--text-dim)" } }, scan.duplicates.length + " duplicate mod id(s) · " + scan.total + " scanned" + (scan.unknown ? " · " + scan.unknown + " without metadata" : "")),
          scan.duplicates.map(g => React.createElement("div", { key: g.modId, style: { padding: "10px 12px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)" } },
            React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 6 } },
              React.createElement("span", { style: { fontSize: 12.5, fontWeight: 700, color: "var(--warn, #e6b450)" } }, g.modId),
              React.createElement("span", { style: { fontSize: 11, color: "var(--text-faint)" } }, g.files.length + " jars"),
              React.createElement(Btn, { variant: "outline", size: "sm", style: { marginLeft: "auto" }, onClick: () => disableDupes(g) }, "Keep 1, disable rest")),
            React.createElement("div", { className: "mono", style: { fontSize: 11, color: "var(--text-faint)", wordBreak: "break-all", lineHeight: 1.6 } }, g.files.join("  ·  ")))))
      : React.createElement("div", { style: { marginTop: 10, fontSize: 12.5, color: "var(--text-dim)" } }, "No duplicate mod ids in " + scan.total + " mods.")));

  return React.createElement("div", {
    style: { display: "flex", flexDirection: "column", gap: 16, position: "relative" },
    onDragEnter: e => { if (hasBridge) { e.preventDefault(); dragCnt.current++; setDragOver(true); } },
    onDragOver:  e => { if (hasBridge) e.preventDefault(); },
    onDragLeave: () => { if (hasBridge && --dragCnt.current <= 0) { dragCnt.current = 0; setDragOver(false); } },
    onDrop: handleModDrop,
  },
    dragOver && React.createElement("div", { style: { position: "absolute", inset: 0, zIndex: 40, borderRadius: "var(--r-2xl)", border: "2px dashed var(--acc-2)", background: "color-mix(in oklab, var(--acc) 14%, var(--panel) 86%)", display: "grid", placeItems: "center", pointerEvents: "none" } },
      React.createElement("div", { style: { textAlign: "center", color: "var(--acc-text)" } },
        React.createElement(Icon, { name: "upload", size: 32 }),
        React.createElement("div", { style: { fontSize: 14.5, fontWeight: 700, marginTop: 8 } }, "Drop .jar mods to add them"))),
    updatesCard,
    depCard,
    scanCard,
    React.createElement(Card, { style: { borderRadius: "var(--r-xl)", padding: 0, overflow: "hidden" } },
    React.createElement("div", { style: { display: "flex", gap: 12, alignItems: "center", padding: 16, borderBottom: "1px solid var(--border)", flexWrap: "wrap" } },
      React.createElement(TextInput, { value: q, onChange: setQ, placeholder: t("mods.search"), icon: "search", size: "sm", style: { flex: 1, minWidth: 180 } }),
      React.createElement(Segmented, {
        size: "sm", value: filter, onChange: setFilter,
        options: [
          { value: "all",      label: t("common.all") },
          { value: "optim",    label: t("mods.optimOnly") + " " + optimCount, icon: "zap" },
          { value: "updates",  label: t("mods.updates") + " " + updateCount },
          { value: "disabled", label: "Disabled " + (mods.length - enabledCount) },
        ],
      }),
      hasBridge && React.createElement(Btn, { variant: "outline", size: "sm", icon: "upload", onClick: pickLocalMods }, "Add .jar"),
    ),
    React.createElement("div", { style: { maxHeight: 520, overflowY: "auto" } },
      filtered.length === 0
        ? React.createElement("div", { style: { padding: 40 } },
            React.createElement(EmptyState, { icon: "package", title: "No mods match", body: t("logs.empty") }))
        : filtered.map((m, i) => React.createElement("div", {
            key: m.id,
            style: {
              display: "flex", alignItems: "center", gap: 12, padding: "11px 16px",
              borderBottom: i < filtered.length - 1 ? "1px solid var(--border-faint)" : "none",
              opacity: m.enabled ? 1 : 0.45, transition: "opacity .2s",
            },
          },
            React.createElement("div", {
              style: { width: 32, height: 32, borderRadius: 9, display: "grid", placeItems: "center", flexShrink: 0,
                background: m.optimization ? "var(--acc-soft)" : "var(--panel-2)",
                color: m.optimization ? "var(--acc-text)" : "var(--text-faint)", border: "1px solid var(--border)" },
            }, React.createElement(Icon, { name: m.optimization ? "zap" : "package", size: 15 })),
            React.createElement("div", { style: { flex: 1, minWidth: 0 } },
              React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 8 } },
                React.createElement("span", { style: { fontSize: 13.5, fontWeight: 600, color: "var(--text)", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, m.name),
                m.optimization && React.createElement(Tip, { label: t("mods.optimTip") },
                  React.createElement(Badge, { tone: "accent", size: "sm" }, "opt")),
                m.update && React.createElement(Badge, { tone: "warn", size: "sm", icon: "download" }, t("mods.update")),
              ),
              React.createElement("div", { className: "mono", style: { fontSize: 11, color: "var(--text-faint)", marginTop: 1 } },
                m.version ? "v" + m.version : ""),
            ),
            React.createElement("span", { className: "tnum", style: { fontSize: 11.5, color: "var(--text-faint)", minWidth: 56, textAlign: "right" } },
              m.sizeMb.toFixed(1) + " MB"),
            React.createElement(Toggle, { checked: m.enabled, onChange: () => toggle(m), size: "sm" }),
          )),
    ),
    React.createElement("div", { style: { padding: "11px 16px", borderTop: "1px solid var(--border)", display: "flex", justifyContent: "space-between", fontSize: 12, color: "var(--text-faint)" } },
      React.createElement("span", { className: "tnum" },
        t("mods.title", { n: filtered.length }) + (filtered.length !== mods.length ? " / " + mods.length : "")),
      React.createElement("span", null,
        optimCount + " optimization · " + updateCount + " updates · " + enabledCount + " enabled"),
    ),
  ));
}

/* ============ INSTANCE SETTINGS ============ */
function JvmChipEditor({ args, onChange, api, t }) {
  const [val, setVal] = tS("");
  function add(raw) {
    const parts = raw.split(/[\s\n,]+/).map(s => s.trim()).filter(Boolean);
    if (parts.length) onChange([...args, ...parts]);
    setVal("");
  }
  return React.createElement("div", null,
    React.createElement("div", { style: { display: "flex", flexWrap: "wrap", gap: 7, marginBottom: 10, minHeight: 34 } },
      args.length === 0 && React.createElement("span", { style: { fontSize: 12, color: "var(--text-faint)", alignSelf: "center" } }, "No JVM arguments — add below or pick a preset"),
      args.map((a, i) => {
        const v = api.validateArg ? api.validateArg(a) : { ok: true, level: "ok", msg: "" };
        const tone = v.level === "error" ? "error" : v.level === "warn" ? "warn" : "neutral";
        return React.createElement(Tip, { key: i, label: v.msg || a },
          React.createElement("span", {
            style: {
              display: "inline-flex", alignItems: "center", gap: 6, padding: "5px 8px 5px 10px",
              borderRadius: 8, fontSize: 11.5, fontWeight: 500,
              background: tone === "error" ? "var(--error-dim)" : tone === "warn" ? "var(--warn-dim)" : "var(--panel-2)",
              border: "1px solid " + (tone === "error" ? "color-mix(in oklab,var(--error) 32%,transparent)" : tone === "warn" ? "color-mix(in oklab,var(--warn) 32%,transparent)" : "var(--border)"),
            }, className: "mono",
          },
            (v.level === "error" || v.level === "warn") && React.createElement(Icon, { name: "alert", size: 12, style: { color: tone === "error" ? "var(--error)" : "var(--warn)" } }),
            React.createElement("span", { style: { color: "var(--text)" } }, a),
            React.createElement("button", {
              onClick: () => onChange(args.filter((_, j) => j !== i)), className: "no-drag",
              style: { display: "grid", placeItems: "center", border: "none", background: "transparent", color: "var(--text-faint)", padding: 0 },
            }, React.createElement(Icon, { name: "x", size: 12 })),
          ));
      }),
    ),
    React.createElement(TextInput, {
      value: val, onChange: setVal, placeholder: t("set.jvmAdd"), icon: "plus", mono: true, size: "sm",
      onKeyDown: e => { if (e.key === "Enter" || e.key === ",") { e.preventDefault(); add(val); } },
    }),
    api.presets && Object.keys(api.presets).length > 0 && React.createElement("div", { style: { display: "flex", gap: 9, marginTop: 9, flexWrap: "wrap", alignItems: "center" } },
      React.createElement("span", { style: { fontSize: 11.5, color: "var(--text-faint)" } }, t("set.preset") + ":"),
      Object.keys(api.presets).map(p =>
        React.createElement(Btn, { key: p, variant: "ghost", size: "sm",
          onClick: () => onChange(api.presets[p].slice()) }, p)),
    ),
  );
}

// Pick an Xmx (MB) from the pack's mod count, capped to the machine's RAM.
// More heap isn't always better (huge heaps mean longer GC pauses), so we also
// cap at 16 GB and always leave headroom for the OS.
function recommendRamMb(mods, sysRamMb) {
  let want = mods >= 350 ? 10240 : mods >= 200 ? 8192 : mods >= 100 ? 6144 : mods >= 30 ? 4096 : 3072;
  const sys = sysRamMb > 0 ? sysRamMb : 8192;
  const headroom = sys >= 16384 ? 4096 : Math.max(2048, Math.floor(sys * 0.25));
  const ceiling = Math.max(2048, sys - headroom);
  want = Math.min(want, ceiling, 16384);
  return Math.max(2048, Math.floor(want / 512) * 512);
}

function SettingsTab({ instance, t, fmt, api, hasBridge }) {
  const [ramMin,   setRamMin]   = tS(instance.ramMin   || 2048);
  const [ramMax,   setRamMax]   = tS(instance.ramMax   || 8192);
  const [args,     setArgs]     = tS([]);
  const [javaPath, setJavaPath] = tS(instance.java || "");
  const [res,      setRes]      = tS("1920×1080");
  const [saving,   setSaving]   = tS(false);
  const [dirty,    setDirty]    = tS(false);
  const [loaded,   setLoaded]   = tS(false);
  const [javas,    setJavas]    = tS(null);    // null = not scanned yet; [] = none found
  const [reqMajor, setReqMajor] = tS(0);       // Java major this instance's MC needs
  const [recPath,  setRecPath]  = tS("");      // recommended java path (or "" → download)
  const [detecting,setDetecting]= tS(false);
  const [sysRamMb, setSysRamMb] = tS(0);       // total physical RAM (caps the Max slider)

  // Load real config from bridge or fall back to mock preset
  tE(() => {
    if (hasBridge && api.getInstanceCfg) {
      api.getInstanceCfg(instance.id).then(cfg => {
        setRamMin(cfg.ramMin || instance.ramMin || 2048);
        setRamMax(cfg.ramMax || instance.ramMax || 8192);
        if (cfg.javaPath) setJavaPath(cfg.javaPath);
        // Parse JVM args string into chip array
        const argArr = (cfg.jvmArgs || "").replace(/^"|"$/g, "").trim().split(/\s+/).filter(Boolean).map(a => a.replace(/^"|"$/g, "")).filter(Boolean);
        setArgs(argArr.length > 0 ? argArr : (api.presets?.["Balanced (G1GC)"] || []));
        setLoaded(true);
      }).catch(() => {
        setArgs(api.presets?.["Balanced (G1GC)"] || []);
        setLoaded(true);
      });
    } else {
      setArgs(api.presets?.["Balanced (G1GC)"] || []);
      setLoaded(true);
    }
  }, [instance.id, hasBridge]);

  // Discover installed Javas (Cryo bundled, Prism, vendor dirs, JAVA_HOME, PATH).
  // fill=true also writes the recommended path into the field (Auto-detect button).
  async function detectJava(fill) {
    if (!hasBridge || !api.detectJavas) return;
    setDetecting(true);
    try {
      const r = await api.detectJavas(instance.id);
      setJavas((r && r.javas) || []);
      setReqMajor((r && r.requiredMajor) || 0);
      setRecPath((r && r.recommendedPath) || "");
      if (fill) {
        const p = (r && r.recommendedPath) || "";
        setJavaPath(p); setDirty(true);
        if (window.toast) window.toast(p
          ? { tone: "success", icon: "check", title: "Java auto-detected", body: "Java " + ((r && r.requiredMajor) || "?") + " · " + p }
          : { tone: "info", icon: "info", title: "Auto Java", body: "Cryo will download Java " + ((r && r.requiredMajor) || "?") + " on launch." });
      }
    } catch (e) {
      if (window.toast) window.toast({ tone: "error", icon: "alert", title: "Java detection failed", body: e.message });
    } finally { setDetecting(false); }
  }
  tE(() => { if (hasBridge) detectJava(false); }, [instance.id, hasBridge]);
  tE(() => { if (hasBridge && api.getSystemRam) api.getSystemRam().then(r => setSysRamMb((r && r.totalMb) || 0)).catch(() => {}); }, [hasBridge]);

  function markDirty(fn, setter) { return v => { setter(v); setDirty(true); }; }

  // One-click tune: RAM from mod count + this PC's RAM, JVM preset from Java major.
  function optimize() {
    const mods = instance.mods || 0;
    const ram  = recommendRamMb(mods, sysRamMb);
    const useZgc = reqMajor >= 21 && ram >= 10240;   // ZGC low-pause for Java 21 + big heaps
    const presetName = useZgc ? "Low-pause (ZGC, Java 21)" : "Aikar's flags";
    const presetArgs = (api.presets && (api.presets[presetName] || api.presets["Balanced (G1GC)"])) || [];
    setRamMax(ram);
    setRamMin(ram);            // fixed heap (Xms == Xmx) — avoids resize stutter
    setArgs(presetArgs.slice());
    setDirty(true);
    if (window.toast) window.toast({
      tone: "success", icon: "zap",
      title: "Optimized for " + (instance.name || "this pack"),
      body: (ram / 1024).toFixed(1) + " GB · " + presetName + " · " + mods + " mods · Java " + (reqMajor || "?"),
    });
  }

  async function save() {
    setSaving(true);
    try {
      if (hasBridge && api.saveInstanceCfg) {
        await api.saveInstanceCfg(instance.id, {
          jvmArgs:  args.join(" "),
          javaPath,
          ramMin,
          ramMax,
        });
        window.toast({ tone: "success", icon: "check", title: "Settings saved", body: instance.name });
      } else {
        window.toast({ tone: "warn", icon: "info", title: "Preview mode", body: "Changes are local only (no bridge)" });
      }
      setDirty(false);
    } catch (err) {
      window.toast({ tone: "error", icon: "alert", title: "Save failed", body: err.message });
    } finally {
      setSaving(false);
    }
  }

  function cancel() {
    // Reload from bridge to revert
    setDirty(false);
    if (hasBridge && api.getInstanceCfg) {
      api.getInstanceCfg(instance.id).then(cfg => {
        setRamMin(cfg.ramMin); setRamMax(cfg.ramMax);
        if (cfg.javaPath) setJavaPath(cfg.javaPath);
        const argArr = (cfg.jvmArgs || "").replace(/^"|"$/g, "").trim().split(/\s+/).filter(Boolean).map(a => a.replace(/^"|"$/g, "")).filter(Boolean);
        setArgs(argArr);
      }).catch(() => {});
    }
  }

  if (!loaded) return React.createElement("div", { style: { display: "flex", justifyContent: "center", padding: 40 } },
    React.createElement(Spinner, { size: 24 }));

  // Match the configured path against detected Javas ignoring slash style
  // (instance.cfg uses forward slashes; detected paths use backslashes).
  const normJava = s => (s || "").replace(/[\\/]+/g, "/").toLowerCase();
  const javaSel  = (javas || []).find(j => normJava(j.path) === normJava(javaPath));
  // Cap the Max-RAM slider at the machine's physical RAM (not a fixed 64 GB).
  // Keep at least the stored value so an existing higher setting still renders.
  const ramCeil  = sysRamMb > 0 ? Math.max(Math.floor(sysRamMb / 512) * 512, ramMax) : 65536;

  return React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 18 } },
    // Unsaved changes banner
    dirty && React.createElement("div", {
      style: { display: "flex", alignItems: "center", gap: 10, padding: "10px 16px", borderRadius: "var(--r-md)", background: "var(--warn-dim)", border: "1px solid color-mix(in oklab, var(--warn) 28%, transparent)" },
    },
      React.createElement(Icon, { name: "alert", size: 16, style: { color: "var(--warn)" } }),
      React.createElement("span", { style: { fontSize: 13, color: "var(--warn)", fontWeight: 600 } }, "Unsaved changes"),
      React.createElement("span", { style: { fontSize: 12, color: "var(--text-dim)" } }, "— saved directly to instance.cfg"),
    ),

    React.createElement(Section, { icon: "ram", title: t("set.ram"),
        desc: sysRamMb > 0 ? ("This PC has " + (sysRamMb / 1024).toFixed(1) + " GB RAM installed — the maximum is capped to that.") : undefined },
      React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 18 } },
        React.createElement(LabeledRow, { label: t("set.ramMin") },
          React.createElement(Slider, { value: ramMin, min: 512, max: ramMax, step: 512, onChange: markDirty(null, setRamMin),
            format: v => (v / 1024).toFixed(1) + " GB" })),
        React.createElement(LabeledRow, { label: t("set.ramMax") },
          React.createElement(Slider, { value: ramMax, min: ramMin, max: ramCeil, step: 512, onChange: markDirty(null, setRamMax),
            format: v => (v / 1024).toFixed(1) + " GB" })),
        React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginTop: 2, flexWrap: "wrap" } },
          React.createElement(Btn, { variant: "accentSoft", size: "sm", icon: "zap", onClick: optimize, disabled: !sysRamMb },
            "Optimize for this pack"),
          React.createElement("span", { style: { fontSize: 11.5, color: "var(--text-faint)" } },
            "Auto-sets RAM + JVM flags from " + (instance.mods || 0) + " mods · "
              + (sysRamMb ? (sysRamMb / 1024).toFixed(0) + " GB RAM" : "your RAM") + " · Java " + (reqMajor || "?"))),
      ),
    ),

    React.createElement(Section, { icon: "cpu", title: t("set.java") },
      React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 14 } },
        React.createElement(LabeledRow, { label: t("set.javaPath") },
          React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 8 } },
            React.createElement("div", { style: { display: "flex", gap: 8, alignItems: "center" } },
              React.createElement("div", { style: { flex: 1, minWidth: 0 } },
                React.createElement(TextInput, { value: javaPath, onChange: v => { setJavaPath(v); setDirty(true); },
                  icon: "folder", mono: true, size: "sm", placeholder: "Auto-detect" })),
              React.createElement(Btn, { variant: "subtle", size: "sm", icon: detecting ? "refresh" : "sparkles", iconSpin: detecting,
                disabled: detecting, onClick: () => detectJava(true) }, "Auto-detect")),
            (javas && javas.length > 0) && React.createElement("div", { style: { display: "flex", gap: 8, alignItems: "center" } },
              React.createElement("span", { style: { fontSize: 11.5, color: "var(--text-faint)", whiteSpace: "nowrap" } },
                "Detected" + (reqMajor ? " · needs Java " + reqMajor : "") + ":"),
              React.createElement(Select, { value: (javaSel ? javaSel.path : ""), size: "sm", width: 380,
                onChange: v => { setJavaPath(v); setDirty(true); },
                options: [{ value: "", label: "Auto" + (recPath ? "" : " (download on launch)") }].concat(
                  javas.map(j => ({ value: j.path, label: (j.recommended ? "★ " : "") + "Java " + j.major + (j.vendor ? " · " + j.vendor : "") + (j.version ? " (" + j.version + ")" : "") }))) })),
            (javas && javas.length === 0) && React.createElement("span", { style: { fontSize: 11.5, color: "var(--text-faint)" } },
              "No Java found on disk — Cryo will download Java " + (reqMajor || "?") + " automatically on launch."))),
        React.createElement(LabeledRow, { label: t("set.window") },
          React.createElement(Select, { value: res, onChange: setRes, width: 200, size: "sm",
            options: ["1280×720", "1600×900", "1920×1080", "2560×1440", "Fullscreen"] })),
      ),
    ),

    React.createElement(Section, { icon: "sliders", title: t("set.jvm"), desc: t("set.jvmHelp") },
      React.createElement(JvmChipEditor, { args, onChange: v => { setArgs(v); setDirty(true); }, api, t }),
    ),

    React.createElement("div", { style: { display: "flex", justifyContent: "flex-end", gap: 10, paddingBottom: 8 } },
      React.createElement(Btn, { variant: "ghost", onClick: cancel, disabled: !dirty || saving }, t("common.cancel")),
      React.createElement(Btn, { variant: "primary", icon: saving ? "loader" : "check", iconSpin: saving,
        onClick: save, disabled: !dirty || saving },
        saving ? "Saving…" : t("common.save")),
    ),
  );
}

// Top-level (NOT defined inside SettingsTab) so its identity is stable across
// renders — otherwise React remounts the whole subtree each keystroke and any
// focused input (e.g. the Java path field) loses focus after every letter.
function Section({ icon, title, desc, children }) {
  return React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, marginBottom: desc ? 4 : 16 } },
      React.createElement(Icon, { name: icon, size: 16, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 14.5, fontWeight: 680 } }, title)),
    desc && React.createElement("p", { style: { margin: "0 0 16px 25px", fontSize: 12, color: "var(--text-faint)" } }, desc),
    children);
}

function LabeledRow({ label, children }) {
  return React.createElement("div", { style: { display: "grid", gridTemplateColumns: "160px 1fr", gap: 16, alignItems: "center" }, className: "cryo-labeled" },
    React.createElement("span", { style: { fontSize: 13, color: "var(--text-dim)", fontWeight: 600 } }, label),
    React.createElement("div", null, children),
  );
}

/* ============ WORLDS TAB — backups + world list ============ */
function WorldsTab({ instance, api, hasBridge, fmt }) {
  const [worlds, setWorlds]   = tS([]);
  const [backups, setBackups] = tS([]);
  const [busy, setBusy]       = tS({});   // { [worldName]: bool }
  const [restoring, setRest]  = tS({});   // { [file]: bool }
  const [tab, setTab]         = tS("worlds");  // "worlds" | "backups"

  async function load() {
    if (!hasBridge) return;
    const [wR, bR] = await Promise.allSettled([api.getWorlds(instance.id), api.getBackups(instance.id)]);
    if (wR.status === "fulfilled") setWorlds((wR.value && wR.value.worlds) || []);
    if (bR.status === "fulfilled") setBackups((bR.value && bR.value.backups) || []);
  }

  tE(() => { load(); }, [hasBridge, instance.id]);

  // Push events
  tE(() => {
    function onDone(e) {
      const d = e.detail || {};
      setBusy(b => { const nb = { ...b }; delete nb[d.worldName]; return nb; });
      setRest(r => { const nr = { ...r }; delete nr[d.file || ""]; return nr; });
      if (d.ok) {
        window.toast({ tone: "success", icon: "check", title: d.restoredAs ? "Restored as " + d.restoredAs : "Backed up: " + d.worldName });
        load();
      }
    }
    function onErr(e) {
      const d = e.detail || {};
      setBusy({}); setRest({});
      window.toast({ tone: "danger", icon: "alert", title: "Backup error", body: d.error || "" });
    }
    window.addEventListener("cryo:backupDone",  onDone);
    window.addEventListener("cryo:backupError", onErr);
    return () => { window.removeEventListener("cryo:backupDone", onDone); window.removeEventListener("cryo:backupError", onErr); };
  }, []);

  async function doBackup(worldName) {
    setBusy(b => ({ ...b, [worldName]: true }));
    await api.backupWorld(instance.id, worldName).catch(e => window.toast({ tone: "danger", icon: "alert", title: "Error", body: String(e) }));
  }
  async function doRestore(file) {
    if (!window.confirm("Restore this backup? It will be extracted as a new world folder (original preserved).")) return;
    setRest(r => ({ ...r, [file]: true }));
    await api.restoreBackup(instance.id, file).catch(e => window.toast({ tone: "danger", icon: "alert", title: "Error", body: String(e) }));
  }
  async function doDelete(file) {
    if (!window.confirm("Delete this backup permanently?")) return;
    const r = await api.deleteBackup(instance.id, file).catch(() => null);
    if (r && r.ok) { window.toast({ tone: "neutral", icon: "trash", title: "Deleted" }); load(); }
  }

  function fmtDate(ts) { return ts ? new Date(ts).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" }) : "—"; }

  return React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 16 } },
    // Header row
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, flexWrap: "wrap" } },
      React.createElement(Segmented, { size: "sm", value: tab, onChange: setTab,
        options: [{ value: "worlds", label: "Worlds (" + worlds.length + ")", icon: "globe" },
                  { value: "backups", label: "Backups (" + backups.length + ")", icon: "database" }] }),
      React.createElement("div", { style: { flex: 1 } }),
      React.createElement(Btn, { variant: "ghost", size: "sm", icon: "refresh", onClick: load }, "Refresh"),
      tab === "worlds" && React.createElement(Btn, { variant: "ghost", size: "sm", icon: "folder", onClick: () => hasBridge && api.openWorldsFolder(instance.id) }, "Open Folder"),
    ),

    // Worlds list
    tab === "worlds" && React.createElement(Card, { pad: false, style: { borderRadius: "var(--r-xl)", overflow: "hidden" } },
      worlds.length === 0
        ? React.createElement("div", { style: { padding: 40, textAlign: "center" } },
            React.createElement(EmptyState, { icon: "globe", title: "No worlds found", body: "Launch the game and create a world first." }))
        : worlds.map((w, i) => React.createElement("div", { key: w.name, style: { display: "flex", alignItems: "center", gap: 14, padding: "14px 18px", borderBottom: i < worlds.length - 1 ? "1px solid var(--border-faint)" : "none" } },
            React.createElement("div", { style: { width: 40, height: 40, borderRadius: 11, display: "grid", placeItems: "center", background: "var(--acc-soft)", border: "1px solid var(--acc-soft-2)", flexShrink: 0 } },
              React.createElement(Icon, { name: "globe", size: 19, style: { color: "var(--acc-text)" } })),
            React.createElement("div", { style: { flex: 1, minWidth: 0 } },
              React.createElement("div", { style: { fontWeight: 680, fontSize: 14 } }, w.name),
              React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 2 } },
                fmt.bytes(w.sizeBytes) + " · Last played " + fmtDate(w.modified))),
            React.createElement(Btn, {
              variant: "outline", size: "sm", icon: busy[w.name] ? "refresh" : "download",
              iconSpin: !!busy[w.name], disabled: !!busy[w.name],
              onClick: () => doBackup(w.name),
            }, busy[w.name] ? "Backing up…" : "Backup"),
          )),
    ),

    // Backups list
    tab === "backups" && React.createElement(Card, { pad: false, style: { borderRadius: "var(--r-xl)", overflow: "hidden" } },
      backups.length === 0
        ? React.createElement("div", { style: { padding: 40, textAlign: "center" } },
            React.createElement(EmptyState, { icon: "database", title: "No backups yet", body: "Go to the Worlds tab and click Backup on any world." }))
        : backups.map((b, i) => React.createElement("div", { key: b.file, style: { display: "flex", alignItems: "center", gap: 14, padding: "13px 18px", borderBottom: i < backups.length - 1 ? "1px solid var(--border-faint)" : "none" } },
            React.createElement("div", { style: { width: 38, height: 38, borderRadius: 10, display: "grid", placeItems: "center", background: "var(--panel-2)", border: "1px solid var(--border)", flexShrink: 0 } },
              React.createElement(Icon, { name: "database", size: 17, style: { color: "var(--text-dim)" } })),
            React.createElement("div", { style: { flex: 1, minWidth: 0 } },
              React.createElement("div", { style: { fontWeight: 600, fontSize: 13, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" } }, b.file),
              React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 2 } },
                fmt.bytes(b.sizeBytes) + " · " + fmtDate(b.modified))),
            React.createElement("div", { style: { display: "flex", gap: 6 } },
              React.createElement(Btn, {
                variant: "outline", size: "sm", icon: restoring[b.file] ? "refresh" : "upload",
                iconSpin: !!restoring[b.file], disabled: !!restoring[b.file],
                onClick: () => doRestore(b.file),
              }, "Restore"),
              React.createElement(Tip, { label: "Delete backup permanently" },
                React.createElement(Btn, { variant: "ghost", size: "icon", onClick: () => doDelete(b.file) },
                  React.createElement(Icon, { name: "trash", size: 14 }))),
            ),
          )),
    ),

    // Info footer
    React.createElement("div", { style: { padding: "10px 14px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 12, color: "var(--text-faint)", display: "flex", alignItems: "center", gap: 8 } },
      React.createElement(Icon, { name: "info", size: 13 }),
      "Backups are stored in %LOCALAPPDATA%\\VSpeedLauncher\\backups\\ and never deleted automatically."),
  );
}

/* ============ EXPORT / IMPORT (used in SettingsTab) ============ */
function ModpackIOCard({ instance, api, hasBridge }) {
  const [exporting, setExport] = tS(false);
  const [importing, setImport] = tS(false);

  tE(() => {
    function onExDone(e) {
      setExport(false);
      const d = e.detail || {};
      if (d.cancelled) return;
      d.ok ? window.toast({ tone: "success", icon: "download", title: "Modpack exported", body: d.path || "" })
           : window.toast({ tone: "danger", icon: "alert", title: "Export failed", body: d.error || "" });
    }
    function onImDone(e) {
      setImport(false);
      const d = e.detail || {};
      if (d.cancelled) return;
      d.ok ? window.toast({ tone: "success", icon: "upload", title: "Modpack imported", body: d.name + " (" + d.id + ")" })
           : window.toast({ tone: "danger", icon: "alert", title: "Import failed", body: d.error || "" });
    }
    window.addEventListener("cryo:exportDone", onExDone);
    window.addEventListener("cryo:importDone", onImDone);
    return () => { window.removeEventListener("cryo:exportDone", onExDone); window.removeEventListener("cryo:importDone", onImDone); };
  }, []);

  return React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 12 } },
      React.createElement(Icon, { name: "download", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Modpack Export / Import")),
    React.createElement("p", { style: { margin: "0 0 14px", fontSize: 12.5, color: "var(--text-dim)", lineHeight: 1.55 } },
      "Export saves mods, config, options.txt and a manifest into a portable ZIP. Import reads the ZIP and creates a new instance. Saves and sensitive data (tokens, logs) are not included."),
    React.createElement("div", { style: { display: "flex", gap: 10, flexWrap: "wrap" } },
      React.createElement(Btn, {
        variant: "primary", icon: exporting ? "refresh" : "download",
        iconSpin: exporting, disabled: exporting || importing,
        onClick: async () => {
          if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
          setExport(true);
          await api.exportModpack(instance.id).catch(e => { setExport(false); window.toast({ tone: "danger", icon: "alert", title: "Error", body: String(e) }); });
        },
      }, exporting ? "Exporting…" : "Export Modpack"),
      React.createElement(Btn, {
        variant: "outline", icon: importing ? "refresh" : "upload",
        iconSpin: importing, disabled: exporting || importing,
        onClick: async () => {
          if (!hasBridge) { window.toast({ tone: "warn", icon: "info", title: "Desktop only" }); return; }
          setImport(true);
          await api.importModpack().catch(e => { setImport(false); window.toast({ tone: "danger", icon: "alert", title: "Error", body: String(e) }); });
        },
      }, importing ? "Importing…" : "Import Modpack"),
    ),
  );
}

/* ============ SERVERS TAB — server list + live ping ============ */
function ServersTab({ instance, api, hasBridge }) {
  const [servers, setServers] = tS([]);
  const [status, setStatus]   = tS({});   // { [ip]: { online, motd, players, maxPlayers, version, latencyMs, error, pinging } }
  const [adding, setAdding]   = tS(false);
  const [newName, setNewName] = tS("");
  const [newIp, setNewIp]     = tS("");

  async function load() {
    if (!hasBridge) return;
    const r = await api.getServers(instance.id).catch(() => ({ servers: [] }));
    const list = (r && r.servers) || [];
    setServers(list);
    list.forEach(s => ping(s.ip));
  }
  tE(() => { load(); }, [hasBridge, instance.id]);

  async function ping(ip) {
    setStatus(st => ({ ...st, [ip]: { ...(st[ip] || {}), pinging: true } }));
    const r = await api.pingServer(ip).catch(() => ({ online: false, error: "ping failed" }));
    setStatus(st => ({ ...st, [ip]: { ...r, pinging: false } }));
  }

  async function add() {
    if (!newIp.trim()) return;
    const r = await api.addServer(instance.id, newName.trim(), newIp.trim()).catch(e => ({ ok: false, error: String(e) }));
    if (r && r.ok) {
      setNewName(""); setNewIp(""); setAdding(false);
      window.toast({ tone: "success", icon: "check", title: "Server added" });
      load();
    } else window.toast({ tone: "danger", icon: "alert", title: "Couldn't add", body: (r && r.error) || "" });
  }

  async function remove(ip) {
    if (!window.confirm("Remove this server from the list?")) return;
    const r = await api.removeServer(instance.id, ip).catch(() => null);
    if (r && r.ok) { window.toast({ tone: "neutral", icon: "trash", title: "Removed" }); load(); }
  }

  async function join(ip) {
    const r = await api.joinServer(instance.id, ip).catch(e => ({ ok: false, error: String(e) }));
    if (r && r.ok) window.toast({ tone: "success", icon: "zap", title: "Launching", body: "Joining " + ip + " on start…" });
    else window.toast({ tone: "danger", icon: "alert", title: "Couldn't launch", body: (r && r.error) || "Sign in and install the instance first." });
  }

  const dot = (s) => {
    if (!s || s.pinging) return { c: "var(--warn)", t: "…" };
    if (s.online) return { c: "var(--success)", t: (s.latencyMs != null ? s.latencyMs + "ms" : "online") };
    return { c: "var(--error)", t: "offline" };
  };

  return React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 16 } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10 } },
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Servers"),
      React.createElement("span", { style: { fontSize: 12, color: "var(--text-faint)" } }, servers.length + " saved"),
      React.createElement("div", { style: { flex: 1 } }),
      React.createElement(Btn, { variant: "ghost", size: "sm", icon: "refresh", onClick: load }, "Refresh"),
      React.createElement(Btn, { variant: adding ? "accentSoft" : "primary", size: "sm", icon: adding ? "x" : "plus", onClick: () => setAdding(a => !a) }, adding ? "Cancel" : "Add server")),

    // Add form
    adding && React.createElement(Card, { style: { borderRadius: "var(--r-lg)" } },
      React.createElement("div", { style: { display: "flex", gap: 10, flexWrap: "wrap", alignItems: "center" } },
        React.createElement(TextInput, { value: newName, onChange: setNewName, placeholder: "Name (optional)", size: "sm", style: { flex: 1, minWidth: 140 } }),
        React.createElement(TextInput, { value: newIp, onChange: setNewIp, placeholder: "address (e.g. play.example.com)", icon: "globe", size: "sm", style: { flex: 2, minWidth: 200 } }),
        React.createElement(Btn, { variant: "primary", size: "sm", icon: "check", disabled: !newIp.trim(), onClick: add }, "Add"))),

    // Server list
    servers.length === 0
      ? React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
          React.createElement(EmptyState, { icon: "globe", title: "No servers saved", body: "Add a server above, or join one in-game and it'll appear here." }))
      : React.createElement(Card, { pad: false, style: { borderRadius: "var(--r-xl)", overflow: "hidden" } },
          servers.map((s, i) => {
            const st = status[s.ip]; const d = dot(st);
            return React.createElement("div", { key: s.ip + i, style: { display: "flex", alignItems: "center", gap: 14, padding: "13px 18px", borderBottom: i < servers.length - 1 ? "1px solid var(--border-faint)" : "none" } },
              React.createElement("span", { style: { width: 9, height: 9, borderRadius: 99, background: d.c, flexShrink: 0, boxShadow: st && st.online ? "0 0 8px " + d.c : "none" } }),
              React.createElement("div", { style: { flex: 1, minWidth: 0 } },
                React.createElement("div", { style: { fontSize: 14, fontWeight: 680, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, s.name || s.ip),
                React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 2, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } },
                  s.ip + (st && st.online && st.motd ? "  ·  " + st.motd.replace(/§./g, "") : ""))),
              st && st.online && React.createElement("span", { className: "tnum", style: { fontSize: 11.5, color: "var(--text-dim)", flexShrink: 0 } },
                st.players + "/" + st.maxPlayers),
              React.createElement("span", { className: "tnum", style: { fontSize: 11, color: d.c, minWidth: 52, textAlign: "right", flexShrink: 0 } }, d.t),
              React.createElement(Tip, { label: "Launch this instance and connect" },
                React.createElement(Btn, { variant: "primary", size: "sm", icon: "zap", onClick: () => join(s.ip) }, "Join")),
              React.createElement(Tip, { label: "Re-ping" },
                React.createElement(Btn, { variant: "ghost", size: "icon", onClick: () => ping(s.ip) }, React.createElement(Icon, { name: "refresh", size: 14 }))),
              React.createElement(Tip, { label: "Remove" },
                React.createElement(Btn, { variant: "ghost", size: "icon", onClick: () => remove(s.ip) }, React.createElement(Icon, { name: "trash", size: 14 }))),
            );
          })),

    React.createElement("div", { style: { padding: "10px 14px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", fontSize: 12, color: "var(--text-faint)", display: "flex", alignItems: "center", gap: 8 } },
      React.createElement(Icon, { name: "info", size: 13 }),
      "Live status is pinged directly from your PC. Edits are saved to servers.dat (a backup .bak is kept)."),
  );
}

/* ============ APPLY PROFILE (used in instance SettingsTab) ============ */
function ProfileApplyCard({ instance, api, hasBridge }) {
  const [profiles, setProfiles] = tS([]);
  const [sel, setSel]           = tS("");
  const [applying, setApplying] = tS(false);

  tE(() => {
    if (!hasBridge) return;
    api.getProfiles().then(r => {
      const list = (r && r.profiles) || [];
      setProfiles(list);
      if (list.length) setSel(s => s || list[0].id);
    }).catch(() => {});
  }, [hasBridge]);

  async function apply() {
    if (!sel) return;
    setApplying(true);
    const r = await api.applyProfile(instance.id, sel).catch(e => ({ ok: false, error: String(e) }));
    setApplying(false);
    if (r && r.ok) window.toast({ tone: "success", icon: "check", title: "Profile applied", body: r.applied + " · " + (r.ramMax / 1024).toFixed(1) + "G" + (r.vspeedEnabled ? " · VSpeed on" : " · VSpeed off") });
    else window.toast({ tone: "danger", icon: "alert", title: "Couldn't apply", body: (r && r.error) || "" });
  }

  if (!hasBridge || profiles.length === 0) return null;
  const cur = profiles.find(p => p.id === sel);

  return React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10, marginBottom: 10 } },
      React.createElement(Icon, { name: "sliders", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Apply a profile")),
    React.createElement("p", { style: { margin: "0 0 12px", fontSize: 12.5, color: "var(--text-dim)", lineHeight: 1.5 } },
      "Overwrite this instance's RAM + JVM arguments with a saved preset. Manage presets in Settings → Profiles."),
    React.createElement("div", { style: { display: "flex", gap: 10, flexWrap: "wrap", alignItems: "center" } },
      React.createElement(Select, { value: sel, onChange: setSel, width: 240, size: "sm",
        options: profiles.map(p => ({ value: p.id, label: p.name })) }),
      cur && React.createElement("span", { style: { fontSize: 11.5, color: "var(--text-faint)" } },
        (cur.ramMax / 1024).toFixed(1) + "G · " + (cur.vspeedEnabled ? "VSpeed on" : "VSpeed off")),
      React.createElement("div", { style: { flex: 1 } }),
      React.createElement(Btn, { variant: "primary", size: "sm", icon: applying ? "refresh" : "check", iconSpin: applying, disabled: applying, onClick: apply }, "Apply")),
  );
}

/* ============ MODPACK UPDATE (used in instance SettingsTab) ============ */
function ModpackUpdateCard({ instance, api, hasBridge }) {
  const [info, setInfo] = tS(null);     // null=loading; {hasSource:false} = not a Cryo-installed pack
  const [busy, setBusy] = tS(false);
  const startedRef = tRf(false);

  async function refresh() {
    if (!hasBridge || !api.getModpackInfo) { setInfo({ hasSource: false }); return; }
    const r = await api.getModpackInfo(instance.id).catch(() => ({ hasSource: false }));
    setInfo(r || { hasSource: false });
  }
  tE(() => { refresh(); }, [hasBridge, instance.id]);

  tE(() => {
    function onDone(e) {
      if (!startedRef.current) return;          // ignore install events from elsewhere
      startedRef.current = false; setBusy(false);
      const d = e.detail || {};
      if (d.ok) { window.toast({ tone: "success", icon: "check", title: "Modpack updated", body: "Old mods were moved to a mods.bak-… folder; worlds kept." }); refresh(); }
      else window.toast({ tone: "danger", icon: "alert", title: "Update failed", body: d.error || "" });
    }
    window.addEventListener("cryo:modpackDone", onDone);
    return () => window.removeEventListener("cryo:modpackDone", onDone);
  }, [instance.id]);

  async function doUpdate() {
    if (!window.confirm("Update this modpack to the latest version?\n\nYour current mods are moved to a backup folder (mods.bak-…) and your worlds are kept. Pack config files may be overwritten.")) return;
    startedRef.current = true; setBusy(true);
    await api.updateModpack(instance.id).catch(e => {
      startedRef.current = false; setBusy(false);
      window.toast({ tone: "danger", icon: "alert", title: "Update failed", body: String(e) });
    });
  }

  if (!hasBridge || !info || !info.hasSource) return null;
  const upd = info.updateAvailable;
  return React.createElement(Section, { icon: "package", title: "Modpack",
      desc: info.name ? ((info.source === "curseforge" ? "CurseForge" : "Modrinth") + " · " + info.name) : undefined },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" } },
      React.createElement("span", { style: { fontSize: 13, fontWeight: 600, color: upd ? "var(--warn)" : "var(--text-dim)" } },
        info.error ? "Couldn't check for updates" : (upd ? ("Update available" + (info.latestName ? ": " + info.latestName : "")) : "Up to date")),
      React.createElement("div", { style: { flex: 1 } }),
      React.createElement(Btn, { variant: "ghost", size: "sm", icon: "refresh", onClick: refresh, disabled: busy }, "Check"),
      upd && React.createElement(Btn, { variant: "primary", size: "sm", icon: busy ? "refresh" : "download", iconSpin: busy, disabled: busy, onClick: doUpdate }, busy ? "Updating…" : "Update")),
  );
}

// ── Health check card (VSpeed diagnostics) ──────────────────────────────────
function HealthCard({ instance, api, hasBridge }) {
  const [h, setH] = tS(null);
  const [loading, setLoading] = tS(false);
  function run() {
    if (!hasBridge || !api.getHealth) return;
    setLoading(true);
    api.getHealth(instance.id).then(r => setH(r || null)).catch(() => {}).finally(() => setLoading(false));
  }
  tE(() => { run(); }, [instance.id]);

  const score = h ? h.score : 0;
  const ring  = score >= 80 ? "#36D399" : score >= 50 ? "#F1C40F" : "#E55A5A";
  const ic = s => s === "ok" ? { n: "check", c: "#36D399" } : s === "fail" ? { n: "alert", c: "#E55A5A" } : { n: "alert", c: "#F1C40F" };

  return React.createElement(Card, { style: { borderRadius: "var(--r-xl)" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 9, flexWrap: "wrap" } },
      React.createElement(Icon, { name: "activity", size: 17, style: { color: "var(--acc-2)" } }),
      React.createElement("h3", { style: { margin: 0, fontSize: 15, fontWeight: 680 } }, "Health check"),
      React.createElement(Btn, { variant: "outline", size: "sm", icon: "refresh", iconSpin: loading, disabled: loading, onClick: run, style: { marginLeft: "auto" } }, loading ? "Checking…" : "Recheck")),
    !hasBridge && React.createElement("div", { style: { marginTop: 10, fontSize: 12.5, color: "var(--text-dim)" } }, "Available in the desktop launcher."),
    h && React.createElement("div", { style: { display: "flex", gap: 18, alignItems: "center", marginTop: 14, flexWrap: "wrap" } },
      React.createElement("div", { style: { width: 78, height: 78, borderRadius: "50%", flexShrink: 0, display: "grid", placeItems: "center", background: "conic-gradient(" + ring + " " + (score * 3.6) + "deg, var(--panel-2) 0deg)" } },
        React.createElement("div", { style: { width: 62, height: 62, borderRadius: "50%", background: "var(--panel-solid)", display: "grid", placeItems: "center" } },
          React.createElement("span", { style: { fontSize: 22, fontWeight: 760, color: "var(--text)" } }, score))),
      React.createElement("div", { style: { flex: 1, minWidth: 240, display: "flex", flexDirection: "column", gap: 7 } },
        (h.checks || []).map((c, i) => React.createElement("div", { key: i, style: { display: "flex", alignItems: "center", gap: 9 } },
          React.createElement(Icon, { name: ic(c.status).n, size: 14, style: { color: ic(c.status).c, flexShrink: 0 } }),
          React.createElement("span", { style: { fontSize: 12.5, fontWeight: 600, minWidth: 120 } }, c.title),
          React.createElement("span", { style: { fontSize: 12, color: "var(--text-dim)" } }, c.detail))))));
}

// ── Screenshots gallery ──────────────────────────────────────────────────────
function ScreenshotsTab({ instance, api, hasBridge }) {
  const [shots, setShots] = tS(null);
  function load() {
    if (hasBridge && api.getScreenshots) api.getScreenshots(instance.id).then(r => setShots((r && r.shots) || [])).catch(() => setShots([]));
    else setShots([]);
  }
  tE(() => { load(); }, [instance.id]);

  function del(s) {
    if (!window.confirm("Delete this screenshot?\n" + s.file)) return;
    api.deleteScreenshot(instance.id, s.file).then(() => { setShots(list => (list || []).filter(x => x.file !== s.file)); window.toast({ tone: "neutral", icon: "trash", title: "Deleted" }); }).catch(() => {});
  }

  if (shots === null) return React.createElement("div", { style: { padding: 48, display: "grid", placeItems: "center" } }, React.createElement(Spinner, { size: 22 }));
  if (!shots.length) return React.createElement(EmptyState, { icon: "image", title: "No screenshots yet", body: "Screenshots you take in-game (F2) will show up here." });
  return React.createElement("div", null,
    React.createElement("div", { style: { fontSize: 12.5, color: "var(--text-dim)", marginBottom: 12 } }, shots.length + " screenshot" + (shots.length === 1 ? "" : "s") + " · click to open"),
    React.createElement("div", { style: { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))", gap: 12 } },
      shots.map(s => React.createElement("div", { key: s.file, className: "glass", style: { borderRadius: "var(--r-lg)", overflow: "hidden", border: "1px solid var(--border)" } },
        React.createElement("img", { src: s.thumb, loading: "lazy", onClick: () => api.openScreenshot(instance.id, s.file),
          style: { width: "100%", height: 132, objectFit: "cover", display: "block", cursor: "pointer", background: "var(--panel-2)" } }),
        React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 8, padding: "8px 10px" } },
          React.createElement("div", { style: { flex: 1, minWidth: 0 } },
            React.createElement("div", { style: { fontSize: 11.5, fontWeight: 600, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, s.file),
            React.createElement("div", { style: { fontSize: 10.5, color: "var(--text-faint)" } }, s.when + " · " + s.sizeKb + " KB")),
          React.createElement("button", { className: "no-drag", title: "Delete", onClick: () => del(s),
            style: { border: "none", background: "transparent", color: "var(--text-dim)", cursor: "pointer", padding: 4, display: "grid", placeItems: "center" } },
            React.createElement(Icon, { name: "trash", size: 14 })))))));
}

window.CryoInstanceTabs = { PerformanceTab, ModsTab, SettingsTab, WorldsTab, ModpackIOCard, ServersTab, ProfileApplyCard, ModpackUpdateCard, HealthCard, ScreenshotsTab };

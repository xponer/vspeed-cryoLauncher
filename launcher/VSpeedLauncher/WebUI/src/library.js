/* ============================================================
   Cryo вЂ” Library screen
   ============================================================ */
const { useState: lS, useEffect: lE, useMemo: lM, useCallback: lCb } = React;
var { useApp } = window.CryoStore;

function cacheBadge(state, t) {
  if (state === "ready")      return React.createElement(Badge, { tone: "accent", icon: "zap",     size: "sm" }, t("cache.ready"));
  if (state === "rebuilding") return React.createElement(Badge, { tone: "warn",   icon: "refresh", size: "sm" }, t("cache.rebuilding"));
  return React.createElement(Badge, { tone: "neutral", size: "sm" }, t("cache.off"));
}

function Banner({ instance, h = 96 }) {
  const a = instance.accent || "#38BDF8";
  return React.createElement("div", {
    style: {
      height: h, borderRadius: "var(--r-lg)", position: "relative", overflow: "hidden",
      background: `radial-gradient(120% 140% at 80% 0%, ${a}55, transparent 60%), linear-gradient(135deg, ${a}33, var(--panel-2))`,
      border: "1px solid var(--border)",
    },
  },
    React.createElement(Icon, { name: "snowflake", size: h, style: { position: "absolute", right: -12, bottom: -18, color: a, opacity: 0.18 } }),
    React.createElement("div", { style: { position: "absolute", left: 14, bottom: 12, width: 40, height: 40, borderRadius: 11, background: "var(--panel-solid)", border: "1px solid var(--border-strong)", display: "grid", placeItems: "center", color: a } },
      React.createElement(Icon, { name: instance.loader === "Fabric" ? "layers" : instance.loader === "Forge" ? "cpu" : "gem", size: 20 })),
  );
}

/* Confirm-delete overlay */
function DeleteConfirm({ instance, onConfirm, onCancel }) {
  return React.createElement("div", {
    onMouseDown: e => { if (e.target === e.currentTarget) onCancel(); },
    style: { position: "fixed", inset: 0, zIndex: 700, display: "grid", placeItems: "center",
      background: "rgba(0,0,0,.55)", backdropFilter: "blur(8px)" },
  },
    React.createElement("div", { className: "glass-pop anim-fadein",
      style: { width: 380, borderRadius: "var(--r-xl)", padding: 28 },
      onClick: e => e.stopPropagation() },
      React.createElement("div", { style: { width: 52, height: 52, borderRadius: 14, background: "var(--error-dim)", display: "grid", placeItems: "center", color: "var(--error)", marginBottom: 18 } },
        React.createElement(Icon, { name: "trash", size: 26 })),
      React.createElement("h3", { style: { margin: "0 0 8px", fontSize: 18, fontWeight: 700 } }, "Remove from launcher?"),
      React.createElement("p", { style: { margin: "0 0 24px", fontSize: 13.5, color: "var(--text-dim)", lineHeight: 1.5 } },
        React.createElement("strong", null, instance.name), " will be removed from Cryo's launcher list.",
        React.createElement("br", null),
        React.createElement("span", { style: { color: "var(--text-faint)" } },
          "The actual Prism instance and game files are NOT deleted.")),
      React.createElement("div", { style: { display: "flex", gap: 10, justifyContent: "flex-end" } },
        React.createElement(Btn, { variant: "ghost", onClick: onCancel }, "Cancel"),
        React.createElement(Btn, { variant: "danger", icon: "trash", onClick: onConfirm }, "Remove"),
      ),
    ),
  );
}

/* Create-instance dialog — native, no Prism */
function CreateInstanceDialog({ api, hasBridge, onClose, onCreated }) {
  const [name, setName]     = lS("");
  const [mc, setMc]         = lS("1.21.1");
  const [loader, setLoader] = lS("NeoForge");
  const [lver, setLver]     = lS("");
  const [lvers, setLvers]   = lS([]);          // NeoForge versions
  const [ram, setRam]       = lS(6144);
  const [busy, setBusy]     = lS(false);
  const sysRam              = useSysRamMb(api);

  // Fetch NeoForge versions when loader/mc changes
  lE(() => {
    if (!hasBridge || loader !== "NeoForge") { setLvers([]); setLver(""); return; }
    let alive = true;
    api.getNeoForgeVersions(mc).then(r => { if (alive && r && r.versions) { setLvers(r.versions); setLver(""); } }).catch(() => {});
    return () => { alive = false; };
  }, [loader, mc, hasBridge]);

  lE(() => { const m = maxRamMb(sysRam); if (sysRam && ram > m) setRam(m); }, [sysRam]);

  async function create() {
    if (!name.trim()) { window.toast({ tone: "warn", icon: "info", title: "Enter a name" }); return; }
    setBusy(true);
    const r = await api.createInstance({ name: name.trim(), mcVersion: mc.trim(), loader, loaderVersion: lver, ramMax: ram }).catch(e => ({ ok: false, error: String(e) }));
    setBusy(false);
    if (r && r.ok) {
      window.toast({ tone: "success", icon: "check", title: "Instance created", body: name + (loader === "NeoForge" ? " · installing NeoForge…" : "") });
      onCreated && onCreated(r.id);
      onClose();
    } else window.toast({ tone: "danger", icon: "alert", title: "Couldn't create", body: (r && r.error) || "" });
  }

  const field = (label, child) => React.createElement("div", { style: { marginBottom: 14 } },
    React.createElement("div", { style: { fontSize: 12, fontWeight: 600, color: "var(--text-dim)", marginBottom: 6 } }, label), child);
  const inp = { height: 36, padding: "0 11px", borderRadius: "var(--r-md)", background: "var(--panel-2)", border: "1px solid var(--border)", color: "var(--text)", fontSize: 13, width: "100%", outline: "none", fontFamily: "inherit" };

  return React.createElement("div", {
    onMouseDown: e => { if (e.target === e.currentTarget) onClose(); },
    style: { position: "fixed", inset: 0, zIndex: 700, display: "grid", placeItems: "center", background: "rgba(0,0,0,.55)", backdropFilter: "blur(8px)" },
  },
    React.createElement("div", { className: "glass-pop anim-fadein", style: { width: 460, borderRadius: "var(--r-xl)", padding: 26 }, onClick: e => e.stopPropagation() },
      React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 11, marginBottom: 18 } },
        React.createElement("div", { style: { width: 42, height: 42, borderRadius: 12, background: "var(--acc-grad)", display: "grid", placeItems: "center", color: "var(--acc-ink)" } }, React.createElement(Icon, { name: "plus", size: 22 })),
        React.createElement("h3", { style: { margin: 0, fontSize: 18, fontWeight: 720 } }, "New Instance")),

      field("Name", React.createElement("input", { value: name, onChange: e => setName(e.target.value), placeholder: "My Modpack", autoFocus: true, style: inp, className: "no-drag" })),
      React.createElement("div", { style: { display: "flex", gap: 12 } },
        React.createElement("div", { style: { flex: 1 } }, field("Minecraft version", React.createElement("input", { value: mc, onChange: e => setMc(e.target.value), placeholder: "1.21.1", style: inp, className: "no-drag" }))),
        React.createElement("div", { style: { flex: 1 } }, field("Loader", React.createElement(Select, { value: loader, onChange: setLoader, width: "100%",
          options: ["NeoForge", "Vanilla", "Fabric", "Forge", "Quilt"] })))),

      loader === "NeoForge" && field("NeoForge version", React.createElement(Select, { value: lver, onChange: setLver, width: "100%",
        options: [{ value: "", label: lvers.length ? "Latest stable" : "Latest (loading…)" }, ...lvers.slice(0, 25).map(v => ({ value: v, label: v }))] })),
      (loader === "Fabric" || loader === "Forge" || loader === "Quilt") && React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginBottom: 14, lineHeight: 1.5 } },
        "The latest " + loader + " loader for this MC version will be installed automatically — launches without Prism."),

      field("Max RAM — " + (ram / 1024).toFixed(1) + " GB", React.createElement(Slider, { value: Math.min(ram, maxRamMb(sysRam)), min: 1024, max: maxRamMb(sysRam), step: 512, onChange: setRam, format: v => (v / 1024).toFixed(1) + "G" })),

      React.createElement("div", { style: { display: "flex", justifyContent: "flex-end", gap: 10, marginTop: 8 } },
        React.createElement(Btn, { variant: "ghost", onClick: onClose, disabled: busy }, "Cancel"),
        React.createElement(Btn, { variant: "primary", icon: busy ? "refresh" : "check", iconSpin: busy, disabled: busy, onClick: create }, busy ? "Creating…" : "Create")),
    ),
  );
}

function InstanceCard({ instance, kpi, onOpen, onPlay, t, fmt, menu }) {
  const stateTone = instance.state === "ready" ? "success" : instance.state === "hibernated" ? "accent" : instance.state === "loading" ? "warn" : "neutral";
  return React.createElement("div", {
    className: "glass sheen lift anim-fadeup", onClick: onOpen,
    style: { borderRadius: "var(--r-xl)", padding: 14, cursor: "pointer", display: "flex", flexDirection: "column", gap: 12 },
  },
    React.createElement(Banner, { instance }),
    React.createElement("div", { style: { display: "flex", alignItems: "flex-start", gap: 8 } },
      React.createElement("div", { style: { flex: 1, minWidth: 0 } },
        React.createElement("div", { style: { fontSize: 15, fontWeight: 680, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, instance.name),
        React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 2 } },
          (instance.loader || "?") + (instance.mc ? " " + instance.mc : "")),
      ),
      React.createElement(Menu, {
        align: "right", items: menu,
        trigger: React.createElement("button", { className: "no-drag", style: { width: 28, height: 28, borderRadius: 8, border: "1px solid var(--border)", background: "var(--panel-2)", color: "var(--text-dim)", display: "grid", placeItems: "center" } },
          React.createElement(Icon, { name: "dots", size: 16 })),
      }),
    ),
    React.createElement("div", { style: { display: "flex", flexWrap: "wrap", gap: 6 } },
      React.createElement(Badge, { tone: "neutral", icon: "package", size: "sm" }, t("lib.modsCount", { n: instance.mods })),
      React.createElement(Badge, { tone: "neutral", icon: "ram",     size: "sm" }, fmt.ram(instance.ramMax)),
      cacheBadge(instance.cacheState, t),
      instance.state && instance.state !== "stopped" &&
        React.createElement(Badge, { tone: stateTone, size: "sm", dot: instance.state === "ready" || instance.state === "hibernated" },
          { stopped: "", loading: "Loading", ready: "Running", hibernated: "Hibernated", waking: "Waking", crashed: "Crashed" }[instance.state] || instance.state),
    ),
    React.createElement("div", { className: "hr" }),
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 10 } },
      React.createElement("div", { style: { flex: 1, minWidth: 0 } },
        React.createElement("div", { style: { fontSize: 11, color: "var(--text-faint)" } },
          instance.lastPlayed ? t("lib.lastPlayed", { ago: fmt.ago(instance.lastPlayed, "en") }) : t("lib.never")),
        kpi && kpi.avg > 0 && React.createElement("div", { className: "tnum", style: { fontSize: 12.5, fontWeight: 600, marginTop: 1 } },
          "~" + kpi.avg + "s " + t("ov.toMenu")),
      ),
      React.createElement(Btn, { variant: "primary", icon: "play", onClick: e => { e.stopPropagation(); onPlay(); } }, t("common.play")),
    ),
  );
}

function InstanceRow({ instance, kpi, onOpen, onPlay, t, fmt, menu }) {
  return React.createElement("div", {
    className: "glass sheen anim-fadeup", onClick: onOpen,
    style: { borderRadius: "var(--r-lg)", padding: 12, cursor: "pointer", display: "flex", alignItems: "center", gap: 14 },
  },
    React.createElement("div", { style: { width: 52, height: 52, flexShrink: 0 } }, React.createElement(Banner, { instance, h: 52 })),
    React.createElement("div", { style: { flex: 1, minWidth: 0 } },
      React.createElement("div", { style: { fontSize: 14.5, fontWeight: 680, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, instance.name),
      React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 2 } },
        (instance.loader || "?") + (instance.mc ? " " + instance.mc : "") + "  •  " + t("lib.modsCount", { n: instance.mods }) + "  •  " + fmt.ram(instance.ramMax)),
    ),
    cacheBadge(instance.cacheState, t),
    kpi && kpi.avg > 0 && React.createElement("span", { className: "tnum", style: { fontSize: 12.5, color: "var(--text-dim)", fontWeight: 600, minWidth: 70, textAlign: "right" } },
      "~" + kpi.avg + "s"),
    React.createElement(Btn, { variant: "primary", icon: "play", size: "sm", onClick: e => { e.stopPropagation(); onPlay(); } }, t("common.play")),
    React.createElement(Menu, {
      align: "right", items: menu,
      trigger: React.createElement("button", { className: "no-drag", style: { width: 30, height: 30, borderRadius: 8, border: "1px solid var(--border)", background: "var(--panel-2)", color: "var(--text-dim)", display: "grid", placeItems: "center" } },
        React.createElement(Icon, { name: "dots", size: 16 })),
    }),
  );
}

function LibraryScreen() {
  const { api, hasBridge, t, fmt, navigate } = useApp();
  const [state, setState] = lS("loading");
  const [instances, setInstances] = lS([]);
  const [kpis, setKpis] = lS({});
  const [q, setQ] = lS("");
  const [sort, setSort] = lS("recent");
  const [view, setView] = lS("grid");
  const [deleteTarget, setDeleteTarget] = lS(null);  // instance to confirm delete
  const [showCreate, setShowCreate] = lS(false);
  const [roots, setRoots] = lS([]);   // instance locations (for the "Move →" menu)

  async function load() {
    setState("loading");
    try {
      const list = await api.getInstances();
      if (!list.length) { setState("empty"); return; }
      const kp = {};
    // Use allSettled so one bad getKpis call does not kill the whole library
    const settled = await Promise.allSettled(list.map(i => api.getKpis(i.id).then(v => ({ id: i.id, v }))));
    settled.forEach(r => { if (r.status === "fulfilled") kp[r.value.id] = r.value.v; });
      setInstances(list); setKpis(kp); setState("ready");
    } catch (e) {
      console.error("[Library] load failed:", e);
      window.toast && window.toast({ tone: "error", icon: "alert", title: "Failed to load instances", body: String(e && e.message || e) });
      setState("error");
    }
  }
  lE(() => { load(); }, []);

  // Live state updates from bridge
  lE(() => {
    if (!hasBridge) return;
    if (api.getInstanceRoots) api.getInstanceRoots().then(r => setRoots((r && r.roots) || [])).catch(() => {});
    function onStateChanged(e) {
      const ev = e.detail;
      setInstances(prev => prev.map(i =>
        i.id === ev.id ? { ...i, state: ev.state, residentMB: ev.residentMB, loadSeconds: ev.loadSeconds } : i
      ));
    }
    // Reload the list when a modpack is imported / instance duplicated.
    function onImport(e) {
      const d = e.detail || {};
      if (d.ok) load();
    }
    function onDuplicate(e) {
      const d = e.detail || {};
      if (d.ok) { load(); window.toast({ tone: "success", icon: "check", title: "Duplicated", body: d.name || "" }); }
      else if (d.error) window.toast({ tone: "danger", icon: "alert", title: "Duplicate failed", body: d.error });
    }
    function onMove(e) {
      const d = e.detail || {};
      if (d.ok) { load(); window.toast({ tone: "success", icon: "check", title: "Instance moved" }); }
      else if (d.error) window.toast({ tone: "danger", icon: "alert", title: "Move failed", body: d.error });
    }
    window.addEventListener("cryo:instanceStateChanged", onStateChanged);
    window.addEventListener("cryo:importDone", onImport);
    window.addEventListener("cryo:duplicateDone", onDuplicate);
    window.addEventListener("cryo:moveDone", onMove);
    return () => {
      window.removeEventListener("cryo:instanceStateChanged", onStateChanged);
      window.removeEventListener("cryo:importDone", onImport);
      window.removeEventListener("cryo:duplicateDone", onDuplicate);
      window.removeEventListener("cryo:moveDone", onMove);
    };
  }, [hasBridge]);

  const filtered = lM(() => {
    let r = instances.filter(i => i.name.toLowerCase().includes(q.toLowerCase()));
    const k = kpis;
    r.sort((a, b) => {
      if (sort === "name")  return a.name.localeCompare(b.name);
      if (sort === "mods")  return b.mods - a.mods;
      if (sort === "start") return (k[a.id]?.avg || 0) - (k[b.id]?.avg || 0);
      return (b.lastPlayed || 0) - (a.lastPlayed || 0);
    });
    return r;
  }, [instances, q, sort, kpis]);

  async function doDelete(inst) {
    setDeleteTarget(null);
    if (hasBridge && api.removeFromLauncher) {
      await api.removeFromLauncher(inst.id).catch(() => {});
    }
    setInstances(prev => prev.filter(i => i.id !== inst.id));
    window.toast({ tone: "neutral", icon: "trash", title: "Removed", body: inst.name });
    if (!instances.filter(i => i.id !== inst.id).length) setState("empty");
  }

  const menuFor = lCb(inst => {
    const items = [
      { icon: "play",       label: t("common.play"),       onClick: () => navigate("instance", { id: inst.id, autoLaunch: true }) },
      { icon: "edit",       label: t("common.edit"),       onClick: () => navigate("instance", { id: inst.id, tab: "settings" }) },
      { icon: "folderOpen", label: t("common.openFolder"), onClick: () => hasBridge && api.openFolder && api.openFolder(inst.id) },
      { icon: "copy",       label: t("common.duplicate"),  onClick: () => { if (api.duplicateInstance) { window.toast({ tone: "neutral", icon: "copy", title: "Duplicating…", body: inst.name }); api.duplicateInstance(inst.id).catch(() => {}); } } },
    ];
    if (roots.length > 1)
      roots.forEach(r => items.push({
        icon: "folder", label: "Move → " + ((r.path || "").split(/[\\/]/).filter(Boolean).pop() || r.path),
        onClick: () => { window.toast({ tone: "neutral", icon: "folder", title: "Moving…", body: inst.name }); api.moveInstance && api.moveInstance(inst.id, r.path).catch(() => {}); },
      }));
    items.push({ divider: true });
    items.push({ icon: "trash", label: t("common.delete"), danger: true, onClick: () => setDeleteTarget(inst) });
    return items;
  }, [hasBridge, t, roots]);

  return React.createElement("div", { style: { padding: "26px 30px", maxWidth: 1320, margin: "0 auto" } },
    // Delete confirmation overlay
    deleteTarget && React.createElement(DeleteConfirm, {
      instance: deleteTarget,
      onConfirm: () => doDelete(deleteTarget),
      onCancel: () => setDeleteTarget(null),
    }),

    // Create-instance dialog
    showCreate && React.createElement(CreateInstanceDialog, {
      api, hasBridge, onClose: () => setShowCreate(false),
      onCreated: (id) => { load(); if (id) navigate("instance", { id }); },
    }),

    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 14, marginBottom: 22, flexWrap: "wrap" } },
      React.createElement("h1", { style: { margin: 0, fontSize: 24, fontWeight: 720, letterSpacing: "-0.02em" } }, t("lib.title")),
      React.createElement("span", { className: "tnum", style: { fontSize: 13, color: "var(--text-faint)", fontWeight: 600 } },
        state === "ready" ? instances.length : ""),
      React.createElement("div", { style: { flex: 1 } }),
      React.createElement(TextInput, { value: q, onChange: setQ, placeholder: t("common.search"), icon: "search", size: "sm", style: { width: 200 } }),
      React.createElement(Select, { value: sort, onChange: setSort, size: "sm", width: 168, icon: "sort",
        options: [
          { value: "recent", label: t("lib.sort.recent") }, { value: "name", label: t("lib.sort.name") },
          { value: "mods",   label: t("lib.sort.mods") },   { value: "start", label: t("lib.sort.start") },
        ] }),
      React.createElement(Segmented, { size: "sm", value: view, onChange: setView,
        options: [{ value: "grid", icon: "grid", label: "" }, { value: "list", icon: "list", label: "" }] }),
      React.createElement(Tip, { label: "Create a new instance (no Prism needed)" },
        React.createElement(Btn, { variant: "primary", icon: "plus", onClick: () => setShowCreate(true) }, t("lib.new"))),
      hasBridge && React.createElement(Tip, { label: "Import a modpack ZIP / .mrpack" },
        React.createElement(Btn, { variant: "subtle", icon: "upload",
          onClick: () => { if (api.importModpack) api.importModpack(); } },
          "Import")),
      hasBridge && React.createElement(Tip, { label: "Refresh instance list" },
        React.createElement(Btn, { variant: "ghost", size: "icon", onClick: load },
          React.createElement(Icon, { name: "refresh", size: 16 }))),
    ),

    state === "loading" && React.createElement("div", { style: { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(280px, 1fr))", gap: "var(--gap-grid)" } },
      Array.from({ length: 3 }).map((_, i) => React.createElement(Card, { key: i, style: { borderRadius: "var(--r-xl)" } },
        React.createElement(Skeleton, { h: 96, r: "var(--r-lg)" }),
        React.createElement(Skeleton, { h: 18, w: "60%", style: { marginTop: 14 } }),
        React.createElement(Skeleton, { h: 13, w: "40%", style: { marginTop: 8 } }),
        React.createElement("div", { style: { display: "flex", gap: 8, marginTop: 14 } },
          React.createElement(Skeleton, { h: 22, w: 70, r: 99 }),
          React.createElement(Skeleton, { h: 22, w: 60, r: 99 })),
      ))),

    state === "error" && React.createElement(ErrorState, {
      title: t("common.error"), body: t("common.errorBody"), onRetry: load, retryLabel: t("common.retry") }),

    state === "empty" && React.createElement(Card, { style: { borderRadius: "var(--r-2xl)" } },
      React.createElement(EmptyState, {
        icon: "snowflake", title: t("lib.empty.title"), body: t("lib.empty.body"),
        action: React.createElement("div", { style: { display: "flex", gap: 10 } },
          React.createElement(Btn, { variant: "primary", icon: "plus", onClick: () => setShowCreate(true) }, t("lib.empty.cta")),
          React.createElement(Btn, { variant: "subtle", icon: "download", onClick: () => navigate("browse") }, "Browse modpacks"),
        ),
      })),

    state === "ready" && (view === "grid"
      ? React.createElement("div", { style: { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(290px, 1fr))", gap: "var(--gap-grid)" } },
          filtered.map(inst => React.createElement(InstanceCard, {
            key: inst.id, instance: inst, kpi: kpis[inst.id], t, fmt, menu: menuFor(inst),
            onOpen:  () => navigate("instance", { id: inst.id }),
            onPlay:  () => navigate("instance", { id: inst.id, autoLaunch: true }),
          })))
      : React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 10 } },
          filtered.map(inst => React.createElement(InstanceRow, {
            key: inst.id, instance: inst, kpi: kpis[inst.id], t, fmt, menu: menuFor(inst),
            onOpen:  () => navigate("instance", { id: inst.id }),
            onPlay:  () => navigate("instance", { id: inst.id, autoLaunch: true }),
          })))
    ),
  );
}

window.CryoLibrary = { LibraryScreen };
Object.assign(window, { cacheBadge, Banner });

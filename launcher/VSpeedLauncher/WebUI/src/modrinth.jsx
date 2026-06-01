/* ============================================================
   Cryo — Modrinth Mod Browser
   Search mods scoped to the selected instance's MC version + loader,
   pick a version, and install (SHA-512 verified) into mods/.
   ============================================================ */
const { useState: mrS, useEffect: mrE, useRef: mrR, useCallback: mrCb } = React;
const { useApp: useAppMR } = window.CryoStore;

function fmtDownloads(n) {
  if (n == null) return "0";
  if (n >= 1e6) return (n / 1e6).toFixed(1) + "M";
  if (n >= 1e3) return (n / 1e3).toFixed(1) + "k";
  return String(n);
}

// One mod result card with an inline version picker + install.
function ModCard({ hit, instId, api, source, kind, onModpackInstall }) {
  const [versions, setVersions] = mrS(null);   // null = not loaded, [] = loaded empty
  const [loadingV, setLoadingV] = mrS(false);
  const [open, setOpen]         = mrS(false);
  const [installing, setInst]   = mrS("");      // versionId being installed

  async function toggleVersions() {
    if (open) { setOpen(false); return; }
    setOpen(true);
    if (versions != null) return;
    setLoadingV(true);
    const r = await (source === "curseforge"
      ? api.getCurseForgeFiles(hit.projectId, instId)
      : api.getModrinthVersions(hit.projectId, instId)).catch(() => ({ ok: false, versions: [] }));
    setLoadingV(false);
    setVersions((r && r.versions) || []);
  }

  async function install(v) {
    if (kind === "modpack") {
      // Install the whole pack as a new instance (progress shown by the screen).
      onModpackInstall && onModpackInstall(hit, v);
      return;
    }
    if (!instId) { window.toast({ tone: "warn", icon: "info", title: "Pick an instance first" }); return; }
    setInst(v.versionId);
    await api.downloadMod(instId, v.url, v.filename, v.sha512, hit.title).catch(e =>
      window.toast({ tone: "danger", icon: "alert", title: "Download error", body: String(e) }));
    // Done/Error arrive via push events handled by parent; clear local spinner after a beat.
    setTimeout(() => setInst(""), 600);
  }

  const typeColor = { release: "var(--success)", beta: "var(--warn)", alpha: "var(--error)" };

  return React.createElement("div", { className: "glass sheen", style: { borderRadius: "var(--r-xl)", padding: 14, display: "flex", flexDirection: "column", gap: 10 } },
    React.createElement("div", { style: { display: "flex", gap: 12, alignItems: "flex-start" } },
      // Icon
      hit.iconUrl
        ? React.createElement("img", { src: hit.iconUrl, width: 48, height: 48, style: { borderRadius: 11, flexShrink: 0, background: "var(--panel-2)", objectFit: "cover" }, onError: e => { e.target.style.visibility = "hidden"; } })
        : React.createElement("div", { style: { width: 48, height: 48, borderRadius: 11, flexShrink: 0, display: "grid", placeItems: "center", background: "var(--acc-soft)", color: "var(--acc-text)" } }, React.createElement(Icon, { name: "package", size: 22 })),
      React.createElement("div", { style: { flex: 1, minWidth: 0 } },
        React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 8 } },
          React.createElement("span", { style: { fontSize: 14.5, fontWeight: 700, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, hit.title),
          hit.author && React.createElement("span", { style: { fontSize: 11, color: "var(--text-faint)", flexShrink: 0 } }, "by " + hit.author)),
        React.createElement("div", { style: { fontSize: 12, color: "var(--text-dim)", lineHeight: 1.45, marginTop: 3, display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" } }, hit.description),
      ),
    ),
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 12 } },
      React.createElement("span", { style: { display: "flex", alignItems: "center", gap: 4, fontSize: 11.5, color: "var(--text-faint)" } },
        React.createElement(Icon, { name: "download", size: 12 }), fmtDownloads(hit.downloads)),
      React.createElement("span", { style: { display: "flex", alignItems: "center", gap: 4, fontSize: 11.5, color: "var(--text-faint)" } },
        React.createElement(Icon, { name: "heart", size: 12 }), fmtDownloads(hit.follows)),
      React.createElement("div", { style: { flex: 1 } }),
      React.createElement(Btn, { variant: open ? "accentSoft" : "primary", size: "sm", icon: open ? "chevronDown" : "download", onClick: toggleVersions }, open ? "Versions" : "Install"),
    ),

    // Version picker (inline)
    open && React.createElement("div", { style: { borderTop: "1px solid var(--border)", paddingTop: 10, display: "flex", flexDirection: "column", gap: 6, maxHeight: 220, overflowY: "auto" } },
      loadingV
        ? React.createElement("div", { style: { padding: "10px 0", textAlign: "center", color: "var(--text-faint)", fontSize: 12.5 } }, "Loading versions…")
        : (versions && versions.length
            ? versions.slice(0, 30).map(v => React.createElement("div", { key: v.versionId, style: { display: "flex", alignItems: "center", gap: 10, padding: "7px 10px", borderRadius: "var(--r-sm)", background: "var(--panel-2)", opacity: v.disabled ? 0.55 : 1 } },
                React.createElement("span", { style: { width: 7, height: 7, borderRadius: 99, background: typeColor[v.versionType] || "var(--text-faint)", flexShrink: 0 } }),
                React.createElement("div", { style: { flex: 1, minWidth: 0 } },
                  React.createElement("div", { style: { fontSize: 12.5, fontWeight: 600, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" } }, v.versionNumber || v.name),
                  React.createElement("div", { style: { fontSize: 10.5, color: "var(--text-faint)" } }, (v.datePublished ? new Date(v.datePublished).toLocaleDateString() : "") + " · " + fmtDownloads(v.downloads) + " dl" + (v.disabled ? " · download disabled by author" : ""))),
                v.disabled
                  ? React.createElement(Tip, { label: "The author disabled third-party downloads on CurseForge" }, React.createElement(Icon, { name: "alert", size: 14, style: { color: "var(--warn)" } }))
                  : React.createElement(Btn, { variant: "outline", size: "sm", icon: installing === v.versionId ? "refresh" : "download", iconSpin: installing === v.versionId, disabled: !!installing, onClick: () => install(v) }, kind === "modpack" ? "Install" : "Get")))
            : React.createElement("div", { style: { padding: "10px 0", textAlign: "center", color: "var(--text-faint)", fontSize: 12.5 } }, "No compatible versions for this instance.")),
    ),
  );
}

function ModrinthScreen() {
  const { api, hasBridge, t, navigate } = useAppMR();
  const [insts, setInsts]   = mrS([]);
  const [instId, setInstId] = mrS(null);
  const [query, setQuery]   = mrS("");
  const [hits, setHits]     = mrS([]);
  const [total, setTotal]   = mrS(0);
  const [loading, setLoading] = mrS(false);
  const [offset, setOffset] = mrS(0);
  const [source, setSource] = mrS("modrinth");   // "modrinth" | "curseforge"
  const [kind, setKind]     = mrS("mod");        // "mod" | "modpack"
  const [curseKey, setCurseKey] = mrS(false);
  const [packProg, setPackProg] = mrS(null);     // modpack install progress { message, done, total }
  const debounceRef = mrR(null);

  const inst = insts.find(i => i.id === instId);

  mrE(() => {
    if (!hasBridge) return;
    api.getInstances().then(l => { setInsts(l || []); if ((l || []).length) setInstId(x => x || l[0].id); }).catch(() => {});
    api.getConfig().then(c => { if (c) setCurseKey(!!c.curseHasKey); }).catch(() => {});
    // Pre-fill search if navigated here from a "Find on Modrinth" action
    if (window.__cryoModSearch) { setQuery(window.__cryoModSearch); window.__cryoModSearch = null; }
  }, [hasBridge]);

  // Download done/error toasts
  mrE(() => {
    function onDone(e) {
      const d = e.detail || {};
      window.toast({ tone: "success", icon: "check", title: "Installed", body: d.filename + (d.projectTitle ? " (" + d.projectTitle + ")" : "") });
    }
    function onErr(e) {
      const d = e.detail || {};
      window.toast({ tone: "danger", icon: "alert", title: "Install failed", body: d.error || "" });
    }
    window.addEventListener("cryo:modDownloadDone",  onDone);
    window.addEventListener("cryo:modDownloadError", onErr);
    return () => { window.removeEventListener("cryo:modDownloadDone", onDone); window.removeEventListener("cryo:modDownloadError", onErr); };
  }, []);

  const runSearch = mrCb(async (q, off) => {
    if (!hasBridge) return;
    setLoading(true);
    const r = await (source === "curseforge"
      ? api.searchCurseForge(q, instId, off || 0, kind)
      : api.searchModrinth(q, instId, off || 0, kind)).catch(() => ({ ok: false, hits: [] }));
    setLoading(false);
    if (r && r.ok) {
      setHits(prev => (off > 0 ? [...prev, ...r.hits] : r.hits));
      setTotal(r.total || 0);
    } else if (r && r.error) {
      window.toast({ tone: "danger", icon: "alert", title: "Search failed", body: r.error });
      if (off === 0) setHits([]);
    }
  }, [hasBridge, instId, source, kind]);

  // Debounced search on query / instance / source / kind change.
  // Modpack search needs no instance; mod search is scoped to one.
  mrE(() => {
    if (!hasBridge) return;
    if (kind === "mod" && !instId) return;
    if (source === "curseforge" && !curseKey) { setHits([]); return; }
    clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => { setOffset(0); runSearch(query, 0); }, 350);
    return () => clearTimeout(debounceRef.current);
  }, [query, instId, hasBridge, source, curseKey, kind]);

  // Modpack install handler + progress listeners
  function onModpackInstall(hit, version) {
    setPackProg({ message: "Starting…", done: 0, total: 0 });
    const p = source === "curseforge"
      ? api.installCurseForgeModpack(hit.projectId, version.versionId, hit.title)
      : api.installModrinthModpack(hit.projectId, version.versionId, hit.title);
    p.catch(e => { setPackProg(null); window.toast({ tone: "danger", icon: "alert", title: "Install failed", body: String(e) }); });
  }
  mrE(() => {
    function onProg(e) { const d = e.detail || {}; setPackProg({ message: d.message || "Working…", done: d.done || 0, total: d.total || 0 }); }
    function onDone(e) {
      const d = e.detail || {};
      setPackProg(null);
      if (d.ok) {
        window.toast({ tone: "success", icon: "check", title: "Modpack installed", body: d.name + (d.failed ? " · " + d.failed + " file(s) skipped" : "") });
        if (d.id) navigate("instance", { id: d.id });
      } else window.toast({ tone: "danger", icon: "alert", title: "Install failed", body: d.error || "" });
    }
    window.addEventListener("cryo:modpackProgress", onProg);
    window.addEventListener("cryo:modpackDone", onDone);
    return () => { window.removeEventListener("cryo:modpackProgress", onProg); window.removeEventListener("cryo:modpackDone", onDone); };
  }, [navigate]);

  if (!hasBridge)
    return React.createElement("div", { style: { padding: 40, display: "grid", placeItems: "center", height: "100%" } },
      React.createElement(Card, { style: { maxWidth: 440, textAlign: "center" } },
        React.createElement(Icon, { name: "package", size: 30, style: { color: "var(--acc-2)" } }),
        React.createElement("h3", { style: { margin: "12px 0 6px" } }, "Mod Browser runs in the desktop launcher"),
        React.createElement("p", { style: { fontSize: 13, color: "var(--text-dim)" } }, "Open Cryo as the installed app to search and install mods from Modrinth.")));

  return React.createElement("div", { style: { height: "100%", display: "flex", flexDirection: "column", overflow: "hidden" } },
    // Header
    React.createElement("div", { style: { padding: "22px 30px 14px" } },
      React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 12, marginBottom: 14, flexWrap: "wrap" } },
        React.createElement("div", { style: { width: 40, height: 40, borderRadius: 12, display: "grid", placeItems: "center", background: "var(--acc-soft)", border: "1px solid var(--acc-soft-2)", color: "var(--acc-text)" } }, React.createElement(Icon, { name: "package", size: 21 })),
        React.createElement("div", null,
          React.createElement("h2", { style: { margin: 0, fontSize: 18, fontWeight: 720 } }, kind === "modpack" ? "Modpack Browser" : "Mod Browser"),
          React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginTop: 1 } },
            (source === "curseforge" ? "CurseForge" : "Modrinth") + " · " + (kind === "modpack" ? "installs as a new instance" : (inst ? "installs into " + (inst.name || inst.id) : "pick an instance")))),
        React.createElement("div", { style: { marginLeft: "auto", display: "flex", alignItems: "center", gap: 10 } },
          React.createElement(Segmented, { size: "sm", value: kind, onChange: setKind,
            options: [{ value: "mod", label: "Mods" }, { value: "modpack", label: "Modpacks" }] }),
          React.createElement(Segmented, { size: "sm", value: source, onChange: setSource,
            options: [{ value: "modrinth", label: "Modrinth" }, { value: "curseforge", label: "CurseForge" }] }),
          kind === "mod" && insts.length > 0 && React.createElement(Select, { value: instId, width: 180, size: "sm",
            options: insts.map(i => ({ value: i.id, label: i.name || i.id })), onChange: setInstId })),
      ),
      // Compat note + search
      kind === "mod" && inst && React.createElement("div", { style: { fontSize: 11.5, color: "var(--text-faint)", marginBottom: 8 } },
        "Filtering for ", React.createElement("strong", { style: { color: "var(--text-dim)" } }, (inst.loader || "?") + " " + (inst.mc || "")), " compatible mods"),
      React.createElement(TextInput, { value: query, onChange: setQuery, placeholder: kind === "modpack" ? "Search modpacks (e.g. All the Mods, Better MC, Create)…" : "Search mods (e.g. sodium, JEI, create)…", icon: "search", autoFocus: true }),
    ),

    // Modpack install progress overlay
    packProg && React.createElement("div", { style: { position: "fixed", inset: 0, zIndex: 750, display: "grid", placeItems: "center", background: "rgba(0,0,0,.6)", backdropFilter: "blur(8px)" } },
      React.createElement("div", { className: "glass-pop", style: { width: 420, borderRadius: "var(--r-xl)", padding: 28, textAlign: "center" } },
        React.createElement(Icon, { name: "download", size: 30, style: { color: "var(--acc-2)" } }),
        React.createElement("h3", { style: { margin: "12px 0 4px", fontSize: 17, fontWeight: 700 } }, "Installing modpack"),
        React.createElement("div", { style: { fontSize: 12.5, color: "var(--text-dim)", marginBottom: 16, minHeight: 18 } }, packProg.message),
        React.createElement("div", { style: { height: 8, borderRadius: 99, background: "var(--panel-2)", overflow: "hidden" } },
          React.createElement("div", { style: { height: "100%", width: (packProg.total > 0 ? Math.round(packProg.done / packProg.total * 100) : 8) + "%", background: "var(--acc-grad)", transition: "width .3s", animation: packProg.total > 0 ? "none" : "shimmer 1.4s linear infinite", backgroundSize: "200% 100%" } })),
        React.createElement("div", { style: { fontSize: 11, color: "var(--text-faint)", marginTop: 10 } }, "Keep this window open — downloading mods and config…"))),

    // Results
    React.createElement("div", { style: { flex: 1, overflowY: "auto", padding: "4px 30px 24px", minHeight: 0 } },
      source === "curseforge" && !curseKey
        ? React.createElement(EmptyState, { icon: "store", title: "CurseForge API key needed",
            body: "Add a free CurseForge API key in Settings → AI Assistant to browse CurseForge. Modrinth works without a key." })
      : loading && hits.length === 0
        ? React.createElement("div", { style: { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(340px, 1fr))", gap: 14 } },
            Array.from({ length: 6 }).map((_, i) => React.createElement(Card, { key: i, style: { borderRadius: "var(--r-xl)" } },
              React.createElement(Skeleton, { h: 48, w: 48, r: 11 }),
              React.createElement(Skeleton, { h: 14, w: "55%", style: { marginTop: 10 } }),
              React.createElement(Skeleton, { h: 12, w: "85%", style: { marginTop: 8 } }))))
        : hits.length === 0
          ? React.createElement(EmptyState, { icon: kind === "modpack" ? "store" : "package",
              title: query ? "Nothing found" : (kind === "modpack" ? "Search for modpacks" : "Search for mods"),
              body: query ? "Try a different search term." : (kind === "modpack" ? "Find a modpack and install it as a ready-to-play instance." : "Type above to search for mods compatible with this instance.") })
          : React.createElement("div", null,
              React.createElement("div", { style: { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(340px, 1fr))", gap: 14 } },
                hits.map(h => React.createElement(ModCard, { key: source + kind + h.projectId, hit: h, instId, api, source, kind, onModpackInstall }))),
              // Load more
              hits.length < total && React.createElement("div", { style: { display: "flex", justifyContent: "center", marginTop: 20 } },
                React.createElement(Btn, { variant: "outline", icon: loading ? "refresh" : "chevronDown", iconSpin: loading, disabled: loading,
                  onClick: () => { const next = offset + 20; setOffset(next); runSearch(query, next); } }, "Load more (" + hits.length + " / " + total + ")"))),
    ),
  );
}

window.CryoModrinth = { ModrinthScreen };

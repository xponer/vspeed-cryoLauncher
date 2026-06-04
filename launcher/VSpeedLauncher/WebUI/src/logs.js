/* ============================================================
   Cryo вЂ” Logs screen: virtualized tail, filters, regex search,
   VSpeed highlighting, crash banner, stacktrace expand.
   ============================================================ */
const { useState: gS, useEffect: gE, useMemo: gM, useRef: gR, useCallback: gCb } = React;
var { useApp } = window.CryoStore;

const LEVEL_META = {
  DEBUG: { color: "var(--text-faint)", bg: "transparent" },
  INFO: { color: "var(--text-dim)", bg: "transparent" },
  WARN: { color: "var(--warn)", bg: "var(--warn-dim)" },
  ERROR: { color: "var(--error)", bg: "var(--error-dim)" },
};
const ROW_H = 24;

function highlight(text, query, regex) {
  if (!query) return text;
  let re;
  try { re = regex ? new RegExp("(" + query + ")", "gi") : new RegExp("(" + query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + ")", "gi"); }
  catch { return text; }
  const parts = text.split(re);
  return parts.map((p, i) => re.test(p) && i % 2 === 1
    ? React.createElement("mark", { key: i, style: { background: "var(--acc-soft-2)", color: "var(--acc-text)", borderRadius: 3, padding: "0 1px" } }, p)
    : p);
}

function LogsScreen() {
  const { api, hasBridge, t, fmt, settings, navigate } = useApp();
  function askAI(prompt, attachObj) {
    window.__cryoAssistantPreload = { instanceId: instId, attach: attachObj || { logs: true, crash: true }, prompt, autoSend: true };
    navigate("assistant");
  }
  const [instances, setInstances] = gS([]);
  const [instId, setInstId] = gS(null);
  const [state, setState] = gS("loading");
  const [all, setAll] = gS([]);
  const [visibleCount, setVisibleCount] = gS(0); // for live-tail reveal
  const [paused, setPaused] = gS(false);
  const [autoscroll, setAutoscroll] = gS(true);
  const [q, setQ] = gS("");
  const [regex, setRegex] = gS(false);
  const [levels, setLevels] = gS({ DEBUG: true, INFO: true, WARN: true, ERROR: true });
  const [thread, setThread] = gS("all");
  const [expanded, setExpanded] = gS({});
  const [scrollTop, setScrollTop] = gS(0);
  const [viewH, setViewH] = gS(480);
  const scrollRef = gR(null);

  // Fetch the real instance list first; default to the first one.
  gE(() => {
    api.getInstances().then(list => {
      setInstances(list);
      if (list.length) setInstId(prev => prev || list[0].id);
      else setState("empty");
    }).catch(() => setState("error"));
  }, []);

  async function load() {
    if (!instId) return;
    setState("loading");
    setAll([]); setVisibleCount(0); setExpanded({});
    try {
      const logs = await api.getLogs(instId);
      setAll(logs);
      setVisibleCount(logs.length);   // real logs: show all immediately
      setState("ready");
    } catch (e) {
      console.error("[Logs] load failed:", e);
      setState("error");
    }
  }
  gE(() => { if (instId) load(); }, [instId]);

  // live tail: reveal remaining lines progressively unless paused
  gE(() => {
    if (state !== "ready" || paused) return;
    if (visibleCount >= all.length) return;
    const id = setInterval(() => setVisibleCount(c => Math.min(all.length, c + 1)), 850);
    return () => clearInterval(id);
  }, [state, paused, visibleCount, all.length]);

  gE(() => {
    if (!scrollRef.current) return;
    const ro = new ResizeObserver(es => { for (const e of es) setViewH(e.contentRect.height); });
    ro.observe(scrollRef.current);
    return () => ro.disconnect();
  }, [state]);

  const live = gM(() => all.slice(0, visibleCount), [all, visibleCount]);
  const threads = gM(() => ["all", ...Array.from(new Set(all.map(l => l.thread)))], [all]);

  const filtered = gM(() => {
    let re = null;
    if (q && regex) { try { re = new RegExp(q, "i"); } catch { re = null; } }
    return live.filter(l => {
      if (!levels[l.level]) return false;
      if (thread !== "all" && l.thread !== thread) return false;
      if (q) {
        if (re) return re.test(l.msg);
        return l.msg.toLowerCase().includes(q.toLowerCase());
      }
      return true;
    });
  }, [live, levels, thread, q, regex]);

  const errorIndices = gM(() => filtered.reduce((a, l, i) => { if (l.level === "ERROR") a.push(i); return a; }, []), [filtered]);
  const hasCrash = gM(() => all.some(l => l.stack), [all]);

  // autoscroll to bottom
  gE(() => {
    if (autoscroll && !paused && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [filtered.length, autoscroll, paused]);

  function jumpNextError() {
    if (!errorIndices.length || !scrollRef.current) return;
    const cur = scrollTop;
    const next = errorIndices.find(i => i * ROW_H > cur + 10) ?? errorIndices[0];
    setAutoscroll(false);
    scrollRef.current.scrollTop = Math.max(0, next * ROW_H - viewH / 2);
  }

  // virtualization
  const total = filtered.length;
  const startIdx = Math.max(0, Math.floor(scrollTop / ROW_H) - 8);
  const endIdx = Math.min(total, Math.ceil((scrollTop + viewH) / ROW_H) + 8);
  const slice = filtered.slice(startIdx, endIdx);

  const lvlToggle = lv => setLevels(s => ({ ...s, [lv]: !s[lv] }));

  return React.createElement("div", { style: { padding: "20px 30px", maxWidth: 1320, margin: "0 auto", height: "100%", display: "flex", flexDirection: "column" } },
    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: 12, marginBottom: 14, flexWrap: "wrap" } },
      React.createElement("h1", { style: { margin: 0, fontSize: 24, fontWeight: 720, letterSpacing: "-0.02em" } }, t("logs.title")),
      React.createElement(Select, {
        value: instId, onChange: id => { setInstId(id); setQ(""); setThread("all"); },
        size: "sm", width: 220, icon: "gem",
        options: instances.map(i => ({ value: i.id, label: i.name })),
      }),
      React.createElement("div", { style: { flex: 1 } }),
      state === "ready" && React.createElement("span", { className: "tnum", style: { fontSize: 12, color: "var(--text-faint)" } }, t("logs.count", { n: filtered.length })),
    ),

    hasCrash && React.createElement("div", {
      className: "anim-fadeup", style: {
        display: "flex", alignItems: "center", gap: 12, padding: "12px 16px", marginBottom: 14,
        borderRadius: "var(--r-lg)", background: "var(--error-dim)", border: "1px solid color-mix(in oklab, var(--error) 32%, transparent)",
      },
    },
      React.createElement("div", { style: { width: 34, height: 34, borderRadius: 10, display: "grid", placeItems: "center", background: "color-mix(in oklab, var(--error) 20%, transparent)", color: "var(--error)" } },
        React.createElement(Icon, { name: "alert", size: 18 })),
      React.createElement("div", { style: { flex: 1 } },
        React.createElement("div", { style: { fontSize: 13.5, fontWeight: 680, color: "var(--error)" } }, t("logs.crash")),
        React.createElement("div", { style: { fontSize: 12, color: "var(--text-dim)", marginTop: 1 } }, t("logs.crashBody")),
      ),
      React.createElement(Btn, { variant: "danger", icon: "file", onClick: () => hasBridge && api.openCrashReport && api.openCrashReport(instId).then(r => !r.ok && window.toast({ tone: "warn", icon: "alert", title: "No crash report found", body: r.error || "" })) }, t("logs.openCrash")),
      hasBridge && React.createElement(Btn, { variant: "primary", icon: "sparkles", onClick: () => askAI("My game crashed. Read the crash report and recent log, explain the cause, and propose fixes.") }, "Ask AI"),
    ),

    // toolbar
    React.createElement(Card, { pad: false, style: { borderRadius: "var(--r-lg) var(--r-lg) 0 0", borderBottom: "none" } },
      React.createElement("div", { style: { display: "flex", flexDirection: "column", gap: 10, padding: 12 } },
        // Row 1: search + regex (own row, full width — never overlaps)
        React.createElement("div", { style: { display: "flex", alignItems: "stretch", gap: 0 } },
          React.createElement(TextInput, { value: q, onChange: setQ, placeholder: t("logs.searchPh"), icon: "search", size: "sm", style: { flex: 1, minWidth: 0, borderRadius: "var(--r-md) 0 0 var(--r-md)", borderRight: "none" } }),
          React.createElement("button", {
            onClick: () => setRegex(r => !r), className: "no-drag mono",
            style: { padding: "0 12px", flexShrink: 0, borderRadius: "0 var(--r-md) var(--r-md) 0", border: "1px solid var(--border)", fontSize: 12, fontWeight: 700,
              background: regex ? "var(--acc-soft)" : "var(--panel-2)", color: regex ? "var(--acc-text)" : "var(--text-dim)" },
          }, ".*"),
        ),
        // Row 2: levels + thread + spacer + actions (wraps on narrow widths)
        React.createElement("div", { style: { display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" } },
          React.createElement("div", { style: { display: "flex", gap: 4, flexShrink: 0 } },
            ["DEBUG", "INFO", "WARN", "ERROR"].map(lv => React.createElement("button", {
              key: lv, onClick: () => lvlToggle(lv), className: "no-drag",
              style: {
                padding: "0 10px", height: 30, borderRadius: "var(--r-sm)", fontSize: 11, fontWeight: 700, letterSpacing: "0.03em",
                border: "1px solid " + (levels[lv] ? "transparent" : "var(--border)"),
                background: levels[lv] ? (LEVEL_META[lv].bg === "transparent" ? "var(--panel-hi)" : LEVEL_META[lv].bg) : "transparent",
                color: levels[lv] ? LEVEL_META[lv].color : "var(--text-faint)", opacity: levels[lv] ? 1 : 0.5,
              },
            }, lv))),
          React.createElement(Select, { value: thread, onChange: setThread, size: "sm", width: 160, icon: "cpu",
            options: threads.map(th => ({ value: th, label: th === "all" ? t("common.all") + " threads" : th })) }),
          React.createElement(Btn, { variant: "ghost", size: "sm", icon: "alert", onClick: jumpNextError }, t("logs.nextError")),
          hasBridge && React.createElement(Btn, { variant: "ghost", size: "sm", icon: "sparkles", onClick: () => askAI("Diagnose the recent errors and warnings in this log and tell me how to fix them.", { logs: true, crash: true }) }, "Ask AI"),
          React.createElement("div", { style: { flex: 1, minWidth: 8 } }),
          React.createElement(Tip, { label: t("logs.autoscroll") }, React.createElement(Btn, { variant: autoscroll ? "accentSoft" : "ghost", size: "icon", onClick: () => setAutoscroll(a => !a) }, React.createElement(Icon, { name: "chevronDown", size: 15 }))),
          React.createElement(Btn, { variant: paused ? "accentSoft" : "ghost", size: "sm", icon: paused ? "play" : "pause", onClick: () => setPaused(p => !p) }, paused ? t("logs.resume") : t("logs.pause")),
          React.createElement(Tip, { label: "Copy" }, React.createElement(Btn, { variant: "ghost", size: "icon", onClick: () => { if (navigator.clipboard) { navigator.clipboard.writeText(filtered.map(l => l.msg).join("\n")); window.toast({ tone: "neutral", icon: "copy", title: "Copied" }); } } }, React.createElement(Icon, { name: "copy", size: 15 }))),
          React.createElement(Tip, { label: "Export" }, React.createElement(Btn, { variant: "ghost", size: "icon", onClick: async () => { if (hasBridge && api.exportLogs) { const content = filtered.map(l => l.msg).join("\n"); const r = await api.exportLogs(instId, content).catch(e => ({ ok: false, error: e.message })); if (r.ok) window.toast({ tone: "success", icon: "download", title: "Logs exported" }); } else if (navigator.clipboard) { navigator.clipboard.writeText(filtered.map(l => l.msg).join("\n")); } } }, React.createElement(Icon, { name: "download", size: 15 }))),
          React.createElement(Tip, { label: "Clear" }, React.createElement(Btn, { variant: "ghost", size: "icon", onClick: () => { setAll([]); setVisibleCount(0); } }, React.createElement(Icon, { name: "trash", size: 15 }))),
        ),
      ),
    ),

    // log viewport
    React.createElement("div", {
      ref: scrollRef, onScroll: e => setScrollTop(e.target.scrollTop),
      className: "glass mono",
      style: {
        flex: 1, minHeight: 280, overflowY: "auto", borderRadius: "0 0 var(--r-lg) var(--r-lg)", borderTop: "none",
        position: "relative", fontSize: 12, lineHeight: ROW_H + "px",
      },
    },
      state === "loading" && React.createElement("div", { style: { padding: 18, display: "flex", flexDirection: "column", gap: 7 } },
        Array.from({ length: 18 }).map((_, i) => React.createElement(Skeleton, { key: i, h: 13, w: (40 + Math.random() * 55) + "%" }))),
      state === "error" && React.createElement(ErrorState, { title: t("common.error"), body: t("common.errorBody"), onRetry: load, retryLabel: t("common.retry") }),
      state === "ready" && (filtered.length === 0
        ? React.createElement("div", { style: { padding: 50, textAlign: "center", color: "var(--text-faint)", fontSize: 13 } }, all.length === 0 ? "No log file found for this instance yet." : t("logs.empty"))
        : React.createElement("div", { style: { height: total * ROW_H, position: "relative" } },
          React.createElement("div", { style: { position: "absolute", top: startIdx * ROW_H, left: 0, right: 0 } },
            slice.map((l) => React.createElement(LogLine, { key: l.id, line: l, q, regex, expanded: !!expanded[l.id], onToggle: () => setExpanded(s => ({ ...s, [l.id]: !s[l.id] })), fmt, onAsk: hasBridge ? () => askAI("Explain and fix this log line:\n" + l.msg + (l.stack ? "\n" + l.stack.join("\n") : "")) : null })),
          ),
        )),
    ),
  );
}

function LogLine({ line, q, regex, expanded, onToggle, fmt, onAsk }) {
  const meta = LEVEL_META[line.level];
  const isVSpeed = line.msg.includes("[VSpeed-Cache]");
  return React.createElement("div", null,
    React.createElement("div", {
      onClick: line.stack ? onToggle : undefined,
      style: {
        display: "flex", alignItems: "center", gap: 10, padding: "0 16px", height: ROW_H, whiteSpace: "nowrap",
        background: isVSpeed ? "var(--acc-soft)" : meta.bg, cursor: line.stack ? "pointer" : "default",
        borderLeft: isVSpeed ? "2px solid var(--acc-2)" : line.level === "ERROR" ? "2px solid var(--error)" : "2px solid transparent",
      },
    },
      React.createElement("span", { style: { color: "var(--text-faint)", flexShrink: 0, fontSize: 11 } }, fmt.clock(line.t)),
      React.createElement("span", { style: { color: meta.color, fontWeight: 700, width: 46, flexShrink: 0, fontSize: 10.5 } }, line.level),
      React.createElement("span", { style: { color: "var(--text-faint)", width: 116, flexShrink: 0, overflow: "hidden", textOverflow: "ellipsis", fontSize: 11 } }, "[" + line.thread + "]"),
      React.createElement("span", { style: { color: isVSpeed ? "var(--acc-text)" : line.level === "ERROR" ? "var(--error)" : "var(--text)", overflow: "hidden", textOverflow: "ellipsis", flex: 1 } },
        line.stack && React.createElement(Icon, { name: expanded ? "chevronDown" : "chevronRight", size: 12, style: { display: "inline", verticalAlign: "middle", marginRight: 5, color: "var(--text-faint)" } }),
        highlight(line.msg, q, regex)),
      onAsk && line.level === "ERROR" && React.createElement("button", {
        className: "no-drag", onClick: e => { e.stopPropagation(); onAsk(); }, title: "Ask AI to explain & fix",
        style: { flexShrink: 0, display: "flex", alignItems: "center", gap: 4, padding: "2px 8px", borderRadius: 6, border: "1px solid var(--acc-soft-2)", background: "var(--acc-soft)", color: "var(--acc-text)", fontSize: 10.5, fontWeight: 700, cursor: "pointer" } },
        React.createElement(Icon, { name: "sparkles", size: 12 }), "Ask AI"),
    ),
    expanded && line.stack && React.createElement("div", {
      style: { padding: "8px 16px 8px 56px", background: "var(--error-dim)", borderLeft: "2px solid var(--error)", whiteSpace: "pre", lineHeight: "18px", fontSize: 11.5, color: "var(--text-dim)" },
    }, line.stack.join("\n")),
  );
}

window.CryoLogs = { LogsScreen };

import { createHub, readToken } from "./hub.js";
import { SpectrumView } from "./spectrum.js";
import { WebAudioPlayer } from "./audio.js";

const $ = (id) => document.getElementById(id);

/** @type {import('./types.js').RadioRemoteSnapshot | null} */
let state = null;
let applying = false;
let selectedDigit = -1;
let activeAfTab = "";
/** @type {Map<string, import('./types.js').AfPluginRemoteEvent[]>} */
const afByPlugin = new Map();
/** @type {Map<string, { name: string, typeId: string }>} */
const afMeta = new Map();
const AF_PER_PLUGIN = 120;
const audio = new WebAudioPlayer();

const spectrumView = new SpectrumView(
  /** @type {HTMLCanvasElement} */ ($("spectrum")),
  /** @type {HTMLCanvasElement} */ ($("waterfall")),
  (hz) => {
    if (state) state.frequencyHz = hz;
    spectrumView.setLocalTuned(hz);
    renderFreqDigits(hz);
    void hubInvoke("SetFrequency", hz);
  }
);

const afView = new SpectrumView(
  /** @type {HTMLCanvasElement} */ ($("afSpectrum")),
  /** @type {HTMLCanvasElement} */ ($("afWaterfall")),
  () => {}
);

/** @type {signalR.HubConnection | null} */
let hub = null;

function setConn(text, ok) {
  $("connStatus").textContent = text;
  $("connStatus").style.color = ok ? "var(--ok)" : "var(--muted)";
}

function updateAudioStatus() {
  const el = $("audioStatus");
  if (!el) return;
  if (audio.unlocked && audio.ctx?.state === "running") {
    el.textContent = `audio on · ${audio.packets}`;
    el.classList.remove("warn");
  } else {
    el.textContent = "audio · click anywhere";
    el.classList.add("warn");
  }
}

function fmtHz(hz) {
  if (!Number.isFinite(hz)) return "—";
  if (Math.abs(hz) >= 1e6) return (hz / 1e6).toFixed(6) + " MHz";
  if (Math.abs(hz) >= 1e3) return (hz / 1e3).toFixed(3) + " kHz";
  return Math.round(hz) + " Hz";
}

function fmtBw(hz) {
  if (hz >= 1000) return `BW ${(hz / 1000).toFixed(hz % 1000 ? 3 : 0)}k`;
  return `BW ${hz}`;
}

async function hubInvoke(method, ...args) {
  if (!hub || hub.state !== signalR.HubConnectionState.Connected) return;
  try { await hub.invoke(method, ...args); } catch (e) { console.warn(method, e); }
}

function renderFreqDigits(hz) {
  const host = $("freqDigits");
  if (!host) return;
  const ghz = hz >= 1e9;
  const value = ghz ? Math.floor(hz / 10) : Math.floor(hz);
  const digits = String(value).padStart(9, "0").slice(-9);
  const seps = ghz ? new Set([0, 3, 6]) : new Set([2, 5]);
  host.innerHTML = "";
  for (let i = 0; i < 9; i++) {
    const d = document.createElement("span");
    d.className = "d" + (i === selectedDigit ? " sel" : "");
    d.textContent = digits[i];
    d.dataset.digit = String(i);
    d.addEventListener("click", (ev) => {
      ev.stopPropagation();
      selectedDigit = i;
      renderFreqDigits(state?.frequencyHz ?? hz);
    });
    d.addEventListener("wheel", (ev) => {
      ev.preventDefault();
      selectedDigit = i;
      const exp = (ghz ? 9 : 8) - i;
      const step = Math.pow(10, exp) * Math.sign(ev.deltaY || 1) * -1;
      void hubInvoke("NudgeFrequency", step);
    }, { passive: false });
    host.appendChild(d);
    if (seps.has(i)) {
      const dot = document.createElement("span");
      dot.className = "dot";
      dot.textContent = ".";
      host.appendChild(dot);
    }
  }
}

function fillSelect(sel, items, selected, getValue, getLabel) {
  const prev = sel.value;
  sel.innerHTML = "";
  for (const item of items) {
    const opt = document.createElement("option");
    opt.value = getValue(item);
    opt.textContent = getLabel(item);
    sel.appendChild(opt);
  }
  const want = selected || prev;
  if ([...sel.options].some((o) => o.value === want)) sel.value = want;
}

function modeLabel(m) {
  if (m === "FREEDV") return "FDV";
  return m;
}

function renderChips(host, items, isOn, onClick, classOn, labelFn) {
  host.innerHTML = "";
  for (const item of items) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "btn chip-btn" + (isOn(item) ? ` ${classOn}` : "");
    const raw = typeof item === "string" ? item : item.name || item.label || item.id;
    btn.textContent = labelFn ? labelFn(item) : raw;
    btn.addEventListener("click", () => onClick(item));
    host.appendChild(btn);
  }
}

function pluginKey(evt) {
  return evt.pluginId || evt.pluginName || "af";
}

function isGuidLike(value) {
  if (!value) return false;
  const s = String(value).replace(/-/g, "");
  return /^[0-9a-f]{16,}$/i.test(s);
}

function shortPluginTitle(id, name) {
  const raw = (name && !isGuidLike(name) ? name : "")
    || (afMeta.get(id)?.name && !isGuidLike(afMeta.get(id).name) ? afMeta.get(id).name : "")
    || (id && !isGuidLike(id) ? id : "")
    || "AF";
  return String(raw).replace(/^builtin\.(af|iq)\./i, "").toUpperCase();
}

function syncAfMeta(plugins) {
  afMeta.clear();
  for (const p of plugins || []) {
    if (typeof p === "string") {
      if (!isGuidLike(p)) afMeta.set(p, { name: shortPluginTitle(p, p), typeId: p });
      else afMeta.set(p, { name: "AF", typeId: "" });
      continue;
    }
    if (!p?.id) continue;
    const name = p.name && !isGuidLike(p.name)
      ? p.name
      : shortPluginTitle(p.typeId || "", p.name || "");
    afMeta.set(p.id, { name: name || "AF", typeId: p.typeId || "" });
  }
}

function fmtClock(evt) {
  const when = evt.utcTicks
    ? new Date(evt.utcTicks / 10000 - 62135596800000)
    : new Date();
  if (Number.isNaN(when.getTime())) return "--:--:--";
  return when.toLocaleTimeString(undefined, {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
}

function isCaptionPlugin(key) {
  const type = (afMeta.get(key)?.typeId || "").toLowerCase();
  const name = (afMeta.get(key)?.name || "").toLowerCase();
  const id = (key || "").toLowerCase();
  return type.includes("neurocaption") || type.includes("caption") ||
    name.includes("caption") || id.includes("neurocaption");
}

function normalizeCaption(text) {
  return String(text || "").trim().replace(/\s+/g, " ");
}

function isStockHallucination(s) {
  const n = String(s || "").trim().replace(/[.!。！]+$/g, "");
  if (!n) return true;
  const stock = [
    "thanks for watching",
    "thank you for watching",
    "please subscribe",
    "subscribe"
  ];
  const lower = n.toLowerCase();
  if (stock.includes(lower) || lower.startsWith("thanks for watching")) return true;
  if (n.includes("ご視聴ありがとうございました")) return true;
  return false;
}

function isDegenerateLoop(s) {
  const compact = String(s || "").replace(/[\s,./]+/g, "");
  if (compact.length >= 16) {
    const unique = new Set(compact).size;
    if (unique <= 3) return true;
  }
  const words = String(s || "").split(/[\s,./]+/).filter(Boolean);
  if (words.length >= 12) {
    const counts = new Map();
    let top = 0;
    for (const w of words) {
      const k = w.toLowerCase();
      const n = (counts.get(k) || 0) + 1;
      counts.set(k, n);
      if (n > top) top = n;
    }
    if (top >= words.length - 1) return true;
  }
  return false;
}

function sanitizeStt(text) {
  let s = String(text || "").trim();
  if (!s || isStockHallucination(s) || isDegenerateLoop(s)) return "";
  s = s.replace(/([^\s.!?,])\1{3,}/g, "$1$1$1");
  const tokens = s.split(/[\s,./·]+/).filter(Boolean);
  if (tokens.length >= 6) {
    const counts = new Map();
    for (const t of tokens) {
      const k = t.toLowerCase();
      counts.set(k, (counts.get(k) || 0) + 1);
    }
    let topKey = tokens[0];
    let topN = 0;
    for (const [k, n] of counts) {
      if (n > topN) {
        topN = n;
        topKey = tokens.find((t) => t.toLowerCase() === k) || k;
      }
    }
    if (topN >= 8 && topN * 2 >= tokens.length) s = topKey;
  }
  s = s.replace(/\s{2,}/g, " ").replace(/^[\s,/·]+|[\s,/·]+$/g, "");
  if (!s || isStockHallucination(s) || isDegenerateLoop(s)) return "";
  return s;
}

function breakCaptionSentences(text) {
  let out = "";
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    out += c;
    if (c !== "." && c !== "。" && c !== "!" && c !== "?" && c !== "！" && c !== "？") continue;
    const next = i + 1 < text.length ? text[i + 1] : "";
    if (!next || next === "\r" || next === "\n") continue;
    if (/\d/.test(next) || (next >= "a" && next <= "z")) continue;
    if (!/\s/.test(next)) out += " ";
    while (i + 1 < text.length && /\s/.test(text[i + 1])) i++;
    out += "\n";
  }
  return out;
}

function captionEventText(evt) {
  return sanitizeStt(evt.fields?.display || evt.fields?.message || evt.text || "");
}

function lastFreqFor(key) {
  const list = afByPlugin.get(key);
  if (!list?.length) return 0;
  for (let i = list.length - 1; i >= 0; i--) {
    if (list[i].frequencyHz > 0) return list[i].frequencyHz;
  }
  return 0;
}

function orderedPluginKeys() {
  for (const id of afMeta.keys()) {
    if (!afByPlugin.has(id)) afByPlugin.set(id, []);
  }
  const preferred = [...afMeta.keys()];
  const keys = [...new Set([...preferred, ...afByPlugin.keys()])];
  keys.sort((a, b) => {
    const ia = preferred.indexOf(a);
    const ib = preferred.indexOf(b);
    if (ia >= 0 && ib >= 0) return ia - ib;
    if (ia >= 0) return -1;
    if (ib >= 0) return 1;
    return a.localeCompare(b);
  });
  // Drop empty GUID-only stubs with no name and no messages.
  return keys.filter((key) => {
    const list = afByPlugin.get(key) || [];
    if (list.length > 0) return true;
    const name = afMeta.get(key)?.name || "";
    return name && !isGuidLike(name) && name !== "AF";
  });
}

/**
 * Show as many plugin columns as width allows (~1.3× wider than before).
 * Remaining plugins → right-side tabs.
 */
function renderAfPanel() {
  const panes = $("afPanes");
  const overflow = $("afOverflow");
  const tabs = $("afTabs");
  const overflowPane = $("afOverflowPane");
  if (!panes || !overflow || !tabs || !overflowPane) return;

  const allKeys = orderedPluginKeys();
  if (allKeys.length === 0) {
    overflow.hidden = true;
    panes.style.gridTemplateColumns = "1fr";
    panes.innerHTML = `<div class="af-empty muted">No AF plugin activity yet</div>`;
    return;
  }

  const body = panes.parentElement;
  const bodyW = Math.max(280, body?.clientWidth || panes.clientWidth || 600);
  const overflowW = 150;
  const paneMin = 220;
  const captionKeys = allKeys.filter(isCaptionPlugin);
  const otherKeys = allKeys.filter((k) => !isCaptionPlugin(k));
  let primary;
  let rest;
  if (captionKeys.length) {
    const captionMin = Math.max(280, Math.floor(bodyW * 0.5));
    let budget = bodyW - captionMin;
    let otherVisible = Math.min(otherKeys.length, Math.max(0, Math.floor(budget / paneMin)));
    if (otherKeys.length > otherVisible) {
      budget = bodyW - captionMin - overflowW;
      otherVisible = Math.min(otherKeys.length, Math.max(0, Math.floor(budget / paneMin)));
    }
    primary = [...otherKeys.slice(0, otherVisible), ...captionKeys];
    rest = otherKeys.slice(otherVisible);
  } else {
    let visibleCount = Math.min(allKeys.length, Math.max(1, Math.floor(bodyW / paneMin)));
    if (allKeys.length > visibleCount) {
      visibleCount = Math.min(
        allKeys.length - 1,
        Math.max(1, Math.floor((bodyW - overflowW) / paneMin))
      );
    }
    if (allKeys.length >= 3) {
      const withStrip = Math.floor((bodyW - overflowW) / paneMin);
      if (withStrip >= 3) visibleCount = Math.min(allKeys.length, withStrip);
      else if (Math.floor(bodyW / paneMin) >= 3 && allKeys.length === 3)
        visibleCount = 3;
    }
    primary = allKeys.slice(0, visibleCount);
    rest = allKeys.slice(visibleCount);
  }
  const hasOverflow = rest.length > 0;

  overflow.hidden = !hasOverflow;
  if (hasOverflow) {
    if (!activeAfTab || !rest.includes(activeAfTab)) activeAfTab = rest[0];
    tabs.innerHTML = "";
    for (const key of rest) {
      const list = afByPlugin.get(key) || [];
      const sample = list.length ? list[list.length - 1] : null;
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "af-tab" + (key === activeAfTab ? " on" : "");
      const title = shortPluginTitle(key, sample?.pluginName);
      btn.textContent = title;
      btn.title = title;
      btn.addEventListener("click", () => {
        activeAfTab = key;
        renderAfPanel();
      });
      tabs.appendChild(btn);
    }
    fillAfPane(overflowPane, activeAfTab);
  } else {
    activeAfTab = "";
    tabs.innerHTML = "";
    overflowPane.innerHTML = "";
  }

  if (captionKeys.length && primary.length) {
    panes.style.gridTemplateColumns = primary
      .map((k) => isCaptionPlugin(k) ? "minmax(0, 1fr)" : "minmax(160px, 220px)")
      .join(" ");
  } else {
    panes.style.gridTemplateColumns = `repeat(${primary.length}, minmax(0, 1fr))`;
  }
  panes.innerHTML = "";
  for (const key of primary) {
    const pane = document.createElement("div");
    pane.className = "af-pane" + (isCaptionPlugin(key) ? " caption" : "");
    fillAfPane(pane, key);
    panes.appendChild(pane);
  }
}

function fillAfPane(pane, key) {
  const list = afByPlugin.get(key) || [];
  const sample = list.length ? list[list.length - 1] : null;
  const title = shortPluginTitle(key, sample?.pluginName || afMeta.get(key)?.name || key);
  const freqHz = lastFreqFor(key);
  const freqText = freqHz > 0 ? fmtHz(freqHz) : "";

  pane.innerHTML = "";
  const caption = isCaptionPlugin(key);
  const head = document.createElement("div");
  head.className = "af-pane-title";
  head.innerHTML = `<span>${escapeHtml(title)}</span>${freqText ? `<span class="freq">${escapeHtml(freqText)}</span>` : ""}`;
  const body = document.createElement("div");
  body.className = caption ? "af-feed caption-log" : "af-feed";
  if (!list.length) {
    body.innerHTML = `<div class="muted">waiting…</div>`;
  } else if (caption) {
    fillCaptionFeed(body, list);
  } else {
    for (const evt of list) body.appendChild(makeAfLine(evt));
  }
  pane.appendChild(head);
  pane.appendChild(body);
  body.scrollTop = body.scrollHeight;
}

function fillCaptionFeed(body, list) {
  let log = "";
  let last = "";
  for (const evt of list) {
    const kind = (evt.kind || "").toUpperCase();
    if (kind && kind !== "CAPTION") continue;
    const t = captionEventText(evt);
    if (!t) continue;
    const norm = normalizeCaption(t).toLowerCase();
    if (norm === last) continue;
    last = norm;
    if (log && !/\s$/.test(log)) log += " ";
    log += t;
  }
  log = breakCaptionSentences(log);
  const el = document.createElement("div");
  el.className = "af-caption-log";
  if (!log) {
    el.classList.add("muted");
    el.textContent = "waiting…";
  } else {
    el.textContent = log;
  }
  body.appendChild(el);
}

function makeAfLine(evt) {
  const kind = (evt.kind || "").toUpperCase();
  const text = (evt.fields?.message || evt.text || "").trim();
  if (kind === "PERIOD_SEP" || /^[—\-]{5,}$/.test(text)) {
    const sep = document.createElement("div");
    sep.className = "af-sep";
    sep.textContent = "—————————";
    return sep;
  }
  const line = document.createElement("div");
  const isCq = /\bCQ\b/i.test(text);
  line.className = "af-line" + (isCq ? " cq" : "");
  const msg = escapeHtml(text).replace(/\bCQ\b/gi, '<span class="cq-token">CQ</span>');
  line.innerHTML = `<span class="t">${fmtClock(evt)}</span><span class="m">${msg}</span>`;
  line.title = text;
  return line;
}

function isNoiseAfEvent(evt) {
  const kind = (evt.kind || "").toUpperCase();
  if (!kind) return true;
  if (kind === "STATUS" || kind === "AUTO DT" || kind === "AUTO_DT_STATE" || kind === "TIME_ADJUST" || kind === "ERROR")
    return true;
  if (kind === "PERIOD_SEP") return false;
  if (kind.includes("DECODE") || kind === "CW_LINE" || kind.endsWith("_CHAR") || kind === "QSO_RECORD" || kind.endsWith("_LINE")
      || kind === "CAPTION" || kind === "DIGITAL_VOICE")
    return false;
  const text = evt.text || "";
  if (/collect|period\s*\d|UTC\s*·|no decode/i.test(text)) return true;
  return true; // default: hide unknown non-decode chatter
}

function isCwStreamEvent(evt) {
  const kind = (evt.kind || "").toUpperCase();
  return kind === "CW_DECODE" || kind === "CW_LINE" || kind === "SKIMMER_CHAR"
    || kind === "FLCW_CHAR" || kind === "KIWICW_CHAR";
}

function formatCwMessage(fields, text) {
  const hz = fields?.trackedHz || "";
  const wpm = fields?.wpm || "";
  const prefix = hz ? (wpm ? `${hz} Hz · ${wpm} WPM` : `${hz} Hz`) : "";
  const body = text || "";
  return prefix ? `${prefix}  ${body}` : body;
}

/** Merge CW character/line updates into one row per channel (desktop-like). */
function pushCwAf(evt) {
  const key = pluginKey(evt);
  if (evt.pluginName && !isGuidLike(evt.pluginName)) {
    const prev = afMeta.get(key);
    afMeta.set(key, { name: evt.pluginName, typeId: prev?.typeId || "" });
  }
  let list = afByPlugin.get(key);
  if (!list) {
    list = [];
    afByPlugin.set(key, list);
  }
  const channel = String(evt.fields?.channel ?? "?");
  const kind = (evt.kind || "").toUpperCase();
  const piece = evt.fields?.text ?? evt.text ?? "";
  let row = list.find((e) => e._cwChannel === channel);
  if (!row) {
    const message = kind === "CW_LINE"
      ? (evt.fields?.message || evt.text || piece)
      : formatCwMessage(evt.fields, piece);
    row = {
      ...evt,
      _cwChannel: channel,
      kind: "CW_LINE",
      text: message,
      fields: { ...(evt.fields || {}), message, text: piece }
    };
    list.push(row);
  } else if (kind === "CW_LINE") {
    const message = evt.fields?.message || evt.text || piece;
    row.text = message;
    row.fields = { ...row.fields, ...(evt.fields || {}), message };
    row.utcTicks = evt.utcTicks || row.utcTicks;
    row.frequencyHz = evt.frequencyHz || row.frequencyHz;
  } else {
    const prevBody = row.fields?.text || "";
    const nextBody = (prevBody + piece).slice(-240);
    const message = formatCwMessage({ ...row.fields, ...evt.fields }, nextBody);
    row.text = message;
    row.fields = { ...row.fields, ...(evt.fields || {}), text: nextBody, message };
    row.utcTicks = evt.utcTicks || row.utcTicks;
    row.frequencyHz = evt.frequencyHz || row.frequencyHz;
  }
  // Keep active channels near the bottom.
  const idx = list.indexOf(row);
  if (idx >= 0 && idx !== list.length - 1) {
    list.splice(idx, 1);
    list.push(row);
  }
  while (list.length > AF_PER_PLUGIN) list.shift();
  scheduleAfRender();
}

let afRenderQueued = false;
function scheduleAfRender() {
  if (afRenderQueued) return;
  afRenderQueued = true;
  requestAnimationFrame(() => {
    afRenderQueued = false;
    renderAfPanel();
  });
}

function pushAf(evt) {
  if (!evt || isNoiseAfEvent(evt)) return;
  if (isCwStreamEvent(evt)) {
    pushCwAf(evt);
    return;
  }
  const key = pluginKey(evt);
  if (evt.pluginName && !isGuidLike(evt.pluginName)) {
    const prev = afMeta.get(key);
    afMeta.set(key, { name: evt.pluginName, typeId: prev?.typeId || "" });
  }
  if ((evt.kind || "").toUpperCase() === "CAPTION") {
    const prev = afMeta.get(key) || {};
    afMeta.set(key, {
      name: prev.name || evt.pluginName || "NeuroCaption",
      typeId: prev.typeId || "builtin.af.neurocaption"
    });
  }
  let list = afByPlugin.get(key);
  if (!list) {
    list = [];
    afByPlugin.set(key, list);
  }
  // Store decode text only (strip any legacy freq prefix server may have sent).
  const message = (evt.fields?.message || evt.text || "").trim();
  const cleaned = {
    ...evt,
    text: message,
    fields: { ...(evt.fields || {}), message }
  };
  if (isCaptionPlugin(key) || (evt.kind || "").toUpperCase() === "CAPTION") {
    const text = sanitizeStt(message);
    if (!text) return;
    const last = list.length ? captionEventText(list[list.length - 1]) : "";
    if (normalizeCaption(text).toLowerCase() === normalizeCaption(last).toLowerCase()) return;
    cleaned.text = text;
    cleaned.fields = { ...cleaned.fields, message: text, display: text };
  }
  list.push(cleaned);
  while (list.length > AF_PER_PLUGIN) list.shift();
  scheduleAfRender();
}

function resetAf() {
  afByPlugin.clear();
  activeAfTab = "";
  renderAfPanel();
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

/** @param {import('./types.js').RadioRemoteSnapshot} s */
function applyState(s) {
  const hz = spectrumView.preferredFrequency(s.frequencyHz);
  state = { ...s, frequencyHz: hz };
  applying = true;
  try {
    renderFreqDigits(hz);
    $("freqLabel").textContent = fmtHz(hz);
    $("signalLabel").textContent = `${(s.signalDb ?? -140).toFixed(1)} dB · ${s.mode}`;
    $("bwLabel").textContent = fmtBw(s.filterBandwidthHz);
    $("gainVal").textContent = String(s.gainPercent);
    $("gainSlider").value = String(s.gainPercent);
    $("vol1").value = String(s.volume1);
    $("vol1Val").textContent = `${s.volume1}%`;
    $("vol2").value = String(s.volume2);
    $("vol2Val").textContent = `${s.volume2}%`;
    $("sql1").checked = !!s.squelch1Enabled;
    $("sql1Thr").value = String(s.squelch1Threshold);
    $("sql2").checked = !!s.squelch2Enabled;
    $("sql2Thr").value = String(s.squelch2Threshold);
    $("sqlLed1").classList.toggle("on", !!s.squelch1Open);
    $("sqlLed2").classList.toggle("on", !!s.squelch2Open);
    const stereoLed = $("stereoLed");
    if (stereoLed) {
      stereoLed.hidden = s.mode !== "WFM";
      stereoLed.classList.toggle("on", !!s.stereoLed);
    }
    $("out2Block").style.opacity = s.audio2Enabled ? "1" : "0.45";
    $("statusLine").textContent = s.status || "";
    syncAfMeta(s.activeAfPlugins);

    const rx = $("rxBtn");
    rx.textContent = s.running ? "RX STOP" : "RX START";
    rx.classList.toggle("on", !!s.running);

    const rfPct = Math.max(0, Math.min(100, ((s.signalDb + 140) / 140) * 100));
    $("rfMeter").style.width = `${rfPct}%`;
    const afPct = Math.max(8, Math.min(100, ((s.audioLevelDb + 80) / 80) * 100));
    $("afMeter").style.width = `${Number.isFinite(s.audioLevelDb) ? afPct : Math.max(8, s.volume1 * 0.7)}%`;

    fillSelect($("sourceSelect"), s.availableSources || [], s.source, (x) => x, (x) => x);
    fillSelect($("sceneSelect"), s.scenes || [], s.selectedSceneId, (x) => x.id, (x) => x.name);

    const modes = (s.availableModes && s.availableModes.length)
      ? s.availableModes
      : ["AM", "SAM", "DMR", "DSTAR", "C4FM", "NFM", "WFM", "USB", "LSB", "CW", "RAW", "FREEDV"];
    renderChips($("modeGrid"), modes, (m) => m === s.mode, (m) => hubInvoke("SetMode", m), "on-mode", modeLabel);

    renderChips(
      $("bandGrid"),
      s.bands || [],
      () => false,
      (b) => hubInvoke("SelectBand", b.id),
      "on-mode"
    );

    const presets = s.bandwidthPresets || [500, 2700, 4000, 7000, 10000, 12500, 180000];
    renderChips(
      $("bwGrid"),
      presets.map((hz) => ({ id: String(hz), name: hz >= 1000 ? `${hz / 1000}k` : String(hz), hz })),
      (p) => p.hz === s.filterBandwidthHz,
      (p) => hubInvoke("SetBandwidth", p.hz),
      "on-bw"
    );

    const chips = $("channelChips");
    chips.innerHTML = "";
    for (const ch of s.channels || []) {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "channel-chip" + (ch.id === s.selectedChannelId ? " on" : "");
      btn.textContent = ch.label || ch.name || fmtHz(ch.frequencyHz);
      btn.title = `${fmtHz(ch.frequencyHz)} · ${ch.mode}`;
      btn.addEventListener("click", () => hubInvoke("SelectChannel", ch.id));
      chips.appendChild(btn);
    }

    const sub = $("subVfoList");
    sub.innerHTML = "";
    if (!s.subVfos?.length) {
      sub.innerHTML = `<div class="muted">No SUB VFOs in this SCENE</div>`;
    } else {
      for (const v of s.subVfos) {
        const row = document.createElement("div");
        row.className = "sub-item";
        row.innerHTML = `<span>${v.name}</span><span>${fmtHz(v.frequencyHz)}</span><span>${v.mode} · ${v.bandwidthHz}</span>`;
        row.style.cursor = "pointer";
        row.addEventListener("click", () => hubInvoke("SetFrequency", v.frequencyHz));
        sub.appendChild(row);
      }
    }

    renderModeExtend(s);
    const overlay = $("digitalOverlay");
    if (overlay) {
      overlay.hidden = !s.digitalOverlay;
      overlay.textContent = s.digitalOverlay || "";
    }
    const sat = $("satBanner");
    if (sat) {
      sat.hidden = !s.satelliteActive;
      sat.textContent = s.satelliteStatus || "SATELLITE";
    }
    const siteRow = $("siteRow");
    if (siteRow) siteRow.hidden = !s.isRemoteSource;
    if (!applying || true) {
      if ($("agcChk")) $("agcChk").checked = !!s.agcEnabled;
      if ($("nrChk")) $("nrChk").checked = !!s.noiseReduction;
      if ($("nrSlider")) $("nrSlider").value = String(s.noiseReductionStrength ?? 55);
      if ($("notchChk")) $("notchChk").checked = !!s.notchEnabled;
      if ($("afFilterChk")) $("afFilterChk").checked = !!s.afFilterEnabled;
    }
    if (s.isRemoteSource && s.siteUrl) {
      const sel = $("siteSelect");
      if (sel && ![...sel.options].some((o) => o.value === s.siteUrl)) {
        const opt = document.createElement("option");
        opt.value = s.siteUrl;
        opt.textContent = s.siteName || s.siteUrl;
        sel.appendChild(opt);
      }
      if (sel) sel.value = s.siteUrl;
    }

    scheduleAfRender();
  } finally {
    applying = false;
  }
}

function renderModeExtend(s) {
  const host = $("modeExtend");
  if (!host) return;
  const mode = s.mode || "";
  const digital = ["DMR", "DSTAR", "C4FM", "FREEDV"].includes(mode);
  const analog = ["AM", "SAM", "NFM"].includes(mode);
  const wfm = mode === "WFM";
  const cw = mode === "CW";
  if (!digital && !analog && !wfm && !cw) {
    host.hidden = true;
    host.innerHTML = "";
    host.dataset.mode = "";
    return;
  }
  host.hidden = false;
  if (host.dataset.mode === mode && host.contains(document.activeElement)) return;

  if (digital) {
    host.innerHTML = `
      <span>${mode === "FREEDV" ? "FREEDV · CODEC2" : "DIGITAL MODE · PCM"}</span>
      ${mode === "FREEDV" ? `
        <label>MODEM <select id="fdvModem">${["Auto","700D","700E","1600","700C"].map((m) =>
          `<option ${m === (s.freeDvModem || "Auto") ? "selected" : ""}>${m}</option>`).join("")}</select></label>
        <label>SIDE <select id="fdvSide">${["Auto","LSB","USB"].map((m) =>
          `<option ${m === (s.freeDvSideband || "Auto") ? "selected" : ""}>${m}</option>`).join("")}</select></label>
      ` : ""}
      <label>OUT <select id="dvOut"><option value="1" ${s.digitalOutputChannel === 2 ? "" : "selected"}>OUT 1</option>
        <option value="2" ${s.digitalOutputChannel === 2 ? "selected" : ""}>OUT 2</option></select></label>
      <label><input id="dvAgc" type="checkbox" ${s.digitalPcmAgc ? "checked" : ""}/> PCM AGC</label>
      <label>PCM VOL <input id="dvVol" type="range" min="0" max="100" value="${s.digitalFeedVolume ?? 70}"/></label>
      <span class="muted">${escapeHtml((s.digitalStatus || "").split("\n")[0])}</span>`;
    host.dataset.mode = mode;
    host.querySelector("#fdvModem")?.addEventListener("change", (e) =>
      hubInvoke("SetFreedv", e.target.value, $("fdvSide")?.value || s.freeDvSideband || "Auto"));
    host.querySelector("#fdvSide")?.addEventListener("change", (e) =>
      hubInvoke("SetFreedv", $("fdvModem")?.value || "Auto", e.target.value));
    const pushDv = () => hubInvoke("SetDigitalFeed",
      Number($("dvOut")?.value || 1), !!$("dvAgc")?.checked, Number($("dvVol")?.value || 70));
    host.querySelector("#dvOut")?.addEventListener("change", pushDv);
    host.querySelector("#dvAgc")?.addEventListener("change", pushDv);
    host.querySelector("#dvVol")?.addEventListener("input", pushDv);
    return;
  }
  if (analog) {
    host.innerHTML = `
      <label><input id="afcOn" type="checkbox" ${s.afcEnabled ? "checked" : ""}/> AFC</label>
      <label>SPEED <select id="afcSpeed">${["Slow","Med","Fast"].map((n, i) =>
        `<option value="${i}" ${i === (s.afcSpeedIndex ?? 1) ? "selected" : ""}>${n}</option>`).join("")}</select></label>
      <label>RANGE <select id="afcRange">${[300,500,1000,2000,3000,5000].map((hz) =>
        `<option value="${hz}" ${hz === (s.afcRangeHz || 1000) ? "selected" : ""}>${hz >= 1000 ? hz / 1000 + "k" : hz}</option>`).join("")}</select></label>
      <span>${escapeHtml(s.afcStatus || "")}</span>
      <span>${escapeHtml(s.analogTone || "")}</span>`;
    host.dataset.mode = mode;
    const push = () => hubInvoke("SetAfc", !!$("afcOn")?.checked,
      Number($("afcSpeed")?.value || 1), Number($("afcRange")?.value || 1000));
    host.querySelector("#afcOn")?.addEventListener("change", push);
    host.querySelector("#afcSpeed")?.addEventListener("change", push);
    host.querySelector("#afcRange")?.addEventListener("change", push);
    return;
  }
  if (wfm) {
    host.innerHTML = `
      <label><input id="wfmStereo" type="checkbox" ${s.wfmStereo ? "checked" : ""}/> STEREO</label>
      <label><input id="wfmHf" type="checkbox" ${s.wfmHfSoft ? "checked" : ""}/> HF SOFT</label>
      <label>EQ <select id="wfmEq">${(s.wfmEqPresets || []).map((n) =>
        `<option ${n === s.wfmEqPreset ? "selected" : ""}>${escapeHtml(n)}</option>`).join("")}</select></label>`;
    host.dataset.mode = mode;
    const push = () => hubInvoke("SetWfm", !!$("wfmStereo")?.checked, !!$("wfmHf")?.checked, $("wfmEq")?.value || "");
    host.querySelector("#wfmStereo")?.addEventListener("change", push);
    host.querySelector("#wfmHf")?.addEventListener("change", push);
    host.querySelector("#wfmEq")?.addEventListener("change", push);
    return;
  }
  host.innerHTML = `
    <label>CW <select id="cwSide"><option value="0" ${s.cwLowerSide ? "" : "selected"}>CW-U</option>
      <option value="1" ${s.cwLowerSide ? "selected" : ""}>CW-L</option></select></label>
    <label>AF ${[100,200,300,400].map((w) =>
      `<button type="button" class="btn chip-btn ${w === (s.cwAfWidthHz || 200) ? "on-bw" : ""}" data-cw="${w}">${w} Hz</button>`).join("")}</label>`;
  host.dataset.mode = mode;
  host.querySelector("#cwSide")?.addEventListener("change", (e) =>
    hubInvoke("SetCw", e.target.value === "1", s.cwAfWidthHz || 200));
  host.querySelectorAll("[data-cw]").forEach((btn) => {
    btn.addEventListener("click", () => hubInvoke("SetCw", !!s.cwLowerSide, Number(btn.getAttribute("data-cw"))));
  });
}

function wireControls() {
  audio.onStatus = updateAudioStatus;
  audio.armAutoStart();
  updateAudioStatus();

  $("rxBtn").addEventListener("click", () => {
    void audio.unlock();
    hubInvoke("SetRunning", !(state?.running));
  });
  $("webVol").addEventListener("input", () => {
    const v = Number($("webVol").value);
    $("webVolVal").textContent = `${v}%`;
    audio.setVolume(v);
    void audio.unlock();
  });
  $("gainSlider").addEventListener("input", () => {
    if (applying) return;
    $("gainVal").textContent = $("gainSlider").value;
    hubInvoke("SetGain", Number($("gainSlider").value));
  });
  $("vol1").addEventListener("input", () => {
    if (applying) return;
    hubInvoke("SetVolume", 1, Number($("vol1").value));
  });
  $("vol2").addEventListener("input", () => {
    if (applying) return;
    hubInvoke("SetVolume", 2, Number($("vol2").value));
  });
  const pushSql = (ch) => {
    if (applying) return;
    const en = $(`sql${ch}`).checked;
    const thr = Number($(`sql${ch}Thr`).value);
    hubInvoke("SetSquelch", ch, en, thr);
  };
  $("sql1").addEventListener("change", () => pushSql(1));
  $("sql2").addEventListener("change", () => pushSql(2));
  $("sql1Thr").addEventListener("change", () => pushSql(1));
  $("sql2Thr").addEventListener("change", () => pushSql(2));
  $("sourceSelect").addEventListener("change", () => {
    if (applying) return;
    hubInvoke("SetSource", $("sourceSelect").value);
  });
  $("sceneSelect").addEventListener("change", () => {
    if (applying) return;
    hubInvoke("ApplyScene", $("sceneSelect").value);
  });
  document.querySelectorAll("[data-bw-nudge]").forEach((btn) => {
    btn.addEventListener("click", () => {
      if (!state) return;
      const dir = Number(btn.getAttribute("data-bw-nudge"));
      const cur = state.filterBandwidthHz;
      const step = cur < 5000 ? 100 : cur < 50000 ? 500 : 5000;
      hubInvoke("SetBandwidth", Math.max(50, cur + dir * step));
    });
  });
  $("centerTuneBtn").addEventListener("click", () => hubInvoke("CenterViewOnTune"));
  $("fullSpanBtn").addEventListener("click", () => {
    if (!state) return;
    hubInvoke("SetViewBandwidth", Math.max(state.viewBandwidthHz * 2, 2_000_000));
  });
  $("narrowSpanBtn").addEventListener("click", () => {
    if (!state) return;
    hubInvoke("SetViewBandwidth", Math.max(10_000, Math.floor(state.viewBandwidthHz / 2)));
  });

  $("freqDisplay").addEventListener("keydown", (ev) => {
    if (selectedDigit < 0 || !state) return;
    if (ev.key === "ArrowUp" || ev.key === "ArrowDown") {
      ev.preventDefault();
      const ghz = state.frequencyHz >= 1e9;
      const exp = (ghz ? 9 : 8) - selectedDigit;
      const step = Math.pow(10, exp) * (ev.key === "ArrowUp" ? 1 : -1);
      hubInvoke("NudgeFrequency", step);
    }
  });
  $("freqDisplay").tabIndex = 0;

  const pushDsp = () => {
    if (applying) return;
    hubInvoke("SetAfDsp", !!$("agcChk")?.checked, !!$("nrChk")?.checked,
      Number($("nrSlider")?.value || 55), !!$("notchChk")?.checked, !!$("afFilterChk")?.checked);
  };
  $("agcChk")?.addEventListener("change", pushDsp);
  $("nrChk")?.addEventListener("change", pushDsp);
  $("nrSlider")?.addEventListener("input", pushDsp);
  $("notchChk")?.addEventListener("change", pushDsp);
  $("afFilterChk")?.addEventListener("change", pushDsp);

  let siteTimer = 0;
  $("siteSearch")?.addEventListener("input", () => {
    clearTimeout(siteTimer);
    siteTimer = setTimeout(() => refreshSites($("siteSearch").value), 250);
  });
  $("siteSelect")?.addEventListener("change", () => {
    if (applying) return;
    hubInvoke("SetSiteUrl", $("siteSelect").value);
  });
}

async function start() {
  wireControls();
  const token = readToken();
  hub = createHub(token);
  hub.on("state", (s) => applyState(normalize(s)));
  hub.on("spectrum", (f) => spectrumView.draw(normalize(f)));
  hub.on("afSpectrum", (f) => afView.draw(normalize(f)));
  hub.on("audio", (msg) => {
    const n = normalize(msg) || msg;
    const b64 = n.pcm16Base64 || n.pcm16 || msg?.pcm16Base64 || msg?.Pcm16Base64;
    const rate = n.sampleRate || msg?.sampleRate || msg?.SampleRate || 48000;
    audio.playPcm16(b64, rate);
  });
  hub.on("af", (evt) => pushAf(normalize(evt)));
  hub.onreconnecting(() => setConn("reconnecting…", false));
  hub.onreconnected(async () => {
    setConn("online", true);
    await refreshAll();
  });
  hub.onclose(() => setConn("offline", false));

  try {
    await hub.start();
    setConn("online", true);
    await refreshAll();
  } catch (e) {
    setConn("failed · " + (e?.message || e), false);
  }

  let deferred = null;
  window.addEventListener("beforeinstallprompt", (e) => {
    e.preventDefault();
    deferred = e;
    $("installBtn").hidden = false;
  });
  $("installBtn").addEventListener("click", async () => {
    if (!deferred) return;
    deferred.prompt();
    deferred = null;
    $("installBtn").hidden = true;
  });

  if ("serviceWorker" in navigator) {
    try { await navigator.serviceWorker.register("/sw.js"); } catch { /* optional */ }
  }

  let resizeTimer = 0;
  window.addEventListener("resize", () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => renderAfPanel(), 80);
  });
}

async function refreshAll() {
  if (!hub) return;
  const s = normalize(await hub.invoke("GetState"));
  applyState(s);
  const feed = normalize(await hub.invoke("GetAfFeed", 120)) || [];
  resetAf();
  for (const evt of feed) pushAf(evt);
  if (s?.isRemoteSource) await refreshSites("");
}

async function refreshSites(query) {
  if (!hub) return;
  try {
    const rows = normalize(await hub.invoke("GetSites", query || "")) || [];
    const sel = $("siteSelect");
    if (!sel) return;
    const current = state?.siteUrl || sel.value;
    fillSelect(sel, rows, current, (x) => x.url, (x) => x.name || x.url);
  } catch { /* optional */ }
}

function normalize(obj) {
  if (obj == null || typeof obj !== "object") return obj;
  if (Array.isArray(obj)) return obj.map(normalize);
  const out = {};
  for (const [k, v] of Object.entries(obj)) {
    const camel = k.length ? k[0].toLowerCase() + k.slice(1) : k;
    out[camel] = typeof v === "object" ? normalize(v) : v;
  }
  return out;
}

start();

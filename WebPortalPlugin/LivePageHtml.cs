namespace WebPortalPlugin;

/// <summary>Public live page (http://SERVER:HTTP_PORT/live): track map, timing tower with gaps, telemetry of one car.</summary>
internal static class LivePageHtml
{
    public const string Html = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Live-Timing</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Barlow+Condensed:wght@500;600;700&family=Barlow:wght@400;500;600&display=swap" rel="stylesheet">
<style>
:root {
  --bg: #e9edf2; --panel: #f7f9fb; --line: #cdd5df; --text: #18202b; --muted: #5d6b7c;
  --track: #c3ccd7; --track-edge: #9eabba; --pitlane: #d6a62a;
  --player: #e2621b; --ai: #6f8197; --clone: #e2621b; --sel: #18202b;
  --purple: #8e3fe0; --green: #138a4e; --yellow: #b98900; --live: #d22f2f;
  --cond: "Barlow Condensed", "Roboto Condensed", "Arial Narrow", sans-serif;
  --body: "Barlow", system-ui, "Segoe UI", sans-serif;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #161c26; --panel: #1e2632; --line: #2f3a4a; --text: #e6ebf2; --muted: #8b98aa;
    --track: #2c3646; --track-edge: #46546a; --pitlane: #c79a24;
    --player: #ff8a3d; --ai: #8fa2bb; --clone: #ff8a3d; --sel: #ffffff;
    --purple: #c08bff; --green: #46d38c; --yellow: #ffd24a; --live: #ff5a5a;
  }
}
* { box-sizing: border-box; }
html, body { height: 100%; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.4 var(--body); font-variant-numeric: tabular-nums; }
button { font: inherit; color: inherit; }
:focus-visible { outline: 2px solid var(--player); outline-offset: 2px; }

.app { display: grid; height: 100vh; grid-template-columns: minmax(300px, 380px) 1fr; grid-template-rows: auto 1fr auto;
  grid-template-areas: "head head" "tower map" "tele tele"; }
header { grid-area: head; display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px 22px; padding: 12px 18px; border-bottom: 1px solid var(--line); }
header h1 { margin: 0; font: 700 26px/1 var(--cond); letter-spacing: .01em; }
header .track { color: var(--muted); font-size: 15px; }
header .session { margin-left: auto; display: flex; gap: 18px; align-items: baseline; flex-wrap: wrap; }
.sess-name { font: 600 20px/1 var(--cond); }
.clock { font: 700 30px/1 var(--cond); }
.wx { color: var(--muted); }
.conn { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; color: var(--muted); }
.conn i { width: 8px; height: 8px; border-radius: 50%; background: var(--muted); }
.conn.on i { background: var(--live); box-shadow: 0 0 0 0 var(--live); animation: pulse 2s infinite; }
@keyframes pulse { 0% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--live) 60%, transparent); } 70% { box-shadow: 0 0 0 7px transparent; } 100% { box-shadow: 0 0 0 0 transparent; } }
@media (prefers-reduced-motion: reduce) { .conn.on i { animation: none; } }

.tower { grid-area: tower; overflow-y: auto; border-right: 1px solid var(--line); background: var(--panel); }
.tower-head { position: sticky; top: 0; z-index: 1; display: grid; grid-template-columns: 34px 1fr 74px 74px; gap: 0 6px; padding: 8px 12px 6px; background: var(--panel);
  border-bottom: 1px solid var(--line); color: var(--muted); font-size: 13px; }
.tower-head button { background: none; border: 0; padding: 0; text-align: right; cursor: pointer; color: var(--muted); text-decoration: underline dotted; text-underline-offset: 3px; }
.row { display: grid; grid-template-columns: 34px 1fr 74px 74px; gap: 0 6px; align-items: center; width: 100%; padding: 6px 12px 6px 9px; background: none;
  border: 0; border-left: 3px solid transparent; border-bottom: 1px solid color-mix(in srgb, var(--line) 55%, transparent); text-align: left; cursor: pointer; }
.row:hover { background: color-mix(in srgb, var(--line) 30%, transparent); }
.row.sel { border-left-color: var(--sel); background: color-mix(in srgb, var(--line) 45%, transparent); }
.row .p { font: 700 22px/1 var(--cond); text-align: center; }
.row .who { min-width: 0; }
.row .nm { display: block; font: 600 18px/1.1 var(--cond); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.row .sub { display: block; font-size: 12px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.row .num { font: 600 17px/1 var(--cond); text-align: right; }
.row .lap { font: 500 16px/1 var(--cond); text-align: right; }
.kind { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 6px; vertical-align: 1px; background: var(--ai); }
.kind.player { background: var(--player); }
.kind.clone { background: transparent; box-shadow: inset 0 0 0 2px var(--clone); }
.tag { display: inline-block; font: 600 12px/1 var(--body); padding: 2px 5px; border-radius: 4px; margin-left: 4px; vertical-align: 2px; }
.tag.box { background: var(--yellow); color: #1b1b1b; }
.tag.fin { background: var(--text); color: var(--bg); }
.lic { color: var(--muted); font-weight: 500; border: 1px solid var(--line); border-radius: 4px; padding: 0 4px; margin-left: 4px; }
.sb { color: var(--purple); } .pb { color: var(--green); }
.empty { padding: 24px 16px; color: var(--muted); }

.map { grid-area: map; position: relative; min-height: 280px; overflow: hidden; }
.map canvas { position: absolute; inset: 0; width: 100%; height: 100%; touch-action: none; }
.map-tools { position: absolute; right: 12px; top: 12px; display: flex; gap: 6px; }
.map-tools button { background: var(--panel); border: 1px solid var(--line); border-radius: 6px; padding: 6px 10px; cursor: pointer; font-size: 14px; }
.map-tools button[aria-pressed="true"] { border-color: var(--text); font-weight: 600; }
.legend { position: absolute; left: 12px; bottom: 10px; display: flex; gap: 14px; font-size: 13px; color: var(--muted); }

.tele { grid-area: tele; display: grid; grid-template-columns: minmax(260px, 340px) 1fr minmax(220px, 300px); border-top: 1px solid var(--line); background: var(--panel); min-height: 200px; }
.car { padding: 12px 16px; border-right: 1px solid var(--line); }
.car h2 { margin: 0; font: 700 24px/1.1 var(--cond); }
.car .model { color: var(--muted); font-size: 14px; }
.gauges { display: grid; grid-template-columns: auto auto 1fr; gap: 4px 16px; align-items: end; margin-top: 10px; }
.speed { font: 700 52px/0.9 var(--cond); }
.speed small, .gear small { font: 500 14px var(--body); color: var(--muted); margin-left: 3px; }
.gear { font: 700 52px/0.9 var(--cond); }
.pedals { display: grid; gap: 6px; align-self: center; }
.bar { height: 8px; border-radius: 4px; background: color-mix(in srgb, var(--line) 70%, transparent); overflow: hidden; }
.bar b { display: block; height: 100%; width: 0; transition: width .2s linear; }
.bar.thr b { background: var(--green); } .bar.brk b { background: var(--live); } .bar.rpm b { background: var(--muted); }
.facts { display: grid; grid-template-columns: repeat(3, auto); gap: 2px 18px; margin-top: 10px; font-size: 13px; color: var(--muted); }
.facts span { font: 600 18px/1.2 var(--cond); color: var(--text); display: block; }
.trace { position: relative; border-right: 1px solid var(--line); }
.trace canvas { position: absolute; inset: 0; width: 100%; height: 100%; }
.trace .cap { position: absolute; left: 14px; top: 10px; font-size: 13px; color: var(--muted); }
.trace .cap b { display: inline-block; width: 14px; height: 3px; vertical-align: 3px; margin: 0 4px 0 10px; }
.events { padding: 10px 14px; overflow-y: auto; max-height: 240px; }
.events h3 { margin: 0 0 6px; font: 600 16px var(--cond); color: var(--muted); }
.events ol { list-style: none; margin: 0; padding: 0; font-size: 14px; }
.events li { padding: 3px 0; border-bottom: 1px solid color-mix(in srgb, var(--line) 50%, transparent); }
.events time { color: var(--muted); margin-right: 8px; font-family: var(--cond); font-size: 15px; }

@media (max-width: 900px) {
  .app { height: auto; grid-template-columns: 1fr; grid-template-rows: auto 56vh auto auto; grid-template-areas: "head" "map" "tower" "tele"; }
  .tower { border-right: 0; border-top: 1px solid var(--line); max-height: none; }
  .tele { grid-template-columns: 1fr; }
  .car, .trace { border-right: 0; border-bottom: 1px solid var(--line); }
  .trace { height: 170px; }
  header .session { margin-left: 0; }
}
</style>
</head>
<body>
<div class="app">
  <header>
    <h1 id="server">Live-Timing</h1>
    <span class="track" id="track"></span>
    <div class="session">
      <span class="sess-name" id="sess">–</span>
      <span class="clock" id="clock">–</span>
      <span class="wx" id="wx"></span>
      <span class="conn" id="conn"><i></i><span id="connText">verbinde …</span></span>
      <a class="wx" href="stats">Bestzeiten</a>
    </div>
  </header>

  <section class="tower" aria-label="Zeitenturm">
    <div class="tower-head">
      <span>Pos</span><span>Fahrer</span>
      <button id="gapMode" title="Umschalten: Abstand zum Führenden oder zum Vordermann">Abstand</button>
      <span style="text-align:right">Letzte</span>
    </div>
    <div id="rows"><div class="empty">Warte auf Daten vom Server …</div></div>
  </section>

  <section class="map" aria-label="Streckenkarte">
    <canvas id="map"></canvas>
    <div class="map-tools">
      <button id="viewAll" aria-pressed="true">Ganze Strecke</button>
      <button id="viewFollow" aria-pressed="false">Auto folgen</button>
    </div>
    <div class="legend"><span><i class="kind player"></i>Spieler</span><span><i class="kind clone"></i>Klon</span><span><i class="kind"></i>KI</span></div>
  </section>

  <section class="tele" aria-label="Ausgewähltes Auto">
    <div class="car">
      <h2 id="cName">Auto wählen</h2>
      <div class="model" id="cModel">Klick auf einen Fahrer im Turm oder auf der Karte.</div>
      <div class="gauges">
        <div class="speed"><span id="cSpeed">–</span><small>km/h</small></div>
        <div class="gear"><span id="cGear">–</span><small>Gang</small></div>
        <div class="pedals" aria-label="Gas, Bremse, Drehzahl">
          <div class="bar thr" title="Gas"><b id="bThr"></b></div>
          <div class="bar brk" title="Bremse"><b id="bBrk"></b></div>
          <div class="bar rpm" title="Drehzahl"><b id="bRpm"></b></div>
        </div>
      </div>
      <div class="facts">
        <div>Position<span id="cPos">–</span></div>
        <div>Letzte Runde<span id="cLast">–</span></div>
        <div>Beste Runde<span id="cBest">–</span></div>
        <div>Vordermann<span id="cAhead">–</span></div>
        <div>Hintermann<span id="cBehind">–</span></div>
        <div id="cExtraL">Stopps<span id="cExtra">–</span></div>
      </div>
    </div>
    <div class="trace">
      <canvas id="trace"></canvas>
      <div class="cap">Tempo über die Runde<b style="background:var(--player)"></b>diese<b style="background:var(--muted)"></b>letzte</div>
    </div>
    <div class="events">
      <h3>Ereignisse</h3>
      <ol id="events"><li class="empty" style="padding:0;border:0">Noch nichts passiert.</li></ol>
    </div>
  </section>
</div>

<script>
const $ = id => document.getElementById(id);
const cssCache = {};
const css = n => cssCache[n] ??= getComputedStyle(document.documentElement).getPropertyValue(n).trim();
matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => { for (const k in cssCache) delete cssCache[k]; });
let frame = null, track = null, trackKey = "", selected = null, gapMode = "gap", follow = false;
const cars = new Map();      // id -> { x, z, vx, vz, at, dx, dz }
const traces = new Map();    // id -> { lap, cur: [[lf, v]], prev: [[lf, v]] }

// ---------- formatting
const fmtLap = ms => ms == null ? "–" : `${Math.floor(ms / 60000)}:${((ms % 60000) / 1000).toFixed(3).padStart(6, "0")}`;
const fmtGap = g => g == null ? "" : typeof g === "string" ? g : `+${g.toFixed(1)}`;
const fmtClock = s => s == null ? "" : s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor(s % 3600 / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
const esc = s => String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const WX = { Clear: "klar", FewClouds: "leicht bewölkt", ScatteredClouds: "bewölkt", BrokenClouds: "stark bewölkt", OvercastClouds: "bedeckt",
  Fog: "Nebel", Mist: "Dunst", Haze: "diesig", LightDrizzle: "Niesel", Drizzle: "Niesel", HeavyDrizzle: "starker Niesel", LightRain: "leichter Regen",
  Rain: "Regen", HeavyRain: "starker Regen", LightThunderstorm: "Gewitter", Thunderstorm: "Gewitter", HeavyThunderstorm: "schweres Gewitter",
  LightSnow: "leichter Schnee", Snow: "Schnee", HeavySnow: "starker Schnee", Windy: "windig", Hot: "heiß", Cold: "kalt" };
const SESS = { Practice: "Training", Qualifying: "Qualifying", Race: "Rennen", Booking: "Buchung" };
function modelName(m) {
  return m.replace(/^ks_/, "").split("_").map(w => /\d/.test(w) || w.length <= 3 ? w.toUpperCase() : w[0].toUpperCase() + w.slice(1)).join(" ");
}

// ---------- data
function connect() {
  if (!window.EventSource) return poll();
  const es = new EventSource("api/live/stream");
  let fails = 0;
  es.onmessage = e => { fails = 0; onFrame(JSON.parse(e.data)); setConn(true); };
  es.onerror = () => { setConn(false); if (++fails > 4) { es.close(); poll(); } };
}
async function poll() {
  try { const r = await fetch("api/live/state", { cache: "no-store" }); if (r.ok) { onFrame(await r.json()); setConn(true, "live (langsam)"); } }
  catch { setConn(false); }
  setTimeout(poll, 1000);
}
function setConn(on, text) { $("conn").classList.toggle("on", on); $("connText").textContent = on ? (text || "live") : "verbinde …"; }

async function loadTrack() {
  try { const r = await fetch("api/live/track", { cache: "no-store" }); const t = await r.json(); track = t.available ? t : null; fitView(); drawTrackLayer(); }
  catch { track = null; }
}

function onFrame(f) {
  const now = performance.now();
  if (f.track !== trackKey) { trackKey = f.track; loadTrack(); cars.clear(); traces.clear(); }
  frame = f;
  const seen = new Set();
  for (const c of f.cars) {
    seen.add(c.id);
    const old = cars.get(c.id);
    const o = old || { dx: c.x, dz: c.z };
    Object.assign(o, { x: c.x, z: c.z, vx: c.vx, vz: c.vz, at: now });
    if (old && Math.hypot(old.dx - c.x, old.dz - c.z) > 80) { o.dx = c.x; o.dz = c.z; }
    cars.set(c.id, o);
    // speed over the lap
    let tr = traces.get(c.id);
    if (!tr) traces.set(c.id, tr = { lap: c.lap, cur: [], prev: [] });
    const lastLf = tr.cur.length ? tr.cur[tr.cur.length - 1][0] : -1;
    if (c.lf < lastLf - 0.5) { tr.prev = tr.cur; tr.cur = []; }
    if (!c.pit) tr.cur.push([c.lf, c.v]);
    if (tr.cur.length > 4000) tr.cur.shift();
  }
  for (const id of [...cars.keys()]) if (!seen.has(id)) cars.delete(id);
  if (selected == null && f.cars.length) selected = (f.cars.find(c => c.k === "player") || f.cars[0]).id;
  renderHeader(f); renderTower(f); renderCar(f); renderEvents(f); drawTrace();
}

// ---------- header, tower, panel
function renderHeader(f) {
  $("server").textContent = f.server.replace(/^Bots:\d+,Player:\d+ - /, "");
  $("track").textContent = track ? "" : f.track;
  document.title = `Live-Timing – ${$("server").textContent}`;
  const s = f.session;
  $("sess").textContent = SESS[s.type] || s.name;
  if (s.type === "Race" && s.timeLeft == null) $("clock").textContent = s.started ? `Runde ${Math.min(s.leaderLap + 1, s.laps)} von ${s.laps}` : "Startaufstellung";
  else $("clock").textContent = fmtClock(s.timeLeft);
  const w = f.weather;
  const wxName = w.type && w.type !== "None" ? `${WX[w.type] || w.type}, ` : "";
  $("wx").textContent = `${wxName}${w.ambient} °C Luft, ${w.road} °C Asphalt${w.wet > 0.05 ? ", nass" : ""}, ${w.time} Uhr${f.viewers > 1 ? `, ${f.viewers} Zuschauer` : ""}`;
}

function renderTower(f) {
  if (!f.cars.length) { $("rows").innerHTML = `<div class="empty">Gerade niemand auf der Strecke.</div>`; return; }
  const sessionBest = Math.min(...f.cars.map(c => c.best ?? Infinity));
  $("gapMode").textContent = gapMode === "gap" ? "Abstand" : "Intervall";
  $("rows").innerHTML = f.cars.map(c => {
    const g = c.pos === 1 ? (f.session.type === "Race" ? (c.fin ? "Ziel" : "Führung") : fmtLap(c.best)) : fmtGap(gapMode === "gap" ? c.gap : c.int);
    const lastCls = c.last != null && c.last === sessionBest ? "sb" : c.last != null && c.last === c.best ? "pb" : "";
    const tags = (c.pit ? `<span class="tag box">Box</span>` : "") + (c.fin ? `<span class="tag fin">Ziel</span>` : "");
    const sub = `${modelName(c.m)}${c.lic ? ` <span class="lic">${esc(c.lic)}</span>` : ""}`;
    return `<button class="row${c.id === selected ? " sel" : ""}" data-id="${c.id}" aria-pressed="${c.id === selected}">
      <span class="p">${c.pos}</span>
      <span class="who"><span class="nm"><i class="kind ${c.k}"></i>${esc(c.n)}${tags}</span><span class="sub">${sub}</span></span>
      <span class="num">${g}</span>
      <span class="lap ${lastCls}">${fmtLap(c.last)}</span></button>`;
  }).join("");
}
$("rows").addEventListener("click", e => { const b = e.target.closest(".row"); if (b) select(+b.dataset.id); });
$("gapMode").onclick = () => { gapMode = gapMode === "gap" ? "int" : "gap"; if (frame) renderTower(frame); };
function select(id) { selected = id; if (frame) { renderTower(frame); renderCar(frame); drawTrace(); } }

function renderCar(f) {
  const i = f.cars.findIndex(c => c.id === selected);
  const c = f.cars[i];
  if (!c) return;
  $("cName").textContent = c.n + (c.k === "clone" ? " (Klon)" : c.k === "ai" ? " (KI)" : "");
  $("cModel").textContent = modelName(c.m) + (c.lic ? `, Lizenz ${c.lic}` : c.k === "player" ? ", noch ohne Lizenz (unter 20 km)" : "");
  $("cSpeed").textContent = c.v;
  $("cGear").textContent = c.g < 0 ? "R" : c.g === 0 ? "N" : c.g;
  $("bThr").style.width = `${Math.round(c.thr * 100)}%`;
  $("bBrk").style.width = c.brk ? "100%" : "0%";
  $("bRpm").style.width = `${Math.min(100, c.rpm / 90)}%`;
  $("cPos").textContent = `${c.pos} von ${f.cars.length}`;
  $("cLast").textContent = fmtLap(c.last);
  $("cBest").textContent = fmtLap(c.best);
  const ahead = f.cars[i - 1], behind = f.cars[i + 1];
  const rel = (gap, o) => o ? `${fmtGap(gap) ? fmtGap(gap) + " " : ""}${o.n.split(" ").pop()}` : "–";
  $("cAhead").textContent = rel(c.int, ahead);
  $("cBehind").textContent = rel(behind?.int, behind);
  if (c.tyre != null) { $("cExtraL").firstChild.textContent = "Reifen"; $("cExtra").textContent = `${Math.round(c.tyre)} %, ${c.stops ?? 0} Stopp${c.stops === 1 ? "" : "s"}`; }
  else { $("cExtraL").firstChild.textContent = "Runden"; $("cExtra").textContent = c.lap; }
}

function renderEvents(f) {
  if (!f.events.length) return;
  $("events").innerHTML = f.events.map(e => `<li><time>${esc(e.t)}</time>${esc(e.text)}</li>`).join("");
}

// ---------- map
const mapCanvas = $("map"), mctx = mapCanvas.getContext("2d");
let trackLayer = null, view = { cx: 0, cz: 0, scale: 1 }, fitScale = 1, dpr = 1;
function resize() {
  dpr = window.devicePixelRatio || 1;
  for (const cv of [mapCanvas, $("trace")]) { const r = cv.getBoundingClientRect(); cv.width = Math.max(1, r.width * dpr); cv.height = Math.max(1, r.height * dpr); }
  fitView(); drawTrackLayer(); drawTrace();
}
function bounds() {
  let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
  for (const p of track.line) for (let k = 0; k < 6; k += 2) { x0 = Math.min(x0, p[k]); x1 = Math.max(x1, p[k]); z0 = Math.min(z0, p[k + 1]); z1 = Math.max(z1, p[k + 1]); }
  return { x0, x1, z0, z1 };
}
function fitView() {
  if (!track) return;
  const b = bounds(), pad = 30 * dpr;
  fitScale = Math.min((mapCanvas.width - 2 * pad) / (b.x1 - b.x0 || 1), (mapCanvas.height - 2 * pad) / (b.z1 - b.z0 || 1));
  if (!follow) view = { cx: (b.x0 + b.x1) / 2, cz: (b.z0 + b.z1) / 2, scale: fitScale };
}
// AC: X east, Z south on the map (screen y = z)
const sx = x => (x - view.cx) * view.scale + mapCanvas.width / 2;
const sy = z => (z - view.cz) * view.scale + mapCanvas.height / 2;
function drawTrackLayer() {
  if (!track) { trackLayer = null; return; }
  trackLayer = document.createElement("canvas");
  trackLayer.width = mapCanvas.width; trackLayer.height = mapCanvas.height;
  const g = trackLayer.getContext("2d");
  const L = track.line;
  // tarmac between the edges
  g.beginPath();
  L.forEach((p, i) => i ? g.lineTo(sx(p[2]), sy(p[3])) : g.moveTo(sx(p[2]), sy(p[3])));
  for (let i = L.length - 1; i >= 0; i--) g.lineTo(sx(L[i][4]), sy(L[i][5]));
  g.closePath();
  g.fillStyle = css("--track"); g.fill();
  g.lineWidth = Math.max(1, 1.2 * dpr); g.strokeStyle = css("--track-edge");
  for (const k of [2, 4]) { g.beginPath(); L.forEach((p, i) => i ? g.lineTo(sx(p[k]), sy(p[k + 1])) : g.moveTo(sx(p[k]), sy(p[k + 1]))); g.closePath(); g.stroke(); }
  // a fixed minimum width so the Nordschleife stays visible when zoomed out
  g.lineWidth = Math.max(5 * dpr, 8 * view.scale); g.strokeStyle = css("--track"); g.lineJoin = "round";
  g.beginPath(); L.forEach((p, i) => i ? g.lineTo(sx(p[0]), sy(p[1])) : g.moveTo(sx(p[0]), sy(p[1]))); g.closePath(); g.stroke();
  if (track.pit?.length) {
    g.setLineDash([5 * dpr, 4 * dpr]); g.lineWidth = 2 * dpr; g.strokeStyle = css("--pitlane");
    g.beginPath(); track.pit.forEach((p, i) => i ? g.lineTo(sx(p[0]), sy(p[1])) : g.moveTo(sx(p[0]), sy(p[1]))); g.stroke(); g.setLineDash([]);
  }
  g.font = `600 ${12 * dpr}px ${css("--body")}`; g.fillStyle = css("--muted"); g.textAlign = "left";
  for (const m of track.marks) {
    if (m.kind === "start") { g.fillStyle = css("--text"); g.fillRect(sx(m.x) - 3 * dpr, sy(m.z) - 3 * dpr, 6 * dpr, 6 * dpr); g.fillText("Start/Ziel", sx(m.x) + 7 * dpr, sy(m.z) - 6 * dpr); g.fillStyle = css("--muted"); }
    else if (m.kind === "section" && view.scale > fitScale * 1.6) g.fillText(m.name, sx(m.x) + 6 * dpr, sy(m.z));
  }
}
let lastDraw = performance.now();
function drawMap() {
  const now = performance.now(), dt = Math.min(0.1, (now - lastDraw) / 1000); lastDraw = now;
  const k = Math.min(1, dt * 12);
  for (const c of cars.values()) {
    const age = Math.min(0.6, (now - c.at) / 1000);
    const tx = c.x + c.vx * age, tz = c.z + c.vz * age;
    c.dx += (tx - c.dx) * k; c.dz += (tz - c.dz) * k;
  }
  if (follow && cars.has(selected)) {
    const c = cars.get(selected), target = fitScale * 6;
    const moved = Math.abs(view.cx - c.dx) + Math.abs(view.cz - c.dz) > 0.5 / view.scale || Math.abs(view.scale - target) > 0.01;
    view.cx = c.dx; view.cz = c.dz;
    if (Math.abs(view.scale - target) > 0.01) view.scale += (target - view.scale) * k;
    if (moved) drawTrackLayer();
  }
  mctx.clearRect(0, 0, mapCanvas.width, mapCanvas.height);
  if (trackLayer) mctx.drawImage(trackLayer, 0, 0);
  if (frame) {
    const r = 9 * dpr;
    const order = [...frame.cars].sort((a, b) => (a.id === selected) - (b.id === selected) || b.pos - a.pos);
    for (const c of order) {
      const p = cars.get(c.id); if (!p) continue;
      const x = sx(p.dx), y = sy(p.dz);
      mctx.beginPath(); mctx.arc(x, y, r, 0, Math.PI * 2);
      if (c.k === "clone") { mctx.fillStyle = css("--panel"); mctx.fill(); mctx.lineWidth = 3 * dpr; mctx.strokeStyle = css("--clone"); mctx.stroke(); }
      else { mctx.fillStyle = c.k === "player" ? css("--player") : css("--ai"); mctx.fill(); }
      if (c.id === selected) { mctx.beginPath(); mctx.arc(x, y, r + 4 * dpr, 0, Math.PI * 2); mctx.lineWidth = 2 * dpr; mctx.strokeStyle = css("--sel"); mctx.stroke(); }
      mctx.fillStyle = c.k === "clone" ? css("--text") : "#fff";
      mctx.font = `700 ${12 * dpr}px ${css("--cond")}`; mctx.textAlign = "center"; mctx.textBaseline = "middle";
      mctx.fillText(c.pos, x, y + 0.5 * dpr);
      if (c.id === selected || (c.k !== "ai" && view.scale > fitScale * 0.9)) {
        mctx.font = `600 ${13 * dpr}px ${css("--body")}`; mctx.textAlign = "left"; mctx.fillStyle = css("--text");
        mctx.fillText(c.n, x + r + 5 * dpr, y);
      }
    }
  }
  requestAnimationFrame(drawMap);
}
mapCanvas.addEventListener("click", e => {
  if (!frame) return;
  const rect = mapCanvas.getBoundingClientRect(), mx = (e.clientX - rect.left) * dpr, my = (e.clientY - rect.top) * dpr;
  let best = null, bd = 20 * dpr;
  for (const c of frame.cars) { const p = cars.get(c.id); if (!p) continue; const d = Math.hypot(sx(p.dx) - mx, sy(p.dz) - my); if (d < bd) { bd = d; best = c.id; } }
  if (best != null) select(best);
});
function setFollow(on) { follow = on; $("viewAll").setAttribute("aria-pressed", !on); $("viewFollow").setAttribute("aria-pressed", on); if (!on) { fitView(); drawTrackLayer(); } }
$("viewAll").onclick = () => setFollow(false);
$("viewFollow").onclick = () => setFollow(true);

// ---------- speed trace
function drawTrace() {
  const cv = $("trace"), g = cv.getContext("2d"), W = cv.width, H = cv.height;
  g.clearRect(0, 0, W, H);
  const tr = traces.get(selected); if (!tr) return;
  const pts = [...tr.prev, ...tr.cur]; if (pts.length < 2) return;
  const vmax = Math.max(100, ...pts.map(p => p[1])) * 1.08;
  const padL = 40 * dpr, padB = 18 * dpr, padT = 30 * dpr, padR = 12 * dpr;
  const X = lf => padL + lf * (W - padL - padR), Y = v => H - padB - v / vmax * (H - padB - padT);
  g.font = `500 ${11 * dpr}px ${css("--body")}`; g.fillStyle = css("--muted"); g.strokeStyle = css("--line"); g.lineWidth = 1;
  for (let v = 50; v < vmax; v += 50) { g.beginPath(); g.moveTo(padL, Y(v)); g.lineTo(W - padR, Y(v)); g.stroke(); g.textAlign = "right"; g.fillText(v, padL - 6 * dpr, Y(v) + 4 * dpr); }
  const line = (arr, color, w) => { if (arr.length < 2) return; g.beginPath(); g.strokeStyle = color; g.lineWidth = w;
    arr.forEach((p, i) => { const x = X(p[0]), y = Y(p[1]); if (i && p[0] < arr[i - 1][0]) g.moveTo(x, y); else i ? g.lineTo(x, y) : g.moveTo(x, y); }); g.stroke(); };
  line(tr.prev, css("--muted"), 1.5 * dpr);
  line(tr.cur, css("--player"), 2.2 * dpr);
}

window.addEventListener("resize", resize);
resize(); connect(); requestAnimationFrame(drawMap);
</script>
</body>
</html>
""";
}

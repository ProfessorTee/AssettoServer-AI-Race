namespace RaceAiPlugin;

/// <summary>Public statistics page (http://SERVER:HTTP_PORT/raceai/stats): best laps of the week / all time, safety ratings, profiles.</summary>
internal static class StatsPageHtml
{
    public const string Html = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bestzeiten &amp; Fahrer</title>
<style>
:root { --bg: #f6f7f9; --panel: #fff; --line: #dde2e8; --text: #1b2027; --muted: #5f6b78; --accent: #1f6fd1; --gold: #b8860b;
        --a: #1f8f4e; --b: #1f6fd1; --c: #b8860b; --d: #c8621d; --r: #c0392b; }
@media (prefers-color-scheme: dark) { :root { --bg: #0f1216; --panel: #161b21; --line: #2a333d; --text: #e6e9ed; --muted: #8b96a3; --accent: #4aa3ff; --gold: #e0b44a;
        --a: #3fcf7f; --b: #4aa3ff; --c: #e0b44a; --d: #f08a3e; --r: #f06b5d; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.45 system-ui, -apple-system, "Segoe UI", Ubuntu, sans-serif; }
main { max-width: 980px; margin: 0 auto; padding: 24px 16px 40px; }
h1 { font-size: 22px; margin: 0 0 2px; }
h2 { font-size: 15px; margin: 0 0 10px; }
.muted { color: var(--muted); }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 12px; padding: 16px; margin-top: 14px; overflow-x: auto; }
.tabs { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 14px; }
.tabs button { background: var(--panel); color: var(--text); border: 1px solid var(--line); border-radius: 999px; padding: 6px 12px; font: inherit; cursor: pointer; }
.tabs button.on { background: var(--accent); border-color: var(--accent); color: #fff; }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 14px; }
table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); white-space: nowrap; }
th { font-size: 12px; color: var(--muted); font-weight: 600; text-transform: uppercase; letter-spacing: .03em; }
td.num, th.num { text-align: right; }
tr.p1 td:first-child, tr.p1 td:nth-child(2) { color: var(--gold); font-weight: 700; }
.sr { display: inline-block; min-width: 26px; text-align: center; border-radius: 6px; padding: 1px 6px; color: #fff; font-weight: 700; }
.sr.A { background: var(--a); } .sr.B { background: var(--b); } .sr.C { background: var(--c); } .sr.D { background: var(--d); } .sr.R { background: var(--r); } .sr.none { background: var(--muted); }
tr.click { cursor: pointer; } tr.click:hover td { background: var(--bg); }
.profile { display: none; } .profile.on { display: table-row; }
.profile td { white-space: normal; color: var(--muted); font-size: 14px; }
</style>
</head>
<body>
<main>
  <h1>Bestzeiten &amp; Fahrer</h1>
  <div class="muted" id="sub">lädt …</div>
  <p><a href="live">Gerade auf der Strecke: Live-Timing öffnen</a></p>
  <div class="tabs" id="tabs"></div>
  <div class="grid">
    <div class="card"><h2 id="weekTitle">Diese Woche</h2><table id="week"></table></div>
    <div class="card"><h2>Allzeit</h2><table id="all"></table></div>
  </div>
  <div class="card">
    <h2>Fahrer &amp; Safety Rating</h2>
    <div class="muted" style="margin-bottom:8px">Safety Rating 0–5 aus Kontakten, Streckenrand-Berührungen und Cuts je gefahrener Strecke (neuere Kilometer zählen mehr).
      A ab 4,0 · B ab 3,0 · C ab 2,0 · D ab 1,0 · R darunter. Bewertet ab 20 km. Zeile anklicken für das Profil.</div>
    <table id="players"></table>
  </div>
</main>
<script>
"use strict";
const $ = id => document.getElementById(id);
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const day = d => new Date(d).toLocaleDateString("de-DE");
const short = d => new Date(d).toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit" });
let data = null, track = null;
function times(list) {
  if (!list.length) return "<tr><td class='muted'>Noch keine Zeiten.</td></tr>";
  return "<tr><th>#</th><th>Zeit</th><th>Fahrer</th><th>Auto</th><th class='num'>Datum</th></tr>" +
    list.map((e, i) => `<tr class="${i === 0 ? "p1" : ""}"><td>${i + 1}</td><td><b>${e.time}</b></td><td>${esc(e.name)}</td><td class="muted">${esc(e.car)}</td><td class="num">${short(e.date)}</td></tr>`).join("");
}
function render() {
  const t = data.tracks.find(x => x.track === track) || { week: [], all: [] };
  $("week").innerHTML = times(t.week);
  $("all").innerHTML = times(t.all);
  $("tabs").innerHTML = data.tracks.map(x => `<button class="${x.track === track ? "on" : ""}" data-t="${esc(x.track)}">${esc(x.track)}</button>`).join("");
  for (const b of $("tabs").querySelectorAll("button")) b.onclick = () => { track = b.dataset.t; render(); };
  $("players").innerHTML = "<tr><th>Fahrer</th><th>SR</th><th class='num'>km</th><th class='num'>Runden</th><th class='num'>Kontakte</th><th class='num'>Rand</th><th class='num'>Cuts</th><th class='num'>Rennen</th><th class='num'>Siege</th><th class='num'>Podien</th></tr>" +
    data.players.map((p, i) => {
      const cls = p.srClass === "-" ? "none" : p.srClass;
      const sr = p.sr == null ? "–" : p.sr.toFixed(2).replace(".", ",");
      const bests = p.bests.map(b => `${esc(b.track)} · ${esc(b.car)}: <b>${b.time}</b> (${b.laps} Runden)`).join("<br>") || "noch keine Bestzeit";
      return `<tr class="click" data-i="${i}"><td>${esc(p.name)}</td><td><span class="sr ${cls}">${p.srClass}</span> ${sr}</td><td class="num">${String(p.km).replace(".", ",")}</td>` +
        `<td class="num">${p.laps} (${p.cleanLaps})</td><td class="num">${p.carContacts}</td><td class="num">${p.wallContacts}</td><td class="num">${p.cuts}</td>` +
        `<td class="num">${p.races} (${p.finishes})</td><td class="num">${p.wins}</td><td class="num">${p.podiums}</td></tr>` +
        `<tr class="profile" id="pr${i}"><td colspan="10">${bests}<br>Zuletzt da: ${day(p.lastSeen)}</td></tr>`;
    }).join("");
  for (const r of $("players").querySelectorAll("tr.click")) r.onclick = () => $("pr" + r.dataset.i).classList.toggle("on");
}
async function load() {
  try {
    data = await (await fetch("/api/tools/stats")).json();
    if (!track) track = data.current;
    $("sub").textContent = `Strecke jetzt: ${data.current} · Woche ${data.week}`;
    $("weekTitle").textContent = `Diese Woche (${data.week})`;
    render();
  } catch (e) { $("sub").textContent = "Statistik nicht erreichbar."; }
}
load(); setInterval(load, 60000);
</script>
</body>
</html>
""";
}

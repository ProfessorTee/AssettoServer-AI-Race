namespace RaceAiPlugin;

/// <summary>Public join page (http://SERVER:HTTP_PORT/raceai/join): links and addresses for friends.</summary>
internal static class JoinPageHtml
{
    public const string Html = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Server beitreten</title>
<style>
:root { --bg: #f6f7f9; --panel: #fff; --line: #dde2e8; --text: #1b2027; --muted: #5f6b78; --accent: #1f6fd1; }
@media (prefers-color-scheme: dark) { :root { --bg: #0f1216; --panel: #161b21; --line: #2a333d; --text: #e6e9ed; --muted: #8b96a3; --accent: #4aa3ff; } }
* { box-sizing: border-box; }
[hidden] { display: none !important; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", Ubuntu, sans-serif; }
main { max-width: 560px; margin: 0 auto; padding: 24px 16px 40px; }
h1 { font-size: 22px; margin: 0 0 4px; }
.muted { color: var(--muted); }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 12px; padding: 16px; margin-top: 14px; }
.card h2 { font-size: 15px; margin: 0 0 8px; }
a.btn { display: block; text-align: center; background: var(--accent); color: #fff; text-decoration: none; border-radius: 8px; padding: 12px; font-weight: 600; margin: 8px 0; }
a.btn.alt { background: transparent; color: var(--accent); border: 1px solid var(--accent); }
.row { display: flex; gap: 8px; align-items: center; margin: 6px 0; }
code { flex: 1; background: var(--bg); border: 1px solid var(--line); border-radius: 6px; padding: 6px 8px; font-size: 14px; overflow-wrap: anywhere; }
button { background: var(--bg); color: var(--text); border: 1px solid var(--line); border-radius: 6px; padding: 6px 10px; font: inherit; cursor: pointer; }
ol { padding-left: 20px; margin: 6px 0 0; }
</style>
</head>
<body>
<main>
  <h1 id="name">Server</h1>
  <div class="muted" id="sub">lädt …</div>

  <div class="card">
    <h2>Mit Content Manager (empfohlen, CSP nötig)</h2>
    <a class="btn" id="cm" href="#">In Content Manager beitreten</a>
    <a class="btn alt" id="cmLan" href="#" hidden>Im selben WLAN / Netzwerk beitreten</a>
    <div class="muted">Öffnet Content Manager und verbindet direkt. Content Manager und Custom Shaders Patch müssen installiert sein.</div>
  </div>

  <div class="card">
    <h2>Nur zuschauen</h2>
    <a class="btn alt" href="live">Live-Timing mit Streckenkarte öffnen</a>
    <div class="muted">Positionen, Abstände und Tempo aller Autos im Browser, ohne Spiel.</div>
  </div>

  <div class="card">
    <h2>Adresse</h2>
    <div class="row"><code id="addr">–</code><button data-copy="addr">Kopieren</button></div>
    <div class="row" id="lanRow" hidden><code id="lan">–</code><button data-copy="lan">Kopieren</button></div>
    <div class="muted" id="pw"></div>
  </div>

  <div class="card">
    <h2>Ohne Content Manager (Original-Launcher)</h2>
    <a class="btn alt" id="steam" href="steam://run/244210">Assetto Corsa über Steam starten</a>
    <ol class="muted">
      <li>Im Launcher auf <b>Drive → Online</b>.</li>
      <li>Den Server über die Suche finden oder in den Favoriten mit der Adresse oben hinzufügen.</li>
      <li>Ein freies Auto wählen und beitreten.</li>
    </ol>
    <div class="muted" style="margin-top:6px">Der Original-Launcher kann einen Server nicht per Link öffnen, daher dieser Weg.</div>
  </div>
</main>
<script>
"use strict";
const $ = id => document.getElementById(id);
fetch("/raceai/api/join").then(r => r.json()).then(j => {
  $("name").textContent = j.name;
  $("sub").textContent = j.track;
  document.title = j.name + " – beitreten";
  if (j.cmLink) $("cm").href = j.cmLink; else $("cm").hidden = true;
  if (j.cmLinkLan) { $("cmLan").href = j.cmLinkLan; $("cmLan").hidden = false; }
  $("addr").textContent = j.publicHost ? `${j.publicHost}:${j.httpPort}` : "(öffentliche Adresse unbekannt)";
  if (j.lanIp) { $("lan").textContent = `${j.lanIp}:${j.httpPort}  (im selben Netzwerk)`; $("lanRow").hidden = false; }
  $("pw").textContent = j.password ? "Der Server hat ein Passwort – frag den Admin danach." : "";
}).catch(() => { $("sub").textContent = "Server nicht erreichbar."; });
document.querySelectorAll("[data-copy]").forEach(b => b.onclick = async () => {
  const t = $(b.dataset.copy).textContent.split(" ")[0];
  try { await navigator.clipboard.writeText(t); b.textContent = "Kopiert"; } catch (e) { b.textContent = t; }
  setTimeout(() => b.textContent = "Kopieren", 1500);
});
</script>
</body>
</html>
""";
}

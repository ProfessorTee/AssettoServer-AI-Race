using System.Net;
using System.Text;
using System.Text.Json;
using AssettoServer.Commands;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Weather;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace RaceAiPlugin;

/// <summary>
/// Dashboard for the desktop GUI: http://127.0.0.1:HTTP_PORT/raceai
/// Only from this computer unless DashboardRemoteAccess is on (then the admin password is needed).
/// </summary>
[ApiController]
public class RaceAiDashboardController : ControllerBase
{
    private readonly RaceAiService _service;
    private readonly SessionManager _sessionManager;
    private readonly WeatherManager _weatherManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly ACServerConfiguration _serverConfig;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ChatService _chatService;
    private readonly JoinInfo _joinInfo;

    public RaceAiDashboardController(RaceAiService service, SessionManager sessionManager, WeatherManager weatherManager,
        EntryCarManager entryCarManager, ACServerConfiguration serverConfig, IHostApplicationLifetime lifetime, ChatService chatService, JoinInfo joinInfo)
    {
        _joinInfo = joinInfo;
        _chatService = chatService;
        _service = service;
        _sessionManager = sessionManager;
        _weatherManager = weatherManager;
        _entryCarManager = entryCarManager;
        _serverConfig = serverConfig;
        _lifetime = lifetime;
    }

    private bool IsLocal
    {
        get
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            return ip == null || IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));
        }
    }

    private bool Allowed()
    {
        if (IsLocal) return true;
        if (!_service.DashboardRemoteAccess || string.IsNullOrEmpty(_service.AdminPassword)) return false;
        return Request.Headers.TryGetValue("X-Admin-Password", out var pw) && pw.ToString() == _service.AdminPassword;
    }

    private IActionResult Denied() => StatusCode(403, new { error = "Dashboard: only from this computer (DashboardRemoteAccess) or with the admin password" });

    [HttpGet("/raceai")]
    public IActionResult Page()
    {
        if (!IsLocal && !_service.DashboardRemoteAccess) return Denied();
        return Content(DashboardPage.Html, "text/html; charset=utf-8");
    }

    /// <summary>Join links (public, no password: the same data a server list shows).</summary>
    [HttpGet("/raceai/api/join")]
    public async Task<IActionResult> Join() => Ok(await _joinInfo.GetAsync());

    /// <summary>Public page for friends: Content Manager link, IP and ports.</summary>
    [HttpGet("/raceai/join")]
    public IActionResult JoinPage() => Content(JoinPageHtml.Html, "text/html; charset=utf-8");

    [HttpGet("/raceai/api/ping")]
    public IActionResult Ping() => Ok(new { ok = true, server = _service.ServerName, local = IsLocal, supervised = Supervised });

    /// <summary>Started by race-ai/server-supervisor.sh, which starts the server again after a restart request.</summary>
    private static bool Supervised => Environment.GetEnvironmentVariable("RACEAI_SUPERVISED") == "1";

    [HttpGet("/raceai/api/state")]
    public IActionResult State() => Allowed() ? Ok(_service.State()) : Denied();

    [HttpGet("/raceai/api/clones")]
    public IActionResult Clones() => Allowed() ? Ok(_service.CloneList()) : Denied();

    [HttpGet("/raceai/api/track")]
    public IActionResult Track() => Allowed() ? Ok(_service.TrackOutline()) : Denied();

    [HttpGet("/raceai/api/log")]
    public IActionResult Log([FromQuery] int lines = 200)
    {
        if (!Allowed()) return Denied();
        try
        {
            var file = Directory.Exists("logs") ? new DirectoryInfo("logs").GetFiles("log-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
            if (file == null) return Ok(new { lines = Array.Empty<string>() });
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            int take = (int)Math.Min(size, 96 * 1024);
            fs.Seek(size - take, SeekOrigin.Begin);
            var buf = new byte[take];
            fs.ReadExactly(buf);
            var all = Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // only real log lines (they start with the date), not stack traces
            var result = all.Where(l => l.Length > 20 && char.IsDigit(l[0])).TakeLast(Math.Clamp(lines, 10, 1000)).ToArray();
            return Ok(new { lines = result });
        }
        catch (Exception ex)
        {
            return Ok(new { lines = new[] { "Log not readable: " + ex.Message } });
        }
    }

    public sealed class BotRequest
    {
        public float? Strength { get; set; }
        public float? Aggression { get; set; }
        public string? Personality { get; set; }
        public bool Pit { get; set; }
    }

    [HttpPost("/raceai/api/bot/{id:int}")]
    public IActionResult Bot(int id, [FromBody] BotRequest req)
    {
        if (!Allowed()) return Denied();
        return _service.UpdateBot(id, req.Strength, req.Aggression, req.Personality, req.Pit) ? Ok(new { ok = true }) : NotFound(new { error = "no such bot" });
    }

    public sealed class AiRequest
    {
        public float? Strength { get; set; }
        public float? Spread { get; set; }
        public float? Aggression { get; set; }
        public string? Feature { get; set; }
        public bool On { get; set; }
        public bool LightTest { get; set; }
        public string? GridOrder { get; set; }
    }

    [HttpPost("/raceai/api/ai")]
    public IActionResult Ai([FromBody] AiRequest req)
    {
        if (!Allowed()) return Denied();
        if (req.Strength is { } st) _service.SetGlobalStrength(st, req.Spread ?? 0);
        if (req.Aggression is { } ag) _service.SetGlobalAggression(ag);
        if (!string.IsNullOrEmpty(req.Feature) && !_service.SetDashboardFeature(req.Feature, req.On)) return BadRequest(new { error = "unknown feature" });
        if (req.LightTest) _service.StartSignalTest();
        if (!string.IsNullOrEmpty(req.GridOrder) && !_service.SetGridOrder(req.GridOrder)) return BadRequest(new { error = "unknown grid order" });
        return Ok(new { ok = true });
    }

    public sealed class ServerRequest
    {
        public string Action { get; set; } = "";
        public string? Text { get; set; }
        public float A { get; set; }
        public float B { get; set; }
        public float C { get; set; }
        public int Id { get; set; }
    }

    [HttpPost("/raceai/api/server")]
    public async Task<IActionResult> Server([FromBody] ServerRequest req)
    {
        if (!Allowed()) return Denied();
        switch (req.Action)
        {
            case "next":
                return Ok(new { ok = _sessionManager.NextSession() });
            case "restart":
                return Ok(new { ok = _sessionManager.RestartSession() });
            case "time":
                if (!TimeSpan.TryParse(req.Text, out var t)) return BadRequest(new { error = "time as HH:mm" });
                _weatherManager.SetTime((int)t.TotalSeconds);
                return Ok(new { ok = true });
            case "cspweather":
                if (!Enum.TryParse(req.Text, true, out WeatherFxType type)) return BadRequest(new { error = "unknown weather" });
                _weatherManager.SetCspWeather(type, (int)Math.Max(1, req.A));
                return Ok(new { ok = true });
            case "rain":
                _weatherManager.CurrentWeather.RainIntensity = Math.Clamp(req.A, 0, 1);
                _weatherManager.CurrentWeather.RainWetness = Math.Clamp(req.B, 0, 1);
                _weatherManager.CurrentWeather.RainWater = Math.Clamp(req.C, 0, 1);
                _weatherManager.SendWeather();
                return Ok(new { ok = true });
            case "grip":
                _serverConfig.Server.DynamicTrack.OverrideGrip = req.A <= 0 ? null : Math.Clamp(req.A, 0.5f, 1f);
                return Ok(new { ok = true });
            case "chat":
                if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "empty" });
                _service.Chat(req.Text.Trim());
                return Ok(new { ok = true });
            case "kick":
            {
                var client = _entryCarManager.EntryCars.ElementAtOrDefault(req.Id)?.Client;
                if (client == null) return NotFound(new { error = "no such player" });
                await _entryCarManager.KickAsync(client, req.Text ?? "kicked from the dashboard");
                return Ok(new { ok = true });
            }
            case "stop":
                _ = Task.Run(async () => { await Task.Delay(500); _lifetime.StopApplication(); });
                return Ok(new { ok = true });
            case "restartserver":
            {
                // the start script (race-ai/server-supervisor.sh) starts the server again after it stopped; "update" pulls
                // the newest version from GitHub and installs it first
                if (!Supervised) return BadRequest(new { error = "Der Server wurde nicht über race-ai/start-server.sh gestartet, ein Neustart ist so nicht möglich." });
                string mode = req.Text == "update" ? "update" : "restart";
                System.IO.File.WriteAllText("restart.request", mode);
                Serilog.Log.Information("Race AI: server {Mode} requested from the dashboard", mode);
                _entryCarManager.BroadcastChat(mode == "update" ? "Server-Update und Neustart … / server update and restart …" : "Server-Neustart … / server restart …");
                _ = Task.Run(async () => { await Task.Delay(1500); _lifetime.StopApplication(); });
                return Ok(new { ok = true, mode });
            }
            case "temperature":
            {
                var w = _weatherManager.CurrentWeather;
                if (req.A > -40 && req.A < 60) w.TemperatureAmbient = req.A;
                if (req.B > -40 && req.B < 80) w.TemperatureRoad = req.B;
                _weatherManager.SendWeather();
                return Ok(new { ok = true });
            }
            case "command":
            {
                if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "empty" });
                var context = new DashboardCommandContext(_entryCarManager, HttpContext.RequestServices);
                string command = req.Text.Trim().TrimStart('/');
                Serilog.Log.Information("Race AI dashboard command: /{Command}", command);
                await _chatService.ProcessCommandAsync(context, command);
                return Ok(new { ok = true, output = context.Output.ToString().Trim() });
            }
            case "weathertypes":
                return Ok(Enum.GetNames<WeatherFxType>().Where(n => n != "None"));
        }
        return BadRequest(new { error = "unknown action" });
    }
}

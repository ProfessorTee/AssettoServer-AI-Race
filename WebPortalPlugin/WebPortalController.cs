using System.Text;
using AssettoServer.Commands;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Weather;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using SharedWeb;

namespace WebPortalPlugin;

/// <summary>
/// Pages: / (landing), /join, /live, /stats (public), /admin (this computer, or with the admin password when DashboardRemoteAccess is on).
/// APIs: /api/web/join and /api/live/... (public), /api/admin/... (admin). Bots and server tools have their own APIs (/api/bots, /api/tools).
/// </summary>
[ApiController]
public class WebPortalController : ControllerBase
{
    private readonly RaceView _view;
    private readonly LiveFeed _live;
    private readonly JoinInfo _joinInfo;
    private readonly WebPortalConfiguration _config;
    private readonly SessionManager _sessionManager;
    private readonly WeatherManager _weatherManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly ACServerConfiguration _serverConfig;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ChatService _chatService;
    private readonly IEnumerable<IAdminWebAccess> _access;

    public WebPortalController(RaceView view, LiveFeed live, JoinInfo joinInfo, WebPortalConfiguration config, SessionManager sessionManager,
        WeatherManager weatherManager, EntryCarManager entryCarManager, ACServerConfiguration serverConfig, IHostApplicationLifetime lifetime,
        ChatService chatService, IEnumerable<IAdminWebAccess> access)
    {
        _view = view;
        _live = live;
        _joinInfo = joinInfo;
        _config = config;
        _sessionManager = sessionManager;
        _weatherManager = weatherManager;
        _entryCarManager = entryCarManager;
        _serverConfig = serverConfig;
        _lifetime = lifetime;
        _chatService = chatService;
        _access = access;
    }

    private bool IsLocal => AdminAccess.IsLocal(HttpContext);
    private bool Allowed() => AdminAccess.Allowed(HttpContext, _serverConfig, _access);
    private IActionResult Denied() => AdminAccess.Denied(HttpContext, _serverConfig, _access);
    private IActionResult Html(string html) => Content(html, "text/html; charset=utf-8");

    // ------------------------------------------------------------------ pages

    /// <summary>The server's address alone (http://SERVER:HTTP_PORT/) opens the join or the live page (LandingPage).</summary>
    [HttpGet("/")]
    public IActionResult Landing() => _config.LandingPage.Trim().ToLowerInvariant() switch
    {
        "live" when _live.Enabled => Redirect("/live"),
        "none" => NotFound(),
        _ => Redirect("/join")
    };

    [HttpGet("/join")]
    public IActionResult JoinPage() => Html(JoinPageHtml.Html);

    [HttpGet("/live")]
    public IActionResult LivePage() => _live.Enabled ? Html(LivePageHtml.Html) : NotFound();

    /// <summary>Best laps and safety ratings (data from ServerToolsPlugin; the page says so when it isn't there).</summary>
    [HttpGet("/stats")]
    public IActionResult StatsPage() => Html(StatsPageHtml.Html);

    /// <summary>The admin page itself has no data; its APIs check the access.</summary>
    [HttpGet("/admin")]
    public IActionResult AdminPage() => Html(DashboardPage.Html);

    // ------------------------------------------------------------------ public APIs

    /// <summary>Join links (the same data a server list shows).</summary>
    [HttpGet("/api/web/join")]
    public async Task<IActionResult> Join() => Ok(await _joinInfo.GetAsync());

    [HttpGet("/api/live/track")]
    public IActionResult LiveTrack() => _live.Enabled ? Ok(_view.TrackOutline()) : NotFound();

    [HttpGet("/api/live/state")]
    public IActionResult LiveState() => _live.Enabled ? File(_live.Latest(), "application/json") : NotFound();

    /// <summary>Server-Sent Events: the live frames pushed a few times per second.</summary>
    [HttpGet("/api/live/stream")]
    public async Task LiveStream()
    {
        if (!_live.Enabled) { Response.StatusCode = 404; return; }
        var reader = _live.Subscribe();
        if (reader == null) { Response.StatusCode = 503; return; }
        try
        {
            HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            var ct = HttpContext.RequestAborted;
            await Response.Body.WriteAsync("retry: 2000\n\n"u8.ToArray(), ct);
            await Response.Body.FlushAsync(ct);
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                await Response.Body.WriteAsync(frame, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* viewer left */ }
        catch (IOException) { /* connection dropped */ }
        finally
        {
            _live.Unsubscribe(reader);
        }
    }

    // ------------------------------------------------------------------ admin APIs

    [HttpGet("/api/admin/ping")]
    public IActionResult Ping() => Ok(new { ok = true, server = _serverConfig.Server.Name, local = IsLocal, bots = _view.HasDrivenCars });

    [HttpGet("/api/admin/state")]
    public IActionResult State() => Allowed() ? Ok(_view.AdminState()) : Denied();

    [HttpGet("/api/admin/track")]
    public IActionResult Track() => Allowed() ? Ok(_view.TrackOutline()) : Denied();

    /// <summary>
    /// Log lines. The first call gets the last <paramref name="lines"/> lines; with the <c>file</c> and <c>offset</c> of the previous
    /// answer only what was written since then (a few hundred bytes instead of ~50 KB every 2 s).
    /// </summary>
    [HttpGet("/api/admin/log")]
    public IActionResult Log([FromQuery] int lines = 200, [FromQuery] string? file = null, [FromQuery] long offset = -1)
    {
        if (!Allowed()) return Denied();
        try
        {
            var fi = Directory.Exists("logs") ? new DirectoryInfo("logs").GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
            if (fi == null) return Ok(new { lines = Array.Empty<string>(), file = "", offset = 0L, append = false });
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            bool append = file == fi.Name && offset >= 0 && offset <= size && size - offset <= 256 * 1024;
            long from = append ? offset : Math.Max(0, size - 96 * 1024);
            fs.Seek(from, SeekOrigin.Begin);
            var buf = new byte[size - from];
            fs.ReadExactly(buf);
            // only complete lines; the rest comes next time
            int end = Array.LastIndexOf(buf, (byte)'\n') + 1;
            if (append && end <= 0) return Ok(new { lines = Array.Empty<string>(), file = fi.Name, offset, append = true });
            var text = Encoding.UTF8.GetString(buf, 0, Math.Max(0, end));
            var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // only real log lines (they start with the date), not stack traces
            var result = all.Where(l => l.Length > 20 && char.IsDigit(l[0])).Select(l => l.TrimEnd('\r'));
            if (!append) result = result.TakeLast(Math.Clamp(lines, 10, 1000));
            return Ok(new { lines = result.ToArray(), file = fi.Name, offset = from + Math.Max(0, end), append });
        }
        catch (Exception ex)
        {
            return Ok(new { lines = new[] { "Log not readable: " + ex.Message }, file = "", offset = -1L, append = false });
        }
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

    [HttpPost("/api/admin/server")]
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
                _entryCarManager.BroadcastChat(req.Text.Trim());
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
                Serilog.Log.Information("Admin page command: /{Command}", command);
                await _chatService.ProcessCommandAsync(context, command);
                return Ok(new { ok = true, output = context.Output.ToString().Trim() });
            }
            case "weathertypes":
                return Ok(Enum.GetNames<WeatherFxType>().Where(n => n != "None"));
        }
        return BadRequest(new { error = "unknown action" });
    }
}

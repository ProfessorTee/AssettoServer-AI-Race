using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using Microsoft.AspNetCore.Mvc;
using SharedConfig;
using SharedWeb;

namespace ServerToolsPlugin;

/// <summary>
/// API of the server tools for the web portal: /api/tools/... Statistics are public, everything else needs admin access
/// (<see cref="AdminAccess"/>).
/// </summary>
[ApiController]
public class ServerToolsController : ControllerBase
{
    private readonly TrackRotation _rotation;
    private readonly PlayerStats _stats;
    private readonly ServerRestart _restart;
    private readonly ServerToolsConfiguration _config;
    private readonly ACServerConfiguration _serverConfig;
    private readonly IEnumerable<IAdminWebAccess> _access;

    public ServerToolsController(TrackRotation rotation, PlayerStats stats, ServerRestart restart, ServerToolsConfiguration config,
        ACServerConfiguration serverConfig, IEnumerable<IAdminWebAccess> access)
    {
        _rotation = rotation;
        _stats = stats;
        _restart = restart;
        _config = config;
        _serverConfig = serverConfig;
        _access = access;
    }

    private bool Allowed() => AdminAccess.Allowed(HttpContext, _serverConfig, _access);
    private IActionResult Denied() => AdminAccess.Denied(HttpContext, _serverConfig, _access);

    /// <summary>Public: best laps and safety ratings.</summary>
    [HttpGet("/api/tools/stats")]
    public IActionResult Stats() => _stats.Enabled ? Ok(_stats.Overview()) : NotFound();

    [HttpGet("/api/tools/state")]
    public IActionResult State() => Allowed()
        ? Ok(new { rotation = _rotation.Info(), realWeather = _config.RealWeather, supervised = ServerRestart.Supervised, stats = _stats.Enabled })
        : Denied();

    public sealed class ToolsRequest
    {
        public string Action { get; set; } = "";
        public string? Text { get; set; }
        public bool On { get; set; }
    }

    [HttpPost("/api/tools/action")]
    public IActionResult Action([FromBody] ToolsRequest req)
    {
        if (!Allowed()) return Denied();
        switch (req.Action)
        {
            case "rotate":
                if (!_rotation.Active) return BadRequest(new { error = "Keine Strecken-Rotation eingerichtet (rotation.yml)" });
                return Ok(new { ok = _rotation.StartChange("dashboard", string.IsNullOrEmpty(req.Text) ? null : req.Text) });
            case "class":
            {
                var parts = (req.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return BadRequest(new { error = "Klasse fehlt" });
                return Ok(new { ok = true, message = _rotation.SetClass(parts[0], parts.Length < 2 || parts[1] != "next") });
            }
            case "restartserver":
            {
                bool update = req.Text == "update";
                return _restart.Request(update) is { } error ? BadRequest(new { error }) : Ok(new { ok = true, mode = update ? "update" : "restart" });
            }
            case "realweather":
                _config.RealWeather = req.On;
                new ConfigWriter(_serverConfig, "plugin_server_tools_cfg.yml", "Server tools").Set("RealWeather", req.On);
                return Ok(new { ok = true });
            default:
                return BadRequest(new { error = "unknown action" });
        }
    }
}

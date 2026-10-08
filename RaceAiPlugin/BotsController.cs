using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using Microsoft.AspNetCore.Mvc;
using SharedWeb;

namespace RaceAiPlugin;

/// <summary>
/// API of the bots for the admin page (WebPortalPlugin): /api/bots/... Admin access only (<see cref="AdminAccess"/>).
/// </summary>
[ApiController]
public class BotsController : ControllerBase
{
    private readonly RaceAiService _service;
    private readonly ACServerConfiguration _serverConfig;
    private readonly IEnumerable<IAdminWebAccess> _access;

    public BotsController(RaceAiService service, ACServerConfiguration serverConfig, IEnumerable<IAdminWebAccess> access)
    {
        _service = service;
        _serverConfig = serverConfig;
        _access = access;
    }

    private bool Allowed() => AdminAccess.Allowed(HttpContext, _serverConfig, _access);
    private IActionResult Denied() => AdminAccess.Denied(HttpContext, _serverConfig, _access);

    [HttpGet("/api/bots/state")]
    public IActionResult State() => Allowed() ? Ok(_service.BotState()) : Denied();

    [HttpGet("/api/bots/clones")]
    public IActionResult Clones() => Allowed() ? Ok(_service.CloneList()) : Denied();

    public sealed class BotRequest
    {
        public float? Strength { get; set; }
        public float? Aggression { get; set; }
        public string? Personality { get; set; }
        public bool Pit { get; set; }
    }

    [HttpPost("/api/bots/bot/{id:int}")]
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
        public float? Rubber { get; set; }
    }

    [HttpPost("/api/bots/ai")]
    public IActionResult Ai([FromBody] AiRequest req)
    {
        if (!Allowed()) return Denied();
        if (req.Strength is { } st) _service.SetGlobalStrength(st, req.Spread ?? 0);
        if (req.Aggression is { } ag) _service.SetGlobalAggression(ag);
        if (!string.IsNullOrEmpty(req.Feature) && !_service.SetDashboardFeature(req.Feature, req.On)) return BadRequest(new { error = "unknown feature" });
        if (req.LightTest) _service.StartSignalTest();
        if (req.Rubber is { } rb) _service.SetRubberBand(rb);
        if (!string.IsNullOrEmpty(req.GridOrder) && !_service.SetGridOrder(req.GridOrder)) return BadRequest(new { error = "unknown grid order" });
        return Ok(new { ok = true });
    }
}

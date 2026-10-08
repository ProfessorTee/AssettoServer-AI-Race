using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace WebPortalPlugin;

/// <summary>plugin_web_portal_cfg.yml</summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class WebPortalConfiguration
{
    [YamlMember(Description = "Admin page (/admin) also from other computers, with the server's ADMIN_PASSWORD. Off = only from the server's own computer")]
    public bool DashboardRemoteAccess { get; set; } = false;
    [YamlMember(Description = "What http://SERVER:HTTP_PORT/ opens: Join (join page), Live (live timing) or None")]
    public string LandingPage { get; set; } = "Join";
    [YamlMember(Description = "Public live timing with map at /live (no password, no admin data)")]
    public bool LiveView { get; set; } = true;
    [YamlMember(Description = "Updates per second of the live page (1-10)")]
    public int LiveViewHz { get; set; } = 5;
    [YamlMember(Description = "Join page: also show the address in the local network")]
    public bool JoinShowLan { get; set; } = false;
    [YamlMember(Description = "Address players use to join (host name or IP). Empty = the one set in ServerToolsPlugin, else the public IP is detected")]
    public string PublicAddress { get; set; } = "";
    [YamlMember(Description = "Language of the pages' event texts: de or en. Empty = the one set in ServerToolsPlugin (else en)")]
    public string? ChatLanguage { get; set; }
    [YamlMember(Description = "Assetto Corsa installation for track data (map, sectors) that isn't in the server's content/ folder. Empty = the one set in ServerToolsPlugin")]
    public string? AssettoCorsaPath { get; set; }
}

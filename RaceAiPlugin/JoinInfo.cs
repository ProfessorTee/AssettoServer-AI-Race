using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AssettoServer.Server.Configuration;

namespace RaceAiPlugin;

/// <summary>Addresses and links to join the server (Content Manager link, IP and ports), for the dashboard and the public join page.</summary>
public sealed class JoinInfo
{
    private readonly ACServerConfiguration _serverConfig;
    private readonly RaceAiConfiguration _config;
    private string? _publicIp;
    private DateTime _publicIpAt = DateTime.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public JoinInfo(ACServerConfiguration serverConfig, RaceAiConfiguration config)
    {
        _serverConfig = serverConfig;
        _config = config;
    }

    public async Task<object> GetAsync()
    {
        var s = _serverConfig.Server;
        string? publicHost = string.IsNullOrWhiteSpace(_config.PublicAddress) ? await PublicIpAsync() : _config.PublicAddress.Trim();
        string? lan = _config.JoinShowLan ? LanIp() : null;
        return new
        {
            name = s.Name,
            track = s.Track.Contains('/') ? s.Track[(s.Track.LastIndexOf('/') + 1)..] : s.Track,
            httpPort = s.HttpPort,
            tcpPort = s.TcpPort,
            udpPort = s.UdpPort,
            password = !string.IsNullOrEmpty(s.Password),
            publicHost,
            lanIp = lan,
            cmLink = publicHost != null ? CmLink(publicHost, s.HttpPort) : null,
            cmLinkLan = lan != null ? CmLink(lan, s.HttpPort) : null,
            steamLink = "steam://run/244210"
        };
    }

    /// <summary>Content Manager's share link: opens CM and joins the server.</summary>
    private static string CmLink(string host, int httpPort)
        => $"https://acstuff.club/s/q:race/online/join?ip={Uri.EscapeDataString(host)}&httpPort={httpPort}";

    private async Task<string?> PublicIpAsync()
    {
        if (_publicIp != null && DateTime.UtcNow - _publicIpAt < TimeSpan.FromMinutes(30)) return _publicIp;
        try
        {
            var ip = (await Http.GetStringAsync("https://api.ipify.org")).Trim();
            if (IPAddress.TryParse(ip, out _))
            {
                _publicIp = ip;
                _publicIpAt = DateTime.UtcNow;
            }
        }
        catch
        {
            // offline or blocked: keep the old one
        }
        return _publicIp;
    }

    /// <summary>The computer's address in the home network (192.168.x.x, 10.x.x.x, 172.16-31.x.x).</summary>
    private static string? LanIp()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                .ToList();
            return (candidates.FirstOrDefault(a => a.GetAddressBytes()[0] == 192) ?? candidates.FirstOrDefault())?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}

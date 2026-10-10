using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SharedWeb;

/// <summary>
/// Who may use the admin APIs (same rule in every plugin): this computer always; other computers only when the web portal allows
/// remote access (<see cref="IAdminWebAccess"/>) and the request carries the server's admin password.
/// </summary>
public static class AdminAccess
{
    /// <summary>
    /// A request from this computer, typed in as localhost/127.0.0.1. A loopback connection with another host name is a web page
    /// that pointed its own domain at 127.0.0.1 (DNS rebinding) or a reverse proxy passing on outside visitors: not local.
    /// </summary>
    public static bool IsLocal(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null || !(IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())))) return false;
        var headers = context.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP")) return false;
        string host = context.Request.Host.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var h) && IPAddress.IsLoopback(h));
    }

    public static bool RemoteAllowed(IEnumerable<IAdminWebAccess> access) => access.Any(a => a.RemoteAccess);

    // password guessing: after MaxFails different wrong passwords from one address it is locked out for LockMinutes
    // ponytail: per plugin and in memory (each plugin compiles its own copy), a server restart clears it
    private const int MaxFails = 10;
    private const int LockMinutes = 15;
    private sealed class Fails { public int Count; public string Last = ""; public DateTime Since; public DateTime LockedUntil; }
    private static readonly ConcurrentDictionary<IPAddress, Fails> _fails = new();

    private static bool Locked(IPAddress ip) => _fails.TryGetValue(ip, out var f) && f.LockedUntil > DateTime.UtcNow;

    public static bool Allowed(HttpContext context, ACServerConfiguration server, IEnumerable<IAdminWebAccess> access)
    {
        if (IsLocal(context)) return true;
        string password = server.Server.AdminPassword ?? "";
        if (!RemoteAllowed(access) || string.IsNullOrEmpty(password)) return false;
        var ip = context.Connection.RemoteIpAddress ?? IPAddress.None;
        if (Locked(ip)) return false;
        string? given = null;
        var headers = context.Request.Headers;
        if (headers.TryGetValue("X-Admin-Password-Enc", out var enc))
        {
            try { given = Uri.UnescapeDataString(enc.ToString()); } catch { given = null; }
        }
        else if (headers.TryGetValue("X-Admin-Password", out var pw)) given = pw.ToString();
        if (given == null) return false;
        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given.Trim()), Encoding.UTF8.GetBytes(password.Trim())))
        {
            _fails.TryRemove(ip, out _);
            return true;
        }
        var f = _fails.GetOrAdd(ip, _ => new Fails());
        lock (f)
        {
            var now = DateTime.UtcNow;
            if (now - f.Since > TimeSpan.FromMinutes(LockMinutes)) { f.Count = 0; f.Since = now; }
            // the page asks several times a second with the same wrong password: that is one try
            if (given != f.Last && ++f.Count >= MaxFails) f.LockedUntil = now.AddMinutes(LockMinutes);
            f.Last = given;
        }
        if (_fails.Count > 10000) _fails.Clear(); // somebody tries from very many addresses: don't grow without end
        return false;
    }

    public static IActionResult Denied(HttpContext context, ACServerConfiguration server, IEnumerable<IAdminWebAccess> access)
        => new ObjectResult(new
        {
            error = "Admin: only from this computer, or with the admin password when remote access is on",
            reason = !IsLocal(context) && !RemoteAllowed(access) ? "remote"
                : string.IsNullOrEmpty(server.Server.AdminPassword) ? "nopassword"
                : Locked(context.Connection.RemoteIpAddress ?? IPAddress.None) ? "locked" : "password"
        }) { StatusCode = 403 };
}

using System.Net;
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
    public static bool IsLocal(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip == null || IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));
    }

    public static bool RemoteAllowed(IEnumerable<IAdminWebAccess> access) => access.Any(a => a.RemoteAccess);

    public static bool Allowed(HttpContext context, ACServerConfiguration server, IEnumerable<IAdminWebAccess> access)
    {
        if (IsLocal(context)) return true;
        string password = server.Server.AdminPassword ?? "";
        if (!RemoteAllowed(access) || string.IsNullOrEmpty(password)) return false;
        string? given = null;
        var headers = context.Request.Headers;
        if (headers.TryGetValue("X-Admin-Password-Enc", out var enc))
        {
            try { given = Uri.UnescapeDataString(enc.ToString()); } catch { given = null; }
        }
        else if (headers.TryGetValue("X-Admin-Password", out var pw)) given = pw.ToString();
        return given != null && given.Trim() == password.Trim();
    }

    public static IActionResult Denied(HttpContext context, ACServerConfiguration server, IEnumerable<IAdminWebAccess> access)
        => new ObjectResult(new
        {
            error = "Admin: only from this computer, or with the admin password when remote access is on",
            reason = !IsLocal(context) && !RemoteAllowed(access) ? "remote" : string.IsNullOrEmpty(server.Server.AdminPassword) ? "nopassword" : "password"
        }) { StatusCode = 403 };
}

using System.Reflection;

namespace WebPortalPlugin;

/// <summary>The admin page (Pages/admin.html, embedded in the plugin DLL).</summary>
internal static class DashboardPage
{
    private static string? _html;

    public static string Html => _html ??= Load();

    private static string Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebPortalPlugin.Pages.admin.html");
        if (stream == null) return "<html><body>Admin page missing (Pages/admin.html was not embedded)</body></html>";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

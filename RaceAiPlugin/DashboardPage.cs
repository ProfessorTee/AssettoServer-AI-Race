using System.Reflection;

namespace RaceAiPlugin;

/// <summary>The dashboard page (Dashboard/index.html, embedded in the plugin DLL).</summary>
internal static class DashboardPage
{
    private static string? _html;

    public static string Html => _html ??= Load();

    private static string Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RaceAiPlugin.Dashboard.index.html");
        if (stream == null) return "<html><body>Dashboard missing (Dashboard/index.html was not embedded)</body></html>";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

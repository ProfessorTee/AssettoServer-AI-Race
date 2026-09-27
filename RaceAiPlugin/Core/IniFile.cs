using System.Globalization;

namespace RaceAiPlugin.Core;

/// <summary>Minimal reader for Kunos-style ini files (sections, KEY=VALUE, ';' and '//' comments).</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Sections => _sections.Keys;

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line[..comment];
            comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!ini._sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ini._sections[name] = current;
                }
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return ini;
    }

    public static IniFile Load(string path) => Parse(File.ReadAllText(path));

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public string? Get(string section, string key)
        => _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public float GetFloat(string section, string key, float fallback)
    {
        var v = Get(section, key);
        if (v == null) return fallback;
        // values like "0,1,2" -> first component
        int comma = v.IndexOf(',');
        if (comma > 0) v = v[..comma];
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
    }

    public int GetInt(string section, string key, int fallback)
        => (int)MathF.Round(GetFloat(section, key, fallback));

    public float[] GetFloats(string section, string key)
    {
        var v = Get(section, key);
        if (v == null) return [];
        return v.Split(',')
            .Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f)
            .ToArray();
    }
}

/// <summary>Kunos .lut lookup table ("x|y" per line).</summary>
public sealed class Lut
{
    public readonly float[] X;
    public readonly float[] Y;

    public Lut(float[] x, float[] y)
    {
        X = x;
        Y = y;
    }

    public static Lut Parse(string text)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            int c = line.IndexOf(';');
            if (c >= 0) line = line[..c];
            line = line.Trim();
            int bar = line.IndexOf('|');
            if (bar <= 0) continue;
            if (float.TryParse(line[..bar], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && float.TryParse(line[(bar + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                xs.Add(x);
                ys.Add(y);
            }
        }
        return new Lut(xs.ToArray(), ys.ToArray());
    }

    public float Max => Y.Length == 0 ? 0 : Y.Max();

    public float At(float x)
    {
        if (X.Length == 0) return 0;
        if (x <= X[0]) return Y[0];
        for (int i = 1; i < X.Length; i++)
        {
            if (x <= X[i])
            {
                float t = (x - X[i - 1]) / MathF.Max(1e-6f, X[i] - X[i - 1]);
                return Y[i - 1] + (Y[i] - Y[i - 1]) * t;
            }
        }
        return Y[^1];
    }
}

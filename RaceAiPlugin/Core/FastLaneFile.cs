using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>
/// One point of a Kunos fast_lane.ai racing line including the "extra" data block
/// (recorded speed, gas, brake, track width left/right, normal, ...).
/// </summary>
public struct FastLanePoint
{
    public Vector3 Position;
    public float Speed;
    public float Gas;
    public float Brake;
    public float Radius;
    public float SideLeft;
    public float SideRight;
    public float Camber;
    public Vector3 Normal;
    public Vector3 Forward;
    public float Grade;
}

/// <summary>
/// Reader for Kunos fast_lane.ai files (version 7). Unlike AssettoServer's traffic parser this keeps
/// all per-point data, because the racing AI needs the track widths and speed hints.
/// </summary>
public static class FastLaneFile
{
    public static FastLanePoint[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static FastLanePoint[] Read(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        int version = reader.ReadInt32();
        if (version != 7)
            throw new InvalidDataException($"Unsupported fast_lane.ai version {version} (expected 7 = Kunos format)");

        int count = reader.ReadInt32();
        reader.ReadInt32(); // lap time
        reader.ReadInt32(); // sample count

        var points = new FastLanePoint[count];
        for (int i = 0; i < count; i++)
        {
            points[i].Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            reader.ReadSingle(); // length
            reader.ReadInt32(); // id
        }

        int extraCount = reader.ReadInt32();
        if (extraCount != count)
            throw new InvalidDataException("Point count does not match extra data count");

        for (int i = 0; i < count; i++)
        {
            ref var p = ref points[i];
            p.Speed = reader.ReadSingle();
            p.Gas = reader.ReadSingle();
            p.Brake = reader.ReadSingle();
            reader.ReadSingle(); // obsolete lat g
            p.Radius = reader.ReadSingle();
            p.SideLeft = reader.ReadSingle();
            p.SideRight = reader.ReadSingle();
            p.Camber = reader.ReadSingle() * reader.ReadSingle(); // camber * direction
            p.Normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            reader.ReadSingle(); // length
            p.Forward = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            reader.ReadSingle(); // tag
            p.Grade = reader.ReadSingle();
        }

        return points;
    }

    /// <summary>Writes a version 7 file. Used by tests and the simulator to create synthetic tracks.</summary>
    public static void Write(Stream stream, IReadOnlyList<FastLanePoint> points)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(7);
        w.Write(points.Count);
        w.Write(0);
        w.Write(0);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            w.Write(p.Position.X); w.Write(p.Position.Y); w.Write(p.Position.Z);
            w.Write(Vector3.Distance(p.Position, points[(i + 1) % points.Count].Position));
            w.Write(i);
        }
        w.Write(points.Count);
        foreach (var p in points)
        {
            w.Write(p.Speed); w.Write(p.Gas); w.Write(p.Brake); w.Write(0f);
            w.Write(p.Radius); w.Write(p.SideLeft); w.Write(p.SideRight);
            w.Write(p.Camber); w.Write(1f);
            w.Write(p.Normal.X); w.Write(p.Normal.Y); w.Write(p.Normal.Z);
            w.Write(0f);
            w.Write(p.Forward.X); w.Write(p.Forward.Y); w.Write(p.Forward.Z);
            w.Write(0f); w.Write(p.Grade);
        }
    }
}

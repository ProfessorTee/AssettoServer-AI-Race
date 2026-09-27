using System.Numerics;
using System.Text;

namespace RaceAiPlugin.Core;

/// <summary>
/// Reads only the dummy nodes (AC_START_x, AC_PIT_x, AC_TIME_x_L/R, ...) and their world transforms from a Kunos .kn5 model.
/// Textures, materials and meshes are skipped.
/// </summary>
public static class Kn5Reader
{
    public readonly record struct Kn5Dummy(string Name, Vector3 Position, Vector3 Forward);

    public static List<Kn5Dummy> ReadDummies(string path, Func<string, bool>? filter = null)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        filter ??= name => name.StartsWith("AC_", StringComparison.Ordinal);

        var magic = Encoding.ASCII.GetString(reader.ReadBytes(6));
        if (magic != "sc6969") throw new InvalidDataException($"{path} is not a kn5 file");
        int version = reader.ReadInt32();
        if (version > 5) reader.ReadInt32();

        int textureCount = reader.ReadInt32();
        for (int i = 0; i < textureCount; i++)
        {
            reader.ReadInt32();
            ReadString(reader);
            int size = reader.ReadInt32();
            stream.Seek(size, SeekOrigin.Current);
        }

        int materialCount = reader.ReadInt32();
        for (int i = 0; i < materialCount; i++)
        {
            ReadString(reader);
            ReadString(reader);
            reader.ReadByte();
            reader.ReadByte();
            if (version > 4) reader.ReadInt32();
            int props = reader.ReadInt32();
            for (int k = 0; k < props; k++)
            {
                ReadString(reader);
                stream.Seek(10 * 4, SeekOrigin.Current);
            }
            int samplers = reader.ReadInt32();
            for (int k = 0; k < samplers; k++)
            {
                ReadString(reader);
                reader.ReadInt32();
                ReadString(reader);
            }
        }

        var result = new List<Kn5Dummy>();
        ReadNode(reader, stream, Matrix4x4.Identity, filter, result);
        return result;
    }

    private static void ReadNode(BinaryReader reader, Stream stream, Matrix4x4 parent, Func<string, bool> filter, List<Kn5Dummy> result)
    {
        int nodeClass = reader.ReadInt32();
        string name = ReadString(reader);
        int children = reader.ReadInt32();
        reader.ReadByte(); // active

        var world = parent;
        switch (nodeClass)
        {
            case 1:
            {
                var m = new Matrix4x4(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                world = m * parent;
                break;
            }
            case 2:
            {
                stream.Seek(3, SeekOrigin.Current);
                int vertices = reader.ReadInt32();
                stream.Seek(vertices * 44L, SeekOrigin.Current);
                int indices = reader.ReadInt32();
                stream.Seek(indices * 2L, SeekOrigin.Current);
                // material id, layer, lodIn, lodOut, bounding sphere (4 floats), isRenderable
                stream.Seek(4 + 4 + 8 + 16 + 1, SeekOrigin.Current);
                break;
            }
            case 3:
            {
                stream.Seek(3, SeekOrigin.Current);
                int bones = reader.ReadInt32();
                for (int i = 0; i < bones; i++)
                {
                    ReadString(reader);
                    stream.Seek(64, SeekOrigin.Current);
                }
                int vertices = reader.ReadInt32();
                stream.Seek(vertices * 76L, SeekOrigin.Current);
                int indices = reader.ReadInt32();
                stream.Seek(indices * 2L, SeekOrigin.Current);
                stream.Seek(4 + 4 + 8, SeekOrigin.Current);
                break;
            }
            default:
                throw new InvalidDataException($"Unknown kn5 node class {nodeClass} ({name})");
        }

        if (filter(name))
            result.Add(new Kn5Dummy(name, new Vector3(world.M41, world.M42, world.M43), Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33))));

        for (int i = 0; i < children; i++)
            ReadNode(reader, stream, world, filter, result);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    /// <summary>
    /// Reads all dummies of a track layout. Uses models_&lt;layout&gt;.ini (or models.ini) when present, otherwise every kn5 in the track root.
    /// </summary>
    public static List<Kn5Dummy> ReadTrackDummies(string trackRoot, string? layout)
    {
        var files = new List<string>();
        string modelsIni = Path.Join(trackRoot, string.IsNullOrEmpty(layout) ? "models.ini" : $"models_{layout}.ini");
        if (File.Exists(modelsIni))
        {
            var ini = IniFile.Load(modelsIni);
            foreach (var section in ini.Sections.Where(s => s.StartsWith("MODEL_", StringComparison.OrdinalIgnoreCase)))
            {
                var file = ini.Get(section, "FILE");
                if (file != null) files.Add(Path.Join(trackRoot, file));
            }
        }
        else
        {
            string main = Path.Join(trackRoot, Path.GetFileName(trackRoot.TrimEnd('/', '\\')) + ".kn5");
            if (File.Exists(main)) files.Add(main);
            else files.AddRange(Directory.GetFiles(trackRoot, "*.kn5"));
        }

        var result = new List<Kn5Dummy>();
        foreach (var file in files.Where(File.Exists))
            result.AddRange(ReadDummies(file));
        return result;
    }
}

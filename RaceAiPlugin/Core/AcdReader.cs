using System.Text;

namespace RaceAiPlugin.Core;

/// <summary>
/// Reads Kunos data.acd archives (the packed "data" folder of a car), the same way Content Manager does.
/// The key is derived from the car folder name.
/// </summary>
public static class AcdReader
{
    public static Dictionary<string, byte[]> Read(string acdPath, string carFolderName)
    {
        var data = File.ReadAllBytes(acdPath);
        var key = Encoding.ASCII.GetBytes(CreateKey(carFolderName));
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        int p = 0;
        if (data.Length >= 8 && BitConverter.ToInt32(data, 0) == -1111)
            p = 8;

        while (p + 4 <= data.Length)
        {
            int nameLength = BitConverter.ToInt32(data, p);
            p += 4;
            if (nameLength <= 0 || p + nameLength > data.Length) break;
            string name = Encoding.ASCII.GetString(data, p, nameLength);
            p += nameLength;
            int length = BitConverter.ToInt32(data, p);
            p += 4;
            if (length < 0 || p + (long)length * 4 > data.Length) break;

            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                int v = BitConverter.ToInt32(data, p + i * 4);
                bytes[i] = (byte)(v - key[i % key.Length]);
            }
            p += length * 4;
            result[name] = bytes;
        }

        return result;
    }

    public static string CreateKey(string folderName)
    {
        var s = folderName.ToLowerInvariant();
        int n = s.Length;
        unchecked
        {
            int a = 0;
            foreach (var ch in s) a += ch;

            int b = 0;
            for (int i = 0; i < n - 1; i += 2)
            {
                b *= s[i];
                b -= s[i + 1];
            }

            int c = 0;
            for (int i = 1; i < n - 3; i += 3)
            {
                c *= s[i];
                c /= s[i + 1] + 0x1b;
                c += -0x1b - s[i - 1];
            }

            int d = 0x1683;
            for (int i = 1; i < n; i++) d -= s[i];

            int e = 0x42;
            for (int i = 1; i < n - 4; i += 4) e = (s[i] + 0xf) * e * (s[i - 1] + 0xf) + 0x16;

            int f = 0x65;
            for (int i = 0; i < n - 2; i += 2) f -= s[i];

            int g = 0xab;
            for (int i = 0; i < n - 2; i += 2) g %= s[i];

            int h = 0xab;
            for (int i = 0; i < n - 1; i++) h = h / s[i] + s[i + 1];

            return string.Join("-", new[] { a, b, c, d, e, f, g, h }.Select(x => (x & 0xff).ToString()));
        }
    }
}

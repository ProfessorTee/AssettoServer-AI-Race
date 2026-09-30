using System.Collections.Generic;

namespace AssettoServer.Utils;

/// <summary>
/// AC sends the length of a chat message in one byte (at most 255 characters). Race AI patch: longer messages broke the
/// packet (the rest showed up as garbage from another car), so they are split into several messages at spaces.
/// </summary>
public static class ChatSplitter
{
    public const int MaxLength = 250;

    public static IEnumerable<string> Split(string? message)
    {
        message ??= "";
        if (CodePoints(message) <= MaxLength)
        {
            yield return message;
            yield break;
        }
        int start = 0;
        while (start < message.Length)
        {
            int end = start, count = 0, lastSpace = -1;
            while (end < message.Length && count < MaxLength)
            {
                if (message[end] == ' ') lastSpace = end;
                end += char.IsSurrogatePair(message, end) ? 2 : 1;
                count++;
            }
            if (end < message.Length && lastSpace > start + MaxLength / 2) end = lastSpace + 1;
            var part = message[start..end].TrimEnd();
            if (part.Length > 0) yield return part;
            start = end;
        }
    }

    private static int CodePoints(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1) n++;
        return n;
    }
}

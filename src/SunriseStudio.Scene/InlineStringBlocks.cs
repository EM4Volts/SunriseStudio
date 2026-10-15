using System.Text;

namespace SunriseStudio.Scene;

/** Class-0x80800065 blocks: a u64 byte length, then NUL-separated UTF-8 strings. */
internal static class InlineStringBlocks
{
    private const uint BlockClass = 0x80800065;
    // A longer string is not a name.
    internal const int MaximumLength = 1024;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /** Each block's header offset and byte range; the scan reads every aligned word, inside blocks too. */
    internal static IEnumerable<(int Offset, int Start, int Length)> Blocks(TagData data)
    {
        for (int offset = 0; offset <= data.Length - 12; offset += 4)
        {
            if (data.U32(offset) != BlockClass) continue;
            ulong length = data.U64(offset + 4);
            if (length == 0 || length > (ulong)(data.Length - offset - 12)) continue;
            yield return (offset, offset + 12, (int)length);
        }
    }

    /** Strings of 1 to 1024 valid UTF-8 bytes with no control character; a bad string drops only itself. */
    internal static List<(int Block, int Start, int Length, string Text)> Strings(TagData data)
    {
        var result = new List<(int, int, int, string)>();
        foreach (var (offset, start, length) in Blocks(data))
        {
            int at = start, end = start + length;
            while (at < end)
            {
                int stop = Array.IndexOf(data.Bytes, (byte)0, at, end - at);
                if (stop < 0) stop = end;
                if (stop - at is > 0 and <= MaximumLength)
                {
                    try
                    {
                        string text = StrictUtf8.GetString(data.Bytes, at, stop - at);
                        if (!text.Any(char.IsControl)) result.Add((offset, at, stop - at, text));
                    }
                    catch (DecoderFallbackException) { }
                }
                at = stop + 1;
            }
        }
        return result;
    }
}

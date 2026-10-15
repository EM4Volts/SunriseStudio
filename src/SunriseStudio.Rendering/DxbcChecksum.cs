using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Rendering;

/** The DXBC container checksum: MD5 rounds over bytes 20.. with the container's own length padding. */
public static class DxbcChecksum
{
    private static readonly int[] Shifts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
    ];
    private static readonly uint[] Constants = Enumerable.Range(0, 64)
        .Select(i => (uint)(long)Math.Floor(Math.Abs(Math.Sin(i + 1)) * 4294967296.0)).ToArray();

    /** True when the 16 bytes at offset 4 equal the checksum of the rest of the container. */
    public static bool Matches(ReadOnlySpan<byte> container)
    {
        if (container.Length < 32 || !container[..4].SequenceEqual("DXBC"u8)) return false;
        Span<uint> hash = stackalloc uint[4];
        Compute(container[20..], hash);
        for (int i = 0; i < 4; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(container[(4 + i * 4)..]) != hash[i]) return false;
        return true;
    }

    private static void Compute(ReadOnlySpan<byte> data, Span<uint> state)
    {
        state[0] = 0x67452301; state[1] = 0xEFCDAB89; state[2] = 0x98BADCFE; state[3] = 0x10325476;
        uint bits = (uint)data.Length * 8, second = (bits >> 2) | 1;
        int leftover = data.Length % 64, full = data.Length - leftover;
        Span<uint> block = stackalloc uint[16];
        for (int offset = 0; offset < full; offset += 64)
        {
            for (int i = 0; i < 16; i++) block[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + i * 4)..]);
            Transform(state, block);
        }
        Span<byte> tail = stackalloc byte[64];
        tail.Clear();
        if (leftover >= 56)
        {
            data[full..].CopyTo(tail);
            tail[leftover] = 0x80;
            Load(tail, block); Transform(state, block);
            block.Clear(); block[0] = bits; block[15] = second;
            Transform(state, block);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tail, bits);
            data[full..].CopyTo(tail[4..]);
            tail[4 + leftover] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(tail[60..], second);
            Load(tail, block); Transform(state, block);
        }
    }

    private static void Load(ReadOnlySpan<byte> bytes, Span<uint> block)
    {
        for (int i = 0; i < 16; i++) block[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
    }

    private static void Transform(Span<uint> state, ReadOnlySpan<uint> block)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (int i = 0; i < 64; i++)
        {
            uint f; int g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
            else { f = c ^ (b | ~d); g = 7 * i % 16; }
            uint next = d; d = c; c = b;
            b += BitOperations.RotateLeft(a + f + Constants[i] + block[g], Shifts[i]);
            a = next;
        }
        state[0] += a; state[1] += b; state[2] += c; state[3] += d;
    }
}

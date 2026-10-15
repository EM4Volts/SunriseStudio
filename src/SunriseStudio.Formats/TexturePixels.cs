using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Pixel previews support RGBA8, BGRA8, R8 and BC1 through BC5 unsigned textures. */
public static class TexturePixels
{
    /** Compressed HDR and BC7 textures retain lossless DDS export when no pixel decoder exists. */
    public static RgbaImage Decode(TextureData texture, int mip = 0, int arraySlice = 0, int depthSlice = 0)
    {
        DdsInterchange.Validate(texture);
        var sub = texture.Subresources.SingleOrDefault(x => x.MipLevel == mip && x.ArraySlice == arraySlice)
            ?? throw new ArgumentOutOfRangeException(nameof(mip));
        if (depthSlice < 0 || depthSlice >= sub.Depth) throw new ArgumentOutOfRangeException(nameof(depthSlice));
        var source = sub.Data.AsSpan(depthSlice * sub.SlicePitch, sub.SlicePitch);
        var output = new byte[checked(sub.Width * sub.Height * 4)];
        int format = texture.Format;
        if (format is 28 or 29 or 87 or 91 or 88 or 93)
        {
            source.CopyTo(output);
            for (int p = 0; p < output.Length; p += 4)
            {
                if (format is 87 or 91 or 88 or 93) (output[p], output[p + 2]) = (output[p + 2], output[p]);
                if (format is 88 or 93) output[p + 3] = 255;
            }
        }
        else if (format == 61)
        {
            for (int i = 0; i < source.Length; i++)
            {
                output[i * 4] = output[i * 4 + 1] = output[i * 4 + 2] = source[i];
                output[i * 4 + 3] = 255;
            }
        }
        else if (format is 71 or 72 or 74 or 75 or 77 or 78 or 80 or 83)
            DecodeBlocks(source, output, sub.Width, sub.Height, format);
        else throw new NotSupportedException($"PNG export does not decode DXGI format {format}. Use DDS.");
        return new(sub.Width, sub.Height, output);
    }

    /** Blocks at image edges still occupy a complete encoded 4 by 4 tile. */
    private static void DecodeBlocks(ReadOnlySpan<byte> source, Span<byte> output, int width, int height, int format)
    {
        int blockSize = format is 71 or 72 or 80 ? 8 : 16;
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        Span<byte> tile = stackalloc byte[64];
        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];
        for (int y = 0; y < blocksY; y++)
            for (int x = 0; x < blocksX; x++)
            {
                var block = source.Slice((y * blocksX + x) * blockSize, blockSize);
                if (format is 80 or 83)
                {
                    DecodeAlpha(block, red);
                    if (format == 83) DecodeAlpha(block[8..], green);
                    for (int i = 0; i < 16; i++)
                    {
                        tile[i * 4] = red[i]; tile[i * 4 + 1] = format == 83 ? green[i] : red[i];
                        tile[i * 4 + 2] = format == 83 ? (byte)0 : red[i]; tile[i * 4 + 3] = 255;
                    }
                }
                else
                {
                    DecodeColor(format is 71 or 72 ? block : block[8..], tile, format is 71 or 72);
                    if (format is 74 or 75)
                        for (int i = 0; i < 16; i++) tile[i * 4 + 3] = (byte)(((block[i / 2] >> ((i % 2) * 4)) & 15) * 17);
                    else if (format is 77 or 78)
                    {
                        DecodeAlpha(block, red);
                        for (int i = 0; i < 16; i++) tile[i * 4 + 3] = red[i];
                    }
                }
                for (int py = 0; py < 4 && y * 4 + py < height; py++)
                    for (int px = 0; px < 4 && x * 4 + px < width; px++)
                        tile.Slice((py * 4 + px) * 4, 4).CopyTo(output.Slice(((y * 4 + py) * width + x * 4 + px) * 4, 4));
            }
    }

    /** BC1 switches to three colors and transparency when its endpoint order is ascending. */
    private static void DecodeColor(ReadOnlySpan<byte> block, Span<byte> tile, bool transparency)
    {
        ushort a = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort b = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Span<byte> colors = stackalloc byte[16];
        Expand565(a, colors); Expand565(b, colors[4..]);
        bool three = transparency && a <= b;
        for (int i = 0; i < 3; i++)
        {
            colors[8 + i] = (byte)(three ? (colors[i] + colors[4 + i]) / 2 : (2 * colors[i] + colors[4 + i]) / 3);
            colors[12 + i] = three ? (byte)0 : (byte)((colors[i] + 2 * colors[4 + i]) / 3);
        }
        colors[11] = 255; colors[15] = three ? (byte)0 : (byte)255;
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int i = 0; i < 16; i++) colors.Slice((int)((indices >> (i * 2)) & 3) * 4, 4).CopyTo(tile.Slice(i * 4, 4));
    }

    private static void Expand565(ushort color, Span<byte> output)
    {
        int r = (color >> 11) & 31, g = (color >> 5) & 63, b = color & 31;
        output[0] = (byte)((r << 3) | (r >> 2)); output[1] = (byte)((g << 2) | (g >> 4));
        output[2] = (byte)((b << 3) | (b >> 2)); output[3] = 255;
    }

    /** BC3 alpha and BC4 channels share an endpoint palette and packed three-bit indices. */
    private static void DecodeAlpha(ReadOnlySpan<byte> block, Span<byte> values)
    {
        Span<byte> palette = stackalloc byte[8];
        palette[0] = block[0]; palette[1] = block[1];
        if (palette[0] > palette[1])
            for (int i = 1; i <= 6; i++) palette[i + 1] = (byte)(((7 - i) * palette[0] + i * palette[1]) / 7);
        else
        {
            for (int i = 1; i <= 4; i++) palette[i + 1] = (byte)(((5 - i) * palette[0] + i * palette[1]) / 5);
            palette[6] = 0; palette[7] = 255;
        }
        ulong packed = 0;
        for (int i = 0; i < 6; i++) packed |= (ulong)block[2 + i] << (i * 8);
        for (int i = 0; i < 16; i++) values[i] = palette[(int)((packed >> (i * 3)) & 7)];
    }
}

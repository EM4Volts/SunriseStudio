using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SunriseStudio.Rendering;

/** These constants support native technique 519 with one fully resident cube texture. */
public sealed record NativeCubemapBinding(uint TextureTag, Matrix4x4 StencilTransform,
    Matrix4x4 BoxTransform, Vector3 Extents, Vector4[] Vertex, Vector4[] Pixel)
{
    public const uint MaterialTag = 0x80B9E157;

    /** Other native permutations must supply their own verified constant and resource contracts. */
    public static NativeCubemapBinding Create(ReadOnlySpan<byte> source, uint textureTag, byte textureScale, int mipCount)
    {
        if (source.Length != 0x1A0 || mipCount < 2)
            throw new InvalidDataException("Cubemap source size or mip count is invalid.");
        var bytes = source.ToArray();
        float F(int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        Vector4 V(int offset) => new(F(offset), F(offset + 4), F(offset + 8), F(offset + 12));
        bool Positive(Vector4 value) => value.X > 0 && value.Y > 0 && value.Z > 0;
        var fadeA = V(0x40); var fadeB = V(0x50);
        bool Nonzero(Vector4 value) => Vector4.Abs(value).X > .0001f || Vector4.Abs(value).Y > .0001f
            || Vector4.Abs(value).Z > .0001f || Vector4.Abs(value).W > .0001f;
        uint flags = Nonzero(fadeA) || Nonzero(fadeB) ? 1u : 0u;
        if (F(0x6C) > 0) flags |= 2;
        if (F(0x158) > 0 && F(0x15C) != 0) flags |= 4;
        if (unchecked(I(0x130) * I(0x134) * I(0x138)) > 1) flags |= 8;
        if (F(0x154) > 0) flags |= 16;
        if (Positive(V(0x80))) flags |= 32;
        int family = (flags & 3) == 3 ? 4 : (flags & 3) == 2 ? 2 : 0;
        uint technique = ((flags >> 4) & 1) + 519 + 2 * (((flags >> 3) & 1)
            + 2 * ((uint)family + ((flags >> 2) & 1) + 6 * ((flags >> 5) & 1)));
        if (technique != 519)
            throw new NotSupportedException($"Cubemap technique {technique} is not supported.");
        var box = MemoryMarshal.Read<Matrix4x4>(source[0xF0..]);
        var stencil = MemoryMarshal.Read<Matrix4x4>(source[0xB0..]);
        var extents = new Vector3(F(0x10), F(0x14), F(0x18));
        if (!Positive(new(extents, 0)) || !Matrix4x4.Invert(box, out _))
            throw new InvalidDataException("Cubemap bounds are invalid.");
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(box));
        Vector4[] vertex = [new(box.Translation, 1), new(rotation.X, rotation.Y, rotation.Z, rotation.W), new(extents, 0)];
        var pixel = new Vector4[18];
        float Ratio(float numerator, float denominator) => Math.Abs(denominator) > .0001f ? numerator / denominator : 1_000_000;
        float Reciprocal(float value) => Ratio(1, value);
        pixel[0] = new(Reciprocal(fadeA.X), Reciprocal(fadeA.Y), Reciprocal(fadeA.Z), 0);
        pixel[1] = new(Reciprocal(fadeB.X), Reciprocal(fadeB.Y), Reciprocal(fadeB.Z), Math.Max(F(0x70), .0001f));
        pixel[2] = new(Ratio(1 - fadeA.X, fadeA.X), Ratio(1 - fadeA.Y, fadeA.Y),
            Ratio(1 - fadeA.Z, fadeA.Z), textureScale / 255f * F(0x30));
        pixel[3] = new(Ratio(1 - fadeB.X, fadeB.X), Ratio(1 - fadeB.Y, fadeB.Y),
            Ratio(1 - fadeB.Z, fadeB.Z), mipCount - 1.5f);
        var scaled = Matrix4x4.CreateScale(extents) * box;
        var rows = MemoryMarshal.Cast<Matrix4x4, Vector4>(new[] { scaled }.AsSpan());
        rows.CopyTo(pixel.AsSpan(5));
        pixel[13] = new(F(0x144) / Math.Max(1, I(0x130)), F(0x144) / Math.Max(1, I(0x134)),
            F(0x144) / Math.Max(1, I(0x138)), F(0x13C));
        float alphaPower = F(0x148);
        pixel[14] = new(F(0x140), F(0x144), alphaPower > .5f ? 1 + MathF.Pow(2 * alphaPower - 1, 4) * 60 : 2 * alphaPower, F(0x74));
        pixel[17] = V(0x150);
        if (vertex.Concat(pixel).Any(value => !float.IsFinite(value.X + value.Y + value.Z + value.W)))
            throw new InvalidDataException("Cubemap constants are not finite.");
        return new(textureTag, stencil, box, extents, vertex, pixel);
    }

    /** The native stencil mode uses camera position in the authored oriented box. */
    public bool Contains(Vector3 camera, float nearPlane)
    {
        Matrix4x4.Invert(BoxTransform, out var inverse);
        var local = Vector3.Transform(camera, inverse);
        var expanded = Extents + new Vector3(nearPlane * 2);
        return Math.Abs(local.X) <= expanded.X && Math.Abs(local.Y) <= expanded.Y && Math.Abs(local.Z) <= expanded.Z;
    }
}

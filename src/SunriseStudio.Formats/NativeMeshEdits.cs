using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

/** Native reimport changes positions only and keeps topology, skinning and other attributes. */
public static class NativeMeshEdits
{
    /** Existing source vertex indices preserve shared buffers and unselected mesh parts. */
    public static IReadOnlyDictionary<uint, byte[]> ApplyPositions(MeshData original, InterchangeMesh edited)
    {
        var native = original.Native ?? throw new NotSupportedException("Mesh has no native source buffers.");
        if (native.Kind is not (NativeMeshKind.Static or NativeMeshKind.Entity))
            throw new NotSupportedException("Position reimport currently supports static and entity meshes.");
        if (edited.Positions.Length != original.Vertices.Length
            || !edited.Indices.SequenceEqual(original.Indices)
            || original.SourceVertexIndices.Length != original.Vertices.Length)
            throw new InvalidDataException("Position reimport needs the exported vertex order and topology.");
        if (native.VertexStreams.Length == 0) throw new InvalidDataException("Mesh has no position stream.");
        var stream = native.VertexStreams[0];
        bool floating = native.Kind == NativeMeshKind.Entity && stream.Stride == 48;
        if (!floating && stream.Stride is not (8 or 12 or 16 or 20 or 24 or 28 or 32))
            throw new NotSupportedException("Position stream layout is unsupported.");
        var scale = new Vector3(native.PositionScale.X, native.PositionScale.Y, native.PositionScale.Z);
        var shift = new Vector3(native.PositionOffset.X, native.PositionOffset.Y, native.PositionOffset.Z);
        if (!Finite(scale) || !Finite(shift) || scale.X == 0 || scale.Y == 0 || scale.Z == 0)
            throw new InvalidDataException("Position transform cannot be inverted.");
        var bytes = (byte[])stream.Data.Clone();
        Dictionary<uint, Vector3> desiredPositions = [];
        for (int i = 0; i < edited.Positions.Length; i++)
        {
            var position = edited.Positions[i];
            if (!Finite(position)) throw new InvalidDataException("Position is not finite.");
            uint sourceIndex = original.SourceVertexIndices[i];
            if (desiredPositions.TryGetValue(sourceIndex, out var prior) && prior != position)
                throw new InvalidDataException("Shared source vertices have conflicting imported positions.");
            desiredPositions[sourceIndex] = position;
            if (position == original.Vertices[i].Position) continue;
            var local = (position - shift) / scale;
            if (!Finite(local)) throw new InvalidDataException("Position exceeds its encoded range.");
            int p = checked((int)sourceIndex * stream.Stride);
            int size = floating ? 12 : 6;
            if (p < 0 || p > bytes.Length - size) throw new InvalidDataException("Vertex leaves its source buffer.");
            if (floating)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p), local.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p + 4), local.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p + 8), local.Z);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(p), Encode(local.X));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(p + 2), Encode(local.Y));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(p + 4), Encode(local.Z));
            }
        }
        return new Dictionary<uint, byte[]> { [stream.DataTag] = bytes };
    }

    private static short Encode(float value)
    {
        if (!float.IsFinite(value) || value is < -1.000002f or > 1.000002f)
            throw new InvalidDataException("Position leaves the original mesh quantization bounds.");
        return checked((short)MathF.Round(Math.Clamp(value, -1, 1) * short.MaxValue));
    }

    private static bool Finite(Vector3 x) => float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z);
}

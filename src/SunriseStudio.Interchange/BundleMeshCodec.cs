using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Existing native meshes retain vertex identity, topology, skinning and unknown attribute lanes. */
public static class BundleMeshCodec
{
    private const float ComparisonTolerance = 0.00001f;

    /** All validation finishes on private copies before any payload edit is returned. */
    public static IReadOnlyDictionary<uint, byte[]> Apply(PackageCatalog catalog, MeshData original,
        BundleGeometry baseline, BundleMeshEdit edit)
    {
        if (edit.Id != baseline.Id || baseline.Positions is null || baseline.Indices is null || edit.Indices is null
            || original.Vertices.Length != baseline.Positions.Length || original.Indices.Length % 3 != 0
            || original.Indices.Any(index => index >= original.Vertices.Length)
            || !baseline.Indices.SequenceEqual(original.Indices) || !edit.Indices.SequenceEqual(baseline.Indices))
            throw new InvalidDataException("Mesh import needs its original identity, vertex order and topology.");
        int count = original.Vertices.Length;
        Validate(baseline.Positions, count, 3); Validate(baseline.Normals, count, 3);
        Validate(baseline.TexCoords, count, 2); Validate(edit.Positions, count, 3);
        if (edit.Normals is not null) Validate(edit.Normals, count, 3);
        if (edit.TexCoords is not null) Validate(edit.TexCoords, count, 2);
        ValidateSkin(baseline, edit, count);
        var authored = baseline.Basis == NativeMeshBasis.StaticProfile ? NativeMeshBasis.Read(catalog, original) : null;
        if (authored is null && baseline.Basis != "Diagnostic")
            throw new NotSupportedException("The exported native tangent basis is unsupported.");
        if (authored is not null) Validate(baseline.Tangents, count, 4);
        else if (baseline.Tangents is not null)
            throw new InvalidDataException("Diagnostic geometry cannot claim an authored tangent basis.");
        var positions = new Vector3[count]; var uvs = new Vector2[count]; var normals = new Vector3[count];
        var normalChanged = new bool[count];
        bool changedPositions = false, changedUvs = false, changedNormals = false;
        for (int i = 0; i < count; i++)
        {
            Vector3 originalNormal = authored?.Normals[i] ?? original.Vertices[i].Normal;
            if (!Close(V3(baseline.Positions[i]), original.Vertices[i].Position)
                || !Close(V3(baseline.Normals[i]), originalNormal)
                || !Close(V2(baseline.TexCoords[i]), original.Vertices[i].TexCoord))
                throw new InvalidDataException("The exported mesh baseline differs from the current asset.");
            if (authored is not null)
                for (int component = 0; component < 4; component++)
                    if (!Close(baseline.Tangents![i][component], authored.Tangents[i][component]))
                        throw new InvalidDataException("The exported tangent baseline differs from the current asset.");
            positions[i] = V3(edit.Positions[i]);
            if (Close(positions[i], original.Vertices[i].Position)) positions[i] = original.Vertices[i].Position;
            else changedPositions = true;
            uvs[i] = edit.TexCoords is null ? original.Vertices[i].TexCoord : V2(edit.TexCoords[i]);
            if (Close(uvs[i], original.Vertices[i].TexCoord)) uvs[i] = original.Vertices[i].TexCoord;
            else changedUvs = true;
            normals[i] = edit.Normals is null ? originalNormal : V3(edit.Normals[i]);
            normalChanged[i] = edit.Normals is not null && !Close(normals[i], originalNormal);
            if (!normalChanged[i]) normals[i] = originalNormal;
            changedNormals |= normalChanged[i];
        }
        if (!changedPositions && !changedUvs && !changedNormals) return new Dictionary<uint, byte[]>();
        var native = original.Native ?? throw new NotSupportedException("Mesh has no native source buffers.");
        if (original.SourceVertexIndices.Length != count)
            throw new InvalidDataException("Mesh source vertex identities are missing.");
        CheckShared(original.SourceVertexIndices, positions, "positions");
        CheckShared(original.SourceVertexIndices, uvs, "UVs");
        if (changedNormals) CheckShared(original.SourceVertexIndices, normals, "normals");
        var originals = new Dictionary<uint, byte[]>();
        var edits = new Dictionary<uint, byte[]>();
        byte[] Writable(NativeVertexStream stream)
        {
            if (!originals.TryGetValue(stream.DataTag, out var prior))
            {
                prior = catalog.Read(stream.DataTag);
                if (!prior.AsSpan().SequenceEqual(stream.Data))
                    throw new InvalidDataException("Native mesh source changed after decoding.");
                originals.Add(stream.DataTag, prior);
            }
            else if (!prior.AsSpan().SequenceEqual(stream.Data))
                throw new InvalidDataException("Shared native streams disagree about their source bytes.");
            if (!edits.TryGetValue(stream.DataTag, out var bytes))
                edits.Add(stream.DataTag, bytes = (byte[])prior.Clone());
            return bytes;
        }
        bool supportedNormals = authored is not null || NativeMeshBasis.IsSupported(catalog, native);
        if (changedNormals && !supportedNormals)
            throw new NotSupportedException("Normal edits need the verified static declaration-123 shader profile.");
        if (changedPositions)
        {
            if (native.VertexStreams.Length == 0) throw new InvalidDataException("Mesh has no position stream.");
            Writable(native.VertexStreams[0]);
            var positionEdits = NativeMeshEdits.ApplyPositions(original,
                new(baseline.Id, positions, normals, uvs, edit.Indices));
            foreach (var pair in positionEdits) edits[pair.Key] = pair.Value;
        }
        if (changedUvs) WriteUvs(original, native, uvs, Writable);
        if (supportedNormals && (changedNormals || changedUvs || changedPositions))
            WriteNormalsAndTangents(original, native, positions, uvs, normals, normalChanged,
                changedPositions || changedUvs, Writable);
        return edits.Where(pair => !pair.Value.AsSpan().SequenceEqual(originals[pair.Key]))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /** Optional attribute arrays must contain one finite tuple per exported vertex. */
    private static void Validate(float[][]? values, int count, int width)
    {
        if (values is null || values.Length != count || values.Any(row => row is null || row.Length != width
            || row.Any(value => !float.IsFinite(value))))
            throw new InvalidDataException("Mesh attribute counts or values are invalid.");
    }

    /** The addon preserves baseline skin arrays exactly after checking Blender's vertex groups. */
    private static void ValidateSkin(BundleGeometry baseline, BundleMeshEdit edit, int count)
    {
        if (baseline.Joints is null && baseline.Weights is null)
        {
            if (edit.Joints is not null || edit.Weights is not null)
                throw new NotSupportedException("Adding skinning to an existing native mesh is unsupported.");
            return;
        }
        if (baseline.Joints is null || baseline.Weights is null || edit.Joints is null || edit.Weights is null
            || baseline.Joints.Length != count || edit.Joints.Length != count)
            throw new InvalidDataException("Skinning identity is missing or its vertex count changed.");
        Validate(baseline.Weights, count, 4); Validate(edit.Weights, count, 4);
        for (int i = 0; i < count; i++)
            if (baseline.Joints[i] is null || edit.Joints[i] is null || baseline.Joints[i].Length != 4
                || !baseline.Joints[i].SequenceEqual(edit.Joints[i])
                || !baseline.Weights[i].SequenceEqual(edit.Weights[i]))
                throw new NotSupportedException("Native joint assignments and skin weights must remain unchanged.");
    }

    private static void CheckShared<T>(uint[] indices, T[] values, string attribute) where T : struct
    {
        var desired = new Dictionary<uint, T>();
        for (int i = 0; i < indices.Length; i++)
        {
            if (desired.TryGetValue(indices[i], out var value) && !value.Equals(values[i]))
                throw new InvalidDataException($"Shared source vertices have conflicting imported {attribute}.");
            desired[indices[i]] = values[i];
        }
    }

    /** Static UVs use X scale for both axes; other verified layouts retain separate X and Y scales. Terrain UVs are TEXCOORD1. */
    private static void WriteUvs(MeshData mesh, NativeMeshData native, Vector2[] values,
        Func<NativeVertexStream, byte[]> writable)
    {
        uint index = native.Kind == NativeMeshKind.Terrain ? 1U : 0U;
        var matches = native.InputElements.Where(x => x.Semantic == "TEXCOORD" && x.SemanticIndex == index).ToArray();
        if (!native.InputLayoutVerified || matches.Length != 1 || matches[0].PerInstance
            || matches[0].Slot < 0 || matches[0].Slot >= native.VertexStreams.Length)
            throw new NotSupportedException($"UV edits need one verified per-vertex TEXCOORD{index} element.");
        var element = matches[0]; var stream = native.VertexStreams[element.Slot];
        bool snorm = element.Format is NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16B16A16SNorm;
        bool half = element.Format == NativeVertexFormat.R16G16Float;
        bool floating = element.Format is NativeVertexFormat.R32G32Float or NativeVertexFormat.R32G32B32A32Float;
        if (!snorm && !half && !floating) throw new NotSupportedException("The UV element format is not supported.");
        var scale = new Vector2(native.UvTransform.X,
            native.Kind == NativeMeshKind.Static ? native.UvTransform.X : native.UvTransform.Y);
        var offset = new Vector2(native.UvTransform.Z, native.UvTransform.W);
        if (!Finite(scale) || !Finite(offset) || scale.X == 0 || scale.Y == 0)
            throw new InvalidDataException("UV transform cannot be inverted.");
        byte[] bytes = writable(stream);
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == mesh.Vertices[i].TexCoord) continue;
            Vector2 uv = (values[i] - offset) / scale;
            if (!Finite(uv)) throw new InvalidDataException("UV exceeds its encoded range.");
            int p = VertexOffset(stream, mesh.SourceVertexIndices[i], element.Offset, floating ? 8 : 4);
            if (snorm) { Snorm(bytes, p, uv.X); Snorm(bytes, p + 2, uv.Y); }
            else if (half)
            {
                Half x = (Half)uv.X, y = (Half)uv.Y;
                if (!Half.IsFinite(x) || !Half.IsFinite(y)) throw new InvalidDataException("UV exceeds half-float range.");
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), BitConverter.HalfToUInt16Bits(x));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p + 2), BitConverter.HalfToUInt16Bits(y));
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p), uv.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p + 4), uv.Y);
            }
        }
    }

    /** Generated preview normals never overwrite authored normals; their fourth lane stays untouched. */
    private static void WriteNormalsAndTangents(MeshData mesh, NativeMeshData native,
        Vector3[] positions, Vector2[] uvs, Vector3[] edits, bool[] changed, bool geometryChanged,
        Func<NativeVertexStream, byte[]> writable)
    {
        if (!float.IsFinite(native.PositionScale.X) || native.PositionScale.X == 0)
            throw new InvalidDataException("Native model scale has no tangent-frame orientation.");
        float scaleSign = MathF.Sign(native.PositionScale.X);
        var stream = native.VertexStreams[1]; byte[] bytes = writable(stream);
        var tangents = new Dictionary<uint, Vector3>(); var bitangents = new Dictionary<uint, Vector3>();
        var affected = new HashSet<uint>();
        for (int i = 0; i < changed.Length; i++)
            if (changed[i]) affected.Add(mesh.SourceVertexIndices[i]);
        for (int triangle = 0; triangle < mesh.Indices.Length; triangle += 3)
        {
            uint a = mesh.Indices[triangle], b = mesh.Indices[triangle + 1], c = mesh.Indices[triangle + 2];
            if (positions[a] != mesh.Vertices[a].Position || positions[b] != mesh.Vertices[b].Position
                || positions[c] != mesh.Vertices[c].Position || uvs[a] != mesh.Vertices[a].TexCoord
                || uvs[b] != mesh.Vertices[b].TexCoord || uvs[c] != mesh.Vertices[c].TexCoord)
            {
                affected.Add(mesh.SourceVertexIndices[a]); affected.Add(mesh.SourceVertexIndices[b]);
                affected.Add(mesh.SourceVertexIndices[c]);
            }
            Vector3 edge1 = positions[b] - positions[a], edge2 = positions[c] - positions[a];
            Vector2 uv1 = uvs[b] - uvs[a], uv2 = uvs[c] - uvs[a];
            float determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (MathF.Abs(determinant) < 1e-12f) continue;
            Vector3 tangent = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
            Vector3 bitangent = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
            if (!Finite(tangent) || !Finite(bitangent)) throw new InvalidDataException("UV derivatives are not finite.");
            foreach (uint local in new[] { a, b, c })
            {
                uint vertex = mesh.SourceVertexIndices[local];
                tangents[vertex] = tangents.GetValueOrDefault(vertex) + tangent;
                bitangents[vertex] = bitangents.GetValueOrDefault(vertex) + bitangent;
            }
        }
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            uint vertex = mesh.SourceVertexIndices[i];
            if (!affected.Contains(vertex)) continue;
            int p = VertexOffset(stream, vertex, 0, 20);
            Vector3 normal = changed[i] ? Normalize(edits[i]) : Normalize(ReadSnorm3(stream.Data, p + 4)) * scaleSign;
            Vector3 tangent = tangents.GetValueOrDefault(vertex);
            tangent -= normal * Vector3.Dot(normal, tangent);
            if (tangent.LengthSquared() <= 1e-12f)
            {
                tangent = ReadSnorm3(stream.Data, p + 12) * scaleSign;
                tangent -= normal * Vector3.Dot(normal, tangent);
                if (tangent.LengthSquared() <= 1e-12f)
                    tangent = Vector3.Cross(MathF.Abs(normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitY, normal);
            }
            tangent = Normalize(tangent);
            var bitangent = bitangents.GetValueOrDefault(vertex);
            float sign = bitangent.LengthSquared() <= 1e-12f ? ReadSnorm(stream.Data, p + 18)
                : Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0 ? -1 : 1;
            if (changed[i]) Snorm3(bytes, p + 4, normal * scaleSign);
            Snorm3(bytes, p + 12, tangent * scaleSign);
            if (geometryChanged) Snorm(bytes, p + 18, sign);
        }
    }

    private static int VertexOffset(NativeVertexStream stream, uint vertex, int offset, int size)
    {
        int p = checked((int)vertex * stream.Stride + offset);
        if (offset < 0 || stream.Stride < offset + size || p < 0 || p > stream.Data.Length - size)
            throw new InvalidDataException("Vertex attribute leaves its source stream.");
        return p;
    }

    private static void Snorm(byte[] bytes, int offset, float value)
    {
        if (!float.IsFinite(value) || value is < -1.000002f or > 1.000002f)
            throw new InvalidDataException("Attribute leaves the original signed-normalized range.");
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset), (short)MathF.Round(Math.Clamp(value, -1, 1) * 32767));
    }
    private static void Snorm3(byte[] bytes, int offset, Vector3 value)
    { Snorm(bytes, offset, value.X); Snorm(bytes, offset + 2, value.Y); Snorm(bytes, offset + 4, value.Z); }
    private static float ReadSnorm(byte[] bytes, int offset) =>
        Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32767f);
    private static Vector3 ReadSnorm3(byte[] bytes, int offset) =>
        new(ReadSnorm(bytes, offset), ReadSnorm(bytes, offset + 2), ReadSnorm(bytes, offset + 4));
    private static Vector3 Normalize(Vector3 value) => Finite(value) && float.IsFinite(value.LengthSquared())
        && value.LengthSquared() > 1e-12f ? Vector3.Normalize(value)
        : throw new InvalidDataException("Normal or tangent cannot be normalized.");
    private static Vector3 V3(float[] value) => new(value[0], value[1], value[2]);
    private static Vector2 V2(float[] value) => new(value[0], value[1]);
    private static bool Close(Vector3 a, Vector3 b) => Close(a.X, b.X) && Close(a.Y, b.Y) && Close(a.Z, b.Z);
    private static bool Close(Vector2 a, Vector2 b) => Close(a.X, b.X) && Close(a.Y, b.Y);
    private static bool Close(float a, float b) => MathF.Abs(a - b) <= ComparisonTolerance;
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

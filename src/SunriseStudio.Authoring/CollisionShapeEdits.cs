using System.Numerics;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Edits that move the vertices of an installed collision shape and keep its topology and size. */
public static class CollisionShapeEdits
{
    /**
     * Moves every vertex of a compressed mesh. Positions follow the decoded shape's vertex order:
     * shared, packed per section, then each custom primitive's decoded vertices.
     * Box primitives take their first four corners; shared slots that hold custom data are ignored.
     * @return A new mesh with the same array sizes, requantized, with refit trees.
     */
    public static CompressedMesh MoveVertices(CompressedMesh installed, IReadOnlyList<Vector3> positions)
    {
        var mesh = Clone(installed);
        int sharedCount = mesh.SharedVertices.Length;
        var packedStart = new int[mesh.Sections.Count];
        int cursor = sharedCount;
        for (int s = 0; s < mesh.Sections.Count; s++)
        {
            packedStart[s] = cursor;
            cursor += mesh.Sections[s].NumPackedVertices;
        }
        int packedEnd = cursor;
        var referenced = new SortedSet<int>();
        var pieces = new List<(CustomPrimitive Piece, int Start)>();
        for (int s = 0; s < mesh.Sections.Count; s++)
        {
            var section = mesh.Sections[s];
            for (int p = 0; p < section.PrimitiveCount; p++)
            {
                uint primitive = mesh.Primitive(section, p);
                var type = CompressedMesh.TypeOf(primitive);
                if (type == CompressedPrimitiveType.Custom)
                {
                    var piece = installed.DecodeCustom(s, p) ?? throw new NotSupportedException("A custom primitive does not decode.");
                    if (piece.Kind == 4) throw new NotSupportedException("Cylinder primitives are not moved.");
                    pieces.Add((piece, cursor));
                    cursor += piece.Vertices.Length;
                }
                else if (type != CompressedPrimitiveType.Empty)
                    foreach (int k in Slots(primitive, type).Where(k => k >= section.PackedCount)) referenced.Add(mesh.SharedNumber(section, k));
            }
        }
        if (cursor != positions.Count) throw new ArgumentException($"Expected {cursor} positions, got {positions.Count}.", nameof(positions));
        var customs = pieces.Select(x =>
        {
            var targets = positions.Skip(x.Start).Take(x.Piece.Vertices.Length).ToArray();
            return (x.Piece, Targets: x.Piece.Kind == 0 ? ExpandBox(targets) : targets);
        }).ToList();

        var points = referenced.Select(i => positions[i])
            .Concat(Enumerable.Range(sharedCount, packedEnd - sharedCount).Select(i => positions[i]))
            .Concat(customs.SelectMany(c => c.Targets)).ToArray();
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        (mesh.DomainMin, mesh.DomainMax) = CompressedMeshEncoder.PaddedDomain(min, max);
        foreach (int i in referenced) mesh.SharedVertices[i] = CompressedMeshEncoder.PackShared(mesh, positions[i]);

        var customAt = customs.ToDictionary(c => (c.Piece.Section, c.Piece.Primitive));
        for (int s = 0; s < mesh.Sections.Count; s++)
        {
            var section = mesh.Sections[s];
            var own = positions.Skip(packedStart[s]).Take(section.NumPackedVertices).ToArray();
            section.Codec = CompressedMeshEncoder.Codec(own);
            for (int k = 0; k < own.Length; k++) mesh.PackedVertices[section.FirstPackedVertex + k] = CompressedMeshEncoder.Pack(section.Codec, own[k]);
            var boxes = new CompoundBox?[section.PrimitiveCount];
            for (int p = 0; p < section.PrimitiveCount; p++)
            {
                uint primitive = mesh.Primitive(section, p);
                var type = CompressedMesh.TypeOf(primitive);
                if (type == CompressedPrimitiveType.Custom) boxes[p] = CompressedMeshEncoder.Box(customAt[(s, p)].Targets);
                else if (type != CompressedPrimitiveType.Empty)
                    boxes[p] = CompressedMeshEncoder.Box(Slots(primitive, type).Select(k => mesh.Vertex(section, k)));
            }
            var domain = boxes.OfType<CompoundBox>().Aggregate((a, b) => a.Union(b));
            section.DomainMin = domain.Min;
            section.DomainMax = domain.Max;
            // An empty slot keeps a point box at the section minimum.
            var leaves = boxes.Select(b => b ?? CompoundBox.From(section.DomainMin, section.DomainMin)).ToArray();
            section.Nodes = MeshTree.Refit(MeshTreeKind.Section, section.Nodes, section.Domain, leaves);
        }
        mesh.TopNodes = MeshTree.Refit(MeshTreeKind.Top, mesh.TopNodes, mesh.Domain, mesh.Sections.Select(x => x.Domain).ToArray());

        // Custom vertices are coded over their leaf box, which only exists after the refit.
        foreach (var (piece, targets) in customs)
        {
            var section = mesh.Sections[piece.Section];
            int leaf = (int)(mesh.Primitive(section, piece.Primitive) >> 8) & 0xFF;
            var box = piece.Codec == 0 ? default : MeshTree.SectionNodeBox(section.Nodes, section.Domain, leaf);
            int stored = piece.Kind == 0 ? 4 : piece.Vertices.Length;
            for (int j = 0; j < stored; j++) WriteCustomVertex(mesh, piece, box, j, targets[j]);
        }
        // Only a problem the installed mesh did not already have is the move's fault.
        var problems = mesh.Check().Except(installed.Check()).ToList();
        if (problems.Count > 0) throw new InvalidDataException($"Moved mesh breaks an invariant: {problems[0]}");
        return mesh;
    }

    /**
     * Moves the vertices of a convex vertices shape and rebuilds its planes and box from them.
     * @return The new packfile. It keeps its size when the plane count does not change.
     */
    public static byte[] MoveConvexVertices(HavokPackfile file, IReadOnlyList<Vector3> positions)
    {
        int root = file.RootOffset;
        if (file.RootClass != CollisionShapes.ConvexVertices) throw new InvalidDataException("Packfile root is not a convex vertices shape.");
        if (file.Pointer(root + 0x78) is not null) throw new NotSupportedException("Convex shape connectivity is not rebuilt.");
        if (positions.Count != file.I32(root + 0x60)) throw new ArgumentException("The vertex count changed.", nameof(positions));
        var prefix = file.Bytes.AsSpan(file.Absolute(root), 0x30).ToArray();
        return CollisionPackfileWriter.WriteConvexVertices(positions, ConvexHull.Planes(positions), file.F32(root + 0x20), prefix);
    }

    /** A deep copy; the arrays of the copy can be written without touching the source. */
    public static CompressedMesh Clone(CompressedMesh mesh) => new()
    {
        Prefix = mesh.Prefix.ToArray(), ConvexRadius = mesh.ConvexRadius, WeldingType = mesh.WeldingType,
        HasPerPrimitiveCollisionFilterInfo = mesh.HasPerPrimitiveCollisionFilterInfo, HasPerPrimitiveUserData = mesh.HasPerPrimitiveUserData,
        FilterPalette = mesh.FilterPalette.ToArray(), UserDataPalette = mesh.UserDataPalette.ToArray(), UserStringCount = mesh.UserStringCount,
        TopNodes = mesh.TopNodes.ToArray(), DomainMin = mesh.DomainMin, DomainMax = mesh.DomainMax,
        DomainMinW = mesh.DomainMinW, DomainMaxW = mesh.DomainMaxW, NumPrimitiveKeys = mesh.NumPrimitiveKeys,
        BitsPerKey = mesh.BitsPerKey, MaxKeyValue = mesh.MaxKeyValue, Primitives = mesh.Primitives.ToArray(),
        SharedIndices = mesh.SharedIndices.ToArray(), PackedVertices = mesh.PackedVertices.ToArray(),
        SharedVertices = mesh.SharedVertices.ToArray(), Runs = mesh.Runs.ToArray(),
        Sections = mesh.Sections.Select(s => new CompressedMeshSection
        {
            Nodes = s.Nodes.ToArray(), DomainMin = s.DomainMin, DomainMax = s.DomainMax, DomainMinW = s.DomainMinW,
            DomainMaxW = s.DomainMaxW, Codec = s.Codec.ToArray(), FirstPackedVertex = s.FirstPackedVertex,
            SharedIndexStart = s.SharedIndexStart, PackedCount = s.PackedCount, FirstPrimitive = s.FirstPrimitive,
            PrimitiveCount = s.PrimitiveCount, FirstRun = s.FirstRun, RunCount = s.RunCount, NumPackedVertices = s.NumPackedVertices,
            NumSharedIndices = s.NumSharedIndices, LeafIndex = s.LeafIndex, Page = s.Page, Flags = s.Flags,
            LayerData = s.LayerData, Unused = s.Unused
        }).ToList()
    };

    private static int[] Slots(uint primitive, CompressedPrimitiveType type) =>
        Enumerable.Range(0, type == CompressedPrimitiveType.Quad ? 4 : 3).Select(i => (int)(primitive >> (8 * i)) & 0xFF).ToArray();

    /** Writes custom vertex j in the primitive's own vertex codec. */
    private static void WriteCustomVertex(CompressedMesh mesh, CustomPrimitive piece, CompoundBox box, int j, Vector3 v)
    {
        switch (piece.Codec)
        {
            case 0:
                mesh.SharedVertices[piece.DataStart + j] = CompressedMeshEncoder.PackShared(mesh, v);
                return;
            case 1:
            {
                uint q = Over(box.MinX, box.MaxX, v.X, 2047) | Over(box.MinY, box.MaxY, v.Y, 2047) << 11 | Over(box.MinZ, box.MaxZ, v.Z, 1023) << 22;
                // Vertex j is u32 j ^ 1 of the u64 array: even j is the high half.
                int at = piece.DataStart + (j >> 1);
                ulong pair = mesh.SharedVertices[at];
                mesh.SharedVertices[at] = (j & 1) == 0 ? (pair & 0xFFFFFFFF) | (ulong)q << 32 : (pair & 0xFFFFFFFF00000000) | q;
                return;
            }
            case 2:
            {
                uint q = Over(box.MinX, box.MaxX, v.X, 31) | Over(box.MinY, box.MaxY, v.Y, 31) << 5 | Over(box.MinZ, box.MaxZ, v.Z, 63) << 10;
                int at = piece.DataStart + (j >> 2), shift = 16 * (3 - (j & 3));
                mesh.SharedVertices[at] = (mesh.SharedVertices[at] & ~(0xFFFFUL << shift)) | (ulong)q << shift;
                return;
            }
            default:
                throw new NotSupportedException("Custom vertex codec 3 is not implemented by the client.");
        }
    }

    private static uint Over(double min, double max, double value, uint steps) => CompressedMeshEncoder.QuantizeRange(value, min, max, steps);

    /** Corners 4 to 7 of a box primitive from its first four, as the client expands them. */
    private static Vector3[] ExpandBox(Vector3[] corners)
    {
        Vector3 c0 = corners[0], c1 = corners[1], c2 = corners[2], c3 = corners[3];
        var e3 = c3 - c0;
        var c4 = c2 - c0 + c1;
        return [c0, c1, c2, c3, c4, c2 + e3, c1 - c0 + c3, c4 + e3];
    }
}

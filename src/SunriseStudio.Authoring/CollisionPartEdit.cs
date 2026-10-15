using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Moves chosen parts inside their shapes, as an import of the same triangles would. */
public static class CollisionPartEdit
{
    // A vertex two placements move to points this far apart cannot take both moves.
    private const float SharedTolerance = 1e-4f;

    /** Why the parts cannot be edited; null when they can. */
    public static string? Refusal(CollisionScene collision, IEnumerable<CollisionPartRef> parts)
    {
        foreach (int index in parts.Select(part => part.Placement).Distinct())
        {
            var placement = collision.Placements[index];
            var piece = collision.Pieces[placement.Piece];
            if (placement.Bone >= 0) return $"{collision.Describe(placement)} is a body on a bone; it follows its skeleton.";
            if (piece.Shape.Class is not (CollisionShapes.CompressedMesh or CollisionShapes.ConvexVertices))
                return $"{collision.Describe(placement)} is a {piece.Kind}; move its instance instead.";
        }
        return null;
    }

    /**
     * The import input with the parts' vertices moved; one input per shape keeps every move.
     * @param transform The edit in world space, applied through each placement's frame.
     */
    public static CollisionImportInput Input(PackageCatalog catalog, CollisionScene collision, IReadOnlyCollection<CollisionPartRef> parts,
        Matrix4x4 transform)
    {
        if (parts.Count == 0) throw new InvalidOperationException("Choose at least one part.");
        if (Refusal(collision, parts) is { } reason) throw new NotSupportedException(reason);
        var shapes = new List<CollisionShapeInput>();
        var sources = new Dictionary<uint, string>();
        foreach (var byShape in parts.GroupBy(part => (collision.Pieces[collision.Placements[part.Placement].Piece].Tag,
            collision.Pieces[collision.Placements[part.Placement].Piece].Offset)))
        {
            var piece = collision.Pieces[collision.Placements[byShape.First().Placement].Piece];
            var shape = piece.Shape;
            var moved = shape.Vertices.ToArray();
            var done = new Dictionary<int, Vector3>();
            foreach (var byPlacement in byShape.GroupBy(part => part.Placement))
            {
                var placement = collision.Placements[byPlacement.Key];
                if (!Matrix4x4.Invert(placement.World, out var toShape)) throw new InvalidDataException("The collision placement is singular.");
                // Shape space to world, the edit in world, then back to shape space.
                var local = placement.World * transform * toShape;
                foreach (int vertex in CollisionParts.Of(shape).Vertices(shape, byPlacement.Select(part => part.Part)))
                {
                    var point = Vector3.Transform(shape.Vertices[vertex], local);
                    if (done.TryGetValue(vertex, out var earlier) && Vector3.Distance(earlier, point) > SharedTolerance)
                        throw new NotSupportedException($"Shape {piece.Tag:X8} is chosen in two placements that would move it two ways.");
                    done[vertex] = moved[vertex] = point;
                }
            }
            shapes.Add(new(piece.Tag, piece.Offset, moved, shape.Indices));
            sources.TryAdd(piece.Tag, BinaryDelta.Hash(catalog.Read(piece.Tag)));
        }
        return new([], shapes, sources);
    }
}

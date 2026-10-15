using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** Which placements a connected selection may reach from its seeds. */
public enum CollisionConnectScope
{
    /** The seeds' own placements. */
    Shape,
    /** Shown placements of the seeds' entities. */
    Entity,
    /** Shown placements that touch the connected set, in any entity. */
    Touching
}

/** Gap is in metres; parts nearer than it are connected. MaxUnits caps the parts examined. */
public sealed record CollisionConnectOptions(float Gap = 0.02f, CollisionConnectScope Scope = CollisionConnectScope.Shape,
    int MaxUnits = 50000);

/** Connected parts sorted by placement and part. Skipped counts placements without triangles. */
public sealed record CollisionConnectResult(IReadOnlyList<CollisionPartRef> Parts, int SkippedPlacements, bool Truncated);

/**
 * Joins parts whose triangles come within the gap plus both convex radii, and convex parts that
 * hold a point of another part. A part inside a concave mesh with no contact stays apart.
 */
public static class CollisionConnectivity
{
    // Triangles this near always touch; it is the part weld step.
    private const float MinimumGap = 1e-4f;
    // Pairs with fewer triangle pairs than this are tested without a grid.
    private const int DirectPairs = 4096;
    // Parts with more triangles than this answer region queries from a grid.
    private const int GridTriangles = 32;
    // A box over more grid cells than this is tested without the grid.
    private const long MaxCells = 512;
    // Grid cells are at least a millimetre, so degenerate triangles cannot make a huge grid.
    private const float MinimumCell = 1e-3f;
    // The face-plane quick reject skips parts with more corners than this.
    private const int SeparationCorners = 3072;

    /** Entity for physics bodies, each its own placement; else Shape, as a mesh can span a map. */
    public static CollisionConnectScope DefaultScope(CollisionScene scene, int placement) =>
        scene.Entities[scene.Placements[placement].Entity].Source == CollisionSource.EntityPhysics
            ? CollisionConnectScope.Entity : CollisionConnectScope.Shape;

    private sealed class Unit
    {
        public required CollisionPartRef Part { get; init; }
        /** World corners, three per triangle. */
        public required Vector3[] Corners { get; init; }
        public required Vector3 Min { get; init; }
        public required Vector3 Max { get; init; }
        /** The convex radius in world units. */
        public required float Pad { get; init; }
        public required bool Convex { get; init; }
        public Vector4[]? Planes { get; set; }
        public (float Cell, Dictionary<(long, long, long), List<int>> Cells, List<int> Wide)? Grid { get; set; }
        public bool Grown { get; set; }
    }

    /**
     * The parts connected to any seed. Seeds outside the scene or without triangles are ignored.
     * @param world The drawn world matrix of a placement.
     * @param shown True for placements the view draws; seeds are used either way.
     */
    public static CollisionConnectResult Connected(CollisionScene scene, IReadOnlyCollection<CollisionPartRef> seeds,
        CollisionConnectOptions options, Func<int, Matrix4x4> world, Func<int, bool> shown)
    {
        if (!float.IsFinite(options.Gap) || options.Gap < 0) throw new ArgumentOutOfRangeException(nameof(options), "The gap must be 0 or more.");
        float gap = MathF.Max(options.Gap, MinimumGap);
        var units = new List<Unit>();
        var unitOf = new Dictionary<CollisionPartRef, int>();
        var parent = new List<int>();
        var placed = new HashSet<int>();
        int skipped = 0;
        bool truncated = false;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        void Add(int index)
        {
            if (truncated || !placed.Add(index)) return;
            var placement = scene.Placements[index];
            var shape = scene.Pieces[placement.Piece].Shape;
            if (shape.TriangleCount == 0) { skipped++; return; }
            var parts = CollisionParts.Of(shape);
            if (units.Count + parts.Triangles.Count > options.MaxUnits) { truncated = true; return; }
            var matrix = world(index);
            float scale = MathF.Max(new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),
                MathF.Max(new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(), new Vector3(matrix.M31, matrix.M32, matrix.M33).Length()));
            bool convexShape = shape.Class is CollisionShapes.ConvexVertices or CollisionShapes.ConvexTranslate or CollisionShapes.Box
                or CollisionShapes.Sphere or CollisionShapes.Capsule or CollisionShapes.Cylinder;
            for (int part = 0; part < parts.Triangles.Count; part++)
            {
                var triangles = parts.Triangles[part];
                var corners = new Vector3[triangles.Length * 3];
                Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                for (int t = 0; t < triangles.Length; t++)
                    for (int corner = 0; corner < 3; corner++)
                    {
                        var point = Vector3.Transform(shape.Vertices[shape.Indices[triangles[t] * 3 + corner]], matrix);
                        corners[t * 3 + corner] = point;
                        min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                    }
                var reference = new CollisionPartRef(index, part);
                unitOf[reference] = units.Count;
                parent.Add(units.Count);
                units.Add(new()
                {
                    Part = reference, Corners = corners, Min = min, Max = max, Pad = shape.ConvexRadius * scale,
                    Convex = convexShape || parts.IsPrimitive(shape, part) && OnePrimitive(shape, triangles)
                });
            }
        }

        var seedPlacements = seeds.Where(seed => seed.Placement >= 0 && seed.Placement < scene.Placements.Count)
            .Select(seed => seed.Placement).Distinct().ToArray();
        foreach (int placement in seedPlacements) Add(placement);
        if (options.Scope == CollisionConnectScope.Entity)
        {
            var entities = seedPlacements.Select(placement => scene.Placements[placement].Entity).ToHashSet();
            for (int i = 0; i < scene.Placements.Count; i++)
                if (entities.Contains(scene.Placements[i].Entity) && shown(i)) Add(i);
        }
        var seedUnits = seeds.Where(unitOf.ContainsKey).Select(seed => unitOf[seed]).ToArray();
        (int Index, Vector3 Min, Vector3 Max)[]? boxes = null;
        float widest = 0;
        int joined = 0;
        while (true)
        {
            Join(units, joined, gap, Find, parent);
            joined = units.Count;
            if (options.Scope != CollisionConnectScope.Touching || truncated) break;
            if (boxes is null)
            {
                boxes = Enumerable.Range(0, scene.Placements.Count).Where(shown).Select(index =>
                {
                    var placement = scene.Placements[index];
                    var (min, max) = placement.Bone < 0 ? (placement.BoundsMin, placement.BoundsMax)
                        : CollisionScene.WorldBounds(scene.Pieces[placement.Piece].Shape, world(index));
                    return (index, min, max);
                }).Where(box => box.min.X <= box.max.X).OrderBy(box => box.min.X).ToArray();
                widest = boxes.Length == 0 ? 0 : boxes.Max(box => box.Max.X - box.Min.X);
            }
            var roots = seedUnits.Select(Find).ToHashSet();
            int before = placed.Count;
            for (int u = 0; u < units.Count; u++)
            {
                var unit = units[u];
                if (unit.Grown || !roots.Contains(Find(u))) continue;
                unit.Grown = true;
                var grow = new Vector3(gap + unit.Pad);
                Vector3 low = unit.Min - grow, high = unit.Max + grow;
                int start = LowerBound(boxes, low.X - widest);
                for (int b = start; b < boxes.Length && boxes[b].Min.X <= high.X; b++)
                    if (!placed.Contains(boxes[b].Index) && Overlaps(low, high, boxes[b].Min, boxes[b].Max)) Add(boxes[b].Index);
            }
            if (placed.Count == before) break;
        }
        var chosen = seedUnits.Select(Find).ToHashSet();
        var result = Enumerable.Range(0, units.Count).Where(u => chosen.Contains(Find(u))).Select(u => units[u].Part)
            .OrderBy(part => part.Placement).ThenBy(part => part.Part).ToArray();
        return new(result, skipped, truncated);
    }

    /** Unions every touching pair in which at least one unit is at or after first. */
    private static void Join(List<Unit> units, int first, float gap, Func<int, int> find, List<int> parent)
    {
        if (first >= units.Count) return;
        // Boxes grown by half the gap and the radius overlap when the parts may be within the gap.
        float Low(int u) => units[u].Min.X - units[u].Pad - gap / 2;
        var order = Enumerable.Range(0, units.Count).OrderBy(Low).ToArray();
        for (int i = 0; i < order.Length; i++)
        {
            var a = units[order[i]];
            var growA = new Vector3(a.Pad + gap / 2);
            for (int j = i + 1; j < order.Length && Low(order[j]) <= a.Max.X + growA.X; j++)
            {
                if (order[i] < first && order[j] < first) continue;
                var b = units[order[j]];
                var growB = new Vector3(b.Pad + gap / 2);
                if (!Overlaps(a.Min - growA, a.Max + growA, b.Min - growB, b.Max + growB)) continue;
                int rootA = find(order[i]), rootB = find(order[j]);
                if (rootA != rootB && Touch(a, b, gap + a.Pad + b.Pad)) parent[rootA] = rootB;
            }
        }
    }

    private static bool Touch(Unit a, Unit b, float limit)
    {
        if (a.Convex && Beyond(a, b, limit) || b.Convex && Beyond(b, a, limit)) return false;
        var reach = new Vector3(limit);
        Vector3 low = Vector3.Max(a.Min, b.Min) - reach, high = Vector3.Min(a.Max, b.Max) + reach;
        if (Query(a, low, high) is { Count: > 0 } near && Query(b, low, high) is { Count: > 0 } other)
        {
            if (near.Count > other.Count) (a, near, b, other) = (b, other, a, near);
            if ((long)near.Count * other.Count <= DirectPairs)
            {
                foreach (int s in near)
                    foreach (int t in other)
                        if (Within(a, s, b, t, limit)) return true;
            }
            else
                foreach (int s in near)
                {
                    var (min, max) = Box(a, s);
                    foreach (int t in Query(b, min - reach, max + reach))
                        if (Within(a, s, b, t, limit)) return true;
                }
        }
        return a.Convex && Inside(a, b.Corners[0], limit) || b.Convex && Inside(b, a.Corners[0], limit);
    }

    /** Triangles of the unit whose box meets the region; a large unit answers from its grid. */
    private static List<int> Query(Unit unit, Vector3 low, Vector3 high)
    {
        var found = new List<int>();
        int count = unit.Corners.Length / 3;
        if (count > GridTriangles)
        {
            var (cell, cells, wide) = unit.Grid ??= BuildGrid(unit);
            if (Cells(low, high, cell) is { } keys)
            {
                foreach (int t in wide)
                    if (Overlaps(Box(unit, t), low, high)) found.Add(t);
                var seen = new HashSet<int>();
                foreach (var key in keys)
                    if (cells.TryGetValue(key, out var list))
                        foreach (int t in list)
                            if (seen.Add(t) && Overlaps(Box(unit, t), low, high)) found.Add(t);
                return found;
            }
        }
        for (int t = 0; t < count; t++)
            if (Overlaps(Box(unit, t), low, high)) found.Add(t);
        return found;
    }

    /** Cells about one triangle wide; a triangle over more than MaxCells cells is listed apart. */
    private static (float Cell, Dictionary<(long, long, long), List<int>> Cells, List<int> Wide) BuildGrid(Unit unit)
    {
        int count = unit.Corners.Length / 3;
        float cell = 0;
        for (int t = 0; t < count; t++)
        {
            var (min, max) = Box(unit, t);
            cell += MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        }
        cell = MathF.Max(cell / count, MinimumCell);
        var cells = new Dictionary<(long, long, long), List<int>>();
        var wide = new List<int>();
        for (int t = 0; t < count; t++)
        {
            var (min, max) = Box(unit, t);
            if (Cells(min, max, cell) is not { } keys) { wide.Add(t); continue; }
            foreach (var key in keys)
            {
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
                list.Add(t);
            }
        }
        return (cell, cells, wide);
    }

    /** The grid cells a box covers; null when it covers more than MaxCells. */
    private static List<(long, long, long)>? Cells(Vector3 min, Vector3 max, float cell)
    {
        long x0 = (long)MathF.Floor(min.X / cell), y0 = (long)MathF.Floor(min.Y / cell), z0 = (long)MathF.Floor(min.Z / cell);
        long x1 = (long)MathF.Floor(max.X / cell), y1 = (long)MathF.Floor(max.Y / cell), z1 = (long)MathF.Floor(max.Z / cell);
        if ((x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > MaxCells) return null;
        var keys = new List<(long, long, long)>();
        for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
                for (long z = z0; z <= z1; z++) keys.Add((x, y, z));
        return keys;
    }

    private static bool Within(Unit a, int s, Unit b, int t, float limit)
    {
        var (minA, maxA) = Box(a, s);
        var (minB, maxB) = Box(b, t);
        if (!Overlaps(minA - new Vector3(limit), maxA + new Vector3(limit), minB, maxB)) return false;
        return TriangleDistance.Distance(a.Corners[s * 3], a.Corners[s * 3 + 1], a.Corners[s * 3 + 2],
            b.Corners[t * 3], b.Corners[t * 3 + 1], b.Corners[t * 3 + 2]) <= limit;
    }

    /** True when the point lies within limit of the inside of the unit's hull. */
    private static bool Inside(Unit unit, Vector3 point, float limit)
    {
        if (!Overlaps(unit.Min - new Vector3(limit), unit.Max + new Vector3(limit), point, point)) return false;
        unit.Planes ??= FacePlanes(unit);
        return unit.Planes.Length > 0 && unit.Planes.All(plane => Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), point) + plane.W <= limit);
    }

    /** True when every triangle has one shape key: one custom primitive's hull, which is convex. */
    private static bool OnePrimitive(CollisionShape shape, int[] triangles) => shape.TriangleKeys.Length >= shape.TriangleCount
        && triangles.All(triangle => shape.TriangleKeys[triangle] == shape.TriangleKeys[triangles[0]]);

    /** True when one face plane of the convex unit has all the other's corners beyond limit. */
    private static bool Beyond(Unit convex, Unit other, float limit)
    {
        if (other.Corners.Length > SeparationCorners) return false;
        convex.Planes ??= FacePlanes(convex);
        foreach (var plane in convex.Planes)
        {
            var normal = new Vector3(plane.X, plane.Y, plane.Z);
            bool all = true;
            foreach (var corner in other.Corners)
                if (Vector3.Dot(normal, corner) + plane.W <= limit) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    /** A convex part's face planes, turned away from its centre whatever the winding. */
    private static Vector4[] FacePlanes(Unit unit)
    {
        // The mean of the corners lies inside a convex part; the box centre need not.
        var center = unit.Corners.Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / unit.Corners.Length;
        var planes = new List<Vector4>();
        for (int t = 0; t < unit.Corners.Length; t += 3)
        {
            var normal = Vector3.Cross(unit.Corners[t + 1] - unit.Corners[t], unit.Corners[t + 2] - unit.Corners[t]);
            if (!(normal.LengthSquared() > 1e-20f)) continue;
            normal = Vector3.Normalize(normal);
            float offset = -Vector3.Dot(normal, unit.Corners[t]);
            planes.Add(Vector3.Dot(normal, center) + offset > 0 ? new(-normal, -offset) : new(normal, offset));
        }
        return planes.ToArray();
    }

    private static (Vector3 Min, Vector3 Max) Box(Unit unit, int triangle)
    {
        Vector3 p = unit.Corners[triangle * 3], q = unit.Corners[triangle * 3 + 1], r = unit.Corners[triangle * 3 + 2];
        return (Vector3.Min(p, Vector3.Min(q, r)), Vector3.Max(p, Vector3.Max(q, r)));
    }

    private static bool Overlaps((Vector3 Min, Vector3 Max) box, Vector3 min, Vector3 max) => Overlaps(box.Min, box.Max, min, max);

    private static bool Overlaps(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB) =>
        minA.X <= maxB.X && minB.X <= maxA.X && minA.Y <= maxB.Y && minB.Y <= maxA.Y && minA.Z <= maxB.Z && minB.Z <= maxA.Z;

    /** First box whose minimum X is at least x. */
    private static int LowerBound((int Index, Vector3 Min, Vector3 Max)[] boxes, float x)
    {
        int low = 0, high = boxes.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (boxes[middle].Min.X < x) low = middle + 1; else high = middle;
        }
        return low;
    }
}

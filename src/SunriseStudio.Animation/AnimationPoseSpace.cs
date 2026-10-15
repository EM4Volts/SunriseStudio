using System.Numerics;

namespace SunriseStudio.Animation;

/** Pose-space kinds stored in the clip; each point is the clip frame with the same index. */
public enum AnimationPoseSpaceKind : sbyte
{
    Line = 1,
    Disk = 2,
    Plane = 3,
    Sphere = 4
}

/** One clip frame picked by a pose space, with its blend weight. */
public readonly record struct AnimationPoseSample(int Frame, float Weight);

/**
 * A clip's authored parameter space: one point per frame and the segments or triangles between them.
 * Selection reproduces the game's walk, so edge and fallback cases pick the same frames.
 */
public sealed class AnimationPoseSpace
{
    private const float Epsilon = 1e-5f, Inside = -1e-4f, Snap = 1e-4f;

    public AnimationPoseSpaceKind Kind { get; }
    public IReadOnlyList<Vector2> Points { get; }

    /** Line and disk rows are {v0, v1}; plane and sphere rows are {v0, v1, v2, n0, n1, n2}. */
    public IReadOnlyList<sbyte[]> Simplices { get; }

    public AnimationPoseSpace(AnimationPoseSpaceKind kind, Vector2[] points, sbyte[][] simplices)
    {
        if (!Enum.IsDefined(kind)) throw new NotSupportedException($"Pose space kind {(int)kind} is not decoded.");
        int width = kind is AnimationPoseSpaceKind.Line or AnimationPoseSpaceKind.Disk ? 2 : 3;
        foreach (var row in simplices)
            if (row.Length < width || row.Take(width).Any(v => v < 0 || v >= points.Length)
                || (width == 3 && row.Skip(3).Take(3).Any(n => n < -1 || n >= simplices.Length)))
                throw new InvalidDataException("Pose space row references a missing point or neighbour.");
        Kind = kind;
        Points = Array.AsReadOnly(points);
        Simplices = Array.AsReadOnly(simplices);
    }

    /** Smallest and largest authored value on each axis, for building an input range. */
    public (Vector2 Minimum, Vector2 Maximum) Bounds =>
        Points.Count == 0 ? (Vector2.Zero, Vector2.Zero)
            : (Points.Aggregate(Vector2.Min), Points.Aggregate(Vector2.Max));

    /**
     * Picks up to three frames for the input, heaviest first; an empty result means no frame matched.
     * @param x First parameter; the yaw for a sphere.
     * @param y Second parameter; the pitch for a sphere. A disk reads only x.
     */
    public IReadOnlyList<AnimationPoseSample> Select(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        var picked = Kind switch
        {
            AnimationPoseSpaceKind.Line => SelectLine(x, y),
            AnimationPoseSpaceKind.Disk => SelectDisk(x),
            AnimationPoseSpaceKind.Plane => SelectPlane(x, y),
            _ => SelectSphere(x, y)
        };
        return picked.Where(sample => sample.Frame >= 0)
            .Select((sample, order) => (sample, order))
            .OrderByDescending(item => item.sample.Weight).ThenBy(item => item.order)
            .Select(item => item.sample).ToArray();
    }

    /** Projects the input onto the line and blends the two points of the segment it lands on. */
    private AnimationPoseSample[] SelectLine(float x, float y)
    {
        if (Points.Count == 1) return [new(0, 1)];
        if (Points.Count < 2 || Simplices.Count == 0) return [];
        var p = new Vector2(Snapped(x), Snapped(y));
        Vector2 a = Points[Simplices[0][0]], b = Points[Simplices[^1][1]];
        var q = ProjectOnSegment(p, a, b, clamp: true, out _);
        float reach = Vector2.DistanceSquared(q, a);
        foreach (var row in Simplices)
        {
            Vector2 p0 = Points[row[0]], p1 = Points[row[1]];
            if (Vector2.DistanceSquared(p0, a) > reach || reach > Vector2.DistanceSquared(p1, a)) continue;
            float length = Vector2.Distance(p0, p1);
            float f = length <= Epsilon ? 0 : Math.Clamp(Vector2.Distance(q, p0) / length, 0, 1);
            return [new(row[0], 1 - f), new(row[1], f)];
        }
        return [];
    }

    /** Blends the two points around the input angle; the last segment wraps around the circle. */
    private AnimationPoseSample[] SelectDisk(float x)
    {
        if (Points.Count == 1) return [new(0, 1)];
        if (Points.Count < 2 || Simplices.Count == 0) return [];
        var p = new Vector2(x, 0);
        Vector2 a = Points[Simplices[0][0]] with { Y = 0 }, b = Points[Simplices[^1][0]] with { Y = 0 };
        var q = ProjectOnSegment(p, a, b, clamp: false, out float t);
        int index = -1;
        if (t < 0 || t > 1) index = Simplices.Count - 1;
        else
        {
            float reach = Vector2.DistanceSquared(q, a);
            for (int i = 0; i < Simplices.Count - 1 && index < 0; i++)
            {
                float first = Vector2.DistanceSquared(Points[Simplices[i][0]] with { Y = 0 }, a);
                float second = Vector2.DistanceSquared(Points[Simplices[i][1]] with { Y = 0 }, a);
                if (first <= reach && reach <= second) index = i;
            }
            if (index < 0) return [];
        }
        var row = Simplices[index];
        Vector2 p0 = Points[row[0]] with { Y = 0 }, p1 = Points[row[1]] with { Y = 0 };
        float length = Vector2.Distance(p0, p1);
        // The wrap segment spans the rest of the circle.
        if (index == Simplices.Count - 1) length = 2 * MathF.PI - length;
        float distance = Vector2.Distance(q, p0);
        if (distance > MathF.PI) distance = 2 * MathF.PI - distance;
        float f = length <= Epsilon ? 0 : Math.Clamp(distance / length, 0, 1);
        return [new(row[0], Math.Clamp(1 - f, 0, 1)), new(row[1], f)];
    }

    /** Walks neighbouring triangles toward the input; leaving the mesh falls back to Boundary. */
    private AnimationPoseSample[] SelectPlane(float x, float y)
    {
        if (Simplices.Count == 0) return [];
        var p = new Vector2(Snapped(x), Snapped(y));
        int current = Simplices.Count / 2;
        for (int step = 0; step < Simplices.Count; step++)
        {
            var row = Simplices[current];
            Vector2 a = Points[row[0]], b = Points[row[1]], c = Points[row[2]];
            float area = Area(a, b, c);
            if (area == 0) return [new(row[0], 0), new(row[1], 0), new(row[2], 0)];
            float wb = Area(a, p, c) / area, wc = Area(a, b, p) / area, wa = 1 - wb - wc;
            if (wa >= Inside && wb >= Inside && wc >= Inside)
                return [new(row[0], Math.Clamp(wa, 0, 1)), new(row[1], Math.Clamp(wb, 0, 1)),
                    new(row[2], Math.Clamp(wc, 0, 1))];
            // Leave across the edge facing the most negative weight.
            int slot = wb < wc ? (wb < wa ? 2 : 1) : (wa < wc ? 1 : 0);
            if (row[3 + slot] < 0) return Boundary(p);
            current = row[3 + slot];
        }
        return [];
    }

    /** Outside the triangles, the nearest boundary edge carries the input. */
    private AnimationPoseSample[] Boundary(Vector2 p)
    {
        float best = float.PositiveInfinity;
        AnimationPoseSample[] result = [];
        foreach (var row in Simplices)
            for (int slot = 0; slot < 3; slot++)
            {
                if (row[3 + slot] != -1) continue;
                int first = row[slot], second = row[(slot + 1) % 3];
                var q = ProjectOnSegment(p, Points[first], Points[second], clamp: true, out float u);
                float distance = Vector2.Distance(p, q);
                if (distance >= best) continue;
                best = distance;
                var weights = new float[3];
                weights[slot] = 1 - u;
                weights[(slot + 1) % 3] = u;
                result = [new(row[0], weights[0]), new(row[1], weights[1]), new(row[2], weights[2])];
            }
        return result;
    }

    /** Casts the input direction at the triangles; a miss blends the nearest hit's two heaviest points. */
    private AnimationPoseSample[] SelectSphere(float yaw, float pitch)
    {
        if (Points.Count < 4) return [];
        var ray = 2 * Direction(yaw, pitch);
        float bestT = float.PositiveInfinity;
        (sbyte[] Row, Vector3 Hit, Vector3 Weights)? best = null;
        foreach (var row in Simplices)
        {
            Vector3 a = Direction(Points[row[0]]), b = Direction(Points[row[1]]), c = Direction(Points[row[2]]);
            var cross = Vector3.Cross(b - a, c - a);
            float length = cross.Length();
            var normal = length <= Epsilon ? Vector3.Zero : cross / length;
            float offset = length <= Epsilon ? 0 : -Vector3.Dot(normal, a);
            float facing = Vector3.Dot(normal, ray);
            if (MathF.Abs(offset) <= Epsilon || MathF.Abs(facing) <= Epsilon) continue;
            float t = -offset / facing;
            if (t <= 0) continue;
            var hit = t * ray;
            var weights = Barycentric(hit, a, b, c);
            if (weights.X >= Inside && weights.Y >= Inside && weights.Z >= Inside)
                return [new(row[0], Math.Clamp(weights.X, 0, 1)), new(row[1], Math.Clamp(weights.Y, 0, 1)),
                    new(row[2], Math.Clamp(weights.Z, 0, 1))];
            if (t < bestT) { bestT = t; best = (row, hit, weights); }
        }
        if (best is not { } fallback) return [];
        float[] w = [fallback.Weights.X, fallback.Weights.Y, fallback.Weights.Z];
        int[] order = [.. Enumerable.Range(0, 3).OrderByDescending(i => w[i])];
        Vector3 l0 = Direction(Points[fallback.Row[order[0]]]), l1 = Direction(Points[fallback.Row[order[1]]]);
        var span = l1 - l0;
        float u = span.LengthSquared() <= 0 ? 0
            : Math.Clamp(Vector3.Dot(fallback.Hit - l0, span) / span.LengthSquared(), 0, 1);
        return [new(fallback.Row[order[0]], 1 - u), new(fallback.Row[order[1]], u),
            new(fallback.Row[order[2]], 0)];
    }

    /** Yaw turns about +Z from +X; positive pitch points down. */
    public static Vector3 Direction(float yaw, float pitch) =>
        new(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch));

    private static Vector3 Direction(Vector2 point) => Direction(point.X, point.Y);

    private static float Snapped(float value) => MathF.Abs(value) < Snap ? 0 : value;

    private static float Area(Vector2 a, Vector2 b, Vector2 c) =>
        .5f * ((b.Y - c.Y) * (a.X - c.X) - (b.X - c.X) * (a.Y - c.Y));

    private static Vector2 ProjectOnSegment(Vector2 p, Vector2 a, Vector2 b, bool clamp, out float t)
    {
        var span = b - a;
        float length = span.LengthSquared();
        t = MathF.Sqrt(length) <= Epsilon ? 0 : Vector2.Dot(p - a, span) / length;
        if (clamp) t = Math.Clamp(t, 0, 1);
        return a + span * t;
    }

    /** Degenerate triangles give zero weights. */
    private static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float denominator = d00 * d11 - d01 * d01;
        if (denominator == 0) return Vector3.Zero;
        float v = (d11 * d20 - d01 * d21) / denominator, w = (d00 * d21 - d01 * d20) / denominator;
        return new(1 - v - w, v, w);
    }
}

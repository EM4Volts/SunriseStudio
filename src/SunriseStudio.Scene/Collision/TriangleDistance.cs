using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** Closest distance between two triangles; touching or crossing triangles are 0 apart. */
internal static class TriangleDistance
{
    /**
     * Two triangles that do not cross are nearest at a corner and a face, or at two edges.
     * @return 0 when the triangles cross or touch.
     */
    public static float Distance(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2)
    {
        if (Crosses(a0, a1, b0, b1, b2) || Crosses(a1, a2, b0, b1, b2) || Crosses(a2, a0, b0, b1, b2)
            || Crosses(b0, b1, a0, a1, a2) || Crosses(b1, b2, a0, a1, a2) || Crosses(b2, b0, a0, a1, a2)) return 0;
        float best = MathF.Min(MathF.Min(PointTriangle(a0, b0, b1, b2), PointTriangle(a1, b0, b1, b2)),
            MathF.Min(PointTriangle(a2, b0, b1, b2), PointTriangle(b0, a0, a1, a2)));
        best = MathF.Min(best, MathF.Min(PointTriangle(b1, a0, a1, a2), PointTriangle(b2, a0, a1, a2)));
        Span<(Vector3, Vector3)> a = [(a0, a1), (a1, a2), (a2, a0)], b = [(b0, b1), (b1, b2), (b2, b0)];
        foreach (var (p, q) in a)
            foreach (var (r, s) in b) best = MathF.Min(best, SegmentSegment(p, q, r, s));
        return best;
    }

    /** True when segment pq crosses triangle abc; one in its plane is left to the distances. */
    private static bool Crosses(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
    {
        double dp = Orient(a, b, c, p), dq = Orient(a, b, c, q);
        if (dp > 0 && dq > 0 || dp < 0 && dq < 0 || dp == 0 && dq == 0) return false;
        double s1 = Orient(p, q, a, b), s2 = Orient(p, q, b, c), s3 = Orient(p, q, c, a);
        return s1 >= 0 && s2 >= 0 && s3 >= 0 || s1 <= 0 && s2 <= 0 && s3 <= 0;
    }

    // Signed volume of the tetrahedron abcd, in double so the side tests agree with each other.
    private static double Orient(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        double bx = b.X - a.X, by = b.Y - a.Y, bz = b.Z - a.Z;
        double cx = c.X - a.X, cy = c.Y - a.Y, cz = c.Z - a.Z;
        double dx = d.X - a.X, dy = d.Y - a.Y, dz = d.Z - a.Z;
        return dx * (by * cz - bz * cy) + dy * (bz * cx - bx * cz) + dz * (bx * cy - by * cx);
    }

    /** Distance from p to the nearest point of triangle abc, by the triangle's Voronoi regions. */
    private static float PointTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + d1 / (d1 - d3) * ab);
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + d2 / (d2 - d6) * ac);
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Vector3.Distance(p, b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b));
        float sum = va + vb + vc;
        // A triangle with no area has only its edges, which the edge tests cover.
        if (!(MathF.Abs(sum) > 1e-30f)) return MathF.Min(Vector3.Distance(p, a), MathF.Min(Vector3.Distance(p, b), Vector3.Distance(p, c)));
        return Vector3.Distance(p, a + ab * (vb / sum) + ac * (vc / sum));
    }

    /** Distance between the nearest points of segments pq and rs. */
    private static float SegmentSegment(Vector3 p, Vector3 q, Vector3 r, Vector3 s)
    {
        Vector3 d1 = q - p, d2 = s - r, between = p - r;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, between);
        float t, u;
        const float tiny = 1e-20f;
        if (a <= tiny && e <= tiny) return Vector3.Distance(p, r);
        if (a <= tiny) { t = 0; u = Math.Clamp(f / e, 0, 1); }
        else
        {
            float c = Vector3.Dot(d1, between);
            if (e <= tiny) { u = 0; t = Math.Clamp(-c / a, 0, 1); }
            else
            {
                float b = Vector3.Dot(d1, d2), denominator = a * e - b * b;
                t = denominator > tiny ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
                u = (b * t + f) / e;
                if (u < 0) { u = 0; t = Math.Clamp(-c / a, 0, 1); }
                else if (u > 1) { u = 1; t = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return Vector3.Distance(p + d1 * t, r + d2 * u);
    }
}

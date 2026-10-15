using System.Numerics;

namespace SunriseStudio.Rendering;

/**
 * One prepared decorator level-of-detail curve: rows 0 to 5 and the highest index byte of 0x7FF742DFEDE0.
 * Rows 0 and 1 drive the stream 2 fade; rows 2 to 5 drive the LOD index.
 */
public readonly record struct SpeedTreeLodCurve(Vector4 Row0, Vector4 Row1, Vector4 Row2, Vector4 Row3, Vector4 Row4,
    Vector4 Row5, int MaxIndex);

/** The CPU level-of-detail math of 0x7FF742F11570; every lane order follows the native SSE code. */
public static class SpeedTreeLod
{
    // Comparisons treat values at or below this as zero.
    private static readonly float Epsilon = BitConverter.UInt32BitsToSingle(0x38D1B717);
    private static readonly Vector4 LaneIndex = new(0, 1, 2, 3), LaneNext = new(1, 2, 3, 4);
    private static readonly float Sixth = BitConverter.UInt32BitsToSingle(0x3E2AAAAB);
    /** foliage_detail rows of 0x7FF743903A20: a distance multiplier, then the caps per extent. */
    private static readonly float[] QualityMultipliers = [0.5f, 1f, BitConverter.UInt32BitsToSingle(0x3FE66666)];
    private static readonly Vector4 QualityCaps = new(1000, 2000, 3000, 4000);
    /** The game's default foliage_detail; 0x7FF742DE12A0 returns it and 0x7FF742DE12F0 accepts 0 to 2. */
    public const int DefaultFoliageDetail = 1;

    /**
     * The curve one instance uses: 0x7FF742DFF050, 0x7FF742E01330 and 0x7FF742E015F0, then 0x7FF742DFEDE0.
     * @param lastLod LOD count minus one, row 0 Y of the type entry.
     * @param weight Row 0 W of the type entry, the first float of the type's +0x10 second array.
     * @param distances The type's +0x10 first array row.
     * @param zoom One unless a zoomed view scales the distances.
     * @param scale The instance scale, record W.
     * @param extent The length of the instance cell's bounds diagonal.
     */
    public static SpeedTreeLodCurve ForInstance(float lastLod, float weight, Vector4 distances, float zoom, float scale,
        float extent, int foliageDetail = DefaultFoliageDetail)
    {
        if ((uint)foliageDetail > 2) throw new ArgumentOutOfRangeException(nameof(foliageDetail));
        var row1 = new Vector4(scale) * (distances * zoom);
        row1 = Vector4.Min(row1 * QualityMultipliers[foliageDetail], new Vector4(extent) * QualityCaps);
        return Prepare(new(0, lastLod, 0, weight), row1);
    }

    /**
     * A ground band's curve, as 0x7FF742F11020 builds it: the distances times the foliage multiplier;
     * zoom scales only the far fade start, and the far fade keeps its width.
     */
    public static SpeedTreeLodCurve ForBand(float lodCount, float weight, Vector4 distances, float zoom,
        int foliageDetail = DefaultFoliageDetail)
    {
        if ((uint)foliageDetail > 2) throw new ArgumentOutOfRangeException(nameof(foliageDetail));
        var scaled = new Vector4(QualityMultipliers[foliageDetail]) * distances;
        var zoomed = scaled * zoom;
        var row1 = new Vector4(scaled.X, scaled.Y, zoomed.Z, (scaled.W - scaled.Z) + zoomed.Z);
        return Prepare(new(0, lodCount, 0, weight), row1);
    }

    /** 0x7FF742DFEDE0 with the quarter-width flag clear. */
    public static SpeedTreeLodCurve Prepare(Vector4 row0, Vector4 row1)
    {
        float last = row0.Y;
        float inverse = MathF.Abs(last) <= Epsilon ? 1f : 1f / last;
        float nearWidth = row1.Y - row1.X, farWidth = row1.W - row1.Z;
        float step = inverse * nearWidth;
        float weighted = step * row0.W;
        var prepared0 = new Vector4(1f / nearWidth, last, 1f / farWidth, row0.W);
        Span<bool> valid = stackalloc bool[4];
        for (int i = 0; i < 4; i++) valid[i] = LaneIndex[i] <= last;
        Vector4 row2 = default, row3 = default, row4 = default, row5 = default;
        for (int i = 0; i < 4; i++)
        {
            bool lastLane = valid[i] ^ (i < 3 && valid[i + 1]);
            row2[i] = lastLane ? row1.Z : valid[i] ? LaneNext[i] * step + row1.X - weighted * Sixth : 0;
            row3[i] = lastLane ? 3f / farWidth : valid[i] ? 3f / weighted : 0;
            row4[i] = valid[i] ? 1 : 0;
            row5[i] = valid[i] ? 4 : 0;
        }
        return new(prepared0, row1, row2, row3, row4, row5, 4 * ((int)last + 1));
    }

    /** 0x7FF742DFF0A0: one near the camera, falling to zero, then to minus one far away. */
    public static float Fade(in SpeedTreeLodCurve curve, float distance)
    {
        float x = Math.Clamp(1 - (distance - curve.Row1.X) * curve.Row0.X, 0, 1);
        float z = Math.Clamp(0 - (distance - curve.Row1.Z) * curve.Row0.Z, -1, 0);
        return z + x;
    }

    /** 0x7FF742DFF170: the fraction of fade times the LOD count, scaled back; whole steps give one. */
    public static float Blend(in SpeedTreeLodCurve curve, float fade)
    {
        float count = curve.Row0.Y;
        if (count <= Epsilon) return 1;
        if (fade <= Epsilon) return 0;
        float scaled = fade * count;
        float fraction = MathF.Abs(scaled) < 8388608f ? scaled - MathF.Floor(scaled) : 0;
        return fraction <= Epsilon ? 1 : fraction * count;
    }

    /** The LOD byte of 0x7FF742F11570: four steps per level, rounded to nearest even, capped at MaxIndex. */
    public static int Index(in SpeedTreeLodCurve curve, float distance)
    {
        var v = Vector4.Min(Vector4.Max((new Vector4(distance) - curve.Row2) * curve.Row3, Vector4.Zero), curve.Row5) * curve.Row4;
        float sum = (v.Y + v.W) + (v.Z + v.X);
        int index = (int)MathF.Round(sum, MidpointRounding.ToEven);
        return Math.Min(index, curve.MaxIndex);
    }

    /** The window 0x7FF742DFE1D0 draws LOD level k with outside the shadow stage: 4k - 3 to 4k + 3. */
    public static bool Draws(int lodLevel, int index) =>
        index >= Math.Max(0, 4 * lodLevel - 3) && index <= Math.Min(16, 4 * lodLevel + 3);

    // Shadow window offsets of 0x7FF742DFE1D0 per level: the bytes of 0x01020000 below, 0x02020201 above.
    private static readonly int[] ShadowBelow = [0, 0, 2, 1], ShadowAbove = [1, 2, 2, 2];

    /**
     * The window 0x7FF742DFE1D0 draws LOD level k with in shadow_generate: 4k - below[k] to 4k + above[k].
     * Level 0 draws 0 to 1, level 1 4 to 6, level 2 6 to 10, level 3 11 to 14. The table has no level 4.
     */
    public static bool DrawsShadow(int lodLevel, int index) =>
        (uint)lodLevel < (uint)ShadowBelow.Length
        && index >= Math.Max(0, 4 * lodLevel - ShadowBelow[lodLevel]) && index <= Math.Min(16, 4 * lodLevel + ShadowAbove[lodLevel]);
}

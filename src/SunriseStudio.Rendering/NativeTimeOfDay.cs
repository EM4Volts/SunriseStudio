using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Rendering;

/** One class-0x80808EF3 direction curve: evenly spaced float4 keys over the day, sampled by 0x7FF74271FFC0. */
public sealed class NativeDirectionCurve
{
    private readonly Vector4[] keys;
    public int Count => keys.Length;

    private NativeDirectionCurve(Vector4[] keys) => this.keys = keys;

    /** The kind byte sits behind the relative link at +0x10; the keys are the typed array at +0x30. */
    public static NativeDirectionCurve Parse(ReadOnlySpan<byte> tag)
    {
        long kindOffset = 0x10 + BinaryPrimitives.ReadInt64LittleEndian(tag[0x10..]);
        if (kindOffset < 0 || kindOffset >= tag.Length || tag[(int)kindOffset] != 0)
            throw new NotSupportedException("Only linear direction curves (kind 0) are verified.");
        int count = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(tag[0x30..]));
        long header = 0x38 + BinaryPrimitives.ReadInt64LittleEndian(tag[0x38..]);
        long data = header + 16;
        if (count < 1 || data < 0 || data + 16L * count > tag.Length)
            throw new InvalidDataException("Direction curve keys leave their tag.");
        var keys = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            var at = tag[(int)(data + 16 * i)..];
            keys[i] = new(BinaryPrimitives.ReadSingleLittleEndian(at), BinaryPrimitives.ReadSingleLittleEndian(at[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(at[8..]), BinaryPrimitives.ReadSingleLittleEndian(at[12..]));
        }
        return new(keys);
    }

    /** Lerps the two keys around (count - 1) * time; the ends clamp and do not wrap. */
    public Vector4 Sample(float time)
    {
        int last = keys.Length - 1;
        float position = Math.Clamp(last * time, 0, last);
        int low = Math.Min((int)position, last), high = Math.Min(low + 1, last);
        float blend = Math.Clamp(position - low, 0, 1);
        return keys[low] * (1 - blend) + keys[high] * blend;
    }
}

/** A class-0x80806F64 curve set: two time windows and four direction curves, read by 0x7FF742E04E80. */
public sealed class NativeTimeOfDay
{
    private readonly float[] windows;
    private readonly NativeDirectionCurve?[] curves;

    public NativeTimeOfDay(float[] windows, NativeDirectionCurve?[] curves)
    {
        if (windows.Length != 4 || curves.Length != 4) throw new ArgumentException("A curve set has two windows and four curves.");
        this.windows = windows; this.curves = curves;
    }

    /** Header floats +0x0 to +0xC and curve handles +0x10 to +0x1C; -1 marks an absent curve. */
    public static (float[] Windows, uint[] Curves) ReadHeader(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 0x20) throw new InvalidDataException("Time-of-day curve set is shorter than its header.");
        var windows = new float[4];
        var handles = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            windows[i] = BinaryPrimitives.ReadSingleLittleEndian(tag[(4 * i)..]);
            handles[i] = BinaryPrimitives.ReadUInt32LittleEndian(tag[(0x10 + 4 * i)..]);
        }
        return (windows, handles);
    }

    /** The window test of 0x7FF742E04E80: [start, end) or, when start is past end, the wrap across midnight. */
    private static bool Inside(float time, float start, float end) =>
        start <= end ? end > start && time >= start && end > time : time >= start || end > time;

    private static Vector3 Normalize(Vector4 value)
    {
        var v = new Vector3(value.X, value.Y, value.Z);
        float length = v.Length();
        // The game's normalize falls back to +Z for a degenerate vector.
        return length > 1e-6f ? v / length : Vector3.UnitZ;
    }

    /**
     * The two directions the time-of-day system publishes for one time.
     * @return Sky is key 0x5C579DFA (frame +0x37010); Light is key 0xC09AC1A6 (frame +0x37020).
     */
    public (Vector3 Sky, Vector3 Light) Evaluate(float time)
    {
        var sky = Vector4.UnitZ;
        if (curves[0] is { } first) sky = first.Sample(time);
        if (curves[1] is { } second && Inside(time, windows[0], windows[1])) sky = second.Sample(time);
        var light = sky;
        bool lightWindow = Inside(time, windows[2], windows[3]);
        if (curves[3] is { } fourth && lightWindow) light = fourth.Sample(time);
        else if (curves[2] is { } third) light = third.Sample(time);
        return (Normalize(sky), Normalize(light));
    }
}

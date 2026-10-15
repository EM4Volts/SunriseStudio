using System.Numerics;

namespace SunriseStudio.Rendering;

/**
 * The SpeedTree wind state one decorator type owns, ported from the binary.
 * Offsets in comments are bytes into the native object at type entry +132.
 */
public sealed class SpeedTreeWind
{
    /** Floats in the type's +0x18 array: 327 object floats, then +1500 to +1512. */
    public const int ParameterCount = 331;
    /** Bytes in the type's +0x28 array, copied to +1468. */
    public const int OptionCount = 31;
    /** Shader rows 0x7FF742F121F0 writes; scope 8 holds them from row 34. */
    public const int ShaderRows = 20;
    // The registration in 0x7FF742F138A0 sets this direction and strength.
    private static readonly Vector3 InitialDirection = new(BitConverter.UInt32BitsToSingle(0x3F34FDF4),
        BitConverter.UInt32BitsToSingle(0x3F34FDF4), 0);
    private const float InitialStrength = 0.25f;
    // 0x7FF742F15020 caps the strength just below one.
    private static readonly float StrengthCap = BitConverter.UInt32BitsToSingle(0x3F7D70A4);
    // 0x7FF7437D7A70 returns this up axis.
    private static readonly Vector3 Up = Vector3.UnitZ;
    private const float Epsilon = 1.1920929e-7f;

    // The native object is 1804 bytes; the options at +1468 are bytes.
    private readonly float[] f = new float[451];
    private readonly byte[] options = new byte[OptionCount];

    /**
     * Copies the type parameters as 0x7FF742F138A0 does, then sets the registration direction and strength.
     * @param parameters The 331 floats of the type's +0x18 array.
     * @param flags The 31 bytes of the type's +0x28 array.
     */
    public SpeedTreeWind(ReadOnlySpan<float> parameters, ReadOnlySpan<byte> flags)
    {
        if (parameters.Length != ParameterCount || flags.Length != OptionCount)
            throw new ArgumentException("SpeedTree wind needs 331 parameters and 31 option bytes.");
        parameters[..327].CopyTo(f);
        parameters[327..].CopyTo(f.AsSpan(1500 / 4));
        flags.CopyTo(options);
        SetDirection(InitialDirection);
        SetStrength(InitialStrength);
    }

    private ref float F(int offset) => ref f[offset >> 2];
    private bool Option(int offset) => options[offset - 1468] != 0;

    /** 0x7FF7437D7750. */
    public void SetDirection(Vector3 direction)
    {
        foreach (int offset in (ReadOnlySpan<int>)[1312, 1392, 1412, 1380])
        { F(offset) = direction.X; F(offset + 4) = direction.Y; F(offset + 8) = direction.Z; }
    }

    /** 0x7FF7437D77C0: starts a strength change whose length grows with the step. */
    public void SetStrength(float strength)
    {
        float current = F(1308);
        if (strength == current) return;
        float length = F(0), half = F(0) * 0.5f, last = F(1324);
        F(1368) = last;
        F(1376) = current;
        F(1364) = strength;
        F(1372) = MathF.Abs(strength - current) * (length - half) + half + last;
    }

    /**
     * One frame of 0x7FF742F15020 for a SpeedTree type.
     * @param direction Render-global wind direction, block +16.
     * @param rawStrength Render-global wind strength, block +32.
     * @return The 20 shader rows for scope 8 rows 34 to 53.
     */
    public Vector4[] Update(float time, Vector3 direction, float rawStrength)
    {
        SetDirection(direction);
        float strength = MathF.Sqrt(rawStrength / 80f);
        if (strength - StrengthCap >= 0) strength = StrengthCap;
        SetStrength(strength);
        Advance(time);
        return ShaderValues(time);
    }

    private static float Sigmoid(float t) => 1f / (MathF.Exp(-(t * 12f - 6f)) + 1f);

    /** 0x7FF7437D4DF0 with wind enabled; the gust flag is cleared at registration. */
    private void Advance(float time)
    {
        float last = F(1324);
        F(1328) = last == -1f ? 0 : time - last;
        F(1324) = time;
        float end = F(1408), start = F(1404);
        float blend = end != start ? MathF.Min(1, MathF.Max(0, (time - start) / (end - start))) : 1f;
        float sigmoid = Sigmoid(blend);
        float eased = (blend - sigmoid) * 0.5f + sigmoid;
        float weight, low, high;
        if (eased >= 0.5f)
        {
            weight = (eased - 0.5f) + (eased - 0.5f);
            F(1312) = (F(1380) - F(1392)) * weight + F(1392);
            F(1316) = (F(1384) - F(1396)) * weight + F(1396);
            low = F(1400); high = F(1388);
        }
        else
        {
            weight = eased + eased;
            F(1312) = (F(1392) - F(1412)) * weight + F(1412);
            F(1316) = (F(1396) - F(1416)) * weight + F(1416);
            low = F(1420); high = F(1400);
        }
        F(1320) = (high - low) * weight + low;
        Normalize(1312);
        float strengthEnd = F(1372), strengthStart = F(1368);
        float progress = strengthEnd == strengthStart ? 0 : MathF.Min(1, MathF.Max(0, (time - strengthStart) / (strengthEnd - strengthStart)));
        float curve = 1f / (MathF.Exp(-(progress * 12f - 6f)) + 1f);
        float strength = ((progress - curve) * 0f + curve) * (F(1364) - F(1376)) + F(1376);
        F(1308) = strength;
        float level = MathF.Max(0, MathF.Min(1, strength + F(1336)));
        F(1424) = level;
        float scaled = level * 9f;
        int lowIndex = (int)scaled, highIndex = lowIndex + 1;
        float t;
        if (highIndex < 10)
        {
            if (lowIndex >= 0) t = scaled - lowIndex;
            else { highIndex = 0; lowIndex = 0; t = 0; }
        }
        else { lowIndex = 9; t = 0; highIndex = 9; }
        float Table(int offset) => (F(offset + 4 * highIndex) - F(offset + 4 * lowIndex)) * t + F(offset + 4 * lowIndex);
        float elapsed = F(1328);
        F(1428) = Table(16) * elapsed + F(1428);
        F(1432) = Table(56) * elapsed + F(1432);
        F(1436) = Table(96) * elapsed + F(1436);
        F(1440) = Table(136) * elapsed + F(1440);
        F(1444) = Table(176) * elapsed + F(1444);
        F(1448) = Table(216) * elapsed + F(1448);
        F(1452) = Table(256) * elapsed + F(1452);
        F(1456) = Table(296) * elapsed + F(1456);
        F(1460) = Table(336) * elapsed + F(1460);
        F(1464) = Table(376) * elapsed + F(1464);
        F(1516) = F(1312); F(1520) = F(1316); F(1524) = F(1320); F(1528) = F(1424);
        F(1532) = F(1428);
        F(1536) = Table(424);
        F(1540) = F(416) == 0 ? 1f : 1f / F(416);
        F(1544) = F(420);
        F(1612) = Table(464);
        F(1548) = F(1432);
        F(1552) = Table(504);
        F(1556) = F(1436);
        F(1560) = Table(636);
        var offset = Offset();
        F(1596) = offset.X; F(1600) = offset.Y; F(1604) = offset.Z;
        F(1616) = Table(544);
        if (Option(1471)) F(1616) *= F(1512);
        F(1628) = F(624);
        F(1620) = Table(676);
        if (Option(1478)) F(1620) *= F(1512);
        F(1632) = F(756);
        F(1564) = F(628); F(1568) = F(632); F(1572) = F(760); F(1576) = F(764);
        F(1580) = Table(584);
        F(1584) = Table(716);
        F(1644) = F(768); F(1648) = F(772); F(1652) = F(776);
        F(1656) = F(780) * 0.1f;
        F(1660) = F(1440);
        F(1724) = F(1452);
        F(1664) = Table(784);
        F(1676) = F(1444);
        F(1728) = Table(1008);
        F(1740) = F(1456);
        F(1680) = Table(824);
        F(1684) = Table(864);
        F(1688) = Table(904);
        F(1744) = Table(1048);
        F(1748) = Table(1088);
        F(1752) = Table(1128);
        F(1692) = Table(944);
        float first = Table(216);
        F(1696) = first >= Epsilon ? 1f / first * F(984) * 10f : 0;
        F(1700) = F(1448);
        F(1708) = F(988); F(1712) = F(992); F(1716) = F(996); F(1720) = F(1000);
        F(1756) = Table(1168);
        float second = Table(336);
        F(1760) = second >= Epsilon ? 1f / second * F(1208) * 10f : 0;
        F(1764) = F(1460);
        F(1772) = F(1212); F(1776) = F(1216); F(1780) = F(1220); F(1784) = F(1224);
        F(1668) = F(1004);
        F(1732) = F(1228);
        F(1788) = F(1464);
        F(1792) = Table(1232);
        F(1796) = F(1272);
        F(1800) = F(1276);
    }

    /** 0x7FF7437D6AE0: a zero vector stays zero. */
    private void Normalize(int offset)
    {
        float x = F(offset), y = F(offset + 4), z = F(offset + 8);
        float length = MathF.Sqrt(x * x + y * y + z * z);
        if (length == 0) { F(offset) = 0; F(offset + 4) = 0; F(offset + 8) = 0; return; }
        F(offset) = x / length; F(offset + 4) = y / length; F(offset + 8) = z / length;
    }

    /** 0x7FF7437D5F20: the direction tilted toward up, scaled, plus the +1500 offset. */
    private Vector3 Offset()
    {
        float lift = F(8);
        float x = lift * Up.X + F(1312), y = lift * Up.Y + F(1316), z = lift * Up.Z + F(1320);
        float length = MathF.Sqrt(y * y + x * x + z * z);
        float nx = 0, ny = 0, nz = 0;
        if (length != 0) { nx = x / length; ny = y / length; nz = z / length; }
        return new(nx * F(1512) * F(12) + F(1500), ny * F(1512) * F(12) + F(1504), nz * F(1512) * F(12) + F(1508));
    }

    /** 0x7FF742F121F0 with four lanes per row, two row-5 lanes and three lanes in rows 3, 12 and 16. */
    private Vector4[] ShaderValues(float time)
    {
        var rows = new Vector4[ShaderRows];
        Vector4 Row(int offset) => new(F(offset), F(offset + 4), F(offset + 8), F(offset + 12));
        Vector4 Three(int offset) => new(F(offset), F(offset + 4), F(offset + 8), 0);
        rows[0] = new(time, 0, 0, 0);
        rows[1] = Row(1548);
        rows[2] = Three(1612);
        rows[3] = Three(1596);
        rows[4] = Row(1564);
        rows[5] = new(F(1580), F(1584), 0, 0);
        rows[7] = Row(1788);
        rows[8] = Row(1532);
        rows[9] = Three(1660);
        rows[10] = Row(1708);
        rows[11] = Row(1676);
        rows[12] = Three(1692);
        rows[13] = Three(1724);
        rows[14] = Row(1772);
        rows[15] = Row(1740);
        rows[16] = Three(1756);
        rows[17] = Row(1644);
        rows[18] = new(F(1628), F(1632), 0, 0);
        rows[19] = Row(1516);
        return rows;
    }
}

using System.Numerics;

namespace SunriseStudio.Scene;

/**
 * Runs a value provider expression as the interpreter 0x7FF742077A80 does, for the operations a
 * channel's provider rows use. Opcode 0x3C pushes input slot n; 0x3E pops into output n.
 */
internal static class ProviderExpression
{
    private const int StackSize = 32;
    // Divisors at or below this magnitude give a signed infinity (0x7FF74433AC40).
    private static readonly float DivisionLimit = BitConverter.UInt32BitsToSingle(0x1FEC1E4A);
    // Gradient ramps narrower than this step at their knot.
    private const float RampMinimumWidth = 1e-4f;

    /** @return Output 0, zero when a lane is not finite; null when the program uses an operation not run here. */
    public static Vector4? Run(ReadOnlySpan<byte> code, ReadOnlySpan<Vector4> constants, ReadOnlySpan<Vector4> inputs)
    {
        Span<Vector4> stack = stackalloc Vector4[StackSize];
        int depth = 0;
        var output = Vector4.Zero;
        for (int cursor = 0; cursor < code.Length;)
        {
            byte operation = code[cursor];
            int size = operation is 0x22 or >= 0x34 and <= 0x3E ? 2 : 1;
            if (cursor + size > code.Length) return null;
            int a = size > 1 ? code[cursor + 1] : 0;
            cursor += size;
            int pops = operation switch
            {
                0x10 or 0x12 or 0x13 => 3,
                >= 0x01 and <= 0x06 or >= 0x08 and <= 0x0F => 2,
                0x34 or 0x3C => 0,
                _ => 1
            };
            if (depth < pops || pops == 0 && depth == StackSize) return null;
            Vector4 top = pops > 0 ? stack[depth - 1] : default, previous = pops > 1 ? stack[depth - 2] : default;
            Vector4 oldest = pops > 2 ? stack[depth - 3] : default;
            Vector4 result;
            switch (operation)
            {
                case 0x01 or 0x06: result = previous + top; break;
                case 0x02: result = previous - top; break;
                case 0x03 or 0x05: result = previous * top; break;
                case 0x04: result = new(Divide(previous.X, top.X), Divide(previous.Y, top.Y), Divide(previous.Z, top.Z), Divide(previous.W, top.W)); break;
                case 0x07: result = Map(top, x => x == 0 ? 1 : 0); break;
                case 0x08: result = Vector4.Min(previous, top); break;
                case 0x09: result = Vector4.Max(previous, top); break;
                case 0x0A: result = new(top.X < previous.X ? 0 : 1, top.Y < previous.Y ? 0 : 1, top.Z < previous.Z ? 0 : 1, top.W < previous.W ? 0 : 1); break;
                case 0x0B: result = new((previous.X * top.X + previous.Z * top.Z) + (previous.Y * top.Y + previous.W * top.W)); break;
                case 0x0C: result = new(previous.X, top.X, top.Y, top.Z); break;
                case 0x0D: result = new(previous.X, previous.Y, top.X, top.Y); break;
                case 0x0E: result = new(previous.X, previous.Y, previous.Z, top.X); break;
                case 0x0F: result = (top.X * previous + new Vector4(top.Y)) * (previous * previous) + (top.Z * previous + new Vector4(top.W)); break;
                case 0x10: result = (previous - oldest) * top + oldest; break;
                case 0x12: result = top + previous * oldest; break;
                case 0x13: result = Vector4.Min(Vector4.Max(oldest, previous), top); break;
                case 0x15: result = Vector4.Abs(top); break;
                case 0x16: result = Map(top, x => x > 0 ? 1 : x < 0 ? -1 : x); break;
                case 0x17: result = Map(top, MathF.Floor); break;
                case 0x18: result = Map(top, MathF.Ceiling); break;
                case 0x19: result = Map(top, x => MathF.Round(x, MidpointRounding.ToEven)); break;
                case 0x1A: result = Map(top, x => x - MathF.Floor(x)); break;
                case 0x1D: result = -top; break;
                case 0x21: result = new(top.X); break;
                case 0x22: result = new(top[a >> 6], top[(a >> 4) & 3], top[(a >> 2) & 3], top[a & 3]); break;
                case 0x23: result = Vector4.Clamp(top, Vector4.Zero, Vector4.One); break;
                case 0x26: result = new(top.Length()); break;
                case 0x27: result = Map(top, x => 2 * MathF.Abs(x - MathF.Round(x, MidpointRounding.ToEven))); break;
                case 0x34: if (a >= constants.Length) return null; result = constants[a]; break;
                case 0x35:
                    if (a + 1 >= constants.Length) return null;
                    result = (constants[a + 1] - constants[a]) * top + constants[a];
                    break;
                case 0x37:
                    if (a + 5 > constants.Length) return null;
                    result = Segments(top, constants.Slice(a, 4), constants[a + 4]);
                    break;
                case 0x38:
                    if (a + 10 > constants.Length) return null;
                    result = constants[a + 9].X <= top.X ? Segments(top, constants.Slice(a + 4, 4), constants[a + 9])
                        : Segments(top, constants.Slice(a, 4), constants[a + 8]);
                    break;
                case 0x3A:
                    if (a + 6 > constants.Length) return null;
                    result = Gradient(top, constants.Slice(a, 6));
                    break;
                case 0x3C: if (a >= inputs.Length) return null; result = inputs[a]; break;
                case 0x3E:
                    if (a == 0) output = top;
                    depth--;
                    continue;
                default: return null;
            }
            depth -= pops;
            stack[depth++] = result;
        }
        return float.IsFinite(output.X) && float.IsFinite(output.Y) && float.IsFinite(output.Z) && float.IsFinite(output.W)
            ? output : Vector4.Zero;
    }

    private static Vector4 Map(Vector4 v, Func<float, float> f) => new(f(v.X), f(v.Y), f(v.Z), f(v.W));

    private static float Divide(float a, float b) =>
        MathF.Abs(b) > DivisionLimit ? a / b : (a > 0 ? 1 : a < 0 ? -1 : float.NaN) * float.PositiveInfinity;

    /** Four cubic segments; the last lane whose knot is at or below that lane of x picks the value for all lanes. */
    private static Vector4 Segments(Vector4 x, ReadOnlySpan<Vector4> c, Vector4 knots)
    {
        Span<bool> reached = [knots.X <= x.X, knots.Y <= x.Y, knots.Z <= x.Z, knots.W <= x.W, false];
        uint bits = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            if (reached[lane] == reached[lane + 1]) continue;
            float value = (c[0][lane] * x[lane] + c[1][lane]) * (x[lane] * x[lane]) + (c[2][lane] * x[lane] + c[3][lane]);
            bits ^= BitConverter.SingleToUInt32Bits(value);
        }
        return new(BitConverter.UInt32BitsToSingle(bits));
    }

    /** Base c0 plus four clamped ramps with knots c5; channel j adds the ramps weighted by c(j+1). */
    private static Vector4 Gradient(Vector4 x, ReadOnlySpan<Vector4> c)
    {
        var knots = c[5];
        Span<float> next = [knots.Y, knots.Z, knots.W, 1];
        Span<float> amount = stackalloc float[4];
        for (int lane = 0; lane < 4; lane++)
        {
            float width = next[lane] - knots[lane], offset = x[lane] - knots[lane];
            float ramp = MathF.Abs(width) > RampMinimumWidth ? offset / width : offset >= 0 ? 1 : 0;
            amount[lane] = Math.Clamp(ramp, 0, 1);
        }
        var result = c[0];
        for (int channel = 0; channel < 4; channel++)
        {
            var w = c[channel + 1];
            result[channel] += (w.X * amount[0] + w.Z * amount[2]) + (w.Y * amount[1] + w.W * amount[3]);
        }
        return result;
    }
}

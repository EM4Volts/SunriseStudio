using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Rendering;

/**
 * A 0x30-byte compiled expression: bytecode at +0x00, float4 constants at +0x10.
 * Runs on the interpreter at 0x7FF742077A80 through 0x7FF742079FE0.
 */
public sealed class NativeExpression
{
    // The native stack holds 32 vec4s.
    private const int StackSize = 32;
    private readonly byte[] code;
    private readonly Vector4[] constants;

    private NativeExpression(byte[] code, Vector4[] constants) { this.code = code; this.constants = constants; }

    public int CodeLength => code.Length;

    /**
     * Reads the expression whose bytecode array field starts at offset.
     * @return Null when the bytecode array is empty.
     */
    public static NativeExpression? Read(ReadOnlySpan<byte> tag, int offset)
    {
        if (offset < 0 || offset + 0x30 > tag.Length) throw new InvalidDataException("Expression record leaves its tag.");
        long codeCount = BinaryPrimitives.ReadInt64LittleEndian(tag[offset..]);
        if (codeCount == 0) return null;
        long constantCount = BinaryPrimitives.ReadInt64LittleEndian(tag[(offset + 16)..]);
        var code = Array(tag, offset + 8, codeCount, 1).ToArray();
        var constantBytes = constantCount == 0 ? [] : Array(tag, offset + 24, constantCount, 16);
        var values = new Vector4[constantCount];
        for (int i = 0; i < values.Length; i++)
        {
            var at = constantBytes[(16 * i)..];
            values[i] = new(BinaryPrimitives.ReadSingleLittleEndian(at), BinaryPrimitives.ReadSingleLittleEndian(at[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(at[8..]), BinaryPrimitives.ReadSingleLittleEndian(at[12..]));
        }
        return new(code, values);
    }

    /** Array data starts 16 bytes past the self-relative link at field. */
    private static ReadOnlySpan<byte> Array(ReadOnlySpan<byte> tag, int field, long count, int stride)
    {
        long start = field + BinaryPrimitives.ReadInt64LittleEndian(tag[field..]) + 16;
        if (count < 0 || start < 0 || start + count * stride > tag.Length)
            throw new InvalidDataException("Expression array leaves its tag.");
        return tag.Slice((int)start, (int)(count * stride));
    }

    /**
     * Runs the program with input slot 0 set to input and returns output slot 0.
     * @return Zero when any output lane is NaN or infinite, as 0x7FF742079FE0 returns.
     */
    public Vector4 Evaluate(Vector4 input)
    {
        Span<Vector4> stack = stackalloc Vector4[StackSize];
        int depth = 0;
        var output = Vector4.Zero;
        for (int cursor = 0; cursor < code.Length;)
        {
            byte operation = code[cursor];
            // Opcodes 0x22 and 0x34 to 0x3E carry one operand byte.
            int size = operation is 0x22 or >= 0x34 and <= 0x3E ? 2 : 1;
            if (cursor + size > code.Length) throw new InvalidDataException("Expression operand leaves its program.");
            byte a = size > 1 ? code[cursor + 1] : (byte)0;
            cursor += size;
            switch (operation)
            {
                case 0x01 or 0x02 or 0x03 or 0x08 or 0x09 or 0x0C:
                {
                    Need(depth, 2);
                    var top = stack[--depth];
                    var previous = stack[depth - 1];
                    stack[depth - 1] = operation switch
                    {
                        0x01 => TfxWaveMath.Add(top, previous),
                        0x02 => previous - top,
                        0x03 => TfxWaveMath.Multiply(top, previous),
                        0x08 => Vector4.Min(previous, top),
                        0x09 => Vector4.Max(previous, top),
                        _ => new Vector4(previous.X, top.X, top.Y, top.Z)
                    };
                    break;
                }
                case 0x0F:
                    // The case at 0x7FF742078205: the top holds the coefficients, the value under it is the input.
                    Need(depth, 2);
                    depth--;
                    stack[depth - 1] = TfxWaveMath.Cubic(stack[depth - 1], stack[depth]);
                    break;
                case 0x12 or 0x13:
                {
                    Need(depth, 3);
                    depth -= 2;
                    Vector4 first = stack[depth - 1], second = stack[depth], top = stack[depth + 1];
                    stack[depth - 1] = operation == 0x12 ? TfxWaveMath.MultiplyAdd(first, second, top)
                        : TfxWaveMath.Clamp(first, second, top);
                    break;
                }
                case 0x1F: Need(depth, 1); stack[depth - 1] = TfxWaveMath.Cosine(stack[depth - 1]); break;
                case 0x23: Need(depth, 1); stack[depth - 1] = Vector4.Clamp(stack[depth - 1], Vector4.Zero, Vector4.One); break;
                // The cases at 0x7FF742078C1F and 0x7FF742078C3A call helpers 0x7FF742077360 and 0x7FF742077990.
                case 0x28: Need(depth, 1); stack[depth - 1] = TfxWaveMath.Jitter(stack[depth - 1]); break;
                case 0x29: Need(depth, 1); stack[depth - 1] = TfxWaveMath.DoubleWave(stack[depth - 1]); break;
                case 0x22: Need(depth, 1); stack[depth - 1] = EmitterProgram.Swizzle(stack[depth - 1], a); break;
                case 0x34: Push(stack, ref depth, Constant(a)); break;
                case 0x35:
                    Need(depth, 1);
                    stack[depth - 1] = TfxWaveMath.Lerp(Constant(a), Constant(a + 1), stack[depth - 1]);
                    break;
                case 0x37:
                    // The case at 0x7FF742079A97: four cubic segments in constants a to a + 3, knots in a + 4.
                    Need(depth, 1);
                    if (a + 5 > constants.Length) throw new InvalidDataException("Expression curve leaves its constants.");
                    stack[depth - 1] = TfxWaveMath.Spline4(stack[depth - 1], constants.AsSpan(a, 5));
                    break;
                case 0x38:
                    Need(depth, 1);
                    if (a + 10 > constants.Length) throw new InvalidDataException("Expression curve leaves its constants.");
                    stack[depth - 1] = TfxWaveMath.Spline8(stack[depth - 1], constants.AsSpan(a, 10));
                    break;
                case 0x3C or 0x3D:
                    // Input slot 0 holds the value; other slots are zero here.
                    Push(stack, ref depth, a == 0 && operation == 0x3C ? input : Vector4.Zero);
                    break;
                case 0x3E:
                    Need(depth, 1);
                    var stored = stack[--depth];
                    if (a == 0) output = stored;
                    break;
                default:
                    throw new NotSupportedException($"Expression opcode 0x{operation:X2} is not decoded.");
            }
        }
        return float.IsFinite(output.X) && float.IsFinite(output.Y) && float.IsFinite(output.Z) && float.IsFinite(output.W)
            ? output : Vector4.Zero;
    }

    private Vector4 Constant(int index) => index < constants.Length ? constants[index]
        : throw new InvalidDataException($"Expression constant {index} is out of range.");

    private static void Need(int depth, int count)
    {
        if (depth < count) throw new InvalidDataException("Expression stack underflow.");
    }

    private static void Push(Span<Vector4> stack, ref int depth, Vector4 value)
    {
        if (depth == stack.Length) throw new InvalidDataException("Expression stack overflow.");
        stack[depth++] = value;
    }
}

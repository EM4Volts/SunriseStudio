using System.Numerics;

namespace SunriseStudio.Rendering;

/** The streams, constants, externs and transform slots one emitter program stage reads and writes. */
public sealed class EmitterProgramContext
{
    public const int GroupCount = 8;
    // Programs address at most vec4 slot 15 of a group.
    public const int GroupSlots = 16;

    /** Stream groups by index; a missing group loads zero and drops stores. */
    public Vector4[]?[] Groups { get; } = new Vector4[]?[GroupCount];
    public Vector4[] Constants { get; set; } = [];
    /** Frame externs; opcode 0x43 indexes floats and opcodes 0x44 and 0x45 index vec4s. */
    public float[] Externs { get; set; } = new float[16];
    /** Transform slots, two vec4s each: rotation, then position with uniform scale in W. */
    public Vector4[] Slots { get; set; } = [];
    /** Linear congruential state shared by opcode 0x3D and the native spawn samplers. */
    public uint Random { get; set; }

    /** Next random value in [0, 1], from the high 16 bits of the native generator. */
    public float NextRandom()
    {
        Random = unchecked(1664525 * Random + 1013904223);
        return (Random >> 16) * (1f / 65535f);
    }
}

/** Interpreter for emitter byte code; the operations follow the native interpreter at 0x7FF742F31140. */
public static class EmitterProgram
{
    // The native stack holds 32 vec4s.
    private const int StackSize = 32;
    // Load lane forms: xyzw, xyzz, xyxy, zwzw, xxxx, yyyy, zzzz, wwww.
    private static readonly byte[] LoadSelectors = [0x1B, 0x1A, 0x11, 0xBB, 0x00, 0x55, 0xAA, 0xFF];
    // Store forms pair a swizzle with a lane mask: all, xyz, xy, zw, x, y, z, w.
    private static readonly byte[] StoreSelectors = [0x1B, 0x1B, 0x1B, 0x11, 0x1B, 0x00, 0x00, 0x00];
    private static readonly Vector4[] StoreMasks =
    [
        new(1, 1, 1, 1), new(1, 1, 1, 0), new(1, 1, 0, 0), new(0, 0, 1, 1),
        new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(0, 0, 0, 1)
    ];
    // Divisors at or below this magnitude give a signed infinity.
    private static readonly float DivisionLimit = BitConverter.UInt32BitsToSingle(0x1FEC1E4A);
    // Smooth-step spans narrower than this step at their start edge.
    private static readonly float SmoothLimit = BitConverter.UInt32BitsToSingle(0x38D1B717);

    [ThreadStatic] private static Vector4[]? stackBuffer;

    /** Bytes per instruction including the opcode; zero ends the program as the native default does. */
    public static int InstructionSize(byte operation) => operation switch
    {
        0x3E or 0x3F => 4,
        0x22 or >= 0x34 and <= 0x3B or >= 0x40 and <= 0x48 => 2,
        >= 0x01 and <= 0x52 and not 0x3C => 1,
        _ => 0
    };

    /** Runs one stage; throws on a stack fault or an opcode whose native helper is not decoded. */
    public static void Run(ReadOnlySpan<byte> code, EmitterProgramContext context)
    {
        var stack = stackBuffer ??= new Vector4[StackSize];
        int depth = 0;
        for (int cursor = 0; cursor < code.Length;)
        {
            byte operation = code[cursor];
            int size = InstructionSize(operation);
            if (size == 0 || cursor + size > code.Length) break;
            byte a = size > 1 ? code[cursor + 1] : (byte)0, b = size > 2 ? code[cursor + 2] : (byte)0,
                c = size > 3 ? code[cursor + 3] : (byte)0;
            cursor += size;
            // Pops, pushes and in-place results all address the stack from its top.
            switch (operation)
            {
                case 0x01 or 0x06 or 0x02 or 0x03 or 0x05 or 0x04 or 0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E:
                {
                    Need(depth, 2);
                    var top = stack[--depth];
                    var previous = stack[depth - 1];
                    stack[depth - 1] = operation switch
                    {
                        0x01 or 0x06 => TfxWaveMath.Add(top, previous),
                        0x02 => previous - top,
                        0x03 or 0x05 => TfxWaveMath.Multiply(top, previous),
                        0x04 => Divide(previous, top),
                        0x08 => Vector4.Min(previous, top),
                        0x09 => Vector4.Max(previous, top),
                        0x0A => TfxWaveMath.StepNotBelow(previous, top),
                        0x0B => new Vector4(Dot4(previous, top)),
                        0x0C => new Vector4(previous.X, top.X, top.Y, top.Z),
                        0x0D => new Vector4(previous.X, previous.Y, top.X, top.Y),
                        _ => new Vector4(previous.X, previous.Y, previous.Z, top.X)
                    };
                    break;
                }
                case 0x0F:
                    Need(depth, 2);
                    depth--;
                    stack[depth - 1] = TfxWaveMath.Cubic(stack[depth - 1], stack[depth]);
                    break;
                case 0x10 or 0x11 or 0x12 or 0x13 or 0x14:
                {
                    Need(depth, 3);
                    depth -= 2;
                    Vector4 first = stack[depth - 1], second = stack[depth], top = stack[depth + 1];
                    stack[depth - 1] = operation switch
                    {
                        0x10 => TfxWaveMath.Lerp(first, second, top),
                        0x11 => Saturate(TfxWaveMath.Lerp(first, second, top)),
                        0x12 => TfxWaveMath.MultiplyAdd(first, second, top),
                        0x13 => TfxWaveMath.Clamp(first, second, top),
                        // The edge just below the input is the start; the one beneath it is the end.
                        _ => SmoothStep(top, second, first)
                    };
                    break;
                }
                case 0x07 or (>= 0x15 and <= 0x2A and not 0x22) or 0x37 or 0x38 or 0x3A or 0x3B or 0x4A or 0x4B:
                    Need(depth, 1);
                    stack[depth - 1] = Unary(operation, stack[depth - 1], a, context);
                    break;
                case 0x22:
                    Need(depth, 1);
                    stack[depth - 1] = Swizzle(stack[depth - 1], a);
                    break;
                case 0x2C:
                {
                    // Axis in XYZ, angle in turns in W.
                    Need(depth, 2);
                    var axisAngle = stack[--depth];
                    var rotation = AxisAngle(axisAngle, axisAngle.W * MathF.Tau);
                    stack[depth - 1] = new Vector4(Rotate(rotation, stack[depth - 1]), 0);
                    break;
                }
                case 0x2E:
                    Need(depth, 5);
                    depth -= 4;
                    stack[depth - 1] = TfxWaveMath.Transform(stack[depth - 1], stack[depth], stack[depth + 1],
                        stack[depth + 2], stack[depth + 3]);
                    break;
                case 0x34: depth = Push(stack, depth, Constant(context, a)); break;
                case 0x35 or 0x36:
                {
                    Need(depth, 1);
                    var mixed = TfxWaveMath.Lerp(Constant(context, a), Constant(context, a + 1), stack[depth - 1]);
                    stack[depth - 1] = operation == 0x36 ? Saturate(mixed) : mixed;
                    break;
                }
                case 0x39:
                    Need(depth, 2);
                    depth--;
                    stack[depth - 1] = TfxWaveMath.Spline8OrPrevious(stack[depth - 1], stack[depth], Table(context, a, 10));
                    break;
                case 0x3D: depth = Push(stack, depth, new Vector4(context.NextRandom())); break;
                case 0x3E: depth = Push(stack, depth, Swizzle(Load(context, a, b), LoadSelectors[c & 7])); break;
                case 0x3F:
                {
                    Need(depth, 1);
                    var value = Swizzle(stack[--depth], StoreSelectors[c & 7]);
                    if (context.Groups[a & 7] is not { } group) break;
                    if (b >= group.Length) throw new InvalidDataException($"Emitter store slot {b} leaves group {a}.");
                    var mask = StoreMasks[c & 7];
                    group[b] = value * mask + group[b] * (Vector4.One - mask);
                    break;
                }
                case 0x40:
                    Need(depth, 2);
                    depth -= 2;
                    WriteSlot(context, a, 0, stack[depth + 1]);
                    WriteSlot(context, a, 1, stack[depth]);
                    break;
                case 0x41 or 0x42:
                    Need(depth, 1);
                    WriteSlot(context, a, operation == 0x41 ? 1 : 0, stack[--depth]);
                    break;
                case 0x43: depth = Push(stack, depth, new Vector4(Extern(context, a))); break;
                case 0x44: depth = Push(stack, depth, ExternVector(context, a)); break;
                case 0x45:
                    for (int row = 0; row < 4; row++) depth = Push(stack, depth, ExternVector(context, a + row));
                    break;
                // Object channels; the native interpreter pushes zero when none is bound.
                case 0x46 or 0x47 or 0x48: depth = Push(stack, depth, Vector4.Zero); break;
                case 0x49:
                {
                    Need(depth, 1);
                    int slot = (int)stack[depth - 1].X;
                    stack[depth - 1] = ReadSlot(context, slot, 1);
                    depth = Push(stack, depth, ReadSlot(context, slot, 0));
                    break;
                }
                case 0x4C:
                {
                    Need(depth, 2);
                    var offset = stack[--depth];
                    stack[depth - 1] += new Vector4(offset.X, offset.Y, offset.Z, 0);
                    break;
                }
                case 0x4D:
                {
                    // Position under the rotation moves by the rotated offset; the rotation stays on top.
                    Need(depth, 3);
                    var offset = stack[--depth];
                    stack[depth - 2] += new Vector4(Rotate(stack[depth - 1], offset), 0);
                    break;
                }
                case 0x4E:
                {
                    Need(depth, 5);
                    var amount = new Vector4(stack[--depth].X);
                    depth -= 2;
                    Vector4 positionB = stack[depth], rotationB = stack[depth + 1];
                    stack[depth - 1] = TfxWaveMath.Normalize(TfxWaveMath.Lerp(stack[depth - 1], rotationB, amount), true);
                    stack[depth - 2] = TfxWaveMath.Lerp(stack[depth - 2], positionB, amount);
                    break;
                }
                case 0x4F or 0x51 or 0x52:
                {
                    // Turns about an axis in the pivot frame; the target pair sits under the pivot pair.
                    Need(depth, 6);
                    depth -= 4;
                    Vector4 pivotPosition = stack[depth], pivotRotation = stack[depth + 1], axis = stack[depth + 2];
                    var turn = AxisAngle(axis, stack[depth + 3].X * MathF.Tau);
                    var inverse = Conjugate(pivotRotation);
                    if (operation != 0x51)
                        stack[depth - 1] = Multiply(pivotRotation, Multiply(turn, Multiply(inverse, stack[depth - 1])));
                    if (operation != 0x52)
                    {
                        var target = stack[depth - 2];
                        var local = Rotate(inverse, target - pivotPosition);
                        var moved = Rotate(pivotRotation, new Vector4(Rotate(turn, new Vector4(local, 0)), 0));
                        stack[depth - 2] = new Vector4(moved + new Vector3(pivotPosition.X, pivotPosition.Y, pivotPosition.Z), 1);
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"Emitter opcode 0x{operation:X2} is not decoded.");
            }
        }
        if (depth != 0) throw new InvalidDataException("Emitter program leaves values on the stack.");
    }

    private static Vector4 Unary(byte operation, Vector4 v, byte operand, EmitterProgramContext context) => operation switch
    {
        0x07 => new Vector4(v.X == 0 ? 1 : 0, v.Y == 0 ? 1 : 0, v.Z == 0 ? 1 : 0, v.W == 0 ? 1 : 0),
        0x15 => Vector4.Abs(v),
        0x16 => new Vector4(Sign(v.X), Sign(v.Y), Sign(v.Z), Sign(v.W)),
        0x17 => TfxWaveMath.FloorVector(v),
        0x18 => TfxWaveMath.CeilingVector(v),
        0x19 => TfxWaveMath.RoundVector(v),
        0x1A => TfxWaveMath.Fraction(v),
        0x1B => TfxWaveMath.Normalize(v, true),
        0x1C => TfxWaveMath.Normalize(v, false),
        0x1D => Vector4.Zero - v,
        0x1E => TfxWaveMath.Sine(v),
        0x1F => TfxWaveMath.Cosine(v),
        0x20 => TfxWaveMath.SineCosine(v),
        0x21 => new Vector4(v.X),
        0x23 => Saturate(v),
        0x24 => TfxWaveMath.Exp2(v),
        0x25 => TfxWaveMath.Log2(v),
        0x26 => TfxWaveMath.Length4(v),
        0x27 => TfxWaveMath.Triangle(v),
        0x28 => TfxWaveMath.Jitter(v),
        0x29 => TfxWaveMath.DoubleWave(v),
        0x2A => TfxWaveMath.FloorHash(v),
        0x37 => TfxWaveMath.Spline4(v, Table(context, operand, 5)),
        0x38 => TfxWaveMath.Spline8(v, Table(context, operand, 10)),
        0x3A => TfxWaveMath.Gradient(v, Table(context, operand, 6)),
        0x3B => TfxWaveMath.Gradient8(v, Table(context, operand, 11)),
        0x4A => ReadSlot(context, (int)v.X, 1),
        0x4B => ReadSlot(context, (int)v.X, 0),
        _ => throw new NotSupportedException($"Emitter opcode 0x{operation:X2} is not decoded.")
    };

    private static void Need(int depth, int count)
    {
        if (depth < count) throw new InvalidDataException("Emitter program stack underflow.");
    }

    private static int Push(Vector4[] stack, int depth, Vector4 value)
    {
        if (depth == stack.Length) throw new InvalidDataException("Emitter program stack overflow.");
        stack[depth] = value;
        return depth + 1;
    }

    private static Vector4 Constant(EmitterProgramContext context, int index) =>
        index < context.Constants.Length ? context.Constants[index]
            : throw new InvalidDataException($"Emitter constant {index} is out of range.");

    private static ReadOnlySpan<Vector4> Table(EmitterProgramContext context, int first, int rows) =>
        first + rows <= context.Constants.Length ? context.Constants.AsSpan(first, rows)
            : throw new InvalidDataException("Emitter curve leaves its constants.");

    private static Vector4 Load(EmitterProgramContext context, int group, int slot)
    {
        if (context.Groups[group & 7] is not { } values) return Vector4.Zero;
        return slot < values.Length ? values[slot]
            : throw new InvalidDataException($"Emitter load slot {slot} leaves group {group}.");
    }

    private static float Extern(EmitterProgramContext context, int index) =>
        index < context.Externs.Length ? context.Externs[index] : 0;

    private static Vector4 ExternVector(EmitterProgramContext context, int index) => new(Extern(context, 4 * index),
        Extern(context, 4 * index + 1), Extern(context, 4 * index + 2), Extern(context, 4 * index + 3));

    private static Vector4 ReadSlot(EmitterProgramContext context, int slot, int part) =>
        slot >= 0 && 2 * slot + part < context.Slots.Length ? context.Slots[2 * slot + part]
            : throw new InvalidDataException($"Emitter transform slot {slot} is out of range.");

    private static void WriteSlot(EmitterProgramContext context, int slot, int part, Vector4 value)
    {
        if (2 * slot + 1 >= context.Slots.Length) throw new InvalidDataException($"Emitter transform slot {slot} is out of range.");
        context.Slots[2 * slot + part] = value;
    }

    /** Lane i takes source lane (selector >> (6 - 2i)) & 3. */
    public static Vector4 Swizzle(Vector4 value, byte selector) =>
        new(value[selector >> 6], value[(selector >> 4) & 3], value[(selector >> 2) & 3], value[selector & 3]);

    private static Vector4 Saturate(Vector4 v) => Vector4.Min(Vector4.Max(v, Vector4.Zero), Vector4.One);

    /** Lanes added in the native pairs: (x + z) + (y + w). */
    private static float Dot4(Vector4 a, Vector4 b)
    {
        var m = a * b;
        return (m.X + m.Z) + (m.Y + m.W);
    }

    private static Vector4 Divide(Vector4 numerator, Vector4 denominator) => new(Divide(numerator.X, denominator.X),
        Divide(numerator.Y, denominator.Y), Divide(numerator.Z, denominator.Z), Divide(numerator.W, denominator.W));

    private static float Divide(float numerator, float denominator) => MathF.Abs(denominator) > DivisionLimit
        ? numerator / denominator : Sign(numerator) * float.PositiveInfinity;

    private static float Sign(float x) => x > 0 ? 1 : x < 0 ? -1 : x;

    private static Vector4 SmoothStep(Vector4 x, Vector4 start, Vector4 end) => new(Smooth(x.X, start.X, end.X),
        Smooth(x.Y, start.Y, end.Y), Smooth(x.Z, start.Z, end.Z), Smooth(x.W, start.W, end.W));

    private static float Smooth(float x, float start, float end)
    {
        float width = end - start;
        float t = Math.Clamp(MathF.Abs(width) > SmoothLimit ? (x - start) / width : float.PositiveInfinity, 0, 1);
        return (-2 * t + 3) * (t * t);
    }

    /** Unit axis from XYZ times sin(angle / 2), with cos(angle / 2) in W. */
    public static Vector4 AxisAngle(Vector4 axis, float angle)
    {
        var unit = TfxWaveMath.Normalize(new Vector4(axis.X, axis.Y, axis.Z, 0), false);
        float s = MathF.Sin(angle * 0.5f);
        return new(unit.X * s, unit.Y * s, unit.Z * s, MathF.Cos(angle * 0.5f));
    }

    /** Rotation of v.xyz by quaternion q: 2((q.v) q + (w^2 - 1/2) v + w (q x v)). */
    public static Vector3 Rotate(Vector4 q, Vector4 v)
    {
        var axis = new Vector3(q.X, q.Y, q.Z);
        var vector = new Vector3(v.X, v.Y, v.Z);
        var turned = Vector3.Dot(axis, vector) * axis + (q.W * q.W - 0.5f) * vector + q.W * Vector3.Cross(axis, vector);
        return turned + turned;
    }

    /** Hamilton product a b. */
    public static Vector4 Multiply(Vector4 a, Vector4 b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /** The native inverse negates W, which turns the same way as the conjugate. */
    public static Vector4 Conjugate(Vector4 q) => new(q.X, q.Y, q.Z, -q.W);
}

using System.Globalization;
using System.Numerics;
using SunriseStudio.Rendering;

namespace SunriseStudio.Interchange;

/** Runs a ps_5_0 light shader on the CPU from its disassembly, with exact register bit semantics. */
internal sealed class WorldLightShader
{
    private enum Op
    {
        Mov, Add, Mul, Mad, Div, Min, Max, Dp2, Dp3, Dp4, Sqrt, Rsq, Log, Exp, Frc, RoundNi, RoundPi, RoundZ, RoundNe,
        Lt, Ge, Eq, Ne, ILt, IGe, IEq, INe, And, Or, Xor, Not, Ftoi, Ftou, Itof, Utof, Movc, IAdd,
        Ld, Sample, SampleLevel, If, Else, EndIf, Ret
    }
    private enum Kind { Temp, Output, Input, Constant, Literal, Texture, Sampler }

    private readonly record struct Operand(Kind Kind, int Index, int Row, int[] Swizzle, bool Negate, bool Absolute, uint[]? Literal);
    private sealed record Instruction(Op Op, bool Saturate, bool Zero, Operand Destination, Operand[] Sources)
    {
        public int Jump { get; set; }
    }

    private readonly Instruction[] program;
    private readonly int temps;

    /** Texture slots the shader declares, with their dimension. */
    public IReadOnlyDictionary<int, string> Textures { get; }

    /**
     * Reads one texture texel or sample.
     * @param slot The t register.
     * @param coordinates Raw coordinate bits; ld passes integers, sample passes floats.
     * @param level The explicit level of a level sample; zero otherwise.
     */
    public delegate Vector4 Fetch(int slot, bool load, uint[] coordinates, float level);

    private WorldLightShader(Instruction[] program, int temps, IReadOnlyDictionary<int, string> textures)
    {
        this.program = program; this.temps = temps; Textures = textures;
    }

    /** Throws NotSupportedException for an instruction or operand outside the light shader subset. */
    public static WorldLightShader Parse(string disassembly)
    {
        var program = new List<Instruction>();
        var textures = new Dictionary<int, string>();
        var open = new Stack<int>();
        int temps = 0;
        foreach (string raw in disassembly.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("ps_", StringComparison.Ordinal)) continue;
            if (line.StartsWith("dcl_temps ", StringComparison.Ordinal)) { temps = int.Parse(line[10..], CultureInfo.InvariantCulture); continue; }
            if (line.StartsWith("dcl_resource_texture", StringComparison.Ordinal))
            {
                string dimension = line[13..line.IndexOf(' ')];
                textures[int.Parse(line[(line.LastIndexOf(" t", StringComparison.Ordinal) + 2)..], CultureInfo.InvariantCulture)] = dimension;
                continue;
            }
            if (line.StartsWith("dcl_", StringComparison.Ordinal)) continue;
            int split = OperationEnd(line);
            string name = line[..split], rest = line[split..].Trim();
            string[] operands = rest.Length == 0 ? [] : DxbcText.SplitOperands(rest);
            bool saturate = name.EndsWith("_sat", StringComparison.Ordinal);
            if (saturate) name = name[..^4];
            Op op = name switch
            {
                "mov" => Op.Mov, "add" => Op.Add, "mul" => Op.Mul, "mad" => Op.Mad, "div" => Op.Div, "min" => Op.Min,
                "max" => Op.Max, "dp2" => Op.Dp2, "dp3" => Op.Dp3, "dp4" => Op.Dp4, "sqrt" => Op.Sqrt, "rsq" => Op.Rsq,
                "log" => Op.Log, "exp" => Op.Exp, "frc" => Op.Frc, "round_ni" => Op.RoundNi, "round_pi" => Op.RoundPi,
                "round_z" => Op.RoundZ, "round_ne" => Op.RoundNe, "lt" => Op.Lt, "ge" => Op.Ge, "eq" => Op.Eq, "ne" => Op.Ne,
                "ilt" => Op.ILt, "ige" => Op.IGe, "ieq" => Op.IEq, "ine" => Op.INe, "and" => Op.And, "or" => Op.Or,
                "xor" => Op.Xor, "not" => Op.Not, "ftoi" => Op.Ftoi, "ftou" => Op.Ftou, "itof" => Op.Itof, "utof" => Op.Utof,
                "movc" => Op.Movc, "iadd" => Op.IAdd, "if_nz" => Op.If, "if_z" => Op.If, "else" => Op.Else, "endif" => Op.EndIf,
                "ret" => Op.Ret,
                _ when name.StartsWith("ld_indexable(texture2d)", StringComparison.Ordinal) => Op.Ld,
                _ when name.StartsWith("sample_indexable(texture", StringComparison.Ordinal) => Op.Sample,
                _ when name.StartsWith("sample_l_indexable(texture", StringComparison.Ordinal) => Op.SampleLevel,
                _ => throw new NotSupportedException($"Light shader instruction {name} is not supported.")
            };
            var parsed = operands.Select(ParseOperand).ToArray();
            Instruction instruction = op switch
            {
                Op.If => new(op, false, name == "if_z", default, parsed),
                Op.Else or Op.EndIf or Op.Ret => new(op, false, false, default, []),
                _ => new(op, saturate, false, parsed[0], parsed[1..])
            };
            if (op == Op.If) open.Push(program.Count);
            else if (op == Op.Else) { int at = open.Pop(); program[at].Jump = program.Count + 1; open.Push(program.Count); }
            else if (op == Op.EndIf) program[open.Pop()].Jump = program.Count + 1;
            program.Add(instruction);
        }
        if (open.Count != 0) throw new NotSupportedException("Light shader branches are unbalanced.");
        return new(program.ToArray(), temps, textures);
    }

    /** The operation token ends at the first space outside parentheses. */
    private static int OperationEnd(string line)
    {
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            else if (line[i] == ')') depth--;
            else if (line[i] == ' ' && depth == 0) return i;
        }
        return line.Length;
    }

    private static readonly int[] Identity = [0, 1, 2, 3];

    private static Operand ParseOperand(string text)
    {
        bool negate = text.StartsWith('-');
        if (negate) text = text[1..];
        bool absolute = text.StartsWith('|');
        if (absolute) text = text.Trim('|');
        if (text.StartsWith("l(", StringComparison.Ordinal))
        {
            var values = text[2..^1].Split(',').Select(value => Literal(value.Trim())).ToArray();
            if (values.Length == 1) values = [values[0], values[0], values[0], values[0]];
            if (values.Length != 4) throw new NotSupportedException($"Light shader literal {text} is not supported.");
            return new(Kind.Literal, 0, 0, Identity, negate, absolute, values);
        }
        int dot = text.LastIndexOf('.');
        string register = dot > 0 && !text[(dot + 1)..].Any(char.IsDigit) ? text[..dot] : text;
        int[] swizzle = register.Length == text.Length ? Identity
            : text[(dot + 1)..].Select(lane => "xyzw".IndexOf(lane)).ToArray();
        if (swizzle.Any(lane => lane < 0)) throw new NotSupportedException($"Light shader operand {text} is not supported.");
        if (register.StartsWith("cb", StringComparison.Ordinal))
        {
            int open = register.IndexOf('[');
            return new(Kind.Constant, int.Parse(register[2..open], CultureInfo.InvariantCulture),
                int.Parse(register[(open + 1)..^1], CultureInfo.InvariantCulture), swizzle, negate, absolute, null);
        }
        Kind kind = register[0] switch
        {
            'r' => Kind.Temp, 'o' => Kind.Output, 'v' => Kind.Input, 't' => Kind.Texture, 's' => Kind.Sampler,
            _ => throw new NotSupportedException($"Light shader operand {text} is not supported.")
        };
        return new(kind, int.Parse(register[1..], CultureInfo.InvariantCulture), 0, swizzle, negate, absolute, null);
    }

    private static uint Literal(string value)
    {
        if (value.StartsWith("0x", StringComparison.Ordinal)) return uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (value.Contains('.') || value.Contains('e') || value.Contains("inf", StringComparison.OrdinalIgnoreCase))
            return BitConverter.SingleToUInt32Bits(float.Parse(value, CultureInfo.InvariantCulture));
        return unchecked((uint)int.Parse(value, CultureInfo.InvariantCulture));
    }

    /**
     * Runs the shader once.
     * @param constants Constant buffers by register, as float rows.
     * @param position The v0 input lanes.
     * @return The o0 and o1 outputs.
     */
    public (Vector4 Target0, Vector4 Target1) Run(IReadOnlyDictionary<int, Vector4[]> constants, Vector4 position, Fetch fetch)
    {
        var r = new uint[Math.Max(temps, 1) * 4];
        var o = new uint[8];
        var v = new uint[] { Bits(position.X), Bits(position.Y), Bits(position.Z), Bits(position.W) };
        uint Read(in Operand operand, int lane)
        {
            int at = operand.Swizzle[Math.Min(lane, operand.Swizzle.Length - 1)];
            uint bits = operand.Kind switch
            {
                Kind.Temp => r[operand.Index * 4 + at],
                Kind.Output => o[operand.Index * 4 + at],
                Kind.Input => v[at],
                Kind.Literal => operand.Literal![at],
                Kind.Constant => constants.TryGetValue(operand.Index, out var rows) && operand.Row < rows.Length
                    ? Bits(rows[operand.Row][at]) : 0,
                _ => throw new NotSupportedException("A texture or sampler is not a value.")
            };
            if (operand.Absolute) bits &= 0x7FFFFFFF;
            if (operand.Negate) bits ^= 0x80000000;
            return bits;
        }
        float F(in Operand operand, int lane) => BitConverter.UInt32BitsToSingle(Read(operand, lane));
        void Write(Instruction instruction, Func<int, uint> lane)
        {
            var destination = instruction.Destination;
            var target = destination.Kind == Kind.Temp ? r : destination.Kind == Kind.Output ? o
                : throw new NotSupportedException("Light shader writes an unsupported register.");
            Span<uint> values = stackalloc uint[4];
            // A destination mask lists its lanes; each lane reads the source lane at its own position.
            foreach (int at in destination.Swizzle) values[at] = lane(at);
            foreach (int at in destination.Swizzle)
                target[destination.Index * 4 + at] = instruction.Saturate ? Bits(Saturate(BitConverter.UInt32BitsToSingle(values[at]))) : values[at];
        }
        for (int pc = 0; pc < program.Length;)
        {
            var instruction = program[pc];
            var s = instruction.Sources;
            switch (instruction.Op)
            {
                case Op.Ret: return (Vector(o, 0), Vector(o, 4));
                case Op.If:
                    bool zero = Read(s[0], 0) == 0;
                    pc = zero != instruction.Zero ? instruction.Jump : pc + 1;
                    continue;
                case Op.Else: pc = instruction.Jump; continue;
                case Op.EndIf: pc++; continue;
                case Op.Dp2: case Op.Dp3: case Op.Dp4:
                    int count = instruction.Op == Op.Dp2 ? 2 : instruction.Op == Op.Dp3 ? 3 : 4;
                    float sum = 0;
                    for (int lane = 0; lane < count; lane++) sum += F(s[0], lane) * F(s[1], lane);
                    Write(instruction, _ => Bits(sum));
                    break;
                case Op.Ld: case Op.Sample: case Op.SampleLevel:
                    var coordinates = new uint[] { Read(s[0], 0), Read(s[0], 1), Read(s[0], 2), Read(s[0], 3) };
                    float level = instruction.Op == Op.SampleLevel ? F(s[3], 0) : 0;
                    var texel = fetch(s[1].Index, instruction.Op == Op.Ld, coordinates, level);
                    var swizzled = s[1].Swizzle;
                    Write(instruction, lane => Bits(texel[swizzled.Length == 1 ? swizzled[0] : swizzled[lane]]));
                    break;
                default:
                    var op = instruction.Op;
                    Write(instruction, lane => Lane(op, s, lane, F, Read));
                    break;
            }
            pc++;
        }
        return (Vector(o, 0), Vector(o, 4));
    }

    private delegate float FloatReader(in Operand operand, int lane);
    private delegate uint BitsReader(in Operand operand, int lane);

    private static uint Lane(Op op, Operand[] s, int lane, FloatReader f, BitsReader bits)
    {
        float a = f(s[0], lane);
        return op switch
        {
            Op.Mov => bits(s[0], lane),
            Op.Add => Bits(a + f(s[1], lane)),
            Op.Mul => Bits(a * f(s[1], lane)),
            Op.Mad => Bits(a * f(s[1], lane) + f(s[2], lane)),
            Op.Div => Bits(a / f(s[1], lane)),
            Op.Min => Bits(Minimum(a, f(s[1], lane))),
            Op.Max => Bits(Maximum(a, f(s[1], lane))),
            Op.Sqrt => Bits(MathF.Sqrt(a)),
            Op.Rsq => Bits(1 / MathF.Sqrt(a)),
            Op.Log => Bits(MathF.Log2(a)),
            Op.Exp => Bits(MathF.Pow(2, a)),
            Op.Frc => Bits(a - MathF.Floor(a)),
            Op.RoundNi => Bits(MathF.Floor(a)),
            Op.RoundPi => Bits(MathF.Ceiling(a)),
            Op.RoundZ => Bits(MathF.Truncate(a)),
            Op.RoundNe => Bits(MathF.Round(a, MidpointRounding.ToEven)),
            Op.Lt => Mask(a < f(s[1], lane)),
            Op.Ge => Mask(a >= f(s[1], lane)),
            Op.Eq => Mask(a == f(s[1], lane)),
            Op.Ne => Mask(a != f(s[1], lane)),
            Op.ILt => Mask((int)bits(s[0], lane) < (int)bits(s[1], lane)),
            Op.IGe => Mask((int)bits(s[0], lane) >= (int)bits(s[1], lane)),
            Op.IEq => Mask(bits(s[0], lane) == bits(s[1], lane)),
            Op.INe => Mask(bits(s[0], lane) != bits(s[1], lane)),
            Op.And => bits(s[0], lane) & bits(s[1], lane),
            Op.Or => bits(s[0], lane) | bits(s[1], lane),
            Op.Xor => bits(s[0], lane) ^ bits(s[1], lane),
            Op.Not => ~bits(s[0], lane),
            Op.IAdd => unchecked(bits(s[0], lane) + bits(s[1], lane)),
            Op.Ftoi => unchecked((uint)(float.IsNaN(a) ? 0 : (int)Math.Clamp(MathF.Truncate(a), int.MinValue, int.MaxValue))),
            Op.Ftou => float.IsNaN(a) || a <= 0 ? 0 : (uint)Math.Min(MathF.Truncate(a), uint.MaxValue),
            Op.Itof => Bits((int)bits(s[0], lane)),
            Op.Utof => Bits(bits(s[0], lane)),
            Op.Movc => bits(s[0], lane) != 0 ? bits(s[1], lane) : bits(s[2], lane),
            _ => throw new NotSupportedException($"Light shader operation {op} has no lane rule.")
        };
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    private static uint Mask(bool value) => value ? uint.MaxValue : 0;
    private static Vector4 Vector(uint[] registers, int at) => new(BitConverter.UInt32BitsToSingle(registers[at]),
        BitConverter.UInt32BitsToSingle(registers[at + 1]), BitConverter.UInt32BitsToSingle(registers[at + 2]),
        BitConverter.UInt32BitsToSingle(registers[at + 3]));
    // D3D saturate maps NaN to zero; min and max return the other operand when one is NaN.
    private static float Saturate(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    private static float Minimum(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : MathF.Min(a, b);
    private static float Maximum(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : MathF.Max(a, b);
}

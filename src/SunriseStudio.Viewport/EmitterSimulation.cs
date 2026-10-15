using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/**
 * One emitter run by its program stages and the native spawn and integration steps.
 * Particles spawn in slot +0x40 and live in slot +0x41; Transform places +0x41 in the world.
 */
internal sealed class EmitterSimulation
{
    // Channel indices in the 55-entry emitter map.
    private const int Age = 0, ParticleRandom = 1, SystemAge = 2, Lifetime = 5, Position = 6, Velocity = 7,
        InitialPosition = 8, InitialVelocity = 9, EmissionRate = 10, ShapeScale = 11, Angle = 12, Radius = 13,
        Length = 14, InitialSpeed = 15, InitialCount = 16, MaxCount = 17, Acceleration0Direction = 18,
        Acceleration0Strength = 19, Acceleration1Direction = 20, Acceleration1Strength = 21, AirFriction = 22,
        AngularVelocity = 23, InitialRotationRate = 24, RotationalAcceleration = 25, RotationalFriction = 26,
        AngularPosition = 27, SecondsElapsed = 30, GravityStrength = 31, SystemLod = 32, EmitTime = 38, AgeCpu = 42,
        SpawnId = 43, SystemCameraDistance = 51, CameraDistance = 52, PlanarCameraDistance = 53, GridCount = 54;
    // Stage order: 7 and 0 build the system, 1 runs each frame, 6 and 2 precede a spawn, 3 spawns, 4 updates.
    private const int CreateFirst = 7, Create = 0, Frame = 1, SpawnFirst = 6, SpawnSetup = 2, Spawn = 3, Update = 4;
    // Assumed gravity in meters per second squared; the native scale is a system input not traced.
    private const float Gravity = 9.81f;
    // Fixed steps keep the integration stable when frames are slow.
    private const float StepSeconds = 1f / 30;
    // Warm-up on reset shows a running effect rather than an empty one.
    private const float WarmUpSeconds = 2;
    public const int TransformSlots = 8;

    /** Waiting counts down the delay; Draining lets live particles die without new ones. */
    private enum Phase { Idle, Waiting, Emitting, Draining }

    private sealed class Particle
    {
        public readonly Vector4[] Cpu = new Vector4[EmitterProgramContext.GroupSlots];
        public readonly Vector4[] Shared = new Vector4[EmitterProgramContext.GroupSlots];
        public float Age;
        public long Serial;
    }

    private readonly EffectEmitter emitter;
    private readonly float duration;
    private readonly int cap;
    private readonly uint seed;
    private readonly int[] stageStart = new int[8];
    private readonly Vector4[] presets = new Vector4[2 * TransformSlots];
    private readonly EmitterProgramContext context = new();
    private readonly Vector4[] spawnGroup = new Vector4[EmitterProgramContext.GroupSlots];
    private readonly Vector4[] systemExtra = new Vector4[EmitterProgramContext.GroupSlots];
    private readonly Vector4[] systemGroup = new Vector4[EmitterProgramContext.GroupSlots];
    private readonly EffectChannel?[] map = new EffectChannel?[EffectReader.ChannelNames.Length];
    private readonly List<Particle> live = [];
    private readonly Stack<Particle> free = [];
    private double clock = double.NaN;
    private float systemAge, carry, elapsed, wait;
    private int pending, spawned;
    private long serials;
    private Phase phase;
    private bool active = true, startRequested;

    /** Set when a stage throws; the emitter then draws nothing and the message says why. */
    public string? Failure { get; private set; }
    /** The node may emit. A rising edge restarts it; a falling edge stops emission. */
    public bool Active
    {
        get => active;
        set { startRequested |= value && !active; active = value; }
    }
    /** A continuous node restarts once its particles are gone; a play-once node then stays idle. */
    public bool Loop { get; set; } = true;
    /** Seconds from a start to the first emission. */
    public float Delay { get; set; }
    /** True while the node waits, emits or has live particles. */
    public bool Running => phase != Phase.Idle || live.Count != 0;
    public int Count => live.Count;
    /** Times the node started; a bake reads its cycle from it. */
    public int Starts { get; private set; }
    /** The serial the next spawned particle takes; serials never repeat. */
    public long NextSerial => serials;
    /** The fixed step and the warm-up, for a bake that must match the viewer. */
    public const float StepLength = StepSeconds, WarmUpLength = WarmUpSeconds;
    /** The entity frame in the world; live particles keep their place inside it when it moves. */
    public Matrix4x4 Owner { get; set; }

    /**
     * @param frames Start frame of each transform slot inside the owner; a missing one is identity.
     * @param duration Node duration in seconds; system age runs from 0 to 1 over it.
     */
    public EmitterSimulation(EffectEmitter emitter, Matrix4x4 owner, IReadOnlyList<Matrix4x4> frames, float duration,
        int cap, uint seed)
    {
        this.emitter = emitter;
        Owner = owner;
        context.Slots = new Vector4[2 * TransformSlots];
        for (int slot = 0; slot < TransformSlots; slot++)
        {
            presets[2 * slot] = presets[2 * slot + 1] = new Vector4(0, 0, 0, 1);
            if (slot < frames.Count) Follow(slot, frames[slot]);
        }
        presets.CopyTo(context.Slots, 0);
        this.duration = duration;
        this.cap = cap;
        this.seed = seed;
        for (int i = 1; i < 8; i++) stageStart[i] = stageStart[i - 1] + emitter.StageLengths[i - 1];
        foreach (var channel in emitter.Channels) map[channel.Index] = channel;
        context.Constants = emitter.ProgramConstants;
        context.Groups[2] = spawnGroup;
        context.Groups[4] = systemExtra;
        context.Groups[5] = systemGroup;
        context.Groups[6] = (Vector4[])emitter.ChannelConstants.Clone();
    }

    /**
     * Sets a transform slot inside Owner; a frame that cannot be decomposed is ignored.
     * A changed frame replaces a program write to the slot; that its location owns it is assumed.
     */
    public void Follow(int slot, Matrix4x4 frame)
    {
        if (slot >= TransformSlots || !Matrix4x4.Decompose(frame, out var scale, out var rotation, out var position))
            return;
        var turn = new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W);
        var place = new Vector4(position, scale.X);
        if (presets[2 * slot] == turn && presets[2 * slot + 1] == place) return;
        presets[2 * slot] = context.Slots[2 * slot] = turn;
        presets[2 * slot + 1] = context.Slots[2 * slot + 1] = place;
    }

    /** The simulation frame in the world: transform slot +0x41 inside the owner. */
    public Matrix4x4 Transform => Slot(emitter.EmitterSlot) * Owner;

    /** A transform slot inside Owner; slot scale is not applied. */
    private Matrix4x4 Slot(byte index)
    {
        int slot = index < TransformSlots ? index : 0;
        var rotation = context.Slots[2 * slot];
        var position = context.Slots[2 * slot + 1];
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(rotation.X, rotation.Y,
            rotation.Z, rotation.W))) * Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    /** Starts the node again at the next advance, as a play does; ignored while inactive. */
    public void Start() => startRequested = true;

    /**
     * Advances to the viewer clock. A jump back or a long gap resets the node.
     * An active continuous node then warms up; any other node waits for a start or a rising edge.
     */
    public void Advance(double seconds, float cameraDistance)
    {
        if (Failure is not null) return;
        try
        {
            if (double.IsNaN(clock) || seconds < clock || seconds - clock > 1)
            {
                Restart(false);
                phase = Phase.Idle;
                clock = seconds;
                if (Active && Loop)
                {
                    Begin(false);
                    clock = seconds - WarmUpSeconds;
                    startRequested = false;
                }
            }
            // A start that came while the emitter was not advanced still runs.
            if (Active && (startRequested || Loop && phase == Phase.Idle)) Begin(true);
            else if (!Active && phase is Phase.Waiting or Phase.Emitting) phase = Phase.Draining;
            startRequested = false;
            while (seconds - clock >= StepSeconds)
            {
                Step(StepSeconds, cameraDistance);
                clock += StepSeconds;
            }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IndexOutOfRangeException)
        {
            Failure = error.Message;
            live.Clear();
        }
    }

    /** Runs exactly one fixed step; Advance must have run once before. */
    public void StepOnce(float cameraDistance) => Advance(clock + 1.5 * StepSeconds, cameraDistance);

    /** The spawn serial of live particle i. */
    public long SerialAt(int index) => live[index].Serial;

    /** Live particle i in emitter-frame coordinates, with its age, spin in radians and random value. */
    public (Vector3 Position, float Age, float Spin, float Random) ParticleAt(int index)
    {
        var particle = live[index];
        return (Read3(Position, particle), particle.Age, Read(AngularPosition, particle),
            Read(ParticleRandom, particle));
    }

    /** Restarts the system and counts down the delay before it emits. */
    private void Begin(bool keepParticles)
    {
        Restart(keepParticles);
        Starts++;
        wait = Delay;
        phase = Delay > 0 ? Phase.Waiting : Phase.Emitting;
    }

    private void Restart(bool keepParticles)
    {
        if (!keepParticles)
        {
            foreach (var particle in live) free.Push(particle);
            live.Clear();
        }
        Array.Clear(spawnGroup);
        Array.Clear(systemExtra);
        Array.Clear(systemGroup);
        var constants = context.Groups[6]!;
        emitter.ChannelConstants.CopyTo(constants, 0);
        presets.CopyTo(context.Slots, 0);
        context.Random = seed;
        systemAge = carry = elapsed = 0;
        spawned = 0;
        Bind(null);
        RunStage(CreateFirst);
        RunStage(Create);
        pending = Math.Max(0, (int)Read(InitialCount, null));
    }

    private void Step(float dt, float cameraDistance)
    {
        if (phase == Phase.Idle && live.Count == 0) return;
        elapsed += dt;
        if (phase == Phase.Waiting && (wait -= dt) <= 0) phase = Phase.Emitting;
        Bind(null);
        Write(SystemLod, null, 0);
        Write(SystemCameraDistance, null, cameraDistance);
        // The node emits until its age reaches 1, so a node shorter than a step still emits once.
        bool emitting = phase == Phase.Emitting && systemAge < 1;
        if (phase != Phase.Waiting) systemAge = MathF.Min(1, systemAge + (duration >= 0.0001f ? dt / duration : 1));
        Write(SystemAge, null, systemAge);
        RunStage(Frame);
        if (emitting)
        {
            float due = Read(EmissionRate, null) * dt + carry;
            int count = pending + (int)MathF.Max(0, MathF.Floor(due));
            carry = due - MathF.Floor(due);
            pending = 0;
            int maximum = (int)Read(MaxCount, null);
            int limit = Math.Min(cap, maximum > 0 ? maximum : ushort.MaxValue);
            for (int i = 0; i < count && live.Count < limit; i++) SpawnParticle(cameraDistance);
        }
        else if (phase == Phase.Emitting) phase = Phase.Draining;
        // A continuous node restarts once its particles are gone; a play-once node goes idle.
        if (phase == Phase.Draining && live.Count == 0)
        {
            if (Active && Loop) { Begin(false); return; }
            phase = Phase.Idle;
        }
        var toLocal = Matrix4x4.Invert(Transform, out var inverse) ? inverse : Matrix4x4.Identity;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var particle = live[i];
            if (UpdateParticle(particle, dt, toLocal, cameraDistance)) continue;
            live.RemoveAt(i);
            free.Push(particle);
        }
    }

    private void SpawnParticle(float cameraDistance)
    {
        var particle = free.Count > 0 ? free.Pop() : new Particle();
        Array.Clear(particle.Cpu);
        Array.Clear(particle.Shared);
        particle.Age = 0;
        particle.Serial = serials++;
        Bind(particle);
        Write(AgeCpu, particle, 0);
        Write(EmitTime, particle, elapsed);
        Write(SpawnId, particle, spawned);
        // Assumed: no native writer of particle_random is traced, so it takes a fresh random value.
        Write(ParticleRandom, particle, context.NextRandom());
        RunStage(SpawnFirst);
        RunStage(SpawnSetup);
        var position = ShapePoint(particle);
        Write3(InitialPosition, particle, position);
        Write3(InitialVelocity, particle, SpawnDirection(position, particle) * Read(InitialSpeed, particle));
        RunStage(Spawn);
        // The spawn frame's point and velocity move into the simulation frame.
        var toSimulation = Matrix4x4.Invert(Slot(emitter.EmitterSlot), out var inverse)
            ? Slot(emitter.SpawnSlot) * inverse : Matrix4x4.Identity;
        if (!toSimulation.IsIdentity)
        {
            Write3(Position, particle, Vector3.Transform(Read3(Position, particle), toSimulation));
            Write3(Velocity, particle, Vector3.TransformNormal(Read3(Velocity, particle), toSimulation));
        }
        spawned++;
        Write(CameraDistance, particle, cameraDistance);
        Write(PlanarCameraDistance, particle, cameraDistance);
        Write(AngularPosition, particle, 0);
        if (Mapped(InitialRotationRate)) Write(AngularVelocity, particle, Read(InitialRotationRate, particle));
        live.Add(particle);
    }

    /** @return False when the particle reaches the end of its life. */
    private bool UpdateParticle(Particle particle, float dt, Matrix4x4 toLocal, float cameraDistance)
    {
        Bind(particle);
        float lifetime = Read(Lifetime, particle);
        float age = particle.Age = MathF.Min(1, particle.Age + (lifetime >= 0.0001f ? dt / lifetime : 1));
        Write(AgeCpu, particle, age);
        Write(Age, particle, age);
        RunStage(Update);
        var velocity = Read3(Velocity, particle);
        if (Mapped(GravityStrength))
            velocity += Read(GravityStrength, particle) * Gravity * dt * Vector3.TransformNormal(-Vector3.UnitZ, toLocal);
        velocity += Acceleration(Acceleration0Direction, Acceleration0Strength, emitter.LocalAcceleration0, particle, toLocal) * dt;
        velocity += Acceleration(Acceleration1Direction, Acceleration1Strength, emitter.LocalAcceleration1, particle, toLocal) * dt;
        velocity *= 1 - Math.Clamp(Read(AirFriction, particle) * dt, 0, 1);
        var position = Read3(Position, particle) + velocity * dt;
        Write3(Position, particle, position);
        Write3(Velocity, particle, velocity);
        Write(CameraDistance, particle, cameraDistance);
        Write(PlanarCameraDistance, particle, cameraDistance);
        float spin = Read(AngularVelocity, particle) + Read(RotationalAcceleration, particle) * dt;
        spin *= 1 - Math.Clamp(Read(RotationalFriction, particle) * dt, 0, 1);
        Write(AngularVelocity, particle, spin);
        // Angular velocity is in turns per second; the position is in radians.
        Write(AngularPosition, particle, Read(AngularPosition, particle) + 2 * spin * dt * MathF.PI);
        Write(SecondsElapsed, particle, lifetime * age);
        return age < 1;
    }

    /** World-authored directions turn into the emitter frame unless the emitter marks them local. */
    private Vector3 Acceleration(int direction, int strength, bool local, Particle particle, Matrix4x4 toLocal)
    {
        if (!Mapped(direction) || !Mapped(strength)) return Vector3.Zero;
        var value = Read3(direction, particle) * Read(strength, particle);
        return local ? value : Vector3.TransformNormal(value, toLocal);
    }

    /** Native spawn shapes before the shape-scale multiply; the emission axis is +X. */
    private Vector3 ShapePoint(Particle particle)
    {
        var point = emitter.Shape switch
        {
            1 => Ring(Read(Radius, particle)),
            2 => MathF.Max(Read(Radius, particle), 0.001f) * RandomUnit(),
            3 => new Vector3(0, (context.NextRandom() - 0.5f) * Read(Length, particle), 0),
            4 => RandomCube() * (Read(Length, particle) * 0.5f),
            5 => Grid(particle),
            _ => Vector3.Zero
        };
        return point * Read3(ShapeScale, particle);
    }

    private Vector3 Ring(float radius)
    {
        float turn = context.NextRandom() * MathF.Tau;
        return new Vector3(0, MathF.Sin(turn) * radius, MathF.Cos(turn) * radius);
    }

    /** A grid over the length box; the counter walks X fastest. */
    private Vector3 Grid(Particle particle)
    {
        var counts = Read3(GridCount, particle);
        var size = Read3(Length, particle);
        int nx = (int)(counts.X + 0.5f), ny = (int)(counts.Y + 0.5f), nz = (int)(counts.Z + 0.5f);
        if (nx <= 0 || ny <= 0 || nz <= 0) return Vector3.Zero;
        int index = spawned;
        static float Axis(float count, float size, int cell) =>
            count > 1.5f ? size / (count - 1) * cell - size * 0.5f : 0;
        return new Vector3(Axis(counts.X, size.X, index % nx), Axis(counts.Y, size.Y, index / nx % ny),
            Axis(counts.Z, size.Z, index / (nx * ny) % nz));
    }

    private Vector3 SpawnDirection(Vector3 position, Particle particle)
    {
        float angle = Read(Angle, particle) * (MathF.PI / 180);
        switch (emitter.Direction)
        {
            case 0:
            {
                float turn = context.NextRandom() * MathF.Tau, tilt = context.NextRandom() * angle;
                return new Vector3(MathF.Cos(tilt), MathF.Sin(turn) * MathF.Sin(tilt), MathF.Cos(turn) * MathF.Sin(tilt));
            }
            case 1:
            {
                float radial = MathF.Max(MathF.Sqrt(position.Y * position.Y + position.Z * position.Z), 0.0001f);
                return new Vector3(MathF.Cos(angle), position.Y / radial * MathF.Sin(angle), position.Z / radial * MathF.Sin(angle));
            }
            case 2: return RandomUnit();
            case 3 or 4: return position.LengthSquared() > 0 ? Vector3.Normalize(position) : Vector3.Zero;
            default: return Vector3.UnitX;
        }
    }

    private Vector3 RandomCube() =>
        new(2 * context.NextRandom() - 1, 2 * context.NextRandom() - 1, 2 * context.NextRandom() - 1);

    private Vector3 RandomUnit()
    {
        var v = RandomCube();
        return v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitX;
    }

    private void RunStage(int stage)
    {
        int length = emitter.StageLengths[stage];
        if (length != 0) EmitterProgram.Run(emitter.Program.AsSpan(stageStart[stage], length), context);
    }

    /** Groups 0 and 1 are the particle's own streams; system stages run without them. */
    private void Bind(Particle? particle)
    {
        context.Groups[0] = particle?.Cpu;
        context.Groups[1] = particle?.Shared;
    }

    private bool Mapped(int channel) => map[channel] is not null;

    private EffectChannel? Find(int channel) => map[channel];

    private Vector4[]? Buffer(EffectChannel channel, Particle? particle) => channel.Kind switch
    {
        0 => particle?.Cpu,
        1 => particle?.Shared,
        <= 7 => context.Groups[channel.Kind],
        _ => null
    };

    private float Read(int channel, Particle? particle)
    {
        if (Find(channel) is not { } entry || Buffer(entry, particle) is not { } buffer) return 0;
        int index = entry.Slot;
        return index >> 2 < buffer.Length ? buffer[index >> 2][index & 3] : 0;
    }

    private Vector3 Read3(int channel, Particle? particle)
    {
        if (Find(channel) is not { } entry || Buffer(entry, particle) is not { } buffer) return Vector3.Zero;
        float Lane(int index) => index >> 2 < buffer.Length ? buffer[index >> 2][index & 3] : 0;
        return new Vector3(Lane(entry.Slot), Lane(entry.Slot + 1), Lane(entry.Slot + 2));
    }

    private void Write(int channel, Particle? particle, float value)
    {
        if (Find(channel) is not { } entry || entry.Kind == 6 || Buffer(entry, particle) is not { } buffer) return;
        int index = entry.Slot;
        if (index >> 2 < buffer.Length) buffer[index >> 2][index & 3] = value;
    }

    private void Write3(int channel, Particle? particle, Vector3 value)
    {
        if (Find(channel) is not { } entry || entry.Kind == 6 || Buffer(entry, particle) is not { } buffer) return;
        for (int lane = 0; lane < 3; lane++)
        {
            int index = entry.Slot + lane;
            if (index >> 2 < buffer.Length) buffer[index >> 2][index & 3] = value[lane];
        }
    }
}

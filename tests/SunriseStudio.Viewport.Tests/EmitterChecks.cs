using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Installed emitter programs run without a stage fault, and spawn shapes follow the native samplers. */
internal static class EmitterChecks
{
    /** @param all Runs every installed emitter instead of every eighth one. */
    public static void Run(string gameRoot, bool all)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var reader = new EffectReader(catalog);
        var tags = catalog.Entries.Where(e => e.Reference == EffectReader.EmitterClass).Select(e => e.TagHash)
            .Distinct().Order().Where((_, i) => all || i % 8 == 0).ToArray();
        int ran = 0, emitting = 0;
        var failures = new Dictionary<string, (int Count, uint Example)>();
        foreach (uint tag in tags)
        {
            if (reader.ReadEmitter(tag) is not { } emitter) continue;
            var simulation = new EmitterSimulation(emitter, Matrix4x4.Identity, [], 5, EffectSimulation.EmitterCap, tag);
            simulation.Advance(0, 10);
            simulation.Advance(0.5, 10);
            ran++;
            if (simulation.Count > 0) emitting++;
            if (simulation.Failure is { } failure)
                failures[failure] = (failures.GetValueOrDefault(failure).Count + 1, failures.GetValueOrDefault(failure).Example is 0 ? tag : failures[failure].Example);
        }
        Require(failures.Count == 0, "Emitter stages failed: " + string.Join("; ",
            failures.Select(p => $"{p.Key} x{p.Value.Count} (0x{p.Value.Example:X8})")));
        Require(emitting > ran / 4, $"Only {emitting} of {ran} emitters have live particles after 2.5 seconds.");
        Console.WriteLine($"emitter_programs ran={ran} emitting={emitting} failed=0 scope={(all ? "all" : "eighth")}");
        Ring(reader);
    }

    /** Emitter 0x80B38183 spawns on a radius-1 ring with no speed; its spawn stage copies that point unchanged. */
    private static void Ring(EffectReader reader)
    {
        const uint tag = 0x80B38183;
        var emitter = reader.ReadEmitter(tag) ?? throw new InvalidOperationException("Ring fixture emitter is missing.");
        Require(emitter is { Shape: 1, Radius: 1, InitialSpeed: 0 } && emitter.ShapeScale == Vector3.One,
            "Ring fixture emitter changed its authored shape.");
        // Each seed spawns its own point; together they must cover the ring.
        var angles = new List<float>();
        for (uint seed = 1; seed <= 16; seed++)
        {
            var simulation = new EmitterSimulation(emitter, Matrix4x4.Identity, [], 5, EffectSimulation.EmitterCap, seed);
            simulation.Advance(0, 10);
            Require(simulation.Count > 0, $"Ring fixture emitter has no live particles for seed {seed}.");
            for (int i = 0; i < simulation.Count; i++)
            {
                var position = simulation.ParticleAt(i).Position;
                float ring = MathF.Sqrt(position.Y * position.Y + position.Z * position.Z);
                Require(MathF.Abs(position.X) < 1e-3f && MathF.Abs(ring - 1) < 1e-3f,
                    $"Emitter 0x{tag:X8} placed {position} off its unit ring.");
                angles.Add(MathF.Atan2(position.Y, position.Z));
            }
        }
        float spread = angles.Max() - angles.Min();
        Require(spread > MathF.PI, $"Ring spawns cover only {spread:0.##} radians.");
        Console.WriteLine($"emitter_ring tag={tag:X8} radius=1 points={angles.Count} spread={spread:0.##} result=pass");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace SunriseStudio.Animation;

/** One clip frame in a layer entry. Frames are 30 per second and may be fractional. */
public sealed record AnimationLayerSample(AnimationControlClip Clip, float Frame, float Weight);

/**
 * One blend entry: a weight, a layer index into the definition's layer-name list, and its samples.
 * Post entries run after every other entry.
 */
public sealed record AnimationLayerEntry(int LayerIndex, float Weight, IReadOnlyList<AnimationLayerSample> Samples,
    bool Post = false);

/** Orders and merges blend entries the way the game builds its per-frame stack. */
public static class AnimationLayerStack
{
    // Weights at or below this are skipped; above the full threshold an entry replaces outright.
    internal const float Skip = 1e-4f, Full = 0.9999f;

    /**
     * Normalizes each entry's sample weights, orders entries by layer, and merges equal layers.
     * @return Entries in run order, each with a weight in 0..1 and sample weights summing to 1.
     */
    public static IReadOnlyList<AnimationLayerEntry> Prepare(IEnumerable<AnimationLayerEntry> entries)
    {
        var ordered = entries.Select(Normalize).Where(entry => entry is not null).Select(entry => entry!)
            .Select((entry, order) => (entry, order))
            .OrderBy(item => item.entry.Post).ThenBy(item => item.entry.LayerIndex).ThenBy(item => item.order)
            .Select(item => item.entry).ToList();
        var merged = new List<AnimationLayerEntry>();
        foreach (var entry in ordered)
        {
            if (merged.Count == 0 || merged[^1].LayerIndex != entry.LayerIndex || merged[^1].Post != entry.Post)
            {
                merged.Add(entry);
                continue;
            }
            var earlier = merged[^1];
            if (entry.Weight >= Full) { merged[^1] = entry with { Weight = 1 }; continue; }
            // Later entries composite over earlier ones on the same layer.
            float weight = Math.Clamp(earlier.Weight + entry.Weight - earlier.Weight * entry.Weight, Skip, 1);
            float later = entry.Weight / weight;
            merged[^1] = earlier with
            {
                Weight = weight,
                Samples = earlier.Samples.Select(s => s with { Weight = s.Weight * (1 - later) })
                    .Concat(entry.Samples.Select(s => s with { Weight = s.Weight * later })).ToArray()
            };
        }
        return merged.AsReadOnly();
    }

    /** Converts absolute sample weights into the factors of a running blend: 1, then w_k over the sum so far. */
    public static float[] SequentialFactors(IReadOnlyList<float> weights)
    {
        var factors = new float[weights.Count];
        float sum = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            sum += weights[i];
            factors[i] = i == 0 || sum <= Skip ? 1 : weights[i] / sum;
        }
        return factors;
    }

    /** Drops near-zero samples and scales the rest to sum to 1; null when none remain. */
    private static AnimationLayerEntry? Normalize(AnimationLayerEntry entry)
    {
        if (!float.IsFinite(entry.Weight)) throw new ArgumentOutOfRangeException(nameof(entry), "Layer weight is not finite.");
        var samples = entry.Samples.Where(sample => sample.Weight > Skip).ToArray();
        if (samples.Any(sample => !float.IsFinite(sample.Weight) || !float.IsFinite(sample.Frame)))
            throw new ArgumentOutOfRangeException(nameof(entry), "Layer sample is not finite.");
        float sum = samples.Sum(sample => sample.Weight);
        if (samples.Length == 0 || sum <= Skip) return null;
        return entry with
        {
            Weight = Math.Clamp(entry.Weight, 0, 1),
            Samples = samples.Select(sample => sample with { Weight = sample.Weight / sum }).ToArray()
        };
    }

    /**
     * Builds the samples of a pose layer from a parameter, scaled by the entry weight.
     * An input no authored frame covers gives an empty list.
     */
    public static IReadOnlyList<AnimationLayerSample> PoseSamples(AnimationControlClip clip, float x, float y,
        float weight = 1)
    {
        if (clip.PoseSpace is null) throw new NotSupportedException("Clip has no pose space.");
        return clip.PoseSpace.Select(x, y)
            .Select(pick => new AnimationLayerSample(clip, pick.Frame, Math.Clamp(pick.Weight * weight, 0, 1)))
            .Where(sample => sample.Weight > Skip).ToArray();
    }
}

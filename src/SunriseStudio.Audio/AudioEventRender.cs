namespace SunriseStudio.Audio;

/** Places a composition plan on the music timeline and mixes it with the timeline's renderer. */
public static class AudioEventRender
{
    // Cross-fade curves: linear for constant gain, sine for constant power.
    private const int LinearCurve = 4, SineCurve = 1;

    /**
     * Lays a plan out as timeline clips.
     * @param mediaSeconds Length of one play of a media file.
     * @return The timeline; an unsupported part throws NotSupportedException with its reason.
     */
    public static AudioTimelinePlan Layout(AudioEventPlan plan, Func<uint, double> mediaSeconds)
    {
        var clips = new List<AudioTimelineClip>(); var notices = new HashSet<string>(plan.Notices, StringComparer.Ordinal);

        double Place(AudioEventNode node, double start)
        {
            start += Seconds(node.DelaySeconds);
            switch (node)
            {
                case AudioEventUnsupported value: throw new NotSupportedException(value.Reason);
                case AudioEventSilence value: return start + Seconds(value.Seconds) * Count(value.Loops);
                case AudioEventSound value:
                {
                    double length = Seconds(mediaSeconds(value.Media));
                    if (length <= 0) return start;
                    int loops = Count(value.Loops);
                    clips.Add(new(0, value.ObjectId, value.Media, start, 0, length * loops, value.GainDb,
                        LoopSeconds: loops > 1 ? length : 0, Pan: value.Pan));
                    return start + length * loops;
                }
                case AudioEventMusic value:
                    foreach (var clip in value.Timeline.Clips)
                        clips.Add(clip with
                        {
                            StartSeconds = clip.StartSeconds + start, FadeIn = Move(clip.FadeIn, start), FadeOut = Move(clip.FadeOut, start)
                        });
                    notices.UnionWith(value.Timeline.Notices);
                    return start + value.Timeline.DurationSeconds;
                case AudioEventGroup value: return Group(value, start);
                default: throw new InvalidDataException("Unknown composition part.");
            }
        }

        double Group(AudioEventGroup group, double start)
        {
            double end = start, previousStart = start, previousEnd = start; int previousFirst = clips.Count; bool first = true;
            double gap = Seconds(group.TransitionSeconds);
            for (int loop = 0; loop < Count(group.Loops); loop++)
            {
                if (group.Kind == AudioEventGroupKind.Layer)
                {
                    double layerEnd = previousEnd;
                    foreach (var item in group.Items) layerEnd = Math.Max(layerEnd, Place(item, previousEnd));
                    previousEnd = layerEnd; end = Math.Max(end, layerEnd);
                    continue;
                }
                foreach (var item in group.Items)
                {
                    double at = first ? start : group.TransitionMode switch
                    {
                        3 => previousEnd + gap,
                        5 when gap > 0 => previousStart + gap,
                        1 or 2 => Math.Max(previousStart, previousEnd - gap),
                        _ => previousEnd
                    };
                    int itemFirst = clips.Count;
                    double itemEnd = Place(item, at);
                    double overlap = previousEnd - at;
                    if (!first && group.TransitionMode is 1 or 2 && overlap > 0)
                    {
                        int curve = group.TransitionMode == 1 ? LinearCurve : SineCurve;
                        for (int index = previousFirst; index < itemFirst; index++)
                            if (clips[index].FadeOut is null) clips[index] = clips[index] with { FadeOut = new(at, overlap, curve, false) };
                        for (int index = itemFirst; index < clips.Count; index++)
                            if (clips[index].FadeIn is null) clips[index] = clips[index] with { FadeIn = new(at, overlap, curve, true) };
                    }
                    previousStart = at; previousEnd = itemEnd; previousFirst = itemFirst; first = false;
                    end = Math.Max(end, itemEnd);
                }
            }
            return end;
        }

        double duration = Place(plan.Root, 0);
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("The composition has nothing to play.");
        if (duration > 1200) throw new InvalidDataException("The composition is longer than twenty minutes.");
        return new(0, [], clips, duration, notices.Order(StringComparer.Ordinal).ToArray());
    }

    /**
     * Mixes a plan to a 48 kHz stereo PCM16 WAV.
     * @param sources Decoded media by ID, mono or stereo PCM16.
     */
    public static AudioTimelineRender Render(AudioEventPlan plan, IReadOnlyDictionary<uint, PcmWave> sources, string path,
        CancellationToken token = default) =>
        Render(plan, sources.ToDictionary(pair => pair.Key, pair => AudioSamples.FromPcm(pair.Value)), path, token);

    /**
     * Mixes a plan to a 48 kHz stereo PCM16 WAV.
     * @param sources Decoded media by ID, mono or stereo, not clipped.
     */
    public static AudioTimelineRender Render(AudioEventPlan plan, IReadOnlyDictionary<uint, AudioSamples> sources, string path,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var timeline = Layout(plan, id => sources.TryGetValue(id, out var source) ? source.DurationSeconds
            : throw new InvalidDataException($"Missing media {id}."));
        return AudioTimeline.Render(timeline, sources, path, token);
    }

    private static AudioEnvelope? Move(AudioEnvelope? envelope, double seconds) =>
        envelope is null ? null : envelope with { StartSeconds = envelope.StartSeconds + seconds };

    private static double Seconds(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 3600 ? value : throw new InvalidDataException("Composition timing is invalid.");

    private static int Count(int loops) => loops is >= 1 and <= 1000 ? loops : throw new InvalidDataException("Composition loop count is invalid.");
}

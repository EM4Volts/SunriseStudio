namespace SunriseStudio.Audio;

/** Channel downmix ported from vgmstream's autodownmix (ISC); speaker gains follow the game's table. */
internal sealed class AudioDownmix
{
    // WAVEFORMATEXTENSIBLE speaker bits.
    private const int FL = 0, FR = 1, FC = 2, BL = 4, BR = 5, BC = 8, SL = 9, SR = 10;
    private const uint Stereo = 1u << FL | 1u << FR;

    private readonly (int Output, int Input, float Volume)[] steps;
    private readonly int inputChannels;

    public int OutputChannels { get; }

    private AudioDownmix(int inputChannels, int outputChannels, (int, int, float)[] steps)
    {
        this.inputChannels = inputChannels;
        OutputChannels = outputChannels;
        this.steps = steps;
    }

    /**
     * Builds the mix from the stream's speaker mask, or folds channels when it has none.
     * @param channels Input channels.
     * @param layout Speaker mask from fmt; 0 when the file has none.
     * @param maxChannels 0 keeps every channel; 2 mixes down when there are more.
     * @return Null when nothing is mixed.
     */
    public static AudioDownmix? Create(int channels, uint layout, int maxChannels)
    {
        if (maxChannels is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(maxChannels), "Use 0 or 2.");
        if (maxChannels == 0 || channels <= maxChannels) return null;
        var steps = layout == 0 ? Fold(channels, maxChannels) : Speakers(channels, layout);
        return new(channels, maxChannels, steps);
    }

    /** Input channel i joins output i % max, scaled by 1 / sqrt(inputs on that output). */
    private static (int, int, float)[] Fold(int channels, int maxChannels)
    {
        var steps = new (int, int, float)[channels];
        int mixes = Math.Max(1, channels / maxChannels);
        for (int input = 0; input < channels; input++)
        {
            int output = input % maxChannels;
            int count = output < channels % (mixes * maxChannels) ? mixes + 1 : mixes;
            steps[input] = (output, input, (float)(1 / Math.Sqrt(count)));
        }
        return steps;
    }

    /** Standard speaker matrix to stereo. */
    private static (int, int, float)[] Speakers(int channels, uint layout)
    {
        float max = 1f, sqrt = (float)(1 / Math.Sqrt(2));
        var matrix = new float[16, 16];
        matrix[FL, FL] = max; matrix[FR, FR] = max;
        matrix[FC, FL] = sqrt; matrix[FC, FR] = sqrt;
        matrix[SL, FL] = sqrt; matrix[SR, FR] = sqrt;
        matrix[BL, FL] = sqrt; matrix[BR, FR] = sqrt;
        // The game sends back centre to both sides.
        matrix[BC, FL] = sqrt; matrix[BC, FR] = sqrt;
        // Steps keep vgmstream's order: input speakers outer, output speakers inner.
        var steps = new List<(int, int, float)>();
        int input = 0;
        for (int speakerIn = 0; speakerIn < 16 && input < channels; speakerIn++)
        {
            if ((layout & 1u << speakerIn) == 0) continue;
            int outputIndex = 0;
            for (int speakerOut = 0; speakerOut < 16; speakerOut++)
            {
                if ((Stereo & 1u << speakerOut) == 0) continue;
                if (matrix[speakerIn, speakerOut] != 0) steps.Add((outputIndex, input, matrix[speakerIn, speakerOut]));
                outputIndex++;
            }
            input++;
        }
        return [.. steps];
    }

    /**
     * Mixes interleaved frames.
     * @param input Interleaved input frames.
     * @param frames Frame count.
     * @param scratch Receives the output; must hold frames * OutputChannels.
     * @return The filled part of scratch.
     */
    public Span<float> Mix(ReadOnlySpan<float> input, int frames, Span<float> scratch)
    {
        var result = scratch[..(frames * OutputChannels)];
        result.Clear();
        for (int f = 0; f < frames; f++)
        {
            var source = input.Slice(f * inputChannels, inputChannels);
            var target = result.Slice(f * OutputChannels, OutputChannels);
            foreach (var (o, i, volume) in steps) target[o] = target[o] + source[i] * volume;
        }
        return result;
    }
}

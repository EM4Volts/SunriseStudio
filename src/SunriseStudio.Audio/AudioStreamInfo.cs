namespace SunriseStudio.Audio;

public sealed record AudioStreamInfo(int SampleRate, int Channels, long Samples, string Encoding)
{
    public double DurationSeconds => SampleRate > 0 ? (double)Samples / SampleRate : 0;
}

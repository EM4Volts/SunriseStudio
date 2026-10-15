using System.Globalization;
using System.Text.RegularExpressions;

namespace SunriseStudio.Editor;

/** Counts describe the current stage; unknown totals never display a guessed percentage. */
public sealed record StudioLoadProgress(string Stage, int Completed = 0, int Total = 0)
{
    private static readonly Regex CountSuffix = new(@"^(.*) ([\d,]+)/([\d,]+)$", RegexOptions.CultureInvariant);
    public float? Fraction => Total > 0 ? Math.Clamp((float)Completed / Total, 0, 1) : null;

    /** Existing loader messages carry counted work as a final completed/total pair. */
    public static StudioLoadProgress FromMessage(string message)
    {
        var match = CountSuffix.Match(message);
        return match.Success
            && int.TryParse(match.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int completed)
            && int.TryParse(match.Groups[3].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int total)
                ? new(match.Groups[1].Value, completed, total) : new(message);
    }
}

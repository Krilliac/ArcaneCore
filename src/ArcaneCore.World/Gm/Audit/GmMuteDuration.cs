namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// The duration argument of <c>.mute</c>: a bare whole number is minutes (what mangos-zero and vmangos take,
/// CommunicationCommands.cpp:88-140), otherwise groups of digits closed by d/h/m/s as in <c>.ban</c>
/// (<c>1d2h30m</c>). Unlike vmangos' <c>TimeStringToSecs</c> the arithmetic is 64-bit and checked, so a huge value is
/// refused instead of wrapping into a short mute, and zero is refused (there is no permanent mute: use a ban).
/// </summary>
public static class GmMuteDuration
{
    /// <summary>The longest mute (365 days), in seconds.</summary>
    public const long MaxSeconds = 365L * 24 * 3600;

    /// <summary>True when <paramref name="text"/> is a well-formed duration of 1 second to <see cref="MaxSeconds"/>.</summary>
    public static bool TryParse(string text, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        long total = 0;
        long buffer = 0;
        bool digits = false;
        bool suffixed = false;
        foreach (char c in text)
        {
            if (c is >= '0' and <= '9')
            {
                buffer = (buffer * 10) + (c - '0');
                digits = true;
                if (buffer > MaxSeconds)
                {
                    return false;
                }

                continue;
            }

            long multiplier = char.ToLowerInvariant(c) switch
            {
                'd' => 86400,
                'h' => 3600,
                'm' => 60,
                's' => 1,
                _ => 0,
            };
            if (multiplier == 0 || !digits)
            {
                return false;
            }

            total += buffer * multiplier;
            buffer = 0;
            digits = false;
            suffixed = true;
            if (total > MaxSeconds)
            {
                return false;
            }
        }

        if (digits)
        {
            if (suffixed)
            {
                return false; // "1h30": the trailing number has no unit
            }

            total = buffer * 60; // a bare number is minutes
        }

        seconds = total;
        return total is >= 1 and <= MaxSeconds;
    }

    /// <summary>Whether a command word looks like a duration (it has a digit); player names never do.</summary>
    public static bool LooksLikeDuration(string word) => word.Any(char.IsAsciiDigit);
}

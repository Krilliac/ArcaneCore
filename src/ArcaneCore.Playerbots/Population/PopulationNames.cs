using System.Security.Cryptography;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// A syllable generator for the names of the characters the population creates: letters only (the realm's name rule), 4 to 12 characters,
/// first letter capitalised, never three identical letters in a row (CharNameThreeConsecutive). Every server name rule still applies
/// through CMSG_CHAR_CREATE, so a refused name is retried with the next candidate. Deterministic for a seed; the production seed is
/// drawn once per process from <see cref="RandomNumberGenerator"/>.
/// <para>Thread affinity: not thread safe; the population pass is the only caller (one pass at a time).</para>
/// </summary>
internal sealed class PopulationNames(int seed)
{
    /// <summary>The longest name a character may have.</summary>
    public const int MaxLength = 12;

    /// <summary>The shortest name this generator makes.</summary>
    public const int MinLength = 4;

    private static readonly string[] Onsets =
    [
        "b", "d", "f", "g", "h", "k", "l", "m", "n", "p", "r", "s", "t", "v", "z", "br", "dr", "gr", "kr", "tr", "th", "sh", "fl", "gl", "st", "sl", "ar", "el", "or",
    ];

    private static readonly string[] Vowels = ["a", "e", "i", "o", "u", "ae", "ia", "ou", "ei"];

    private static readonly string[] Codas = ["", "", "", "n", "r", "l", "s", "th", "k", "m", "nd", "rn"];

    private readonly Random _random = new(seed);

    /// <summary>A generator with a process-random seed.</summary>
    public static PopulationNames Create() => new(RandomNumberGenerator.GetInt32(int.MaxValue));

    /// <summary>The next candidate name.</summary>
    public string Next()
    {
        while (true)
        {
            int syllables = _random.Next(2, 4);
            var text = new System.Text.StringBuilder(MaxLength + 4);
            for (int i = 0; i < syllables; i++)
            {
                text.Append(Onsets[_random.Next(Onsets.Length)]).Append(Vowels[_random.Next(Vowels.Length)]);
                if (i == syllables - 1 || _random.Next(4) == 0) text.Append(Codas[_random.Next(Codas.Length)]);
            }

            if (text.Length > MaxLength) text.Length = MaxLength;
            if (text.Length < MinLength || HasTripleLetter(text)) continue;
            text[0] = char.ToUpperInvariant(text[0]);
            return text.ToString();
        }
    }

    private static bool HasTripleLetter(System.Text.StringBuilder text)
    {
        for (int i = 2; i < text.Length; i++)
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(text[i - 1]) && char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(text[i - 2]))
                return true;
        return false;
    }
}

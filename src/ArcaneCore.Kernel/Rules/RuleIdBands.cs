namespace ArcaneCore.Kernel.Rules;

/// <summary>
/// The id plan of <c>rule</c> and <c>rule_text</c> rows. Below <see cref="CustomStart"/> are shipped rows, <see cref="CustomStart"/> up to
/// <see cref="ConvertedStart"/> (exclusive) are server-authored rows, and <see cref="ConvertedStart"/> up is reserved for converters: each
/// converter owns one sub-band of <see cref="SubBandSize"/> ids and rewrites only that sub-band, so converters never touch each other's rows
/// or anything authored. The catalog accepts an id at or above <see cref="ConvertedStart"/> only inside a named sub-band.
/// </summary>
public static class RuleIdBands
{
    /// <summary>Server-authored ids start here; shipped content stays below.</summary>
    public const int CustomStart = 1_000_000;

    /// <summary>Converted content starts here (2^30); authored ids stay below.</summary>
    public const int ConvertedStart = 1 << 30;

    /// <summary>How many ids one converter's sub-band holds (2^26).</summary>
    public const int SubBandSize = 1 << 26;

    /// <summary>The EventAI converter (<c>creature_ai_scripts</c>, <c>creature_ai_texts</c>, <c>broadcast_text</c>): 2^30 up to 2^30 + 2^26 - 1.</summary>
    public const int EventAi = ConvertedStart;

    /// <summary>Reserved for the DB script converter (<c>*_scripts</c> tables): 2^30 + 2^26 up to 2^30 + 2 * 2^26 - 1.</summary>
    public const int DbScripts = ConvertedStart + SubBandSize;

    /// <summary>Reserved for the SmartAI converter (<c>smart_scripts</c>): 2^30 + 2 * 2^26 up to 2^30 + 3 * 2^26 - 1.</summary>
    public const int SmartAi = ConvertedStart + 2 * SubBandSize;

    /// <summary>The first id past the last named sub-band; nothing is accepted from here up.</summary>
    public const int End = ConvertedStart + 3 * SubBandSize;

    /// <summary>True when <paramref name="id"/> is below the converted range or inside one of the named converter sub-bands.</summary>
    public static bool IsAcceptable(int id) => id < End;
}

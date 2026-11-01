using System.Globalization;
using ArcaneCore.Kernel.Configuration;

namespace ArcaneCore.Game.WorldState;

/// <summary>
/// War Mode, a server-wide world-PvP switch (configuration section <c>World:WarMode</c>). It is a deliberate deviation from 1.12.1 and
/// nothing like retail War Mode: there is no phasing, no per-player toggle and no bonus XP or reputation. While <see cref="Enabled"/>, every
/// player and every bot is PvP-flagged outside the exempt areas (<see cref="Zones.PvpAreaState.IsWarModeExempt"/>), an honourable kill is
/// worth <see cref="HonorMultiplier"/> times as much honour, and an open-world PvP death wears <see cref="PvpDeathDurabilityLoss"/> of the
/// victim's equipment. Every key is live; the object is shared by reference with every reader (the world's <see cref="WorldStateHooks.WarMode"/>).
/// </summary>
public sealed class WarModeOptions
{
    public const string SectionName = "World:WarMode";

    /// <summary>Lowest honour multiplier.</summary>
    public const float MinHonorMultiplier = 1f;

    /// <summary>Highest honour multiplier.</summary>
    public const float MaxHonorMultiplier = 10f;

    /// <summary>
    /// Switch War Mode on for every player and bot, with no opt-out. Default false (retail). Switching it off returns the flags to
    /// the normal rules: a flag then lapses by its 5-minute timer unless the player wants it.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// While War Mode is on, the honour of an honourable kill is multiplied by this (1 to 10, default 1.5). 1 gives the same honour as with War Mode off.
    /// </summary>
    [ConfigAllowed("1 to 10")]
    public float HonorMultiplier { get; set; } = 1.5f;

    /// <summary>
    /// While War Mode is on, the fraction of every equipped item's durability a player loses when killed by another player outside a
    /// battleground (0 to 1, default 0.10). 0 costs nothing. Retail, and War Mode off: a PvP death costs no durability.
    /// <c>Items:DurabilityLossEnable</c> = false still turns all durability loss off.
    /// </summary>
    [ConfigAllowed("0 to 1")]
    public float PvpDeathDurabilityLoss { get; set; } = 0.10f;

    /// <summary>The default <see cref="ExemptAreas"/>: Booty Bay, Ratchet, Gadgetzan, Everlook, Nighthaven, Light's Hope Chapel and Cenarion Hold.</summary>
    public const string DefaultExemptAreas = "35,392,976,2255,2361,2268,3425";

    private sealed record Parsed(string Text, IReadOnlySet<uint> Ids);

    // One reference, swapped whole: the parsed list is rebuilt only when the text is replaced (a reload), and a reader never sees half of one.
    private volatile Parsed? _parsed;

    /// <summary>
    /// Area or zone ids (comma separated) War Mode leaves alone besides the capitals and cities of a player's own team: the neutral towns, which the 1.12.1
    /// area table flags neither Capital nor City (they carry flag 0x40 and no team). The default is 35 Booty Bay, 392 Ratchet, 976 Gadgetzan, 2255 Everlook,
    /// 2361 Nighthaven, 2268 Light's Hope Chapel and 3425 Cenarion Hold. Arenas (the Gurubashi Battle Ring, 2177; not the stands around it, 1741) and
    /// battlegrounds are always exempt. Live.
    /// </summary>
    public string ExemptAreas { get; set; } = DefaultExemptAreas;

    /// <summary>The ids of <see cref="ExemptAreas"/>; an entry that is not an unsigned number is left out (<see cref="Validate"/> refuses the text).</summary>
    public IReadOnlySet<uint> ExemptAreaIds()
    {
        string text = ExemptAreas ?? string.Empty;
        if (_parsed is { } cached && ReferenceEquals(cached.Text, text))
        {
            return cached.Ids;
        }

        var ids = new HashSet<uint>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                ids.Add(id);
            }
        }

        _parsed = new Parsed(text, ids);
        return ids;
    }

    /// <summary>The problem with an <see cref="ExemptAreas"/> text, or null when it is a list of area ids.</summary>
    public static string? CheckExemptAreas(string? text)
    {
        foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return $"\"{part}\" is not an area id";
            }
        }

        return null;
    }

    /// <summary>The problem with a <see cref="HonorMultiplier"/> value, or null.</summary>
    public static string? CheckHonorMultiplier(float value)
        => float.IsFinite(value) && value is >= MinHonorMultiplier and <= MaxHonorMultiplier ? null : "must be 1..10";

    /// <summary>The problem with a <see cref="PvpDeathDurabilityLoss"/> value, or null.</summary>
    public static string? CheckPvpDeathDurabilityLoss(float value)
        => float.IsFinite(value) && value is >= 0f and <= 1f ? null : "must be 0..1";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first key that is out of range.</summary>
    public void Validate()
    {
        if (CheckHonorMultiplier(HonorMultiplier) is { } honor)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{nameof(HonorMultiplier)} ({HonorMultiplier}) {honor}."));
        }

        if (CheckExemptAreas(ExemptAreas) is { } areas)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(ExemptAreas)} must be a comma separated list of area ids ({areas}).");
        }

        if (CheckPvpDeathDurabilityLoss(PvpDeathDurabilityLoss) is { } loss)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{nameof(PvpDeathDurabilityLoss)} ({PvpDeathDurabilityLoss}) {loss}."));
        }
    }
}

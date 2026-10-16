namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Opportunities</c>: a bot weighs the chests, gathering nodes and rare creatures it sees against the guards beside
/// them and goes for the ones worth the risk (<see cref="PlayerbotOpportunities"/>, docs/areas/playbots-opportunities.md). Every key is
/// live: <c>.reload config</c> changes this object in place and a bot reads it at its next decision. Not vmangos keys: chest seeking is an
/// ArcaneCore feature.
/// </summary>
public sealed class PlayerbotOpportunityOptions
{
    /// <summary>Let bots seek chests, nodes and rares (on by default). Off: bots ignore them, and gather nodes with no regard for guards.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far (yards, 10..150; default 45) from the bot an opportunity may lie to be considered.</summary>
    public float ScanRadiusYards { get; set; } = 45f;

    /// <summary>
    /// The aggressiveness knob for opportunities alone (0.25..4; default 1), applied on top of <c>Risk:Tolerance</c>: it scales the risk a
    /// bot accepts for the reward, as Tolerance does for a pull. 2 takes finds twice as dangerous, 0.5 only half. The cap on the death
    /// probability (<see cref="MaxDeathProbability"/>) holds whatever this says.
    /// </summary>
    public float RiskAppetite { get; set; } = 1f;

    /// <summary>
    /// The least net reward worth a trip (0..20; default 0.75), in same-level kills: the find's loot and its guards' experience less the
    /// time to walk and fight. 1 is about what a same-level kill gives.
    /// </summary>
    public float MinValue { get; set; } = 0.75f;

    /// <summary>The most guards a find may have (0..12; default 4): a larger pack is refused whatever it holds. 0 takes unguarded finds only.</summary>
    public int MaxGuards { get; set; } = 4;

    /// <summary>
    /// The highest chance of dying (0.01..0.5; default 0.15) a bot accepts for a find, whatever the reward: the fight's risk run through
    /// a logistic curve (risk 0.95 is an even chance, 0.7 about 15%). A hard refusal.
    /// </summary>
    public float MaxDeathProbability { get; set; } = 0.15f;

    /// <summary>
    /// How far a bot's own temperament moves <see cref="RiskAppetite"/> (0..0.5; default 0.15): each bot gets a stable factor from its guid
    /// between 1 minus and 1 plus this, so a crowd of bots does not take the same finds.
    /// </summary>
    public float PersonalityVariance { get; set; } = 0.15f;

    /// <summary>Seek unlocked treasure chests, battered and solid chests and footlockers (on by default).</summary>
    public bool Chests { get; set; } = true;

    /// <summary>Seek locked chests the bot can open: a rogue with Pick Lock at the skill the lock needs, or a bot that carries the key (on by default).</summary>
    public bool LockedChests { get; set; } = true;

    /// <summary>Weigh the guards of the herb and mining nodes the bot gathers and leave the guarded ones it could not take (on by default).</summary>
    public bool Nodes { get; set; } = true;

    /// <summary>Hunt rare creatures with loot worth the fight (on by default).</summary>
    public bool Rares { get; set; } = true;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Opportunities";
        if (CheckRadius(ScanRadiusYards) is { } radius) throw new InvalidOperationException($"{section}: ScanRadiusYards {radius}.");
        if (CheckAppetite(RiskAppetite) is { } appetite) throw new InvalidOperationException($"{section}: RiskAppetite {appetite}.");
        if (CheckMinValue(MinValue) is { } value) throw new InvalidOperationException($"{section}: MinValue {value}.");
        if (CheckGuards(MaxGuards) is { } guards) throw new InvalidOperationException($"{section}: MaxGuards {guards}.");
        if (CheckDeath(MaxDeathProbability) is { } death) throw new InvalidOperationException($"{section}: MaxDeathProbability {death}.");
        if (CheckVariance(PersonalityVariance) is { } variance) throw new InvalidOperationException($"{section}: PersonalityVariance {variance}.");
    }

    internal static string? CheckRadius(float value) => float.IsFinite(value) && value is >= 10f and <= 150f ? null : "must be 10..150";

    internal static string? CheckAppetite(float value) => float.IsFinite(value) && value is >= 0.25f and <= 4f ? null : "must be 0.25..4";

    internal static string? CheckMinValue(float value) => float.IsFinite(value) && value is >= 0f and <= 20f ? null : "must be 0..20";

    internal static string? CheckGuards(int value) => value is >= 0 and <= 12 ? null : "must be 0..12";

    internal static string? CheckDeath(float value) => float.IsFinite(value) && value is >= 0.01f and <= 0.5f ? null : "must be 0.01..0.5";

    internal static string? CheckVariance(float value) => float.IsFinite(value) && value is >= 0f and <= 0.5f ? null : "must be 0..0.5";
}

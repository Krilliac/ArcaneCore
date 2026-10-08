using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// vmangos PartyBotAI::UpdateOutOfCombatAI_Shaman / UpdateInCombatAI_Shaman (PartyBotAI.cpp:1389-1508) and
/// CombatBotBaseAI::SummonShamanTotems (CombatBotBaseAI.cpp:3037-3072): one totem per element for the fight.
/// </summary>
internal sealed class PlayerbotShamanRotation : PlayerbotClassRotation
{
    internal const string LightningBolt = "Lightning Bolt";
    internal const string ChainLightning = "Chain Lightning";
    internal const string EarthShock = "Earth Shock";
    internal const string FlameShock = "Flame Shock";
    internal const string FrostShock = "Frost Shock";
    internal const string Purge = "Purge";
    internal const string Stormstrike = "Stormstrike";
    internal const string ElementalMastery = "Elemental Mastery";
    internal const string LightningShield = "Lightning Shield";
    internal const string ManaTideTotem = "Mana Tide Totem";
    internal const string CurePoison = "Cure Poison";
    internal const string CureDisease = "Cure Disease";
    internal const string SearingTotem = "Searing Totem";
    internal const string MagmaTotem = "Magma Totem";
    internal const string StoneskinTotem = "Stoneskin Totem";
    internal const string StrengthOfEarthTotem = "Strength of Earth Totem";
    internal const string HealingStreamTotem = "Healing Stream Totem";
    internal const string ManaSpringTotem = "Mana Spring Totem";
    internal const string GraceOfAirTotem = "Grace of Air Totem";
    internal const string WindfuryTotem = "Windfury Totem";

    /// <summary>Totem names by element (a new totem of an element replaces the old one).</summary>
    internal static readonly string[] EarthTotems = [StoneskinTotem, StrengthOfEarthTotem, "Earthbind Totem", "Stoneclaw Totem", "Tremor Totem"];
    internal static readonly string[] FireTotems = [SearingTotem, MagmaTotem, "Fire Nova Totem", "Flametongue Totem", "Frost Resistance Totem"];
    internal static readonly string[] WaterTotems = [HealingStreamTotem, ManaSpringTotem, ManaTideTotem, "Fire Resistance Totem", "Poison Cleansing Totem", "Disease Cleansing Totem"];
    internal static readonly string[] AirTotems = [GraceOfAirTotem, WindfuryTotem, "Nature Resistance Totem", "Windwall Totem", "Tranquil Air Totem"];

    public override Class Class => Class.Shaman;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        LightningBolt, ChainLightning, EarthShock, FlameShock, FrostShock, Purge, Stormstrike, ElementalMastery, LightningShield,
        ManaTideTotem, CurePoison, CureDisease, SearingTotem, MagmaTotem, StoneskinTotem, StrengthOfEarthTotem, HealingStreamTotem,
        ManaSpringTotem, GraceOfAirTotem, WindfuryTotem,
    ];

    protected override IEnumerable<string> Dispels => [CureDisease, CurePoison];

    /// <summary>An enhancement shaman fights in melee; elemental and restoration shamans cast from range.</summary>
    public override float PreferredRange(RotationState state)
        => state.Role == PlayerbotRole.MeleeDps || !state.Spells.Has(LightningBolt) ? MeleeRange : CasterRange;

    protected override RotationAction? Upkeep(RotationState s)
        => Do(s, LightningShield, s.Self)
            ?? (s.Role == PlayerbotRole.Healer ? HealInjuredAlly(s) : null);

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        RotationAction? support = Do(s, ManaTideTotem, s.Self, s.InCombat && s.PowerPercent < 50f && !HasTotem(s, WaterTotems))
            ?? (s.Role == PlayerbotRole.Healer ? HealInjuredAlly(s, 50f, 90f) : null);
        if (support is not null) return support;
        if (s.Role == PlayerbotRole.Healer && !HealerMayFight(s)) return Totems(s, v);

        return Do(s, ElementalMastery, s.Self, s.InCombat && s.Attackers.Count == 0)
            ?? Do(s, EarthShock, v, v.IsCasting)
            ?? Do(s, FrostShock, v, v.IsMoving && s.InCombat)
            ?? Do(s, Stormstrike, v)
            ?? Do(s, ChainLightning, v, AttackersWithin(s, 30f) > 1)
            ?? PurgeVictim(s, Purge, v)
            ?? Do(s, FlameShock, v, s.InCombat)
            ?? Do(s, LightningBolt, v, s.Role != PlayerbotRole.MeleeDps || !v.InMeleeRange)
            ?? Totems(s, v)
            ?? (s.Role != PlayerbotRole.Healer && s.Self.HealthPercent < 20f ? HealInjured(s, s.Self) : null);
    }

    /// <summary>
    /// vmangos SummonShamanTotems once the fight is on and the victim is close: a fire totem in reach of the victim, an earth
    /// totem for the role, a water totem when someone is hurt or mana runs low, and an air totem for melee.
    /// </summary>
    internal static RotationAction? Totems(RotationState s, RotationUnit v)
    {
        if (!s.InCombat || v.Distance > 20f) return null;
        bool melee = s.Role is PlayerbotRole.MeleeDps or PlayerbotRole.Tank;
        bool hurt = s.Friends.Any(f => f.IsAlive && f.HealthPercent < 80f);
        return (HasTotem(s, FireTotems) ? null : Do(s, MagmaTotem, s.Self, AttackersWithin(s, 8f) > 2) ?? Do(s, SearingTotem, s.Self))
            ?? (HasTotem(s, EarthTotems) ? null
                : melee ? Do(s, StrengthOfEarthTotem, s.Self) ?? Do(s, StoneskinTotem, s.Self)
                : Do(s, StoneskinTotem, s.Self, s.Attackers.Count > 0))
            ?? (HasTotem(s, WaterTotems) ? null
                : Do(s, HealingStreamTotem, s.Self, hurt) ?? Do(s, ManaSpringTotem, s.Self, s.PowerPercent < 60f))
            ?? (HasTotem(s, AirTotems) || !melee ? null : Do(s, WindfuryTotem, s.Self) ?? Do(s, GraceOfAirTotem, s.Self));
    }

    internal static bool HasTotem(RotationState s, string[] element)
        => s.Totems.Any(t => element.Any(name => t.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
}

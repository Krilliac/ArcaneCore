namespace ArcaneCore.Game.Spells.Rules.Diminishing;

/// <summary>Diminishing-return groups (vmangos SharedDefines.h:1350-1375 DiminishingGroup).</summary>
public enum DiminishingGroup
{
    None = 0,
    ControlStun,
    TriggerStun,
    Sleep,
    ControlRoot,
    TriggerRoot,
    Fear,
    Charm,
    Polymorph,
    KidneyShot,
    DeathCoil,
    WarlockFear,
    Disarm,
    Silence,
    Freeze,
    Knockout,
    Banish,

    /// <summary>Only meant to cap the duration at 10 s in PvP; vmangos never enforces it, so neither does this code.</summary>
    LimitOnly,
}

/// <summary>Whom a group diminishes (vmangos SharedDefines.h:1339-1345 DiminishingReturnsType).</summary>
public enum DiminishingType
{
    None = 0,

    /// <summary>Only when the aura lands on a player (and, for the duration, the caster is player-like too).</summary>
    Player = 1,

    /// <summary>Always, creatures included.</summary>
    All = 2,
}

/// <summary>The diminishing levels (vmangos SharedDefines.h:1381-1386) and their duration multipliers (SpellEntry.h:78-95).</summary>
public enum DiminishingLevel
{
    Level1 = 0,
    Level2 = 1,
    Level3 = 2,
    Immune = 3,
}

/// <summary>Group facts: the type of a group and the duration multiplier of a level.</summary>
public static class DiminishingGroups
{
    /// <summary>vmangos GetDiminishingReturnsGroupType (SpellEntry.h:52-74).</summary>
    public static DiminishingType TypeOf(DiminishingGroup group) => group switch
    {
        DiminishingGroup.ControlStun or DiminishingGroup.TriggerStun or DiminishingGroup.KidneyShot => DiminishingType.All,
        DiminishingGroup.Sleep or DiminishingGroup.ControlRoot or DiminishingGroup.TriggerRoot or DiminishingGroup.Fear
            or DiminishingGroup.Charm or DiminishingGroup.Polymorph or DiminishingGroup.Silence or DiminishingGroup.Disarm
            or DiminishingGroup.DeathCoil or DiminishingGroup.Freeze or DiminishingGroup.Banish or DiminishingGroup.WarlockFear
            or DiminishingGroup.Knockout => DiminishingType.Player,
        _ => DiminishingType.None,
    };

    /// <summary>vmangos GetDiminishingRate (SpellEntry.h:76-95): 1, 1/2, 1/4, 0.</summary>
    public static float RateOf(DiminishingLevel level) => level switch
    {
        DiminishingLevel.Level1 => 1.0f,
        DiminishingLevel.Level2 => 0.5f,
        DiminishingLevel.Level3 => 0.25f,
        DiminishingLevel.Immune => 0.0f,
        _ => 1.0f,
    };
}

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature tuning, bound from the <c>Creatures</c> configuration section. Defaults are
/// vmangos' and cmangos-classic's World.cpp defaults unless stated.
/// </summary>
public sealed partial class CreatureOptions
{
    public const string SectionName = "Creatures";

    /// <summary>Corpse.Decay.NORMAL (s): vmangos + cmangos-classic 300.</summary>
    public uint CorpseDecayNormalSeconds { get; set; } = 300;

    /// <summary>Corpse.Decay.RARE (s): 900.</summary>
    public uint CorpseDecayRareSeconds { get; set; } = 900;

    /// <summary>Corpse.Decay.ELITE (s): 600.</summary>
    public uint CorpseDecayEliteSeconds { get; set; } = 600;

    /// <summary>Corpse.Decay.RAREELITE (s): 1200.</summary>
    public uint CorpseDecayRareEliteSeconds { get; set; } = 1200;

    /// <summary>Corpse.Decay.WORLDBOSS (s): 3600.</summary>
    public uint CorpseDecayWorldBossSeconds { get; set; } = 3600;

    /// <summary>Random and waypoint movement; off leaves every creature idle at its spawn point.</summary>
    public bool MovementEnabled { get; set; } = true;

    /// <summary>Rate.Creature.Aggro: multiplies every aggro radius (0 disables aggro on sight).</summary>
    public float AggroRate { get; set; } = 1.0f;

    /// <summary>CreatureFamilyAssistanceRadius (yd): same-faction creatures this close to a creature entering combat join it.</summary>
    public float AssistanceRadius { get; set; } = 10.0f;

    /// <summary>CreatureFamilyAssistanceDelay (ms) before those helpers attack.</summary>
    public uint AssistanceDelayMs { get; set; } = 1500;

    /// <summary>CreatureFamilyFleeAssistanceRadius (yd): how far a fleeing creature looks for help.</summary>
    public float FleeAssistanceRadius { get; set; } = 30.0f;

    /// <summary>CreatureFamilyFleeDelay (ms): timed flight when no helper is found.</summary>
    public uint FleeDelayMs { get; set; } = 7000;

    /// <summary>ThreatRadius (yd): a victim farther than this from where combat began is dropped (leash); none in instances.</summary>
    public float ThreatRadius { get; set; } = 60.0f;

    /// <summary>FactionTemplate.dbc for creature hostility when no catalog is registered (empty = nobody aggroes on sight).</summary>
    public string? FactionTemplateDbcPath { get; set; }
}

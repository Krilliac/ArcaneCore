namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>Creature respawn and corpse fidelity switches (<c>Creatures:Respawn</c>). Every default is retail.</summary>
    public CreatureRespawnOptions Respawn { get; } = new();
}

/// <summary>
/// <c>Creatures:Respawn:*</c>. A non-default value is a deliberate deviation from retail 1.12.1 and is documented in
/// docs/areas/creature-movement-spawns.md.
/// </summary>
public sealed class CreatureRespawnOptions
{
    /// <summary>
    /// <c>Creatures:Respawn:DrawDelayAtLoad</c>: a spawn's respawn delay (<c>urand(spawntimesecsmin, spawntimesecsmax)</c>) is drawn once when
    /// the creature object is created and reused at every death (vmangos Creature::LoadFromDB, Objects/Creature.cpp:1963; SetDeathState
    /// reads <c>m_respawnDelay</c>, :2246). False draws again at every death (the earlier ArcaneCore behaviour).
    /// </summary>
    public bool DrawDelayAtLoad { get; set; } = true;

    /// <summary>
    /// <c>Creatures:Respawn:HonorTemplateCorpseDecay</c>: let a template's <c>CorpseDecay</c> column override the rank delay. It is a
    /// cmangos column; vmangos sets the corpse delay by rank alone (Creature.cpp:1326-1343), which is retail.
    /// </summary>
    public bool HonorTemplateCorpseDecay { get; set; }

    /// <summary>
    /// <c>Creatures:Respawn:AlternateEntries</c>: a spawn with <c>creature_spawn_entry</c> rows (vmangos <c>id2</c> ... <c>id5</c>) becomes one of
    /// those entries when it loads and again at every respawn (cmangos Creature::LoadFromDB / ResetEntry; vmangos Creature.cpp:830-841,
    /// :1936-1944). False ignores the rows: a spawn whose <c>id</c> is 0 then never spawns (the earlier behaviour).
    /// </summary>
    public bool AlternateEntries { get; set; } = true;
}

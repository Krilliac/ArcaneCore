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

    /// <summary>
    /// <c>Creatures:Respawn:Persist</c>: dead spawns keep their respawn time across restarts (vmangos <c>creature_respawn</c>, characters
    /// database). False keeps the timers in memory only, as before.
    /// </summary>
    public bool Persist { get; set; } = true;

    /// <summary>
    /// <c>Creatures:Respawn:SaveImmediately</c>: every database spawn saves its respawn time at death (vmangos SaveRespawnTimeImmediately = 1,
    /// mangosd.conf.dist.in:397, World.cpp:729). False saves a normal creature only when it leaves the map or at shutdown; a world boss is
    /// always saved at death (Creature.cpp:2262-2263).
    /// </summary>
    public bool SaveImmediately { get; set; } = true;

    /// <summary>
    /// <c>Creatures:Respawn:Linked</c>: carry the creature_linking aggro, respawn and despawn events (cmangos CreatureLinkingHolder::ProcessSlave,
    /// Entities/CreatureLinkingMgr.cpp:555-612, and CanSpawn, :699-751): a boss's trash respawns, despawns or dies with it, and a slave with
    /// FLAG_CANT_SPAWN_IF_BOSS_DEAD / ALIVE waits on its master. Retail; false keeps only FLAG_FOLLOW and the instance scripts' own handling.
    /// </summary>
    public bool Linked { get; set; } = true;

    /// <summary>
    /// <c>Creatures:Respawn:DynamicRate</c>: TrinityCore Respawn.DynamicRateCreature (Map::ApplyDynamicModeRespawnScaling, Maps/Map.cpp:3312-3354).
    /// A dying open-world spawn's respawn delay is multiplied by <c>DynamicRate / players in its zone</c> when that is below 1, never under
    /// <see cref="DynamicMinimumSeconds"/>. 0 (the default) is off, which is retail 1.12.1; TrinityCore's suggested value is 10.
    /// Dungeons, raids, battlegrounds, rares and world bosses are never scaled.
    /// </summary>
    public float DynamicRate { get; set; }

    /// <summary><c>Creatures:Respawn:DynamicMinimumSeconds</c>: TrinityCore Respawn.DynamicMinimumCreature (default 10 s).</summary>
    public uint DynamicMinimumSeconds { get; set; } = 10;
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// What scripted creature AIs (the ScriptDev counterparts: <see cref="EscortAI"/> and the battleground scripts) need from the map: a script AI
/// per entry for this map (vmangos ScriptName, which the AI selector tries before the template's AIName), the home position, a near
/// teleport, idle movement, script texts, and the creatures of an entry around a point.
/// </summary>
public sealed partial class CreatureMapSystem
{
    private readonly Dictionary<uint, Func<Creature, CreatureAI>> _entryAis = [];
    private readonly HashSet<uint> _reportedEscorts = [];
    private readonly HashSet<int> _reportedTexts = [];
    private readonly HashSet<Creature> _corpseDespawns = new(ReferenceEqualityComparer.Instance);

    /// <summary>A player-linked escort failed (vmangos npc_escortAI::JustDied): its quest fails for the player's group.</summary>
    /// <summary>vmangos Player::KilledMonster(cInfo, guid) from a script: the kill credit of <paramref name="entry"/> without group credit.</summary>
    internal void KilledMonsterCredit(Player player, uint entry, ObjectGuid source) => _ai.ScriptQuests?.KilledMonsterCredit(player, entry, source);

    internal void FailEscortQuest(Player player, uint questId) => _ai.ScriptQuests?.GroupEventFailHappens(player, questId);

    /// <summary>
    /// mangos-classic Player::RewardPlayerAndGroupAtEventExplored (Player.cpp:13427): the quest's exploration or event objective is done for
    /// the player and every group member near <paramref name="source"/> (what an escort gives at its last point). Without the quest seam nothing.
    /// </summary>
    internal void RewardGroupEventExplored(Player player, uint questId, Creature source) => _ai.QuestEvents?.EventHappened(player, questId, source, rewardGroup: true);

    /// <summary>The online members of an escort player's group (vmangos npc_escortAI::IsPlayerOrGroupInRange); empty when not grouped.</summary>
    internal IReadOnlyList<Player> EscortGroupMembers(Player player) => _ai.ScriptQuests?.GroupMembersOf(player) ?? [];

    /// <summary>
    /// Give every creature of <paramref name="entry"/> on this map the AI <paramref name="factory"/> builds (vmangos FactorySelector::selectAI
    /// asks the script name first, AI/CreatureAISelector.cpp:37-50). Creatures already in the map take it at once; a later registration of the
    /// same entry replaces the earlier one. An instance script registering from <c>OnCreatureCreate</c> passes
    /// <paramref name="rebuildExisting"/> = <c>creature.AI is not null</c>: during a grid load the hook runs before the creature's AI is built (so
    /// nothing needs rebuilding), while an instance data attached after a grid loaded early sees creatures whose AI already exists.
    /// </summary>
    public void RegisterEntryAi(uint entry, Func<Creature, CreatureAI> factory, bool rebuildExisting = true)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _entryAis[entry] = factory;
        if (rebuildExisting) RebuildAi(entry);
    }

    /// <summary>Stop giving creatures of <paramref name="entry"/> a script AI; those in the map go back to their template's AI.</summary>
    public void UnregisterEntryAi(uint entry)
    {
        if (_entryAis.Remove(entry))
        {
            RebuildAi(entry);
        }
    }

    /// <summary>
    /// The script AI of the creature's entry on this map, if any (consulted before the template's AIName). As vmangos selectAI allows
    /// (AI/CreatureAISelector.cpp:39-46): not for a charmed creature nor a controlled pet; a wild summon, a guardian or a mini pet may be
    /// scripted.
    /// </summary>
    private CreatureAI? CreateEntryAi(Creature creature)
    {
        if (creature.Summon is { Kind: SummonKind.Pet } || !creature.CharmerGuid.IsEmpty)
        {
            return null;
        }

        if (_entryAis.TryGetValue(creature.Template.Entry, out Func<Creature, CreatureAI>? factory))
        {
            return factory(creature);
        }

        return Instances.Scripts.DungeonBossAis.Create(creature, Map.MapId);
    }

    /// <summary>Whether this map gives creatures of <paramref name="entry"/> a script AI (<see cref="RegisterEntryAi"/>).</summary>
    internal bool HasEntryAi(uint entry) => _entryAis.ContainsKey(entry);

    private void RebuildAi(uint entry)
    {
        foreach (Creature creature in _creatures.Values.Where(c => c.Template.Entry == entry).ToArray())
        {
            ForgetAi(creature);
            CreateAi(creature);
            if (creature.IsAlive)
            {
                creature.AI?.OnRespawn();
            }
        }
    }

    /// <summary>vmangos Creature::SetHomePosition: where the creature goes back to after a fight (and, here, where it respawns).</summary>
    public void SetHomePosition(Creature creature, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(creature);
        creature.SetHome(new CreatureHome(x, y, z, orientation));
    }

    /// <summary>
    /// vmangos Unit::NearTeleportTo for a creature of this map: it stops, is put at the place and its observers see the jump
    /// (MSG_MOVE_TELEPORT before and after the relocation, as the pet revival does).
    /// </summary>
    public void NearTeleport(Creature creature, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        StopMoving(creature);
        MovementInfo moved = creature.Movement;
        moved.X = x;
        moved.Y = y;
        moved.Z = z;
        moved.Orientation = orientation;
        moved.Time = _serverTime();
        byte[] teleport = TeleportPackets.BuildMoveTeleport(creature.Guid, moved);
        Map.BroadcastToObservers(creature, WorldOpcode.MsgMoveTeleport, teleport);
        creature.Relocate(x, y, z, orientation, _serverTime());
        Map.OnObjectMoved(creature);
        Map.BroadcastToObservers(creature, WorldOpcode.MsgMoveTeleport, teleport);
    }

    /// <summary>vmangos MotionMaster::MovementExpired + MoveIdle: the creature stops and idles as its default movement.</summary>
    public void MoveIdle(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        StopMoving(creature);
        creature.Motion.Initialize(IdleMovementGenerator.Instance, this, start: true);
    }

    /// <summary>
    /// vmangos SetDefaultMovementType(RANDOM_MOTION_TYPE) + SetWanderDistance taking effect (MotionMaster::Initialize): the creature's default
    /// movement becomes a wander of <paramref name="wanderDistance"/> yards around its home, started now.
    /// </summary>
    public void SetDefaultRandomMovement(Creature creature, float wanderDistance)
    {
        ArgumentNullException.ThrowIfNull(creature);
        creature.Motion.Initialize(new RandomMovementGenerator(wanderDistance, creature.Home, run: null), this, start: creature.IsAlive);
    }

    /// <summary>
    /// ScriptDev DoScriptText(textId, source, target) with a broadcast text or creature_ai_texts id: the line is spoken the way its chat type
    /// says. A missing text is logged once and nothing is said.
    /// </summary>
    public void SayText(Creature creature, int textId, Unit? target = null)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (_content.Ai.FindText(textId) is { } text)
        {
            Say(creature, text, target);
        }
        else if (_reportedTexts.Add(textId))
        {
            _logger.LogWarning("script text {Text} of creature {Entry} is not in broadcast_text or creature_ai_texts; nothing is said", textId, creature.Template.Entry);
        }
    }

    /// <summary>
    /// ScriptDev GetCreatureListWithEntryInGrid: the creatures of <paramref name="entry"/> of this map within <paramref name="range"/> yards of
    /// <paramref name="center"/> (3D, the living and the dead that are still in the world), nearest first.
    /// </summary>
    public IReadOnlyList<Creature> CreaturesOfEntryInRange(WorldObject center, uint entry, float range)
    {
        ArgumentNullException.ThrowIfNull(center);
        float rangeSq = range * range;
        return [.. _creatures.Values
            .Where(c => c.Template.Entry == entry && c.DeathState != CreatureDeathState.Dead && DistanceSquared3D(center, c) <= rangeSq)
            .OrderBy(c => DistanceSquared3D(center, c))];
    }

    /// <summary>
    /// ScriptDev SummonCreature(entry, x, y, z, o, TEMPSUMMON_CORPSE_DESPAWN): a temporary creature at the place, summoned by
    /// <paramref name="summoner"/> (its AI hears of it), that stays until it dies and then goes with its corpse at once. Null for a missing
    /// template (reported once).
    /// </summary>
    public Creature? SummonCorpseDespawn(Creature summoner, uint entry, float x, float y, float z, float orientation)
    {
        Creature? summoned = SummonDeadDespawn(summoner, entry, x, y, z, orientation);
        if (summoned is not null)
        {
            _corpseDespawns.Add(summoned);
        }

        return summoned;
    }

    /// <summary>
    /// ScriptDev SummonCreature(entry, x, y, z, o, TEMPSPAWN_DEAD_DESPAWN, 0) (cmangos Entities/Object.h: "despawns when the creature
    /// disappears"), the type nearly every ScriptDev2 instance summon uses: a temporary creature at the place, summoned by
    /// <paramref name="summoner"/> (its AI hears of it), that stays until it dies and then lies as an ordinary corpse - lootable - until the
    /// corpse decays, when it is gone for good (a temporary creature never respawns). Null for a missing template (reported once).
    /// </summary>
    public Creature? SummonDeadDespawn(Creature summoner, uint entry, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(summoner);
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("{Creature} script summons missing creature_template {Entry}; skipped", summoner.Guid, entry);
            }

            return null;
        }

        return SpawnTemporary(template, x, y, z, orientation, summoner);
    }

    /// <summary>Instance script summon when the event has no creature summoner (SD2 player or game-object summon).</summary>
    public Creature? SummonInstanceCreature(uint entry, float x, float y, float z, float orientation)
    {
        if (_content.FindTemplate(entry) is not { } template)
        {
            return null;
        }

        Creature creature = SpawnTemporary(template, x, y, z, orientation);
        MarkCorpseDespawn(creature);
        return creature;
    }

    /// <summary>
    /// Instance script summon without a creature summoner (a game object's SummonCreature) as cmangos TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN:
    /// it goes after <paramref name="despawnMs"/> alive, out of combat and uncharmed, and a dead one with its corpse decay.
    /// </summary>
    public Creature? SummonInstanceCreatureTimedOocOrDead(uint entry, float x, float y, float z, float orientation, uint despawnMs)
    {
        if (_content.FindTemplate(entry) is not { } template)
        {
            return null;
        }

        Creature creature = SpawnTemporary(template, x, y, z, orientation);
        AddTimedSummon(creature, despawnMs, SummonTimer.OutOfCombatUncharmed);
        return creature;
    }

    /// <summary>
    /// TEMPSUMMON_CORPSE_DESPAWN for a temporary creature a script put into the map itself (an object's summon): it goes with its corpse at
    /// once when it dies. A database spawn is left alone.
    /// </summary>
    public void MarkCorpseDespawn(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (summoned.Spawn is null && _creatures.ContainsKey(summoned.Guid))
        {
            _corpseDespawns.Add(summoned);
        }
    }

    /// <summary>A creature summoned with <see cref="SummonCorpseDespawn"/> died: its corpse goes on the next update (TemporarySummon CORPSE_DESPAWN).</summary>
    private void DespawnCorpseOfSummon(Creature creature)
    {
        if (_corpseDespawns.Remove(creature))
        {
            ForcedDespawn(creature, 1);
        }
    }

    /// <summary>
    /// vmangos Creature::SelectNearestTarget(range) for a script: the nearest unit within <paramref name="range"/> yards the creature may attack
    /// on sight but for the aggro radius (a living player who is not a game master, or another creature with <c>CreatureAggroOnCreatures</c>;
    /// hostile, attackable and in line of sight). Null when there is none.
    /// </summary>
    public Unit? SelectNearestTarget(Creature creature, float range)
    {
        ArgumentNullException.ThrowIfNull(creature);
        float rangeSq = range * range;
        IEnumerable<Unit> candidates = Map.Players;
        if (_options.CreatureAggroOnCreatures)
        {
            candidates = candidates.Concat(_creatures.Values);
        }

        return candidates
            .Where(u => IsAggroTarget(creature, u) && DistanceSquared3D(creature, u) <= rangeSq
                && Map.Combat.Hooks.CanAttack(creature, u) && _ai.Hostility.IsHostile(creature, u) && InLineOfSight(creature, u))
            .OrderBy(u => DistanceSquared3D(creature, u))
            .FirstOrDefault();
    }

    /// <summary>An escort was started on an entry without escort points (vmangos "EscortAI Start with 0 waypoints", once per entry).</summary>
    internal void ReportEscortWithoutPath(Creature creature, string path)
    {
        if (_reportedEscorts.Add(creature.Template.Entry))
        {
            _logger.LogWarning("escort of creature {Entry} has no points ({Path}); it does not start", creature.Template.Entry, path);
        }
    }

    private static float DistanceSquared3D(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

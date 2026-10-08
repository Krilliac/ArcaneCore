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

    /// <summary>
    /// Give every creature of <paramref name="entry"/> on this map the AI <paramref name="factory"/> builds (vmangos FactorySelector::selectAI
    /// asks the script name first, AI/CreatureAISelector.cpp:37-50). Creatures already in the map take it at once; a later registration of the
    /// same entry replaces the earlier one.
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
        => _entryAis.Count > 0 && creature.Summon is not { Kind: SummonKind.Pet } && creature.CharmerGuid.IsEmpty
            && _entryAis.TryGetValue(creature.Template.Entry, out Func<Creature, CreatureAI>? factory)
            ? factory(creature)
            : null;

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
        ArgumentNullException.ThrowIfNull(summoner);
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("{Creature} script summons missing creature_template {Entry}; skipped", summoner.Guid, entry);
            }

            return null;
        }

        Creature summoned = SpawnTemporary(template, x, y, z, orientation, summoner);
        _corpseDespawns.Add(summoned);
        return summoned;
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
    internal void ReportEscortWithoutPath(Creature creature)
    {
        if (_reportedEscorts.Add(creature.Template.Entry))
        {
            _logger.LogWarning("escort of creature {Entry} has no points (creature_movement_template path {Path}); it does not start",
                creature.Template.Entry, EscortAI.EscortPathId);
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

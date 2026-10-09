using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Temporary summons with a lifetime: cmangos EventAI SUMMON and the guards a civilian calls.</summary>
public sealed partial class CreatureMapSystem
{
    private readonly List<TimedSummon> _summons = [];

    /// <summary>When a timed temporary creature goes away.</summary>
    private enum SummonTimer
    {
        /// <summary>ScriptDev2 TEMPSPAWN_TIMED_DESPAWN: lifetime runs even in combat.</summary>
        Absolute,
        /// <summary>
        /// vmangos TEMPSUMMON_TIMED_OR_DEAD_DESPAWN (Objects/TemporarySummon.cpp:127-148): the lifetime counts down only while the creature
        /// is alive and out of combat, and starts again from the whole lifetime while it fights (or lies dead: its corpse then goes with
        /// the corpse decay, as IsDespawned does).
        /// </summary>
        OutOfCombat,

        /// <summary>
        /// cmangos TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN (Entities/TemporarySpawn.cpp:129-149): <see cref="OutOfCombat"/>, and the timer is
        /// also held while the creature is charmed.
        /// </summary>
        OutOfCombatUncharmed,

        /// <summary>
        /// cmangos TEMPSPAWN_TIMED_OOC_OR_CORPSE_DESPAWN (Entities/TemporarySpawn.cpp:107-127): combat restarts the whole lifetime and an
        /// alive creature out of combat counts it down; a dead one goes on the first update after it dies (the case opens with
        /// <c>if (IsDead()) UnSummon()</c>, and Unit::IsDead, Unit.h:1817, is true for both CORPSE and DEAD), so it leaves no corpse.
        /// </summary>
        OutOfCombatOrCorpse,

        /// <summary>
        /// cmangos TEMPSPAWN_TIMED_OOC_DESPAWN (Entities/TemporarySpawn.cpp:45-58): the lifetime counts down only while the creature is alive
        /// and out of combat and starts again while it fights; a dead one is left to its corpse decay. EventAI uses it with 0 ms for a
        /// summon with no lifetime, which then goes as soon as it is alive and out of combat.
        /// </summary>
        AliveOutOfCombat,

        /// <summary>
        /// cmangos TEMPSPAWN_CORPSE_TIMED_DESPAWN (Entities/TemporarySpawn.cpp:67-83, :281-282): no timer while alive; the lifetime starts at
        /// death and the corpse goes when it runs out.
        /// </summary>
        CorpseTimed,
    }

    /// <summary>A temporary creature with a lifetime; <see cref="DespawnAtMs"/> moves forward while an out-of-combat timer is held.</summary>
    private sealed class TimedSummon(Creature creature, uint lifetimeMs, SummonTimer timer, long despawnAtMs)
    {
        public Creature Creature { get; } = creature;

        public uint LifetimeMs { get; } = lifetimeMs;

        public SummonTimer Timer { get; } = timer;

        public long DespawnAtMs { get; set; } = despawnAtMs;
    }

    private void AddTimedSummon(Creature creature, uint lifetimeMs, SummonTimer timer)
        => _summons.Add(new TimedSummon(creature, lifetimeMs, timer, _clockMs + lifetimeMs));

    /// <summary>ScriptDev2 TEMPSPAWN_TIMED_OOC_DESPAWN (TemporarySpawn.cpp:45-58): a script's temporary creature
    /// counts down only while alive and out of combat; combat restarts its full lifetime.</summary>
    public void MarkTimedOutOfCombatDespawn(Creature creature, uint lifetimeMs)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Spawn is null && _creatures.ContainsKey(creature.Guid))
            AddTimedSummon(creature, lifetimeMs, SummonTimer.AliveOutOfCombat);
    }

    /// <summary>
    /// Despawn the timed summons whose time is up (after every creature's tick). An <see cref="SummonTimer.OutOfCombat"/> timer is put back
    /// to the whole lifetime on every update the creature is in combat or dead (vmangos <c>m_timer = m_lifetime</c>), so it runs out only
    /// after a whole lifetime alive and out of combat.
    /// </summary>
    private void UpdateTimedSummons()
    {
        if (_summons.Count == 0)
        {
            return;
        }

        List<Creature>? expired = null;
        for (int i = _summons.Count - 1; i >= 0; i--)
        {
            TimedSummon summon = _summons[i];
            Creature creature = summon.Creature;
            bool held = summon.Timer switch
            {
                SummonTimer.OutOfCombat or SummonTimer.AliveOutOfCombat => creature.Combat.IsInCombat || !creature.IsAlive,
                SummonTimer.OutOfCombatUncharmed => creature.Combat.IsInCombat || !creature.IsAlive || !creature.CharmerGuid.IsEmpty,
                SummonTimer.OutOfCombatOrCorpse => creature.IsAlive && creature.Combat.IsInCombat,
                SummonTimer.CorpseTimed => creature.IsAlive,
                _ => false,
            };
            if (held)
            {
                summon.DespawnAtMs = _clockMs + summon.LifetimeMs;
                continue;
            }

            // TEMPSPAWN_TIMED_OOC_OR_CORPSE_DESPAWN unsummons a dead creature at once, whatever is left of its lifetime.
            bool goneAtDeath = summon.Timer == SummonTimer.OutOfCombatOrCorpse && !creature.IsAlive;
            if (goneAtDeath || summon.DespawnAtMs <= _clockMs)
            {
                _summons.RemoveAt(i);
                (expired ??= []).Add(creature);
            }
        }

        if (expired is not null)
        {
            foreach (Creature gone in expired)
            {
                Despawn(gone);
            }
        }
    }

    /// <summary>
    /// cmangos EventAI SUMMON: a temporary creature at the summoner's position that attacks
    /// <paramref name="target"/> and despawns after <paramref name="despawnMs"/> alive, out of combat and
    /// uncharmed (TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN, CreatureEventAI.cpp:819-820). 0 is TEMPSPAWN_TIMED_OOC_DESPAWN
    /// with 0 ms (:821-822): the summon despawns as soon as it is alive and out of combat, so one that does not
    /// start fighting goes on the next update. A missing template is reported once and summons nothing.
    /// </summary>
    public Creature? Summon(Creature summoner, uint entry, Unit? target, uint despawnMs)
    {
        ArgumentNullException.ThrowIfNull(summoner);
        return SummonAt(summoner, entry, summoner.X, summoner.Y, summoner.Z, summoner.Orientation, target, despawnMs);
    }

    /// <summary>
    /// <see cref="Summon"/> at a given position (cmangos EventAI SUMMON_ID: the creature_ai_summons row's position and lifetime,
    /// CreatureEventAI.cpp:1003-1029). <paramref name="oocOrCorpse"/> is a ScriptDev2 TEMPSPAWN_TIMED_OOC_OR_CORPSE_DESPAWN summon instead
    /// (<see cref="SummonTimer.OutOfCombatOrCorpse"/>).
    /// </summary>
    public Creature? SummonAt(Creature summoner, uint entry, float x, float y, float z, float orientation, Unit? target, uint despawnMs,
        bool oocOrCorpse = false)
    {
        ArgumentNullException.ThrowIfNull(summoner);
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("{Creature} EventAI summons missing creature_template {Entry}; skipped", summoner.Guid, entry);
            }

            return null;
        }

        Creature summoned = SpawnTemporary(template, x, y, z, orientation, summoner); // cmangos SummonCreature → JustSummoned
        AddTimedSummon(summoned, despawnMs, oocOrCorpse ? SummonTimer.OutOfCombatOrCorpse
            : despawnMs > 0 ? SummonTimer.OutOfCombatUncharmed : SummonTimer.AliveOutOfCombat);
        AttackOnSummon(summoned, target);
        return summoned;
    }

    /// <summary>
    /// ScriptDev2 SummonCreature(..., TEMPSPAWN_CORPSE_TIMED_DESPAWN, <paramref name="corpseMs"/>): alive until killed, then a corpse for
    /// <paramref name="corpseMs"/> (<see cref="SummonTimer.CorpseTimed"/>); it attacks <paramref name="target"/> as a JustSummoned
    /// AttackStart would. Null for a missing template.
    /// </summary>
    public Creature? SummonCorpseTimedDespawn(Creature summoner, uint entry, float x, float y, float z, float orientation, Unit? target, uint corpseMs)
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
        AddTimedSummon(summoned, corpseMs, SummonTimer.CorpseTimed);
        AttackOnSummon(summoned, target);
        return summoned;
    }

    private void AttackOnSummon(Creature summoned, Unit? target)
    {
        if (target is not null && target.IsAlive)
        {
            if (summoned.AI is { } ai)
            {
                ai.AttackStart(target);
            }
            else
            {
                AttackStart(summoned, target);
            }
        }
    }

    /// <summary>ScriptDev2 GameObject::SummonCreature with TEMPSPAWN_TIMED_DESPAWN, used by Maraudon's larva spewer.</summary>
    public Creature? SummonFromGameObject(uint entry, float x, float y, float z, float orientation, uint lifetimeMs)
    {
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("instance game object summons missing creature_template {Entry}; skipped", entry);
            }

            return null;
        }

        Creature creature = SpawnTemporary(template, x, y, z, orientation);
        AddTimedSummon(creature, lifetimeMs, SummonTimer.Absolute);
        return creature;
    }

    /// <summary>ScriptDev2 instance summon with TEMPSPAWN_DEAD_DESPAWN; the temporary creature goes with its corpse.</summary>
    public Creature? SummonForInstance(uint entry, float x, float y, float z, float orientation)
    {
        if (_content.FindTemplate(entry) is not { } template)
        {
            if (_reportedAi.Add($"summon:{entry}"))
            {
                _logger.LogWarning("instance script summons missing creature_template {Entry}; skipped", entry);
            }

            return null;
        }

        Creature creature = SpawnTemporary(template, x, y, z, orientation);
        MarkCorpseDespawn(creature);
        return creature;
    }
}

using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Temporary EventAI summons (cmangos EventAI SUMMON).</summary>
public sealed partial class CreatureMapSystem
{
    private readonly List<TimedSummon> _summons = [];

    /// <summary>When a timed temporary creature goes away.</summary>
    private enum SummonTimer
    {
        /// <summary>A fixed time after the summon, whatever the creature is doing.</summary>
        Fixed,

        /// <summary>
        /// vmangos TEMPSUMMON_TIMED_OR_DEAD_DESPAWN (Objects/TemporarySummon.cpp:127-148): the lifetime counts down only while the creature
        /// is alive and out of combat, and starts again from the whole lifetime while it fights (or lies dead: its corpse then goes with
        /// the corpse decay, as IsDespawned does).
        /// </summary>
        OutOfCombat,
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
            if (summon.Timer == SummonTimer.OutOfCombat && (summon.Creature.Combat.IsInCombat || !summon.Creature.IsAlive))
            {
                summon.DespawnAtMs = _clockMs + summon.LifetimeMs;
                continue;
            }

            if (summon.DespawnAtMs <= _clockMs)
            {
                _summons.RemoveAt(i);
                (expired ??= []).Add(summon.Creature);
            }
        }

        if (expired is not null)
        {
            foreach (Creature creature in expired)
            {
                Despawn(creature);
            }
        }
    }

    /// <summary>
    /// cmangos EventAI SUMMON: a temporary creature at the summoner's position that attacks
    /// <paramref name="target"/> and despawns after <paramref name="despawnMs"/> (0 = stays until
    /// it dies or its grid unloads). A missing template is reported once and summons nothing.
    /// </summary>
    public Creature? Summon(Creature summoner, uint entry, Unit? target, uint despawnMs)
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

        Creature summoned = SpawnTemporary(template, summoner.X, summoner.Y, summoner.Z, summoner.Orientation);
        if (despawnMs > 0)
        {
            AddTimedSummon(summoned, despawnMs, SummonTimer.Fixed);
        }

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

        return summoned;
    }
}

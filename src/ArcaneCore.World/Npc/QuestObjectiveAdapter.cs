using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Routes authoritative creature deaths and spell hits to the online quest journals, on the world
/// thread: vmangos RewardPlayerAndGroupAtKill / RewardPlayerAndGroupAtCast. A grouped actor shares
/// credit with members on the same map inside the group reward distance (dead members that have
/// not released included); kills in a raid group only credit raid quests.
/// </summary>
public sealed class QuestObjectiveAdapter(IQuestObjectiveEvents objectives, Func<Player, RewardGroup?>? groups = null,
    float rewardDistance = 74.0f) : IDisposable
{
    private readonly HashSet<MapCombat> _combat = [];
    private readonly HashSet<SpellSystem> _spells = [];

    public void Attach(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (_combat.Add(map.Combat))
        {
            map.Combat.UnitKilled += OnUnitKilled;
        }
    }

    /// <summary>Stop following an unloaded map instance.</summary>
    public void Detach(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.FindUpdater<MapCombat>() is { } combat && _combat.Remove(combat))
        {
            combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>Spell-cast objectives: a player's spell reaching a creature (vmangos Spell::DoAllEffectOnTarget).</summary>
    public void Attach(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (_spells.Add(spells))
        {
            spells.SpellHitTarget += OnSpellHit;
        }
    }

    public void Dispose()
    {
        foreach (MapCombat combat in _combat)
        {
            combat.UnitKilled -= OnUnitKilled;
        }

        foreach (SpellSystem spells in _spells)
        {
            spells.SpellHitTarget -= OnSpellHit;
        }

        _combat.Clear();
        _spells.Clear();
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (!Authoritative(killer, victim, out Player? player, out Creature? creature))
        {
            return;
        }

        RewardGroup? group = groups?.Invoke(player);
        if (group is null || !group.Members.Contains(player.Guid))
        {
            if (player.IsAlive)
            {
                objectives.KilledMonsterCredit(player, creature.Entry, creature.Guid);
            }

            return;
        }

        foreach (Player member in KillRewards.Recipients(player, creature, group, rewardDistance))
        {
            if (!KillRewards.CanReceiveQuestCredit(member))
            {
                continue;
            }

            if (objectives is QuestNpcServices quests)
            {
                quests.KilledMonsterCredit(member, creature.Entry, creature.Guid, group.IsRaid);
            }
            else if (!group.IsRaid)
            {
                objectives.KilledMonsterCredit(member, creature.Entry, creature.Guid);
            }
        }
    }

    private void OnSpellHit(Unit caster, Unit target, uint spellId)
    {
        if (!Authoritative(caster, target, out Player? player, out Creature? creature))
        {
            return;
        }

        RewardGroup? group = groups?.Invoke(player);
        if (group is null || !group.Members.Contains(player.Guid))
        {
            objectives.CastedCreatureOrGo(player, creature.Entry, creature.Guid, true, spellId);
            return;
        }

        foreach (Player member in KillRewards.Recipients(player, creature, group, rewardDistance))
        {
            if (!KillRewards.CanReceiveQuestCredit(member))
            {
                continue;
            }

            if (objectives is QuestNpcServices quests)
            {
                quests.CastedCreatureOrGo(member, creature.Entry, creature.Guid, true, spellId, ReferenceEquals(member, player));
            }
            else if (ReferenceEquals(member, player))
            {
                objectives.CastedCreatureOrGo(member, creature.Entry, creature.Guid, true, spellId);
            }
        }
    }

    private static bool Authoritative(Unit? actor, Unit target,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Player? player,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Creature? creature)
    {
        player = actor as Player;
        creature = target as Creature;
        return player is { IsInWorld: true } && creature is not null && player.Map is { } map && creature.IsInWorld
            && ReferenceEquals(creature.Map, map) && ReferenceEquals(map.FindPlayer(player.Guid), player)
            && ReferenceEquals(map.FindObject(creature.Guid), creature);
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Routes authoritative creature deaths and spell hits to the online quest journals, on the world
/// thread: vmangos RewardPlayerAndGroupAtKill / RewardPlayerAndGroupAtCast. A grouped actor shares
/// credit with members on the same map inside the group reward distance. A corpse that has not
/// released still receives KilledMonster, solo or grouped; a released ghost does not. Kills in a
/// raid group only credit raid quests.
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
            spells.SpellHitGameObject += OnSpellHitGameObject;
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
            spells.SpellHitGameObject -= OnSpellHitGameObject;
        }

        _combat.Clear();
        _spells.Clear();
    }

    /// <summary>
    /// vmangos Unit::Kill (Unit.cpp:981-1008, 1094-1097): the player behind the killing blow (a pet's, totem's, guardian's or
    /// charmed creature's master; <see cref="KillRewards.CreditedPlayer"/>) starts the tap, the creature's original loot recipient
    /// and the group of the tap replace him (<see cref="KillRewards.ResolveTap"/>), and KilledMonster goes to that group
    /// (Group.cpp:2373-2380) or the tapper alone. A creature no player controls credits only a tap that passed the damage-origin rule.
    /// </summary>
    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is not Creature { IsInWorld: true } creature || creature.Map is not { } map
            || !ReferenceEquals(map.FindObject(creature.Guid), creature))
        {
            return;
        }

        // Player::RewardSinglePlayerAtKill / RewardGroupAtKill (Player.cpp:19961, 19968-19982; Group.cpp:2364, 2378): a victim a player
        // owns or charms (a totem is owned by its summoner) is a PvP kill, which gives no KilledMonster credit.
        if (KillRewards.IsPvpVictim(creature) || (TotemQuery.TryGet(creature, out TotemInfo totem) && totem.Owner is Player))
        {
            return;
        }

        Player? player = KillRewards.CreditedPlayer(killer);
        if (player is not { IsInWorld: true } || !ReferenceEquals(map.FindPlayer(player.Guid), player))
        {
            player = null;
        }

        (Player? tapper, RewardGroup? group) = KillRewards.ResolveTap(player, creature, groups ?? (_ => null));
        if (group is null)
        {
            // RewardSinglePlayerAtKill (Player.cpp:19980-19981) always calls KilledMonster for a non-PvP creature.
            // A corpse that has not released still receives it, the same rule as the group helper (Group.cpp:2345-2350);
            // a released ghost does not.
            if (tapper is not null && KillRewards.CanReceiveQuestCredit(tapper))
            {
                objectives.KilledMonsterCredit(tapper, creature.Entry, creature.Guid);
            }

            return;
        }

        foreach (Player member in KillRewards.TapRecipients(tapper, creature, group, rewardDistance))
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

    /// <summary>
    /// vmangos Spell::DoAllEffectOnTarget(GOTargetInfo) (Spell.cpp:1845-1851): the cast's charmer, owner or caster player gets
    /// CastedCreatureOrGo for the object, with his group members within reward range (Player::RewardPlayerAndGroupAtCast,
    /// Player.cpp:20019-20040); a dead member only while his body is not released, and only the caster counts as the original caster.
    /// </summary>
    internal void OnSpellHitGameObject(Unit caster, GameObject go, uint spellId)
    {
        Player? player = KillRewards.CreditedPlayer(caster);
        if (player is not { IsInWorld: true } || player.Map is not { } map || !ReferenceEquals(map.FindPlayer(player.Guid), player)
            || !go.IsInWorld || !ReferenceEquals(go.Map, map))
        {
            return;
        }

        RewardGroup? group = groups?.Invoke(player);
        if (group is null || !group.Members.Contains(player.Guid))
        {
            if (KillRewards.CanReceiveQuestCredit(player))
            {
                objectives.CastedCreatureOrGo(player, go.Entry, go.Guid, false, spellId);
            }

            return;
        }

        foreach (Player member in KillRewards.Recipients(player, go, group, rewardDistance))
        {
            if (!KillRewards.CanReceiveQuestCredit(member))
            {
                continue;
            }

            if (objectives is QuestNpcServices quests)
            {
                quests.CastedCreatureOrGo(member, go.Entry, go.Guid, false, spellId, ReferenceEquals(member, player));
            }
            else if (ReferenceEquals(member, player))
            {
                objectives.CastedCreatureOrGo(member, go.Entry, go.Guid, false, spellId);
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

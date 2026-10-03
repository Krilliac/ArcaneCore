using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.World.Npc;

/// <summary>Routes authoritative direct creature deaths to the online quest journal, on the world thread.</summary>
public sealed class QuestObjectiveAdapter(IQuestObjectiveEvents objectives) : IDisposable
{
    private readonly HashSet<MapCombat> _combat = [];

    public void Attach(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (_combat.Add(map.Combat))
        {
            map.Combat.UnitKilled += OnUnitKilled;
        }
    }

    public void Dispose()
    {
        foreach (MapCombat combat in _combat)
        {
            combat.UnitKilled -= OnUnitKilled;
        }

        _combat.Clear();
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (killer is not Player { IsAlive: true, IsInWorld: true } player || victim is not Creature creature
            || player.Map is not { } map || !creature.IsInWorld || !ReferenceEquals(creature.Map, map)
            || !ReferenceEquals(map.FindPlayer(player.Guid), player) || !ReferenceEquals(map.FindObject(creature.Guid), creature))
        {
            return;
        }

        objectives.KilledMonsterCredit(player, creature.Entry, creature.Guid);
    }
}

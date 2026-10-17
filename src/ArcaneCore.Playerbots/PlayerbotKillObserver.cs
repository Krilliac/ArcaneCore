using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// One kill listener per map combat, with a direct lookup for the managed bot that died.
/// MapCombat.Kill raises UnitKilled after it clears the victim's combat lists; the event
/// still carries the actual killer when a lethal hit falls between two bot updates.
/// </summary>
internal sealed class PlayerbotKillObserver(MapCombat combat)
{
    private static readonly ConditionalWeakTable<MapCombat, PlayerbotKillObserver> ByCombat = new();
    private readonly Dictionary<Player, Action<Unit?>> _bots = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    internal static PlayerbotKillObserver For(MapCombat combat) => ByCombat.GetValue(combat, static key => new PlayerbotKillObserver(key));

    internal void Add(Player player, Action<Unit?> onKilled)
    {
        lock (_gate)
        {
            bool first = _bots.Count == 0;
            _bots[player] = onKilled;
            if (first) combat.UnitKilled += OnUnitKilled;
        }
    }

    internal void Remove(Player player)
    {
        lock (_gate)
        {
            if (!_bots.Remove(player) || _bots.Count != 0) return;
            combat.UnitKilled -= OnUnitKilled;
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        Action<Unit?>? onKilled = null;
        if (victim is Player player)
            lock (_gate) _bots.TryGetValue(player, out onKilled);
        onKilled?.Invoke(killer);
    }
}

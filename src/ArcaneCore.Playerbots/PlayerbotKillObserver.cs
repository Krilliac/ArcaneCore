using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// One kill listener per map combat, with a direct lookup for the managed bot that died.
/// MapCombat.Kill raises UnitKilled after it clears the victim's combat lists. The lethal
/// <see cref="MapCombat.DamageTaken"/> still sees those lists (the victim is alive), so the pack
/// that was attacking is captured then and handed over with the killer. A listener that waited
/// for UnitKilled would see nobody but the killing blow, and the bot would walk back into the rest.
/// </summary>
internal sealed class PlayerbotKillObserver(MapCombat combat)
{
    private static readonly ConditionalWeakTable<MapCombat, PlayerbotKillObserver> ByCombat = new();
    private static readonly Unit[] NoPack = [];
    private readonly Dictionary<Player, Action<Unit?, IReadOnlyList<Unit>>> _bots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Player, Unit[]> _lethalPack = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    /// <summary>
    /// A player died (killer null for the environment). Raised once per kill on the map's combat while any bot is on that map, which holds whenever a bot
    /// is the victim or the killer (the bot social memory's PvP rivalry); world thread. A handler must not throw.
    /// </summary>
    internal static event Action<Unit?, Unit>? PlayerKilled;

    internal static PlayerbotKillObserver For(MapCombat combat) => ByCombat.GetValue(combat, static key => new PlayerbotKillObserver(key));

    internal void Add(Player player, Action<Unit?, IReadOnlyList<Unit>> onKilled)
    {
        lock (_gate)
        {
            bool first = _bots.Count == 0;
            _bots[player] = onKilled;
            if (!first) return;
            combat.DamageTaken += OnDamageTaken;
            combat.UnitKilled += OnUnitKilled;
        }
    }

    internal void Remove(Player player)
    {
        lock (_gate)
        {
            _lethalPack.Remove(player);
            if (!_bots.Remove(player) || _bots.Count != 0) return;
            combat.DamageTaken -= OnDamageTaken;
            combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>
    /// A hit that will kill <paramref name="victim"/> (same check as <see cref="MapCombat.DealDamage"/>, before
    /// <c>Kill</c> stops combat). Self damage is not a pack: environmental deaths never publish
    /// <see cref="MapCombat.DamageTaken"/>.
    /// </summary>
    private void OnDamageTaken(Unit attacker, Unit victim, uint damage)
    {
        if (victim is not Player player || ReferenceEquals(attacker, player)) return;
        bool lethal = player.InvincibilityHpThreshold == 0 && player.Health <= damage;
        Unit[]? pack = lethal ? SnapshotPack(player) : null;
        lock (_gate)
        {
            if (!_bots.ContainsKey(player)) return;
            if (pack is null) _lethalPack.Remove(player);
            else _lethalPack[player] = pack;
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        Action<Unit?, IReadOnlyList<Unit>>? onKilled = null;
        IReadOnlyList<Unit> pack = NoPack;
        if (victim is Player) PlayerKilled?.Invoke(killer, victim);
        if (victim is Player player)
        {
            lock (_gate)
            {
                _bots.TryGetValue(player, out onKilled);
                // A self kill (a fall, drowning) is not this pack, even if an earlier hit had snapped one.
                if (_lethalPack.Remove(player, out Unit[]? snapped) && !ReferenceEquals(killer, player)) pack = snapped;
            }
        }

        onKilled?.Invoke(killer, pack);
    }

    /// <summary>Attackers and units holding the victim in threat, copied before <c>CombatStop</c> clears both.</summary>
    private static Unit[] SnapshotPack(Player player)
    {
        int count = player.Combat.Attackers.Count + player.Combat.ThreatenedBy.Count;
        if (count == 0) return NoPack;
        var pack = new List<Unit>(count);
        foreach (Unit unit in player.Combat.Attackers) pack.Add(unit);
        foreach (Unit unit in player.Combat.ThreatenedBy)
        {
            if (!pack.Contains(unit)) pack.Add(unit);
        }

        return pack.Count == 0 ? NoPack : pack.ToArray();
    }
}

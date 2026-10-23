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
    private readonly Dictionary<Player, Watch> _bots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Player, Unit[]> _lethalPack = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    /// <summary>A watched bot's callbacks: its own death (killer and pack), and a player's death it took part in as victim or killer.</summary>
    private readonly record struct Watch(Action<Unit?, IReadOnlyList<Unit>> OnKilled, Action<Unit?, Player>? OnPlayerKilled);

    internal static PlayerbotKillObserver For(MapCombat combat) => ByCombat.GetValue(combat, static key => new PlayerbotKillObserver(key));

    /// <summary>
    /// Watches <paramref name="player"/>, a bot on this map. <paramref name="onKilled"/> runs at its own death with the killer and the pack.
    /// <paramref name="onPlayerKilled"/> (the bot social memory's PvP rivalry) runs once per player death on this map that the bot took part in as
    /// victim or as killer (a pet or totem counts as its owner); when two watched bots took part, only the victim's runs. World thread; must not throw.
    /// </summary>
    internal void Add(Player player, Action<Unit?, IReadOnlyList<Unit>> onKilled, Action<Unit?, Player>? onPlayerKilled = null)
    {
        lock (_gate)
        {
            bool first = _bots.Count == 0;
            _bots[player] = new Watch(onKilled, onPlayerKilled);
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
        if (victim is not Player player) return;
        Action<Unit?, IReadOnlyList<Unit>>? onKilled = null;
        Action<Unit?, Player>? onPlayerKilled = null;
        IReadOnlyList<Unit> pack = NoPack;
        Player? slayer = killer is null ? null : DuelRules.ControllingPlayer(killer);
        lock (_gate)
        {
            if (_bots.TryGetValue(player, out Watch watch))
            {
                onKilled = watch.OnKilled;
                onPlayerKilled = watch.OnPlayerKilled;
            }

            // The killing bot hears of the death only when the victim is not a watched bot: one record per death, and it notes both sides.
            if (onPlayerKilled is null && slayer is not null && _bots.TryGetValue(slayer, out Watch killerWatch)) onPlayerKilled = killerWatch.OnPlayerKilled;
            // A self kill (a fall, drowning) is not this pack, even if an earlier hit had snapped one.
            if (_lethalPack.Remove(player, out Unit[]? snapped) && !ReferenceEquals(killer, player)) pack = snapped;
        }

        onPlayerKilled?.Invoke(killer, player);
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

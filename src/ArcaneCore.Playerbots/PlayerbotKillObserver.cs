using System.Runtime.CompilerServices;
using ArcaneCore.Game;
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
/// It also counts each managed bot's progress hits (<see cref="ProgressHitsBy"/>), which is the stall watch's progress for a
/// bot fighting in place (see <see cref="PlayerbotStallWatch"/>).
/// </summary>
internal sealed class PlayerbotKillObserver(MapCombat combat)
{
    private static readonly ConditionalWeakTable<MapCombat, PlayerbotKillObserver> ByCombat = new();
    private static readonly Unit[] NoPack = [];
    private readonly Dictionary<Player, Action<Unit?, IReadOnlyList<Unit>>> _bots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Player, Unit[]> _lethalPack = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Player, HitProgress> _progress = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    internal static PlayerbotKillObserver For(MapCombat combat) => ByCombat.GetValue(combat, static key => new PlayerbotKillObserver(key));

    internal void Add(Player player, Action<Unit?, IReadOnlyList<Unit>> onKilled)
    {
        lock (_gate)
        {
            bool first = _bots.Count == 0;
            _bots[player] = onKilled;
            _progress.TryAdd(player, new HitProgress());
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
            _progress.Remove(player);
            if (!_bots.Remove(player) || _bots.Count != 0) return;
            combat.DamageTaken -= OnDamageTaken;
            combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>
    /// Two jobs, both before the hit is applied (the victim is still alive and at its health before the hit).
    /// Victim side: a hit that will kill <paramref name="victim"/> (same check as <see cref="MapCombat.DealDamage"/>, before
    /// <c>Kill</c> stops combat) snapshots the pack. Self damage is not a pack: environmental deaths never publish
    /// <see cref="MapCombat.DamageTaken"/>.
    /// Attacker side: a hit by a registered bot is counted (<see cref="ProgressHitsBy"/>) when it takes the victim's health to a
    /// new low for that victim. Any damage is not enough: a target held at a health threshold by death prevention or a scripted
    /// surrender (<c>InvincibilityHpThreshold</c>, which the real post-hit health honours), and a target that is healed
    /// back up between hits, take hits without the fight getting anywhere, and the stall watch must still see those as stalls.
    /// </summary>
    private void OnDamageTaken(Unit attacker, Unit victim, uint damage)
    {
        Player? dealer = attacker as Player;
        Player? player = victim as Player;
        if (dealer is null && player is null) return;
        if (ReferenceEquals(attacker, victim)) return;
        Unit[]? pack = null;
        if (player is not null)
        {
            bool lethal = player.InvincibilityHpThreshold == 0 && player.Health <= damage;
            pack = lethal ? SnapshotPack(player) : null;
        }

        // The health the hit leaves, as MapCombat.DealDamage applies it: a victim with an invincibility threshold (a scripted
        // surrender at 15% or 20%, Spirit of Redemption) never goes below it, and one already at or below it loses nothing.
        uint health = victim.Health;
        uint threshold = victim.InvincibilityHpThreshold;
        uint after = threshold == 0
            ? (health > damage ? health - damage : 0)
            : (health > threshold ? health - Math.Min(health - threshold, damage) : health);
        lock (_gate)
        {
            if (dealer is not null && _progress.TryGetValue(dealer, out HitProgress? hits)) hits.Note(victim.Guid, after);
            if (player is null || !_bots.ContainsKey(player)) return;
            if (pack is null) _lethalPack.Remove(player);
            else _lethalPack[player] = pack;
        }
    }

    /// <summary>
    /// How many hits <paramref name="player"/> has landed that took a victim's health to a new low for that victim, since it was
    /// added (0 when unknown). Only ever increases; the stall watch compares it between looks.
    /// </summary>
    internal ulong ProgressHitsBy(Player player)
    {
        lock (_gate) return _progress.TryGetValue(player, out HitProgress? hits) ? hits.Count : 0;
    }

    /// <summary>
    /// One bot's progress hits and the lowest health each recent victim was taken to. Known limit: a bot that cycles hits over more
    /// than <see cref="MaxVictims"/> victims that never die loses the memory each time it fills, so those hits are counted again;
    /// that is rare and the memory stays bounded.
    /// </summary>
    internal sealed class HitProgress
    {
        /// <summary>Victims remembered per bot; when one more would be added the memory is cleared (a fight rarely spans more).</summary>
        internal const int MaxVictims = 16;
        private readonly Dictionary<ObjectGuid, uint> _lows = [];

        internal ulong Count { get; private set; }

        /// <summary>
        /// Counts the hit when <paramref name="victim"/> is new or <paramref name="after"/> is below the lowest recorded, and records
        /// the lower. A lethal hit records 0 and keeps the entry (the victim may survive it, as death prevention does) until
        /// <see cref="Forget"/>.
        /// </summary>
        internal void Note(ObjectGuid victim, uint after)
        {
            if (_lows.TryGetValue(victim, out uint recorded))
            {
                if (after >= recorded) return;
            }
            else if (_lows.Count >= MaxVictims)
            {
                _lows.Clear();
            }

            _lows[victim] = after;
            Count = unchecked(Count + 1);
        }

        internal void Forget(ObjectGuid victim) => _lows.Remove(victim);
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        // The victim is dead for good: a respawn under the same guid starts a new fight with a new low.
        lock (_gate)
        {
            foreach (HitProgress hits in _progress.Values) hits.Forget(victim.Guid);
        }

        Action<Unit?, IReadOnlyList<Unit>>? onKilled = null;
        IReadOnlyList<Unit> pack = NoPack;
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

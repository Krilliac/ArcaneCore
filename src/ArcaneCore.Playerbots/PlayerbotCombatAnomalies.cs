using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// H1 outcome checks of a bot's fight: a cast the server keeps refusing, a cast that never finishes, a caster out of mana for the whole
/// fight, and a combat flag nothing holds up. One instance per bot (owned by <see cref="PlayerbotCombatSpells"/> and
/// <see cref="PlayerbotRisk"/>), world thread. Every check costs a few integer compares per think on success and formats its text only
/// after it failed; each reports once per episode (re-armed when the thing recovers) through <see cref="PlayerbotAnomalies"/>.
/// </summary>
internal sealed class PlayerbotCombatWatch
{
    /// <summary>A spell the server refused this many times in a row, with no acceptance between, is stuck (each refusal backs it off 4 s).</summary>
    internal const int RefusalLimit = 10;

    /// <summary>A refusal streak older than this is over (the bot did other things; the next refusal starts a new streak).</summary>
    internal const uint RefusalStaleMs = 120_000;

    /// <summary>A cast still preparing or casting this long after its cast time and channel duration should have ended is stuck.</summary>
    internal const uint StuckSlackMs = 15_000;

    /// <summary>Mana below this share of the maximum counts as out of mana.</summary>
    internal const uint LowManaPct = 10;

    /// <summary>A caster out of mana for this long of continuous fight (world time) is in an out-of-mana loop.</summary>
    internal const uint LowManaMs = 45_000;

    /// <summary>A gap between two fight decisions longer than this means the fight was over in between (a streak does not span it).</summary>
    internal const uint FightGapMs = 30_000;

    private const int MaxTracked = 16;

    private readonly Dictionary<uint, (int Count, uint LastMs, bool Reported)> _refused = [];

    private SpellCast? _castSeen;
    private uint _castSeenMs;
    private bool _castReported;

    private uint _lowManaSinceMs;
    private uint _lastFightMs;
    private bool _lowManaReported;

    /// <summary>A rotation or fallback spell was submitted and the server did not take it.</summary>
    internal void Refused(Player player, SpellInfo spell, uint nowMs, string? reason = null)
    {
        if (_refused.Count >= MaxTracked && !_refused.ContainsKey(spell.Id)) _refused.Clear();
        (int count, uint last, bool reported) = _refused.TryGetValue(spell.Id, out var entry) ? entry : (0, 0u, false);
        if (count > 0 && unchecked(nowMs - last) > RefusalStaleMs) (count, reported) = (0, false);
        count++;
        if (count >= RefusalLimit && !reported)
        {
            reported = true;
            PlayerbotAnomalies.Report(player, "combat", "cast-refused-repeatedly",
                string.Create(CultureInfo.InvariantCulture,
                    $"spell {spell.Id} '{spell.Name}' chosen and submitted {count} times in a row, the server never started it (last result {reason ?? "none sent"})"));
        }

        _refused[spell.Id] = (count, nowMs, reported);
    }

    /// <summary>The server took <paramref name="spell"/>: its refusal streak is over.</summary>
    internal void Accepted(SpellInfo spell) => _refused.Remove(spell.Id);

    /// <summary>The bot is casting <paramref name="current"/> now (seen at a fight decision).</summary>
    internal void Casting(Player player, SpellCast current, uint nowMs)
    {
        if (!ReferenceEquals(_castSeen, current))
        {
            _castSeen = current;
            _castSeenMs = nowMs;
            _castReported = false;
            return;
        }

        if (_castReported) return;
        long allowed = (long)Math.Max(0, current.CastTime) + Math.Max(0, current.Duration) + (current.PushbackCount * 1000L) + StuckSlackMs;
        uint elapsed = unchecked(nowMs - _castSeenMs);
        if (elapsed <= int.MaxValue && elapsed > allowed)
        {
            _castReported = true;
            PlayerbotAnomalies.Report(player, "combat", "cast-stuck",
                string.Create(CultureInfo.InvariantCulture,
                    $"spell {current.Spell.Id} '{current.Spell.Name}' still {current.State} {elapsed} ms after it began (cast time {current.CastTime} ms, duration {current.Duration} ms)"));
        }
    }

    /// <summary>No cast is running at this fight decision.</summary>
    internal void NotCasting()
    {
        _castSeen = null;
        _castReported = false;
    }

    /// <summary>One fight decision: a caster below <see cref="LowManaPct"/> of its mana for <see cref="LowManaMs"/> straight is in a loop.</summary>
    internal void Fight(Player player, uint nowMs)
    {
        bool gap = _lastFightMs != 0 && unchecked(nowMs - _lastFightMs) > FightGapMs;
        _lastFightMs = Math.Max(1u, nowMs);
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        // A battleground fight never ends: vmangos BattleBotAI keeps a caster below 10% mana in the fight, meleeing (CombatBotBaseAI::BeginChasing),
        // and drinks only once the fighting stops (PlayerbotBattlegroundAI.Rest). A dry caster there is the reference behaviour, not a loop (#813).
        bool low = player.PowerType == PowerType.Mana && maxMana > 0 && player.Map?.Template is not { IsBattleground: true }
            && SpellSystemPower(player) * 100UL < maxMana * (ulong)LowManaPct;
        if (!low || gap)
        {
            _lowManaSinceMs = low ? Math.Max(1u, nowMs) : 0;
            _lowManaReported = false;
            return;
        }

        if (_lowManaSinceMs == 0) _lowManaSinceMs = Math.Max(1u, nowMs);
        uint elapsed = unchecked(nowMs - _lowManaSinceMs);
        if (_lowManaReported || elapsed < LowManaMs || elapsed > int.MaxValue) return;
        _lowManaReported = true;
        PlayerbotAnomalies.Report(player, "combat", "out-of-mana-loop",
            string.Create(CultureInfo.InvariantCulture,
                $"{player.Class} below {LowManaPct}% mana ({SpellSystemPower(player)}/{maxMana}) for {elapsed} ms of one fight"));
    }

    private static ulong SpellSystemPower(Player player) => ArcaneCore.Game.Spells.SpellSystem.GetPower(player, PowerType.Mana);

    /// <summary>Forget everything (the combat lifetime ended: death, reclaim).</summary>
    internal void Reset()
    {
        _refused.Clear();
        _castSeen = null;
        _castReported = false;
        _lowManaSinceMs = 0;
        _lastFightMs = 0;
        _lowManaReported = false;
    }
}

/// <summary>
/// The combat flag nothing holds up (<c>combat/phantom-combat</c>): the bot is in combat, no living creature attacks it or has it on a
/// threat list, and it has no victim, for <see cref="PhantomMs"/> of world time without a single look that disagrees. One look per think
/// (<see cref="PlayerbotRisk.Track"/>); a few set lookups and no allocation on success.
/// </summary>
internal sealed class PlayerbotPhantomCombatWatch
{
    /// <summary>The longest a combat flag may stand with nothing behind it (the server's own timers are seconds).</summary>
    internal const uint PhantomMs = 45_000;

    private uint _sinceMs;
    private bool _reported;

    internal void Look(Player player, uint nowMs)
    {
        if (!player.IsAlive || !player.Combat.IsInCombat || player.Combat.Victim is not null || HasLiveOpponent(player))
        {
            _sinceMs = 0;
            _reported = false;
            return;
        }

        if (_sinceMs == 0)
        {
            _sinceMs = Math.Max(1u, nowMs);
            return;
        }

        uint elapsed = unchecked(nowMs - _sinceMs);
        if (_reported || elapsed < PhantomMs || elapsed > int.MaxValue) return;
        _reported = true;
        PlayerbotAnomalies.Report(player, "combat", "phantom-combat",
            string.Create(CultureInfo.InvariantCulture,
                $"in combat for {elapsed} ms with no attacker, no threat-list holder and no victim; {Holders(player)}"));
    }

    private static bool HasLiveOpponent(Player player)
    {
        foreach (Unit attacker in player.Combat.Attackers)
            if (attacker.IsAlive && ReferenceEquals(attacker.Map, player.Map)) return true;
        foreach (Unit holder in player.Combat.ThreatenedBy)
            if (holder.IsAlive && ReferenceEquals(holder.Map, player.Map)) return true;
        // vmangos Unit::Update "Pet in combat ?" (Unit.cpp:264-266): the owner stays in combat while its pet is held on a threat list, a pet
        // tanking a creature out of the owner's sight included (#834).
        if (player.PetGuid is { IsEmpty: false } petGuid && player.Map?.FindObject(petGuid) is Unit { IsInWorld: true } pet
            && ReferenceEquals(pet.Map, player.Map))
        {
            foreach (Unit holder in pet.Combat.ThreatenedBy)
                if (holder.IsAlive && ReferenceEquals(holder.Map, player.Map)) return true;
        }
        return false;
    }

    /// <summary>What still holds the flag up, for the report: holders the look skipped (dead, another map) keep the server's timer from ending it.</summary>
    private static string Holders(Player player)
    {
        int dead = 0, elsewhere = 0, total = 0;
        foreach (Unit holder in player.Combat.ThreatenedBy)
        {
            total++;
            if (!holder.IsAlive) dead++;
            else if (!ReferenceEquals(holder.Map, player.Map)) elsewhere++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"threatened-by {total} (dead {dead}, other map {elsewhere}), attackers {player.Combat.Attackers.Count}, pet {(player.PetGuid.IsEmpty ? "none" : "yes")}");
    }
}

/// <summary>
/// Retreat checks (<c>risk/retreat-loop</c>: <see cref="Limit"/> retreats begun inside <see cref="WindowMs"/>, the bot runs and comes
/// back to the same fight). One call per retreat start.
/// </summary>
internal sealed class PlayerbotRetreatLoopWatch
{
    internal const int Limit = 3;
    internal const uint WindowMs = 180_000;

    private readonly uint[] _starts = new uint[Limit];
    private int _count;
    private bool _reported;

    internal void Started(Player player, uint nowMs, string reason)
    {
        _starts[_count % Limit] = Math.Max(1u, nowMs);
        _count++;
        if (_count < Limit) return;
        uint oldest = _starts[_count % Limit];
        uint span = unchecked(nowMs - oldest);
        if (span > WindowMs)
        {
            _reported = false;
            return;
        }

        if (_reported) return;
        _reported = true;
        PlayerbotAnomalies.Report(player, "risk", "retreat-loop",
            string.Create(CultureInfo.InvariantCulture, $"{Limit} retreats begun within {span} ms (latest reason '{reason}')"));
    }

    internal void Reset()
    {
        _count = 0;
        _reported = false;
        Array.Clear(_starts);
    }
}

/// <summary>
/// Consumable use checks (<c>gear/consumable-not-consumed</c>, <c>gear/consumable-no-effect</c>, <c>gear/consumable-wasted</c>). The
/// caller takes a snapshot before it sends CMSG_USE_ITEM (<see cref="Begin"/>) and hands it back once the server took the packet
/// (<see cref="Sent"/>); it is judged at the first <see cref="Poll"/> after the use should have settled (the server may finish an item's
/// spell on a later tick, so never inside the sending call). A use with a cast time that a fight or a step may have interrupted is
/// dropped, not blamed. One item in flight at a time (a bot uses one item every two seconds). World thread.
/// </summary>
internal sealed class PlayerbotConsumableWatch
{
    internal const uint SettleSlackMs = 2_000;

    /// <summary>The effect (an aura, a rise in health or mana) is judged only when the poll comes this soon after the use settled; later it has worn off or been eaten by damage.</summary>
    internal const uint EffectWindowMs = 8_000;

    /// <summary>Consecutive failed uses of one item entry before it is reported (one is a race with the server; two is a pattern).</summary>
    internal const int FailLimit = 2;

    private readonly record struct Pending(Item Item, ulong Guid, uint Entry, uint CountBefore, uint SpellId, uint DueMs,
        ulong HealthBefore, ulong ManaBefore, bool WantsFood, bool WantsDrink, bool WantsAura, bool WantsHeal, bool WantsMana, bool CastTime);

    /// <summary>What the caller knew before the packet went out.</summary>
    internal readonly record struct Use(Item Item, SpellInfo? Spell, uint CountBefore, ulong HealthBefore, ulong ManaBefore);

    private Pending? _pending;
    private uint _failEntry;
    private int _fails;
    private bool _failReported;

    /// <summary>Snapshot <paramref name="item"/> and the bot's vitals before CMSG_USE_ITEM is sent for it.</summary>
    internal static Use Begin(Player player, Item item, SpellInfo? spell)
        => new(item, spell, item.Count, player.Health,
            player.PowerType == PowerType.Mana ? ArcaneCore.Game.Spells.SpellSystem.GetPower(player, PowerType.Mana) : 0UL);

    /// <summary>The server took the packet for <paramref name="use"/> (its handler ran): check it, or wait for its cast.</summary>
    internal void Sent(Player player, in Use use, SpellSystem system, uint nowMs)
    {
        ItemTemplate template = use.Item.Template;
        PlayerbotConsumableKind kind = PlayerbotConsumables.Classify(template, system.Store);
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        bool fullHealth = player.MaxHealth > 0 && use.HealthBefore >= player.MaxHealth;
        bool fullMana = player.PowerType != PowerType.Mana || maxMana == 0 || use.ManaBefore >= maxMana;
        bool food = kind == PlayerbotConsumableKind.Food, drink = kind == PlayerbotConsumableKind.Drink;
        bool heal = use.Spell is { } s1 && s1.Effects.Any(e => e.Effect == SpellEffectName.Heal && e.BasePoints >= 0);
        bool energize = use.Spell is { } s2
            && s2.Effects.Any(e => e.Effect == SpellEffectName.Energize && e.MiscValue == (int)PowerType.Mana && e.BasePoints >= 0);

        // A restoring item used when everything it restores is already full is a wasted item (eating at full health, drinking at full mana).
        bool restoresHealth = food || heal, restoresMana = drink || energize;
        if ((restoresHealth || restoresMana) && (!restoresHealth || fullHealth) && (!restoresMana || fullMana))
        {
            PlayerbotAnomalies.Report(player, "gear", "consumable-wasted",
                string.Create(CultureInfo.InvariantCulture,
                    $"used item {template.Entry} ({kind}) at health {use.HealthBefore}/{player.MaxHealth}, mana {use.ManaBefore}/{maxMana}"));
        }

        int castMs = use.Spell is { } spell ? Math.Max(0, spell.GetCastTime(player.Level)) : 0;
        var pending = new Pending(use.Item, use.Item.Guid.Value, template.Entry, use.CountBefore, use.Spell?.Id ?? 0,
            unchecked(nowMs + (uint)castMs + (castMs > 0 ? SettleSlackMs : 0)), use.HealthBefore, use.ManaBefore,
            food, drink, use.Spell is { } sp && sp.Effects.Any(e => e.Effect == SpellEffectName.ApplyAura) && !food && !drink,
            heal && !food && !drink && !fullHealth, energize && !drink && !fullMana, castMs > 0);
        _pending = pending;
    }

    /// <summary>Judge the item in flight once its cast should have ended (call once per think).</summary>
    internal void Poll(Player player, SpellSystem? system, uint nowMs)
    {
        if (_pending is not { } pending) return;
        if (unchecked((int)(pending.DueMs - nowMs)) > 0) return;
        // With spell batching on, the use may still wait in the batch for its go (nothing consumed, nothing applied yet): judged after it.
        if (system is not null && player.IsInWorld && system.HasPendingGo(player)) return;
        _pending = null;
        if (system is null || !player.IsInWorld || !player.IsAlive) return;
        Judge(player, pending, system, unchecked(nowMs - pending.DueMs) <= EffectWindowMs);
    }

    /// <summary>Forget the item in flight (death, logout).</summary>
    internal void Reset()
    {
        _pending = null;
        _fails = 0;
        _failReported = false;
    }

    private void Judge(Player player, Pending pending, SpellSystem system, bool effectStillVisible)
    {
        // A cast with a cast time ends with a step or a blow: the bot may have interrupted its own use.
        if (pending.CastTime && (player.Combat.IsInCombat || player.Movement.HasFlag(MovementFlags.MaskMoving))) return;

        bool consumed = pending.Item.Inventory is null || pending.Item.Count < pending.CountBefore;
        if (!consumed)
        {
            Failed(player, pending.Entry, "consumable-not-consumed",
                string.Create(CultureInfo.InvariantCulture,
                    $"item {pending.Entry} (spell {pending.SpellId}) was used and the use should have settled, its stack is still {pending.Item.Count}"));
            return;
        }

        bool effect = true;
        if (effectStillVisible)
        {
            if (pending.WantsFood || pending.WantsDrink || pending.WantsAura)
                effect = HasAura(player, system, pending.SpellId)
                    || ((pending.WantsFood || pending.WantsDrink) && PlayerbotConsumables.HasActiveFoodDrink(player, system));
            else if (pending.WantsHeal)
                effect = player.Health > pending.HealthBefore;
            else if (pending.WantsMana)
                effect = player.PowerType != PowerType.Mana || ArcaneCore.Game.Spells.SpellSystem.GetPower(player, PowerType.Mana) > pending.ManaBefore;
        }

        if (!effect)
        {
            Failed(player, pending.Entry, "consumable-no-effect",
                string.Create(CultureInfo.InvariantCulture,
                    $"item {pending.Entry} (spell {pending.SpellId}) was consumed but left no aura and no health or mana gain (health {pending.HealthBefore} to {player.Health})"));
            return;
        }

        _fails = 0;
        _failReported = false;
    }

    private void Failed(Player player, uint entry, string code, string detail)
    {
        if (_failEntry != entry)
        {
            _failEntry = entry;
            _fails = 0;
            _failReported = false;
        }

        _fails++;
        if (_fails < FailLimit || _failReported) return;
        _failReported = true;
        PlayerbotAnomalies.Report(player, "gear", code, detail);
    }

    private static bool HasAura(Player player, SpellSystem system, uint spellId)
    {
        if (spellId == 0) return true;
        foreach (SpellAuraHolder holder in system.GetAuras(player))
            if (!holder.IsRemoved && holder.Spell.Id == spellId) return true;
        return false;
    }
}

using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// Finds the target a bot cannot kill because its health is held at its death-prevention threshold (<c>InvincibilityHpThreshold</c> nonzero,
/// health at or below it: a creature an EventAI script keeps at 1 health until a condition is met, such as the Blasted Lands servants of
/// quest 2681 "The Stones That Bind Us", which only Shackle Shatter lets die). Each think of a fight calls <see cref="Observe"/>; it answers
/// true once the target has stayed held for the limit, and the brain then sets the creature aside. A creature with the threshold set whose
/// health is above it, one at low health with no threshold, or a different target starts the clock afresh.
/// <para>
/// The abandonment is reported to <see cref="PlayerbotAnomalies"/> (area <c>combat</c>, code <see cref="AnomalyCode"/>) once per creature
/// entry per bot, then again only after a backoff that doubles from <see cref="ReportBackoffMs"/> up to <see cref="ReportBackoffMaxMs"/>.
/// </para>
/// Ownership: one bot's brain. Thread affinity: the world thread. Memory: a few fields and at most <see cref="MaxEntries"/> report slots;
/// no allocation per think.
/// </summary>
internal sealed class PlayerbotInvincibleWatch
{
    /// <summary>The anomaly code of an abandoned invincible target (area <c>combat</c>).</summary>
    internal const string AnomalyCode = "invincible-target-abandoned";

    /// <summary>A watch not consulted for this long (world ms: the bot fought nothing, rested, travelled) starts afresh.</summary>
    internal const uint GapMs = 15_000;

    /// <summary>The first wait before the same entry is reported again by the same bot.</summary>
    internal const uint ReportBackoffMs = 600_000;

    /// <summary>The longest wait between two reports of one entry.</summary>
    internal const uint ReportBackoffMaxMs = 6 * 3_600_000;

    /// <summary>The most entries whose report backoff is remembered.</summary>
    internal const int MaxEntries = 32;

    private ObjectGuid _target;
    private uint _since;
    private uint _seen;
    private readonly Dictionary<uint, (uint NextMs, uint BackoffMs)> _reported = new(MaxEntries);

    /// <summary>
    /// One look at the fight with <paramref name="target"/> at <paramref name="nowMs"/> (the world's wrapping millisecond clock). True when
    /// the target has been held at or below its threshold for <paramref name="limitMs"/> without a break; the watch then starts over.
    /// </summary>
    internal bool Observe(ArcaneCore.Game.Entities.Player player, Creature target, uint nowMs, uint limitMs)
    {
        if (player.Map?.Template is { IsDungeon: true } || (target.UnitFlags & UnitFlags.NotSelectable) != 0)
        {
            // A scripted boss phase (a fake death at 1 health on an instance map) or a creature the client cannot select is not a creature
            // the bot is stuck on.
            _target = default;
            _seen = nowMs;
            return false;
        }

        uint threshold = target.InvincibilityHpThreshold;
        bool held = threshold != 0 && target.IsAlive && target.Health <= threshold;
        bool gap = unchecked(nowMs - _seen) > GapMs;
        _seen = nowMs;
        if (!held || gap || target.Guid != _target)
        {
            _target = held ? target.Guid : default;
            _since = nowMs;
            return false;
        }

        uint heldFor = unchecked(nowMs - _since);
        if (heldFor < limitMs) return false;
        _target = default;
        Report(player, target, nowMs, threshold, heldFor);
        return true;
    }

    /// <summary>Forgets the watched target (the brain calls this when it chooses a target afresh, so a bot that returns to the same creature within <see cref="GapMs"/> starts the clock over).</summary>
    internal void Reset() => _target = default;

    private void Report(ArcaneCore.Game.Entities.Player player, Creature target, uint nowMs, uint threshold, uint heldMs)
    {
        uint entry = target.Entry;
        uint backoff = ReportBackoffMs;
        if (_reported.TryGetValue(entry, out (uint NextMs, uint BackoffMs) known))
        {
            if (unchecked((int)(known.NextMs - nowMs)) > 0) return;
            backoff = Math.Min(known.BackoffMs * 2, ReportBackoffMaxMs);
        }
        else if (_reported.Count >= MaxEntries)
        {
            _reported.Clear();
        }

        _reported[entry] = (unchecked(nowMs + backoff), backoff);
        PlayerbotAnomalies.Report(player, "combat", AnomalyCode,
            string.Create(CultureInfo.InvariantCulture,
                $"creature entry {entry} stayed at {target.Health} health (death-prevention threshold {threshold}) for {heldMs / 1000} s of the fight, set aside as a target the bot cannot kill"));
    }
}

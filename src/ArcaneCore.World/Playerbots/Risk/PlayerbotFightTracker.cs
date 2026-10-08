using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The observed damage rates of the bot's current fight over a rolling window (<see cref="WindowMs"/> of world time): the health
/// the bot lost per second (net of its heals) and the health its enemies lost per second, each enemy counted only while it was
/// seen in two samples, so an add that just joined does not look like damage dealt. Until the window spans
/// <see cref="MinimumSpanMs"/> the priors of the pre-engagement estimate stand in. It also remembers where each enemy stood when it
/// joined (where the creature's leash is measured from, vmangos Creature::IsOutOfThreatArea uses the combat start point) and keeps
/// the bot's damage per second across fights as an average for the next estimate. World thread.
/// </summary>
internal sealed class PlayerbotFightTracker
{
    internal const uint WindowMs = 8_000;
    internal const uint MinimumSpanMs = 2_000;
    private const int MaxEnemies = 16;

    private readonly Queue<Sample> _samples = new();
    private readonly Dictionary<ObjectGuid, Vector3> _anchors = [];
    private float _priorIn, _priorOut;

    /// <summary>The bot's damage per second as observed in its fights (an average), or null before its first one.</summary>
    internal float? ObservedDps { get; private set; }

    /// <summary>Whether a fight is being tracked.</summary>
    internal bool Active => _samples.Count > 0;

    /// <summary>Where each enemy of this fight stood when it joined it.</summary>
    internal IReadOnlyDictionary<ObjectGuid, Vector3> Anchors => _anchors;

    /// <summary>The last enemies seen (the creatures a death or a retreat is remembered against).</summary>
    internal IReadOnlyList<Creature> LastEnemies { get; private set; } = [];

    /// <summary>The last verdict (inspection).</summary>
    internal PlayerbotFightVerdict? LastVerdict { get; private set; }

    /// <summary>Set the priors (from the pre-engagement estimate) for the start of a fight.</summary>
    internal void Prime(float dpsIn, float dpsOut)
    {
        _priorIn = dpsIn;
        _priorOut = dpsOut;
    }

    /// <summary>A fight ended (won, fled or the bot died): fold its observed damage into the average and forget it.</summary>
    internal void End()
    {
        if (_samples.Count >= 2 && Rate(out _, out float dpsOut, out bool measured) && measured && dpsOut > 0)
            ObservedDps = ObservedDps is { } average ? (average * 0.7f) + (dpsOut * 0.3f) : dpsOut;
        _samples.Clear();
        _anchors.Clear();
        LastVerdict = null;
    }

    /// <summary>One sample of the fight: the bot's health and each enemy's health now.</summary>
    internal PlayerbotFightFacts Observe(Player player, IReadOnlyList<Creature> enemies, Creature target, uint nowMs)
    {
        var health = new Dictionary<ObjectGuid, uint>(enemies.Count);
        foreach (Creature enemy in enemies.Take(MaxEnemies))
        {
            health[enemy.Guid] = enemy.Health;
            _anchors.TryAdd(enemy.Guid, new Vector3(enemy.X, enemy.Y, enemy.Z));
        }

        LastEnemies = [.. enemies];
        _samples.Enqueue(new Sample(nowMs, player.Health, health));
        while (_samples.Count > 2 && unchecked(nowMs - _samples.Peek().AtMs) > WindowMs) _samples.Dequeue();
        Rate(out float dpsIn, out float dpsOut, out bool measured);
        if (!measured)
        {
            dpsIn = MathF.Max(dpsIn, _priorIn);
            dpsOut = _priorOut > 0 ? _priorOut : dpsOut;
        }

        uint enemyHealth = 0;
        foreach (uint value in health.Values) enemyHealth += value;
        float targetPct = target.MaxHealth == 0 ? 0 : target.Health * 100f / target.MaxHealth;
        return new PlayerbotFightFacts(player.Health, player.MaxHealth, dpsIn, dpsOut, enemyHealth, health.Count, targetPct);
    }

    internal void Record(PlayerbotFightVerdict verdict) => LastVerdict = verdict;

    private bool Rate(out float dpsIn, out float dpsOut, out bool measured)
    {
        dpsIn = dpsOut = 0;
        measured = false;
        if (_samples.Count < 2) return false;
        Sample first = _samples.Peek();
        Sample last = _samples.Last();
        uint span = unchecked(last.AtMs - first.AtMs);
        if (span == 0 || span > int.MaxValue) return false;
        float seconds = span / 1000f;
        dpsIn = MathF.Max(0, ((float)first.BotHealth - last.BotHealth) / seconds);
        float dealt = 0;
        foreach ((ObjectGuid guid, uint now) in last.Enemies)
            if (first.Enemies.TryGetValue(guid, out uint before) && before > now) dealt += before - now;
        // An enemy that died inside the window took its remaining health with it.
        foreach ((ObjectGuid guid, uint before) in first.Enemies)
            if (!last.Enemies.ContainsKey(guid)) dealt += before;
        dpsOut = dealt / seconds;
        measured = span >= MinimumSpanMs;
        return true;
    }

    private sealed record Sample(uint AtMs, uint BotHealth, Dictionary<ObjectGuid, uint> Enemies);
}

using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// The no-progress watch of one bot's group fight: the fight makes progress while the target's health drops below the least it has been, or the
/// bot closes in on it by more than <see cref="ProgressYards"/> beyond the nearest it has been. A fight with none for <see cref="StallMs"/>
/// is given up by the caller (the target is shunned for <see cref="PlayerbotGroupAI.FightStallShunMs"/>). Live: bots that "fought" a creature
/// out of reach, immune to them or evading, for as long as the step lasted. Times are the world's millisecond clock (<c>uint</c>, which
/// wraps). Owned by one bot's group AI; world thread only; allocation-free.
/// </summary>
internal sealed class PlayerbotFightWatch
{
    /// <summary>How long a fight may go without progress.</summary>
    internal const uint StallMs = 20_000;

    /// <summary>The distance the bot must close, beyond the nearest it has been, to count as progress (yards).</summary>
    internal const float ProgressYards = 1f;

    private ObjectGuid _target;
    private uint _leastHealth;
    private float _nearestYards;
    private uint _progressMs;

    /// <summary>
    /// Note one think of the fight against <paramref name="target"/>. True when the fight has gone <see cref="StallMs"/> without progress (the
    /// watch then starts afresh, so the caller gives up at most once per window). A new target starts the watch.
    /// </summary>
    internal bool Observe(ObjectGuid target, uint health, float distance, uint nowMs)
    {
        if (_target.IsEmpty || target != _target)
        {
            Start(target, health, distance, nowMs);
            return false;
        }

        if (health < _leastHealth || distance < _nearestYards - ProgressYards)
        {
            _leastHealth = Math.Min(_leastHealth, health);
            _nearestYards = Math.Min(_nearestYards, distance);
            _progressMs = nowMs;
            return false;
        }

        // Wrap-safe: the time since the last progress as an unsigned distance, taken as a stall only while it is a plausible span (not a clock step back).
        uint idle = unchecked(nowMs - _progressMs);
        if (idle < StallMs || idle > int.MaxValue) return false;
        Start(target, health, distance, nowMs);
        return true;
    }

    /// <summary>Forget the fight (no target this think, a new errand).</summary>
    internal void Reset() => _target = default;

    private void Start(ObjectGuid target, uint health, float distance, uint nowMs)
    {
        _target = target;
        _leastHealth = health;
        _nearestYards = distance;
        _progressMs = nowMs;
    }
}

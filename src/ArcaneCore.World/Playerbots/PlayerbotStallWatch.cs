using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Notices a living bot that makes no progress at all: it has not moved <see cref="ProgressYards"/> from where it stood, and
/// nothing about it changed (level, experience, money, bags, quest log, spells), for <see cref="PlayerbotOptions.StallSeconds"/>
/// of world time. Such a bot used to stand still for hours without a fault (live 2026-10-08: Ironwander re-selling an item the
/// vendor refused, Graveweaver in the Deathknell crypt, Dawnrover beside William Pestle), and nothing told the operator.
/// <para>
/// A stall is reported (<see cref="Report"/>: the error column of <c>.playerbot list</c> and <c>status</c>, a line of
/// <c>.playerbot inspect</c>, and one warning in the log) until the bot makes progress again, and the brain gives up what it
/// was doing (<see cref="PlayerbotBrain"/>): the goal's target is set aside for <see cref="PlayerbotSuspensions.SuspendMs"/>.
/// The bot is not faulted: quarantine and a restart would put it back in the same place with the same choice.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal sealed class PlayerbotStallWatch
{
    /// <summary>Moving this far from where the watch last saw progress is progress.</summary>
    internal const float ProgressYards = 10f;

    private bool _started;
    private Vector3 _anchor;
    private uint _anchorMap;
    private Fingerprint _fingerprint;
    private uint _progressMs;

    /// <summary>The current stall (null while the bot makes progress).</summary>
    internal string? Report { get; private set; }

    /// <summary>The last stall reported, kept after progress resumed (inspection).</summary>
    internal string? LastReport { get; private set; }

    /// <summary>Stalls reported so far.</summary>
    internal int Count { get; private set; }

    /// <summary>Forget everything (death, a new life: the recovery has its own bounds).</summary>
    internal void Reset()
    {
        _started = false;
        Report = null;
    }

    /// <summary>
    /// Look at the bot once per think. True exactly when a new stall begins (the caller gives up the goal); the stall stays
    /// reported until the bot makes progress.
    /// </summary>
    internal bool Observe(Player player, PlayerbotGoalKind goal, uint target, uint quest, uint nowMs, uint stallMs,
        Func<Player, Fingerprint> fingerprint)
    {
        Vector3 position = new(player.X, player.Y, player.Z);
        Fingerprint current = fingerprint(player);
        if (!_started || player.MapId != _anchorMap || !current.Equals(_fingerprint)
            || Vector3.Distance(position, _anchor) >= ProgressYards)
        {
            _started = true;
            _anchor = position;
            _anchorMap = player.MapId;
            _fingerprint = current;
            _progressMs = nowMs;
            Report = null;
            return false;
        }

        uint idle = unchecked(nowMs - _progressMs);
        if (idle > int.MaxValue || idle < stallMs) return false;
        bool fresh = Report is null;
        Report = string.Create(CultureInfo.InvariantCulture,
            $"stalled {idle / 1000}s: goal={goal} target={target} quest={quest} at {position.X:F1},{position.Y:F1},{position.Z:F1} map {player.MapId}");
        if (!fresh) return false;
        LastReport = Report;
        Count++;
        return true;
    }

    /// <summary>What counts as progress besides moving.</summary>
    internal readonly record struct Fingerprint(uint Level, uint Experience, uint Money, uint Items, int Quests, int Rewarded,
        uint Objectives, int Spells);
}

/// <summary>
/// What a stalled bot set aside (<see cref="PlayerbotStallWatch"/>): creature entries and quests the brain's goals skip until
/// <see cref="SuspendMs"/> of world time passed. Bounded; the oldest goes first.
/// </summary>
internal sealed class PlayerbotSuspensions
{
    /// <summary>How long a stalled goal's target is set aside.</summary>
    internal const uint SuspendMs = 600_000;

    private const int Max = 64;
    private readonly Dictionary<uint, uint> _entries = [];
    private readonly Dictionary<uint, uint> _quests = [];

    internal bool IsEntrySuspended(uint entry, uint nowMs) => entry != 0 && Active(_entries, entry, nowMs);

    internal bool IsQuestSuspended(uint quest, uint nowMs) => quest != 0 && Active(_quests, quest, nowMs);

    internal void SuspendEntry(uint entry, uint nowMs) => Add(_entries, entry, nowMs);

    internal void SuspendQuest(uint quest, uint nowMs) => Add(_quests, quest, nowMs);

    private static bool Active(Dictionary<uint, uint> set, uint key, uint nowMs)
    {
        if (!set.TryGetValue(key, out uint until)) return false;
        if (unchecked((int)(until - nowMs)) > 0) return true;
        set.Remove(key);
        return false;
    }

    private static void Add(Dictionary<uint, uint> set, uint key, uint nowMs)
    {
        if (key == 0) return;
        foreach (uint expired in set.Where(entry => unchecked((int)(entry.Value - nowMs)) <= 0).Select(entry => entry.Key).ToArray())
            set.Remove(expired);
        if (set.Count >= Max && !set.ContainsKey(key)) set.Remove(set.OrderBy(entry => entry.Value).First().Key);
        set[key] = unchecked(nowMs + SuspendMs);
    }
}

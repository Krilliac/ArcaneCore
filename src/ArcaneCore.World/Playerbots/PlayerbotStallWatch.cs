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
/// <para>
/// A bot that keeps dying in one place makes no progress either, though it moves and every recovery step on its own is
/// progress (rehearsal 2026-10-08: Dawnrover reclaimed its body 36 yards below a Scourge invasion point, chose a Skeletal
/// Soldier for its next fight and was killed by its Scourge Strike within seconds, over and over; between deaths it waited out
/// reclaim delays of up to two minutes as a ghost, with <c>stall=none</c>). Deaths are watched too (<see cref="RecordDeath"/>):
/// <see cref="DeathLoopDeaths"/> deaths within <see cref="DeathLoopYards"/> of each other on one map inside
/// <see cref="DeathLoopWindowMs"/> are a death loop. It is reported like a stall, and stays reported while the bot is dead and
/// until it has lived through a whole stall bound; the brain then takes the spirit healer for that death (the graveyard, away
/// from the place) instead of reviving at the body again. A creature entry among the bot's attackers at
/// <see cref="KillerDeaths"/> deaths inside the window is returned to be set aside, so the bot stops choosing it as a target.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal sealed class PlayerbotStallWatch
{
    /// <summary>Moving this far from where the watch last saw progress is progress.</summary>
    internal const float ProgressYards = 10f;

    /// <summary>Deaths that make a death loop (<see cref="RecordDeath"/>).</summary>
    internal const int DeathLoopDeaths = 3;

    /// <summary>A death loop's deaths all lie within this distance of the latest one (a reclaim radius and some way back).</summary>
    internal const float DeathLoopYards = 60f;

    /// <summary>The window deaths are counted in (the brain's suspensions last as long, <see cref="PlayerbotSuspensions.SuspendMs"/>).</summary>
    internal const uint DeathLoopWindowMs = PlayerbotSuspensions.SuspendMs;

    /// <summary>Deaths among whose attackers a creature entry was, inside the window, that set the entry aside.</summary>
    internal const int KillerDeaths = 2;

    private const int MaxDeaths = 16;
    private readonly List<Death> _deaths = [];
    private string? _stallReport;
    private string? _deathLoopReport;
    private uint _aliveSinceMs;

    private bool _started;
    private Vector3 _anchor;
    private uint _anchorMap;
    private Fingerprint _fingerprint;
    private uint _progressMs;
    private uint _gaveUpMs;

    /// <summary>The current stall (null while the bot makes progress): a death loop first, else a living bot standing still.</summary>
    internal string? Report => _deathLoopReport ?? _stallReport;

    /// <summary>The last stall reported, kept after progress resumed (inspection).</summary>
    internal string? LastReport { get; private set; }

    /// <summary>Stalls reported so far.</summary>
    internal int Count { get; private set; }

    /// <summary>
    /// Times <see cref="Observe"/> told the caller to give its goal up: when a stall begins, and again after each further stall bound
    /// it lasts (the goal given up first was not the one holding the bot).
    /// </summary>
    internal int GiveUps { get; private set; }

    /// <summary>
    /// Forget the living watch (death, a new life: the recovery has its own bounds). A death loop stays reported
    /// (<see cref="RecordDeath"/>).
    /// </summary>
    internal void Reset()
    {
        _started = false;
        _stallReport = null;
    }

    /// <summary>
    /// A death of the bot at <paramref name="position"/> on <paramref name="map"/>, with the creature entries that were attacking
    /// it (<paramref name="attackers"/>). Returns whether the deaths now make a death loop, and the attacker entries to set aside
    /// (present at <see cref="KillerDeaths"/> deaths inside the window).
    /// </summary>
    internal (bool Loop, IReadOnlyList<uint> SetAside) RecordDeath(uint nowMs, uint map, Vector3 position,
        IReadOnlyCollection<uint> attackers)
    {
        _deaths.RemoveAll(death => unchecked(nowMs - death.AtMs) > DeathLoopWindowMs);
        if (_deaths.Count >= MaxDeaths) _deaths.RemoveAt(0);
        _deaths.Add(new Death(nowMs, map, position, [.. attackers.Distinct()]));

        uint[] setAside = [.. attackers.Distinct().Where(entry => entry != 0
            && _deaths.Count(death => death.Attackers.Contains(entry)) >= KillerDeaths)];
        Death[] here = [.. _deaths.Where(death => death.Map == map && Vector3.Distance(death.Position, position) <= DeathLoopYards)];
        if (here.Length < DeathLoopDeaths) return (false, setAside);

        uint span = unchecked(nowMs - here[0].AtMs);
        uint[] killers = [.. here.SelectMany(death => death.Attackers).Distinct().Order()];
        bool fresh = _deathLoopReport is null;
        _deathLoopReport = string.Create(CultureInfo.InvariantCulture,
            $"death loop: died {here.Length} times in {span / 1000}s within {DeathLoopYards:F0} yd of {position.X:F1},{position.Y:F1},{position.Z:F1} map {map} attackers={(killers.Length == 0 ? "none" : string.Join('/', killers))}");
        LastReport = _deathLoopReport;
        if (fresh) Count++;
        return (true, setAside);
    }

    /// <summary>
    /// Look at the bot once per think. True when a new stall begins, and again after every further stall bound without progress
    /// (the caller gives up the goal it holds then); the stall stays reported, and counted once, until the bot makes progress.
    /// Before, the goal was given up only when the stall began: a bot whose next goal held it just the same was reported stalled
    /// for hours and never gave anything up again (Ironwander, live 2026-10-09: <c>stalled 7960s: goal=Grind target=1196</c>).
    /// </summary>
    internal bool Observe(Player player, PlayerbotGoalKind goal, uint target, uint quest, uint nowMs, uint stallMs,
        Func<Player, Fingerprint> fingerprint)
    {
        Vector3 position = new(player.X, player.Y, player.Z);
        if (!_started) _aliveSinceMs = nowMs;
        // A death loop is over once the bot has lived through a whole stall bound.
        if (_deathLoopReport is not null && unchecked(nowMs - _aliveSinceMs) >= stallMs) _deathLoopReport = null;
        Fingerprint current = fingerprint(player);
        if (!_started || player.MapId != _anchorMap || !current.Equals(_fingerprint)
            || Vector3.Distance(position, _anchor) >= ProgressYards)
        {
            _started = true;
            _anchor = position;
            _anchorMap = player.MapId;
            _fingerprint = current;
            _progressMs = nowMs;
            _stallReport = null;
            return false;
        }

        uint idle = unchecked(nowMs - _progressMs);
        if (idle > int.MaxValue || idle < stallMs) return false;
        bool fresh = _stallReport is null;
        _stallReport = string.Create(CultureInfo.InvariantCulture,
            $"stalled {idle / 1000}s: goal={goal} target={target} quest={quest} at {position.X:F1},{position.Y:F1},{position.Z:F1} map {player.MapId}");
        if (!fresh && unchecked(nowMs - _gaveUpMs) < stallMs) return false;
        _gaveUpMs = nowMs;
        GiveUps++;
        if (!fresh) return true;
        LastReport = _stallReport;
        Count++;
        return true;
    }

    private readonly record struct Death(uint AtMs, uint Map, Vector3 Position, uint[] Attackers);

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

    internal bool IsEntrySuspended(uint entry, uint nowMs)
        => entry != 0 && (Active(_entries, entry, nowMs) || Shared?.IsEntrySetAside(entry, nowMs) == true);

    internal bool IsQuestSuspended(uint quest, uint nowMs)
        => quest != 0 && (Active(_quests, quest, nowMs) || Shared?.IsQuestSetAside(quest, nowMs) == true);

    /// <summary>What every bot of the world sets aside (<see cref="PlayerbotSharedSetAsides"/>), or null.</summary>
    internal PlayerbotSharedSetAsides? Shared { get; set; }

    private uint _trainingUntilMs;
    private bool _training;

    /// <summary>Whether trainer visits are set aside (a death loop on the way to one, <see cref="SuspendTraining"/>).</summary>
    internal bool IsTrainingSuspended(uint nowMs)
    {
        if (_training && unchecked((int)(_trainingUntilMs - nowMs)) <= 0) _training = false;
        return _training;
    }

    /// <summary>Set every trainer visit aside for <see cref="SuspendMs"/>: the class trainers of a zone are usually in one town.</summary>
    internal void SuspendTraining(uint nowMs)
    {
        _training = true;
        _trainingUntilMs = unchecked(nowMs + SuspendMs);
    }

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

/// <summary>
/// The goals set aside for every bot of the world: a quest (or a quest giver, vendor or trainer without one) on which
/// <see cref="BotsToShare"/> different bots stalled within <see cref="WindowMs"/> has a structural cause, the same for the next bot
/// that picks it, and is skipped by all of them for <see cref="SetAsideMs"/>. Live stress test 2026-10-08: 200 bots, 74 of them
/// stalled, most on a handful of quests (1656 99 times, 2159 30 times); each bot set its quest aside for ten minutes, picked it again,
/// stalled again, and the next bot did the same. Bounded (<see cref="MaxKeys"/>, the one reported longest ago goes first); a
/// set-aside that ends is tried again, and a repeat lasts twice as long (at most <see cref="MaxSetAsideMs"/>).
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal sealed class PlayerbotSharedSetAsides
{
    /// <summary>Different bots that must stall on one goal for it to be set aside for all.</summary>
    internal const int BotsToShare = 3;

    /// <summary>How long a stall counts towards <see cref="BotsToShare"/>.</summary>
    internal const uint WindowMs = 1_800_000;

    /// <summary>How long the first set-aside of a goal lasts.</summary>
    internal const uint SetAsideMs = 1_800_000;

    /// <summary>The longest set-aside (repeats double it).</summary>
    internal const uint MaxSetAsideMs = 7_200_000;

    /// <summary>The most goals remembered.</summary>
    internal const int MaxKeys = 256;

    private readonly Dictionary<(bool Quest, uint Id), Record> _records = [];

    /// <summary>Goals remembered (bounded by <see cref="MaxKeys"/>).</summary>
    internal int Count => _records.Count;

    /// <summary>
    /// Bot <paramref name="bot"/> stalled on <paramref name="goal"/> with target <paramref name="entry"/> for quest
    /// <paramref name="quest"/>. Returns what is newly set aside for every bot ("quest 1656 for 30 min"), or null.
    /// </summary>
    internal string? Report(PlayerbotGoalKind goal, uint entry, uint quest, ulong bot, uint nowMs)
    {
        (bool Quest, uint Id) key = goal switch
        {
            PlayerbotGoalKind.Quest when quest != 0 => (true, quest),
            PlayerbotGoalKind.Quest or PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train when entry != 0 => (false, entry),
            _ => default,
        };
        if (key.Id == 0) return null;
        if (!_records.TryGetValue(key, out Record? record))
        {
            if (_records.Count >= MaxKeys)
                _records.Remove(_records.OrderBy(pair => pair.Value.LastMs).First().Key);
            _records[key] = record = new Record();
        }

        record.LastMs = nowMs;
        record.Bots.RemoveAll(seen => unchecked(nowMs - seen.AtMs) > WindowMs || seen.Bot == bot);
        record.Bots.Add((bot, nowMs));
        if (record.Bots.Count < BotsToShare || Active(record, nowMs)) return null;
        uint length = (uint)Math.Min((ulong)SetAsideMs << Math.Min(record.Times, 8), MaxSetAsideMs);
        record.Times++;
        record.UntilMs = unchecked(nowMs + length);
        record.Bots.Clear();
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(key.Quest ? "quest" : "entry")} {key.Id} for {length / 60_000} min");
    }

    internal bool IsQuestSetAside(uint quest, uint nowMs) => _records.TryGetValue((true, quest), out Record? record) && Active(record, nowMs);

    internal bool IsEntrySetAside(uint entry, uint nowMs) => _records.TryGetValue((false, entry), out Record? record) && Active(record, nowMs);

    private static bool Active(Record record, uint nowMs) => record.UntilMs != 0 && unchecked((int)(record.UntilMs - nowMs)) > 0;

    private sealed class Record
    {
        public List<(ulong Bot, uint AtMs)> Bots { get; } = [];
        public uint UntilMs { get; set; }
        public int Times { get; set; }
        public uint LastMs { get; set; }
    }
}

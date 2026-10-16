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
    private uint _deathSequence;
    private uint _lastLoopSequence;
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

    /// <summary>Whether this record completed a new set of three deaths, rather than extending the same loop report.</summary>
    internal bool NewDeathLoop { get; private set; }

    /// <summary>
    /// Times <see cref="Observe"/> told the caller to give its goal up: when a stall begins, and again after each further stall bound
    /// it lasts (the goal given up first was not the one holding the bot).
    /// </summary>
    internal int GiveUps { get; private set; }

    /// <summary>
    /// Whether the latest give-up <see cref="Observe"/> asked for came while the stall it began was still unresolved: the goal given up
    /// at the stall's start (or at the previous bound) did not free the bot (<see cref="PlayerbotBrain"/> reports it as such).
    /// </summary>
    internal bool LastGiveUpRepeated { get; private set; }

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
        NewDeathLoop = false;
        _deaths.RemoveAll(death => unchecked(nowMs - death.AtMs) > DeathLoopWindowMs);
        if (_deaths.Count >= MaxDeaths) _deaths.RemoveAt(0);
        _deaths.Add(new Death(nowMs, map, position, [.. attackers.Distinct()], ++_deathSequence));

        uint[] setAside = [.. attackers.Distinct().Where(entry => entry != 0
            && _deaths.Count(death => death.Attackers.Contains(entry)) >= KillerDeaths)];
        Death[] here = [.. _deaths.Where(death => death.Map == map && Vector3.Distance(death.Position, position) <= DeathLoopYards)];
        if (here.Length < DeathLoopDeaths) return (false, setAside);

        uint span = unchecked(nowMs - here[0].AtMs);
        uint[] killers = [.. here.SelectMany(death => death.Attackers).Distinct().Order()];
        bool fresh = _deathLoopReport is null;
        NewDeathLoop = here.Count(death => death.Sequence > _lastLoopSequence) >= DeathLoopDeaths;
        if (NewDeathLoop) _lastLoopSequence = _deathSequence;
        _deathLoopReport = string.Create(CultureInfo.InvariantCulture,
            $"death loop: died {here.Length} times in {span / 1000}s within {DeathLoopYards:F0} yd of {position.X:F1},{position.Y:F1},{position.Z:F1} map {map} attackers={(killers.Length == 0 ? "none" : string.Join('/', killers))}");
        LastReport = _deathLoopReport;
        // The stall count keeps its old meaning (one per loop report) so logged death-loop counts stay comparable across runs;
        // NewDeathLoop only gates the loop-zone hazard.
        if (fresh) Count++;
        return (true, setAside);
    }

    /// <summary>
    /// The deaths recorded inside <see cref="DeathLoopWindowMs"/> within <see cref="DeathLoopYards"/> of <paramref name="position"/>
    /// on <paramref name="map"/>, the latest one included (<see cref="RecordDeath"/> is called as the bot dies, before its recovery
    /// asks). Two mean the bot already died once where its body lies now (<see cref="PlayerbotRecovery.JudgeBody"/>).
    /// </summary>
    internal int DeathsNear(uint map, Vector3 position, uint nowMs)
        => _deaths.Count(death => unchecked(nowMs - death.AtMs) <= DeathLoopWindowMs && death.Map == map
            && Vector3.Distance(death.Position, position) <= DeathLoopYards);

    /// <summary>The centre and creature entries of the deaths that made the latest loop.</summary>
    internal (Vector3 Centre, uint[] Killers) LoopZone(uint map, Vector3 position, uint nowMs)
    {
        Death[] here = [.. _deaths.Where(death => unchecked(nowMs - death.AtMs) <= DeathLoopWindowMs && death.Map == map
            && Vector3.Distance(death.Position, position) <= DeathLoopYards)];
        return (new Vector3(here.Average(death => death.Position.X), here.Average(death => death.Position.Y),
            here.Average(death => death.Position.Z)),
            [.. here.SelectMany(death => death.Attackers).Where(entry => entry != 0).Distinct().Order()]);
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
        PlayerbotThinkProbe.Phase("stall decision");
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
        LastGiveUpRepeated = !fresh;
        GiveUps++;
        if (!fresh) return true;
        LastReport = _stallReport;
        Count++;
        return true;
    }

    /// <summary>Size (yards) of the coordinate cell in which repeated stalls of one bot are counted as a dead-end pin.</summary>
    internal const float PinCellYards = 10f;

    /// <summary>Stalls of the same bot in one <see cref="PinCellYards"/> cell that mark the cell a dead-end pin and escalate recovery.</summary>
    internal const int PinStallsToEscalate = 2;

    internal const int MaxPinCells = 64;

    internal readonly record struct PinCell(uint MapId, int CellX, int CellY);

    private readonly Dictionary<PinCell, int> _pinStalls = [];
    private readonly Queue<PinCell> _pinOrder = [];

    internal static PinCell CellOf(uint mapId, float x, float y)
        => new(mapId, (int)MathF.Floor(x / PinCellYards), (int)MathF.Floor(y / PinCellYards));

    /// <summary>Record a stall give-up at <c>(x, y)</c> on <paramref name="mapId"/> and return how many times this bot has stalled in that 10-yard cell.</summary>
    internal int RecordPinStall(uint mapId, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return 0;
        PinCell cell = CellOf(mapId, x, y);
        if (!_pinStalls.TryGetValue(cell, out int count))
        {
            if (_pinStalls.Count >= MaxPinCells && _pinOrder.TryDequeue(out PinCell oldest))
                _pinStalls.Remove(oldest);
            _pinOrder.Enqueue(cell);
        }

        count++;
        _pinStalls[cell] = count;
        return count;
    }

    /// <summary>How many times this bot has stalled inside the 10-yard cell containing <c>(x, y)</c> on <paramref name="mapId"/>.</summary>
    internal int PinStallCount(uint mapId, float x, float y)
        => float.IsFinite(x) && float.IsFinite(y) ? _pinStalls.GetValueOrDefault(CellOf(mapId, x, y)) : 0;

    /// <summary>Whether the 10-yard cell containing <c>(x, y)</c> on <paramref name="mapId"/> has stalled this bot at least <see cref="PinStallsToEscalate"/> times.</summary>
    internal bool IsDeadEndPin(uint mapId, float x, float y)
        => PinStallCount(mapId, x, y) >= PinStallsToEscalate;

    /// <summary>
    /// A stall another part of the bot found and explains itself (a tram trip that failed, <see cref="PlayerbotTram"/>): reported and
    /// counted like one this watch found, so the feature logs it and the caller gives the goal up. Progress clears it as usual.
    /// </summary>
    internal void ReportExternal(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _stallReport = reason;
        LastReport = reason;
        Count++;
        GiveUps++;
        _started = false;
    }

    private readonly record struct Death(uint AtMs, uint Map, Vector3 Position, uint[] Attackers, uint Sequence);

    /// <summary>
    /// What counts as progress besides moving. <paramref name="RestMark"/>: the most health and mana seen while the bot rests (0
    /// otherwise), so regeneration is a resting bot's progress.
    /// </summary>
    internal readonly record struct Fingerprint(uint Level, uint Experience, uint Money, uint Items, int Quests, int Rewarded,
        uint Objectives, int Spells, ulong RestMark = 0);
}

/// <summary>
/// What a stalled bot set aside (<see cref="PlayerbotStallWatch"/>): creature entries and quests the brain's goals skip until
/// <see cref="SuspendMs"/> of world time passed. Bounded; the oldest goes first.
/// </summary>
internal sealed class PlayerbotSuspensions
{
    /// <summary>How long a stalled goal's target is set aside.</summary>
    internal const uint SuspendMs = 600_000;

    /// <summary>The longest a repeated dead-end pin stall sets an entry aside (<see cref="SuspendEntryEscalating"/>).</summary>
    internal const uint MaxSuspendMs = 8 * SuspendMs;

    private const int Max = 64;
    private readonly Dictionary<uint, uint> _entries = [];
    private readonly Dictionary<uint, uint> _quests = [];
    private readonly HashSet<uint> _unreachableQuests = [];
    private readonly Dictionary<uint, int> _questSetAsides = [];
    private readonly Dictionary<uint, int> _entryStrikes = [];

    /// <summary>
    /// The set-asides of one quest after which a completed quest that is held against the usual rules (a graduated home's leave-home quest,
    /// or a quest across the tram for a bot too low for it) is abandoned instead of set aside again: three stalled return trips, about half
    /// an hour of world time with the 10 minute set-aside.
    /// </summary>
    internal const int MaxReturnSetAsides = 3;

    /// <summary>How many times this bot has set the quest aside (kept in memory; a restart starts the count again).</summary>
    internal int QuestSetAsideCount(uint quest) => _questSetAsides.GetValueOrDefault(quest);

    private readonly Queue<uint> _unreachableOrder = [];
    private int _version;
    internal int Version => Volatile.Read(ref _version);

    internal bool IsEntrySuspended(uint entry, uint nowMs)
        => entry != 0 && (Active(_entries, entry, nowMs) || Shared?.IsEntrySetAside(entry, nowMs) == true);

    internal bool IsQuestSuspended(uint quest, uint nowMs)
        => quest != 0 && (_unreachableQuests.Contains(quest) || Active(_quests, quest, nowMs)
            || Shared?.IsQuestSetAside(quest, nowMs) == true);

    internal bool IsQuestUnreachable(uint quest) => quest != 0 && _unreachableQuests.Contains(quest);

    /// <summary>Never take again a completed quest this bot abandoned after three unreachable-ender diagnoses.</summary>
    internal void MarkUnreachableQuest(uint quest)
    {
        if (quest == 0 || !_unreachableQuests.Add(quest)) return;
        if (_unreachableOrder.Count >= Max) _unreachableQuests.Remove(_unreachableOrder.Dequeue());
        _unreachableOrder.Enqueue(quest);
        Interlocked.Increment(ref _version);
    }

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
        Interlocked.Increment(ref _version);
    }

    internal void SuspendEntry(uint entry, uint nowMs)
    {
        if (entry == 0) return;
        if (_entryStrikes.Count >= 256 && !_entryStrikes.ContainsKey(entry)) _entryStrikes.Clear();
        _entryStrikes[entry] = Math.Max(1, _entryStrikes.GetValueOrDefault(entry));
        Add(_entries, entry, nowMs);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Lift the set-aside of <paramref name="entry"/> before its time (the reason for it passed). Nothing else about the entry changes.</summary>
    internal void ReleaseEntry(uint entry)
    {
        if (_entries.Remove(entry)) Interlocked.Increment(ref _version);
    }

    /// <summary>Lift the set-aside of <paramref name="quest"/> before its time (the reason for it passed). The count of set-asides is kept.</summary>
    internal void ReleaseQuest(uint quest)
    {
        if (_quests.Remove(quest)) Interlocked.Increment(ref _version);
    }

    internal int EntryStrikeCount(uint entry) => _entryStrikes.GetValueOrDefault(entry);

    /// <summary>
    /// Set <paramref name="entry"/> aside with doubling backoff (<c>2 * SuspendMs</c> up to <see cref="MaxSuspendMs"/>) when a bot
    /// repeatedly stalls on it at a dead-end pin.
    /// </summary>
    internal uint SuspendEntryEscalating(uint entry, uint nowMs)
    {
        if (entry == 0) return 0;
        if (_entryStrikes.Count >= 256 && !_entryStrikes.ContainsKey(entry)) _entryStrikes.Clear();
        int strikes = Math.Max(2, _entryStrikes.GetValueOrDefault(entry) + 1);
        _entryStrikes[entry] = strikes;
        uint duration = (uint)Math.Min((ulong)SuspendMs << Math.Min(strikes - 1, 3), MaxSuspendMs);
        Add(_entries, entry, nowMs, duration);
        Interlocked.Increment(ref _version);
        return duration;
    }

    /// <param name="countsAsSetAside">False for a set-aside that says nothing about abandoning the quest (a walk refused as a proven dead
    /// end): the count gates abandoning completed quests (<see cref="MaxReturnSetAsides"/>).</param>
    internal void SuspendQuest(uint quest, uint nowMs, bool countsAsSetAside = true)
    {
        if (quest == 0) return;
        Add(_quests, quest, nowMs);
        if (countsAsSetAside)
        {
            if (_questSetAsides.Count >= 256 && !_questSetAsides.ContainsKey(quest)) _questSetAsides.Clear();
            _questSetAsides[quest] = _questSetAsides.GetValueOrDefault(quest) + 1;
        }

        Interlocked.Increment(ref _version);
    }

    internal PlayerbotSetAside[] Export(Guid botId, int characterId, uint nowMs, long unixNowMs)
    {
        var rows = new List<PlayerbotSetAside>();
        foreach ((uint id, uint until) in _entries)
            if (Remaining(until, nowMs) is > 0 and var remaining)
                rows.Add(new(botId, characterId, PlayerbotSetAsideKind.Entry, id, unixNowMs + remaining));
        foreach ((uint id, uint until) in _quests)
            if (Remaining(until, nowMs) is > 0 and var remaining)
                rows.Add(new(botId, characterId, PlayerbotSetAsideKind.Quest, id, unixNowMs + remaining));
        foreach (uint id in _unreachableOrder)
            rows.Add(new(botId, characterId, PlayerbotSetAsideKind.UnreachableQuest, id, long.MaxValue));
        if (_training && Remaining(_trainingUntilMs, nowMs) is > 0 and var trainingRemaining)
            rows.Add(new(botId, characterId, PlayerbotSetAsideKind.Training, 0, unixNowMs + trainingRemaining));
        return [.. rows];
    }

    internal void Restore(IEnumerable<PlayerbotSetAside> rows, uint nowMs, long unixNowMs)
    {
        foreach (PlayerbotSetAside row in rows)
        {
            if (row.Kind == PlayerbotSetAsideKind.UnreachableQuest && row.TargetId != 0
                && row.ExpiresUnixMs == long.MaxValue)
            {
                MarkUnreachableQuest(row.TargetId);
                continue;
            }
            long remaining = row.ExpiresUnixMs - unixNowMs;
            if (remaining <= 0 || remaining > SuspendMs) continue;
            uint until = unchecked(nowMs + (uint)remaining);
            switch (row.Kind)
            {
                case PlayerbotSetAsideKind.Entry when row.TargetId != 0: Restore(_entries, row.TargetId, until, nowMs); break;
                case PlayerbotSetAsideKind.Quest when row.TargetId != 0: Restore(_quests, row.TargetId, until, nowMs); break;
                case PlayerbotSetAsideKind.Training when row.TargetId == 0: _training = true; _trainingUntilMs = until; break;
            }
        }
    }

    private static long Remaining(uint until, uint nowMs) => Math.Max(0, unchecked((int)(until - nowMs)));

    private static void Restore(Dictionary<uint, uint> set, uint key, uint until, uint nowMs)
    {
        if (set.Count >= Max && !set.ContainsKey(key))
            set.Remove(set.OrderBy(entry => Remaining(entry.Value, nowMs)).First().Key);
        set[key] = until;
    }

    private static bool Active(Dictionary<uint, uint> set, uint key, uint nowMs)
    {
        if (!set.TryGetValue(key, out uint until)) return false;
        if (unchecked((int)(until - nowMs)) > 0) return true;
        set.Remove(key);
        return false;
    }

    private static void Add(Dictionary<uint, uint> set, uint key, uint nowMs, uint durationMs = SuspendMs)
    {
        if (key == 0) return;
        foreach (uint expired in set.Where(entry => unchecked((int)(entry.Value - nowMs)) <= 0).Select(entry => entry.Key).ToArray())
            set.Remove(expired);
        if (set.Count >= Max && !set.ContainsKey(key)) set.Remove(set.OrderBy(entry => entry.Value).First().Key);
        set[key] = unchecked(nowMs + durationMs);
    }
}

/// <summary>
/// The goals set aside for every bot of the world: a quest (or a quest giver, vendor or trainer without one) on which
/// <see cref="BotsToShare"/> different bots stalled within <see cref="WindowMs"/> has a structural cause, the same for the next bot
/// that picks it, and is skipped by all of them for <see cref="SetAsideMs"/>. Live stress test 2026-10-08: 200 bots, 74 of them
/// stalled, most on a handful of quests (1656 99 times, 2159 30 times); each bot set its quest aside for ten minutes, picked it again,
/// stalled again, and the next bot did the same. Bounded (<see cref="MaxKeys"/>, the one reported longest ago goes first); a
/// set-aside that ends is tried again, and a repeat lasts twice as long (at most <see cref="MaxSetAsideMs"/>).
/// <para>
/// Only the stalls of bots inside their home area count (the brain does not report the others, PlayerbotBrain.GiveUpStalledGoal): a
/// bot outside its home stalls where its home does not want it, which says nothing about the goal for the bots whose home it is.
/// Live 2026-10-11 (200 bots homed at Sentinel Hill, build f5bc89a5): 46 of the 49 stalls behind the 50 shared set-asides were made
/// outside the home, by dwarves and gnomes walking from Westfall towards Dun Morogh and Ironforge enders and by bots stuck at Raven Hill
/// and on the hill west of Lakeshire; quest 109's ender (Gryan Stoutmantle) stands at Sentinel Hill, yet the quest was set aside for
/// every bot for up to two hours.
/// </para>
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

    /// <summary>After a day without a report, a goal's next shared stall starts at the first duration again.</summary>
    internal const uint RepeatQuietMs = 86_400_000;

    /// <summary>The most goals remembered.</summary>
    internal const int MaxKeys = 256;

    private readonly Dictionary<(bool Quest, uint Id), Record> _records = [];
    private int _version;
    internal int Version => Volatile.Read(ref _version);

    /// <summary>Goals remembered (bounded by <see cref="MaxKeys"/>).</summary>
    internal int Count => _records.Count;

    /// <summary>
    /// Bot <paramref name="bot"/> stalled on <paramref name="goal"/> with target <paramref name="entry"/> for quest
    /// <paramref name="quest"/> at <paramref name="position"/> on <paramref name="map"/>. Returns what is newly set aside for every bot
    /// (<see cref="PlayerbotSharedSetAside.Goal"/> "quest 1656 for 30 min", and where the bots that set it aside stalled), or null.
    /// </summary>
    internal PlayerbotSharedSetAside? Report(PlayerbotGoalKind goal, uint entry, uint quest, ulong bot, uint nowMs, uint map = 0,
        Vector3 position = default)
    {
        (bool Quest, uint Id) key = goal switch
        {
            PlayerbotGoalKind.Quest when quest != 0 => (true, quest),
            PlayerbotGoalKind.Quest or PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train or PlayerbotGoalKind.Mount when entry != 0 => (false, entry),
            _ => default,
        };
        if (key.Id == 0) return null;
        if (!_records.TryGetValue(key, out Record? record))
        {
            if (_records.Count >= MaxKeys)
                _records.Remove(_records.OrderBy(pair => pair.Value.LastMs).First().Key);
            _records[key] = record = new Record();
        }

        if (record.Times != 0 && unchecked(nowMs - record.LastMs) > RepeatQuietMs && !Active(record, nowMs))
        {
            record.Times = 0;
            record.UntilMs = 0;
        }
        record.LastMs = nowMs;
        record.Bots.RemoveAll(seen => unchecked(nowMs - seen.AtMs) > WindowMs || seen.Bot == bot);
        record.Bots.Add(new Stall(bot, nowMs, map, position));
        if (record.Bots.Count < BotsToShare || Active(record, nowMs)) return null;
        uint length = (uint)Math.Min((ulong)SetAsideMs << Math.Min(record.Times, 8), MaxSetAsideMs);
        record.Times = Math.Min(record.Times + 1, PlayerbotSetAside.MaxRepeatCount);
        record.UntilMs = unchecked(nowMs + length);
        Interlocked.Increment(ref _version);
        string stalls = string.Join("; ", record.Bots.Select(seen => string.Create(CultureInfo.InvariantCulture,
            $"{seen.Position.X:F1},{seen.Position.Y:F1},{seen.Position.Z:F1} map {seen.Map}")));
        record.Bots.Clear();
        return new PlayerbotSharedSetAside(string.Create(CultureInfo.InvariantCulture,
            $"{(key.Quest ? "quest" : "entry")} {key.Id} for {length / 60_000} min"), stalls);
    }

    internal bool IsQuestSetAside(uint quest, uint nowMs) => _records.TryGetValue((true, quest), out Record? record) && Active(record, nowMs);

    internal bool IsEntrySetAside(uint entry, uint nowMs) => _records.TryGetValue((false, entry), out Record? record) && Active(record, nowMs);

    internal PlayerbotSetAside[] Export(uint nowMs, long unixNowMs)
        => [.. _records.Where(pair => pair.Key.Id != 0 && Remaining(pair.Value.UntilMs, nowMs) is > 0)
            .Select(pair => new PlayerbotSetAside(Guid.Empty, 0,
                pair.Key.Quest ? PlayerbotSetAsideKind.SharedQuest : PlayerbotSetAsideKind.SharedEntry,
                pair.Key.Id, unixNowMs + Remaining(pair.Value.UntilMs, nowMs), pair.Value.Times))];

    internal void Restore(IEnumerable<PlayerbotSetAside> rows, uint nowMs, long unixNowMs)
    {
        foreach (PlayerbotSetAside row in rows)
        {
            long remaining = row.ExpiresUnixMs - unixNowMs;
            if (remaining <= 0 || remaining > MaxSetAsideMs || row.TargetId == 0 || _records.Count >= MaxKeys) continue;
            bool quest = row.Kind == PlayerbotSetAsideKind.SharedQuest;
            if (!quest && row.Kind != PlayerbotSetAsideKind.SharedEntry) continue;
            _records[(quest, row.TargetId)] = new Record
            {
                UntilMs = unchecked(nowMs + (uint)remaining), Times = Math.Clamp(row.RepeatCount, 1, PlayerbotSetAside.MaxRepeatCount), LastMs = nowMs,
            };
        }
    }

    private static long Remaining(uint until, uint nowMs) => Math.Max(0, unchecked((int)(until - nowMs)));

    private static bool Active(Record record, uint nowMs) => record.UntilMs != 0 && unchecked((int)(record.UntilMs - nowMs)) > 0;

    private readonly record struct Stall(ulong Bot, uint AtMs, uint Map, Vector3 Position);

    private sealed class Record
    {
        public List<Stall> Bots { get; } = [];
        public uint UntilMs { get; set; }
        public int Times { get; set; }
        public uint LastMs { get; set; }
    }
}

/// <summary>
/// A goal newly set aside for every bot (<see cref="PlayerbotSharedSetAsides.Report"/>): <see cref="Goal"/> as the log names it
/// ("quest 1656 for 30 min") and <see cref="Stalls"/>, where the bots whose stalls set it aside stood ("x,y,z map m; ..."), oldest first.
/// </summary>
internal readonly record struct PlayerbotSharedSetAside(string Goal, string Stalls);

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
internal sealed partial class PlayerbotStallWatch
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

    private PlayerbotDeferLedger? _ledger;

    /// <summary>The world whose clock the pin backoffs are read against (<see cref="PlayerbotNavigation.IsDeadEndPin"/> asks at its <c>NowMs</c>); null: no pin is ever reported.</summary>
    internal ArcaneCore.Game.Maps.WorldRuntime? World { get; init; }

    /// <summary>
    /// Where the pin cells are kept (<see cref="DeferKind.PinCell"/>): the bot's own ledger, which the brain binds at construction
    /// (<see cref="PlayerbotSuspensions.Ledger"/>); a watch used alone has a ledger of its own.
    /// </summary>
    internal PlayerbotDeferLedger Ledger
    {
        get => _ledger ??= new PlayerbotDeferLedger();
        init => _ledger = value;
    }

    /// <summary>The ledger's time for the world time <paramref name="nowMs"/> (<see cref="PlayerbotWorldClock.Ms(ArcaneCore.Game.Maps.WorldRuntime, uint)"/>; as it is when the watch has no world).</summary>
    private long LedgerTime(uint nowMs) => World is { } world ? PlayerbotWorldClock.Ms(world, nowMs) : nowMs;

    /// <summary>The pin cell of <c>(x, y)</c> on <paramref name="mapId"/>: map in bits 48-63, cell X in 24-47, cell Y in 0-23.</summary>
    internal static DeferKey PinKey(uint mapId, float x, float y)
        => new(DeferKind.PinCell, ((ulong)(mapId & 0xFFFFu) << 48)
            | ((ulong)(uint)((int)MathF.Floor(x / PinCellYards) & 0xFFFFFF) << 24)
            | (ulong)(uint)((int)MathF.Floor(y / PinCellYards) & 0xFFFFFF));

    /// <summary>
    /// Record a stall give-up at <c>(x, y)</c> on <paramref name="mapId"/> at world time <paramref name="nowMs"/> and return how many times this bot has stalled in that 10-yard
    /// cell. The cell is a dead-end pin (<see cref="IsDeadEndPin"/>) for the ledger's backoff from <paramref name="nowMs"/>: 10, 30, then
    /// 60 minutes from the second stall (the first is no pin yet); a cell whose last backoff ended an hour ago starts again.
    /// </summary>
    internal int RecordPinStall(uint mapId, float x, float y, uint nowMs)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return 0;
        DeferKey key = PinKey(mapId, x, y);
        long now = LedgerTime(nowMs);
        // From the third stall a replaced navigation dataset also frees the cell: the mesh the bot kept failing on is gone.
        return Ledger.Defer(key, now, Ledger.EffectiveStrikes(key, now) + 1 >= 3 ? DeferWake.NavDataset : DeferWake.None).Strikes;
    }

    /// <summary>How many times this bot has stalled inside the 10-yard cell containing <c>(x, y)</c> on <paramref name="mapId"/> (while remembered).</summary>
    internal int PinStallCount(uint mapId, float x, float y)
        => float.IsFinite(x) && float.IsFinite(y) ? Ledger.Strikes(PinKey(mapId, x, y)) : 0;

    /// <summary>
    /// Whether the 10-yard cell containing <c>(x, y)</c> on <paramref name="mapId"/> has stalled this bot at least
    /// <see cref="PinStallsToEscalate"/> times and its backoff since the last stall has not ended at world time <paramref name="nowMs"/>.
    /// </summary>
    internal bool IsDeadEndPin(uint mapId, float x, float y, uint nowMs)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        DeferKey key = PinKey(mapId, x, y);
        return Ledger.Strikes(key) >= PinStallsToEscalate && Ledger.IsDeferred(key, LedgerTime(nowMs));
    }

    /// <summary>The pins every bot of the world shares (<see cref="PlayerbotSharedPins"/>), or null (a watch used alone).</summary>
    internal PlayerbotSharedPins? SharedPins { get; set; }

    /// <summary>
    /// Whether other bots' dead-end pins made the cell containing <c>(x, y)</c> a world pin at world time <paramref name="nowMs"/>. Not part of
    /// <see cref="IsDeadEndPin"/>: route planning keeps the per-bot rule; world pins only steer exploring and a first stall's escalation.
    /// </summary>
    internal bool IsWorldPin(uint mapId, uint instanceId, float x, float y, uint nowMs)
        => SharedPins?.IsWorldPin(mapId, instanceId, x, y, nowMs) == true;

    /// <summary>
    /// This bot dead-end-pinned the cell containing <c>(x, y)</c>: tell the world. Returns what newly became a world pin, or null.
    /// </summary>
    internal string? ReportDeadEndPin(ulong bot, uint mapId, uint instanceId, float x, float y, uint nowMs)
        => SharedPins?.Report(mapId, instanceId, x, y, bot, nowMs);

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
    /// <paramref name="Kills"/>: kills the bot had loot rights to, so a bot capped at its level (no experience) or with full bags (no coin or items)
    /// that kills respawns is progressing; kill loops are the death-loop watch's to catch.
    /// </summary>
    internal readonly record struct Fingerprint(uint Level, uint Experience, uint Money, uint Items, int Quests, int Rewarded,
        uint Objectives, int Spells, ulong RestMark = 0, uint Kills = 0);
}

/// <summary>
/// What a stalled bot set aside (<see cref="PlayerbotStallWatch"/>): creature entries, quests, trainer visits and unreachable quests the
/// brain's goals skip until a deadline or a change that makes them worth trying again. A facade over the bot's
/// <see cref="PlayerbotDeferLedger"/> (<see cref="Ledger"/>), which holds the entries, the strikes that escalate repeated set-asides
/// and the persisted rows; goal components that defer for other reasons use the ledger directly. Bounded by the ledger's caps.
/// <para>Thread affinity: world thread (<see cref="Version"/> may be read from the save thread).</para>
/// </summary>
internal sealed class PlayerbotSuspensions(PlayerbotDeferLedger? ledger = null, ArcaneCore.Game.Maps.WorldRuntime? world = null)
{
    /// <summary>How long a stalled goal's target is set aside.</summary>
    internal const uint SuspendMs = 600_000;

    /// <summary>The longest a repeated dead-end pin stall sets an entry aside (<see cref="SuspendEntryEscalating"/>).</summary>
    internal const uint MaxSuspendMs = 8 * SuspendMs;

    /// <summary>
    /// The set-asides of one quest after which a completed quest that is held against the usual rules (a graduated home's leave-home quest,
    /// or a quest across the tram for a bot too low for it) is abandoned instead of set aside again: three stalled return trips, about half
    /// an hour of world time with the 10 minute set-aside.
    /// </summary>
    internal const int MaxReturnSetAsides = 3;

    private static readonly DeferKey TrainingKey = new(DeferKind.Training, 0);

    private readonly Dictionary<uint, int> _questSetAsides = [];

    /// <summary>The bot's deferrals: the entries behind every set-aside here (and the stall watch's pin cells).</summary>
    internal PlayerbotDeferLedger Ledger { get; } = ledger ?? new PlayerbotDeferLedger();

    /// <summary>The ledger's time for the world time <paramref name="nowMs"/>: against the world's 64-bit uptime, so the 32-bit clock's wrap never reaches the ledger (without a world, as given).</summary>
    private long T(uint nowMs) => world is null ? nowMs : PlayerbotWorldClock.Ms(world, nowMs);

    /// <summary>How many times this bot has set the quest aside (kept in memory; a restart starts the count again).</summary>
    internal int QuestSetAsideCount(uint quest) => _questSetAsides.GetValueOrDefault(quest);

    /// <summary>Changes whenever what <see cref="Export"/> would write changed.</summary>
    internal int Version => Ledger.Version;

    /// <summary>What every bot of the world sets aside (<see cref="PlayerbotSharedSetAsides"/>), or null.</summary>
    internal PlayerbotSharedSetAsides? Shared { get; set; }

    internal bool IsEntrySuspended(uint entry, uint nowMs)
    {
        return entry != 0 && (Ledger.IsDeferred(new(DeferKind.Entry, entry), T(nowMs)) || Shared?.IsEntrySetAside(entry, nowMs) == true);
    }

    internal bool IsQuestSuspended(uint quest, uint nowMs)
    {
        long now = T(nowMs);
        return quest != 0 && (Ledger.IsDeferred(new(DeferKind.UnreachableQuest, quest), now)
            || Ledger.IsDeferred(new(DeferKind.Quest, quest), now) || Shared?.IsQuestSetAside(quest, nowMs) == true);
    }

    /// <summary>Whether the quest's ender was diagnosed unreachable and the diagnosis has not expired.</summary>
    internal bool IsQuestUnreachable(uint quest, uint nowMs)
        => quest != 0 && Ledger.IsDeferred(new(DeferKind.UnreachableQuest, quest), T(nowMs));

    /// <summary>
    /// Do not take again a completed quest this bot abandoned after three unreachable-ender diagnoses: for 60 seconds, then 10 minutes,
    /// 1 hour, 6 hours and 24 hours with each repeat (the last holds), and sooner when the bot levels or the quest data, creature
    /// definitions or navigation dataset is replaced. Repeated while it is set aside already, it changes nothing.
    /// </summary>
    internal void MarkUnreachableQuest(uint quest, uint nowMs)
    {
        if (quest == 0) return;
        DeferKey key = new(DeferKind.UnreachableQuest, quest);
        long now = T(nowMs);
        if (Ledger.IsDeferred(key, now)) return;
        Ledger.Defer(key, now);
    }

    /// <summary>Whether trainer visits are set aside (a death loop on the way to one, <see cref="SuspendTraining"/>).</summary>
    internal bool IsTrainingSuspended(uint nowMs)
    {
        return Ledger.IsDeferred(TrainingKey, T(nowMs));
    }

    /// <summary>Set every trainer visit aside for <see cref="SuspendMs"/>: the class trainers of a zone are usually in one town.</summary>
    internal void SuspendTraining(uint nowMs)
    {
        long now = T(nowMs);
        Ledger.Defer(TrainingKey, now, untilMs: now + SuspendMs, strike: false);
    }

    internal void SuspendEntry(uint entry, uint nowMs)
    {
        if (entry == 0) return;
        long now = T(nowMs);
        Ledger.Defer(new(DeferKind.Entry, entry), now, untilMs: now + SuspendMs, strike: false);
    }

    /// <summary>
    /// The bot turned <paramref name="quest"/> in: forget its unreachable-quest and quest strikes and its set-aside count, so a quest that
    /// once succeeded starts clean.
    /// </summary>
    internal void QuestSucceeded(uint quest)
    {
        if (quest == 0) return;
        Ledger.Succeeded(new(DeferKind.UnreachableQuest, quest));
        Ledger.Succeeded(new(DeferKind.Quest, quest));
        _questSetAsides.Remove(quest);
    }

    /// <summary>Lift the set-aside of <paramref name="entry"/> before its time (the reason for it passed). Its strikes stay.</summary>
    internal void ReleaseEntry(uint entry, uint nowMs) => Ledger.Wake(new(DeferKind.Entry, entry), T(nowMs));

    /// <summary>Lift the set-aside of <paramref name="quest"/> before its time (the reason for it passed). The count of set-asides is kept.</summary>
    internal void ReleaseQuest(uint quest, uint nowMs) => Ledger.Wake(new(DeferKind.Quest, quest), T(nowMs));

    internal int EntryStrikeCount(uint entry) => Ledger.Strikes(new(DeferKind.Entry, entry));

    /// <summary>
    /// Set <paramref name="entry"/> aside with doubling backoff (<c>2 * SuspendMs</c> up to <see cref="MaxSuspendMs"/>) when a bot
    /// repeatedly stalls on it at a dead-end pin. Returns the length of the set-aside.
    /// </summary>
    internal uint SuspendEntryEscalating(uint entry, uint nowMs)
    {
        if (entry == 0) return 0;
        return Ledger.Defer(new(DeferKind.Entry, entry), T(nowMs), minStrikes: 2).Ms;
    }

    /// <param name="countsAsSetAside">False for a set-aside that says nothing about abandoning the quest (a walk refused as a proven dead
    /// end): the count gates abandoning completed quests (<see cref="MaxReturnSetAsides"/>).</param>
    internal void SuspendQuest(uint quest, uint nowMs, bool countsAsSetAside = true)
    {
        if (quest == 0) return;
        long now = T(nowMs);
        Ledger.Defer(new(DeferKind.Quest, quest), now, untilMs: now + SuspendMs, strike: false);
        if (!countsAsSetAside) return;
        if (_questSetAsides.Count >= 256 && !_questSetAsides.ContainsKey(quest)) _questSetAsides.Clear();
        _questSetAsides[quest] = _questSetAsides.GetValueOrDefault(quest) + 1;
    }

    /// <summary>
    /// The rows to save: the live set-asides of the persisted kinds (entry, quest, training, unreachable quest) with their absolute
    /// expiry, and an unreachable quest whose set-aside ended with its strikes (expiry now), which only a turn-in clears. An entry's and
    /// an unreachable quest's <see cref="PlayerbotSetAside.RepeatCount"/> carries its strikes.
    /// </summary>
    internal PlayerbotSetAside[] Export(Guid botId, int characterId, uint nowMs, long unixNowMs)
    {
        List<DeferRecord> live = Ledger.Persistable(T(nowMs));
        var rows = new PlayerbotSetAside[live.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            DeferRecord record = live[i];
            var kind = (PlayerbotSetAsideKind)PlayerbotDeferLedger.PersistAs(record.Key.Kind);
            int repeat = kind is PlayerbotSetAsideKind.Entry or PlayerbotSetAsideKind.UnreachableQuest
                ? Math.Min(record.Strikes, PlayerbotSetAside.MaxRepeatCount) : 0;
            rows[i] = new PlayerbotSetAside(botId, characterId, kind, (uint)record.Key.Id,
                unixNowMs + record.RemainingMs, repeat);
        }

        return rows;
    }

    /// <summary>Count of live persisted set-asides with time remaining, without allocating an export array.</summary>
    internal int ActiveExpiringCount(uint nowMs) => Ledger.LivePersistedCount(T(nowMs));

    /// <summary>
    /// Put back saved set-asides. A row that ended is dropped (an unreachable quest's keeps its strikes); one longer than its kind allows (a clock set back) is shortened to the
    /// kind's longest (escalated entries last up to <see cref="MaxSuspendMs"/>). A legacy never-expiring unreachable-quest row
    /// (<c>ExpiresUnixMs == long.MaxValue</c>) becomes a finite one: a minute plus a stable share of four more, so the bots that
    /// restart together do not all retry the quest at once, with two strikes; the next save writes it finite.
    /// </summary>
    internal void Restore(IEnumerable<PlayerbotSetAside> rows, uint nowMs, long unixNowMs)
    {
        long now = T(nowMs);
        foreach (PlayerbotSetAside row in rows)
        {
            DeferKind kind;
            switch (row.Kind)
            {
                case PlayerbotSetAsideKind.Entry when row.TargetId != 0: kind = DeferKind.Entry; break;
                case PlayerbotSetAsideKind.Quest when row.TargetId != 0: kind = DeferKind.Quest; break;
                case PlayerbotSetAsideKind.Training when row.TargetId == 0: kind = DeferKind.Training; break;
                case PlayerbotSetAsideKind.UnreachableQuest when row.TargetId != 0: kind = DeferKind.UnreachableQuest; break;
                default: continue;
            }

            DeferKey key = new(kind, row.TargetId);
            if (kind == DeferKind.UnreachableQuest && row.ExpiresUnixMs == long.MaxValue)
            {
                Ledger.RestoreEntry(key, now, 60_000u + LegacyStagger(row.BotId, row.TargetId), 2);
                Ledger.MarkChanged();
                continue;
            }

            long remaining = row.ExpiresUnixMs - unixNowMs;
            if (remaining <= 0)
            {
                // An unreachable quest keeps its strikes after its set-aside ended: only a turn-in clears them.
                if (kind == DeferKind.UnreachableQuest && row.RepeatCount > 0) Ledger.RestoreEntry(key, now, 0, row.RepeatCount);
                continue;
            }

            uint ms = (uint)Math.Min(remaining, PlayerbotDeferLedger.PolicyOf(kind).MaxMs);
            int strikes = kind is DeferKind.Entry or DeferKind.UnreachableQuest ? Math.Max(1, row.RepeatCount) : 1;
            Ledger.RestoreEntry(key, now, ms, strikes);
        }
    }

    /// <summary>A stable 0 to 240 s share for a legacy row (<see cref="Restore"/>).</summary>
    internal static uint LegacyStagger(Guid botId, uint quest)
    {
        Span<byte> bytes = stackalloc byte[16];
        botId.TryWriteBytes(bytes);
        ulong bot = BitConverter.ToUInt64(bytes) ^ PlayerbotDeferLedger.Mix(BitConverter.ToUInt64(bytes[8..]));
        return (uint)(PlayerbotDeferLedger.Mix(bot ^ ((ulong)quest * 0x9E3779B97F4A7C15UL)) % 240_000UL);
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

    /// <summary>
    /// The world whose 64-bit uptime is the records' clock (<see cref="PlayerbotWorldClock.Ms(ArcaneCore.Game.Maps.WorldRuntime, uint)"/>): a
    /// record is kept until its key is evicted, so its deadline can be weeks old when it is asked, past what a 32-bit difference tells apart.
    /// Without a world (tests only; the feature sets it on attach) the given time is used as it is, with no wrap handling, so a caller
    /// without a world must keep its times well below <see cref="uint.MaxValue"/>.
    /// </summary>
    internal ArcaneCore.Game.Maps.WorldRuntime? World { get; set; }

    // The records' 64-bit time for a 32-bit world time: see World.
    private long T(uint nowMs) => World is { } world ? PlayerbotWorldClock.Ms(world, nowMs) : nowMs;

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
        long now = T(nowMs);
        if (!_records.TryGetValue(key, out Record? record))
        {
            if (_records.Count >= MaxKeys)
                _records.Remove(_records.OrderBy(pair => pair.Value.LastMs).First().Key);
            _records[key] = record = new Record();
        }

        if (record.Times != 0 && now - record.LastMs > RepeatQuietMs && !Active(record, now))
        {
            record.Times = 0;
            record.UntilMs = 0;
        }
        record.LastMs = now;
        record.Bots.RemoveAll(seen => now - seen.AtMs > WindowMs || seen.Bot == bot);
        record.Bots.Add(new Stall(bot, now, map, position));
        if (record.Bots.Count < BotsToShare || Active(record, now)) return null;
        uint length = (uint)Math.Min((ulong)SetAsideMs << Math.Min(record.Times, 8), MaxSetAsideMs);
        record.Times = Math.Min(record.Times + 1, PlayerbotSetAside.MaxRepeatCount);
        record.UntilMs = now + length;
        Interlocked.Increment(ref _version);
        string stalls = string.Join("; ", record.Bots.Select(seen => string.Create(CultureInfo.InvariantCulture,
            $"{seen.Position.X:F1},{seen.Position.Y:F1},{seen.Position.Z:F1} map {seen.Map}")));
        record.Bots.Clear();
        return new PlayerbotSharedSetAside(string.Create(CultureInfo.InvariantCulture,
            $"{(key.Quest ? "quest" : "entry")} {key.Id} for {length / 60_000} min"), stalls);
    }

    internal bool IsQuestSetAside(uint quest, uint nowMs) => _records.TryGetValue((true, quest), out Record? record) && Active(record, T(nowMs));

    internal bool IsEntrySetAside(uint entry, uint nowMs) => _records.TryGetValue((false, entry), out Record? record) && Active(record, T(nowMs));

    internal PlayerbotSetAside[] Export(uint nowMs, long unixNowMs)
    {
        long now = T(nowMs);
        return [.. _records.Where(pair => pair.Key.Id != 0 && Remaining(pair.Value.UntilMs, now) is > 0)
            .Select(pair => new PlayerbotSetAside(Guid.Empty, 0,
                pair.Key.Quest ? PlayerbotSetAsideKind.SharedQuest : PlayerbotSetAsideKind.SharedEntry,
                pair.Key.Id, unixNowMs + Remaining(pair.Value.UntilMs, now), pair.Value.Times))];
    }

    internal void Restore(IEnumerable<PlayerbotSetAside> rows, uint nowMs, long unixNowMs)
    {
        long now = T(nowMs);
        foreach (PlayerbotSetAside row in rows)
        {
            long remaining = row.ExpiresUnixMs - unixNowMs;
            if (remaining <= 0 || remaining > MaxSetAsideMs || row.TargetId == 0 || _records.Count >= MaxKeys) continue;
            bool quest = row.Kind == PlayerbotSetAsideKind.SharedQuest;
            if (!quest && row.Kind != PlayerbotSetAsideKind.SharedEntry) continue;
            _records[(quest, row.TargetId)] = new Record
            {
                UntilMs = now + remaining, Times = Math.Clamp(row.RepeatCount, 1, PlayerbotSetAside.MaxRepeatCount), LastMs = now,
            };
        }
    }

    private static long Remaining(long until, long now) => Math.Max(0, until - now);

    private static bool Active(Record record, long now) => record.UntilMs != 0 && record.UntilMs > now;

    private readonly record struct Stall(ulong Bot, long AtMs, uint Map, Vector3 Position);

    private sealed class Record
    {
        public List<Stall> Bots { get; } = [];
        public long UntilMs { get; set; }
        public int Times { get; set; }
        public long LastMs { get; set; }
    }
}

/// <summary>
/// A goal newly set aside for every bot (<see cref="PlayerbotSharedSetAsides.Report"/>): <see cref="Goal"/> as the log names it
/// ("quest 1656 for 30 min") and <see cref="Stalls"/>, where the bots whose stalls set it aside stood ("x,y,z map m; ..."), oldest first.
/// </summary>
internal readonly record struct PlayerbotSharedSetAside(string Goal, string Stalls);

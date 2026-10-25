using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.WorldState;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>How the coordinator finds a catalogued boss in the world.</summary>
internal enum PlayerbotWorldBossLocatorKind : byte
{
    /// <summary>A database spawn on a continent: found by its spawn guid (the respawn ledger knows its death).</summary>
    Spawn,

    /// <summary>A Dragon of Nightmare: found at the portal the dragon event puts it at (<see cref="NightmareDragonsFeature.Portals"/>).</summary>
    Portal,

    /// <summary>Placed by something other than a database spawn (an operator's <c>entry@map</c>): looked for by entry on the map.</summary>
    Placed,
}

/// <summary>Where a boss is looked for (<see cref="PlayerbotWorldBossLocatorKind"/>).</summary>
internal sealed record PlayerbotWorldBossLocator(PlayerbotWorldBossLocatorKind Kind, uint MapId, uint SpawnGuid, Vector3 Position, int PortalIndex)
{
    internal static PlayerbotWorldBossLocator Spawn(uint mapId, uint spawnGuid, Vector3 position) => new(PlayerbotWorldBossLocatorKind.Spawn, mapId, spawnGuid, position, -1);

    internal static PlayerbotWorldBossLocator Portal(int portalIndex, uint mapId, Vector3 position) => new(PlayerbotWorldBossLocatorKind.Portal, mapId, 0, position, portalIndex);

    internal static PlayerbotWorldBossLocator Placed(uint mapId) => new(PlayerbotWorldBossLocatorKind.Placed, mapId, 0, Vector3.Zero, -1);
}

/// <summary>One boss of the catalogue: its template's name and level, where it is found, the operator's raid size for it, and whether the content rule found it.</summary>
internal sealed record PlayerbotWorldBossEntry(uint Entry, string Name, byte Level, PlayerbotWorldBossLocator Locator, int? SizeOverride, bool Derived);

/// <summary>
/// The world bosses the bots raid (docs/areas/playbots-groups.md, World-boss raids): Azuregos, Lord Kazzak and the four Dragons of Nightmare by
/// entry, whatever else the content rule derives from the creature rows, plus the operator's <c>IncludedEntries</c>, less the
/// <c>ExcludedEntries</c>. Immutable once built; <see cref="EntrySet"/> is the frozen snapshot the map-thread kill talk reads.
/// <para>
/// <b>The derive rule.</b> A creature spawn on map 0 or 1 whose template is rank 3 (world boss), no racial leader, without the instance-bind
/// extra flag, with none of the unit flags 2 (non-attackable), 256 (immune to players) or 512 (immune to NPCs), whose faction is not friendly to
/// the human or the orc starting faction, and whose spawn no game event places. In the world database that keeps exactly Azuregos, Lord Kazzak
/// and Teremus the Devourer of the 21 rank-3 creatures on the continents: the faction leaders, Archbishop Benedictus, Varimathras, Lady Jaina,
/// Mekkatorque, Vol'jin, Anachronos, the Spirit of Azuregos, General Rajaxx and the Colossi each fail a rule (docs: the rows are pinned by
/// <c>PlayerbotWorldBossCatalogueTests</c>). A faction the data does not know fails closed.
/// </para>
/// </summary>
internal sealed class PlayerbotWorldBossCatalogue
{
    /// <summary>creature_template.ExtraFlags bit 0 (CREATURE_FLAG_EXTRA_INSTANCE_BIND).</summary>
    private const uint ExtraInstanceBind = 1;

    /// <summary>Unit flags 2 (non-attackable), 256 (immune to players), 512 (immune to NPCs).</summary>
    private const uint ExcludedUnitFlags = 2 | 256 | 512;

    /// <summary>The creature rank of a world boss (<c>CreatureRank.WorldBoss</c>).</summary>
    private const uint RankWorldBoss = 3;

    /// <summary>The faction templates of the two teams' starting races the derive rule tests friendliness against (Human, Orc).</summary>
    private const uint HumanFactionTemplate = 1, OrcFactionTemplate = 2;

    private static readonly (uint Entry, int Portal)[] Known =
    [
        (6109, -1),                                      // Azuregos
        (12397, -1),                                     // Lord Kazzak
        (NightmareDragonsFeature.Ysondre, 0),
        (NightmareDragonsFeature.Lethon, 1),
        (NightmareDragonsFeature.Emeriss, 2),
        (NightmareDragonsFeature.Taerar, 3),
    ];

    private readonly Dictionary<uint, PlayerbotWorldBossEntry> _byEntry;

    private PlayerbotWorldBossCatalogue(List<PlayerbotWorldBossEntry> entries, List<string> warnings)
    {
        Entries = entries;
        _byEntry = entries.ToDictionary(e => e.Entry);
        Warnings = warnings;
        EntrySet = _byEntry.Keys.ToFrozenSet();
    }

    /// <summary>The bosses, in the order known, derived, included.</summary>
    internal IReadOnlyList<PlayerbotWorldBossEntry> Entries { get; }

    /// <summary>The operator's entries that were ignored, each with its reason (logged once per build).</summary>
    internal IReadOnlyList<string> Warnings { get; }

    /// <summary>The frozen set of the entries: safe to read from any thread.</summary>
    internal FrozenSet<uint> EntrySet { get; }

    internal PlayerbotWorldBossEntry? Find(uint entry) => _byEntry.GetValueOrDefault(entry);

    /// <summary>Whether <paramref name="entry"/> is a catalogued world boss (any thread).</summary>
    internal bool IsWorldBossEntry(uint entry) => EntrySet.Contains(entry);

    internal static PlayerbotWorldBossCatalogue Empty { get; } = new([], []);

    /// <summary>Build the catalogue from the creature rows in force. <paramref name="eventSpawns"/> are the spawn guids a game event places.</summary>
    internal static PlayerbotWorldBossCatalogue Build(PlayerbotWorldBossOptions options, CreatureContent content, FactionTemplateCatalog factions, IReadOnlySet<uint> eventSpawns)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(eventSpawns);
        var entries = new List<PlayerbotWorldBossEntry>();
        var seen = new HashSet<uint>();
        var warnings = new List<string>();

        foreach ((uint entry, int portal) in Known)
        {
            if (content.FindTemplate(entry) is not { } template) continue;
            PlayerbotWorldBossLocator? locator = portal >= 0
                ? PlayerbotWorldBossLocator.Portal(portal, NightmareDragonsFeature.Portals[portal].MapId,
                    new Vector3(NightmareDragonsFeature.Portals[portal].X, NightmareDragonsFeature.Portals[portal].Y, NightmareDragonsFeature.Portals[portal].Z))
                : SpawnLocator(content, entry);
            if (locator is null || !seen.Add(entry)) continue;
            entries.Add(new PlayerbotWorldBossEntry(entry, template.Name, template.MaxLevel, locator, null, Derived: false));
        }

        if (options.DeriveFromContent)
            foreach (PlayerbotWorldBossEntry derived in Derive(content, factions, eventSpawns))
                if (seen.Add(derived.Entry)) entries.Add(derived);

        foreach (string token in PlayerbotWorldBossOptions.Tokens(options.IncludedEntries))
        {
            if (!PlayerbotWorldBossOptions.TryParseIncluded(token, out uint entry, out uint? map))
            {
                warnings.Add($"IncludedEntries: '{token}' is not an entry or entry@map with map 0 or 1; ignored");
                continue;
            }

            if (seen.Contains(entry)) continue;
            if (content.FindTemplate(entry) is not { } template)
            {
                warnings.Add($"IncludedEntries: creature {entry} has no template; ignored");
                continue;
            }

            PlayerbotWorldBossLocator? locator = map is { } placedOn ? PlayerbotWorldBossLocator.Placed(placedOn) : SpawnLocator(content, entry);
            if (locator is null)
            {
                warnings.Add($"IncludedEntries: creature {entry} has no spawn on a continent (write {entry}@0 or {entry}@1 if something else places it); ignored");
                continue;
            }

            seen.Add(entry);
            entries.Add(new PlayerbotWorldBossEntry(entry, template.Name, template.MaxLevel, locator, null, Derived: false));
        }

        var excluded = new HashSet<uint>();
        foreach (string token in PlayerbotWorldBossOptions.Tokens(options.ExcludedEntries))
            if (PlayerbotWorldBossOptions.TryParseEntry(token, out uint entry)) excluded.Add(entry);
        entries.RemoveAll(e => excluded.Contains(e.Entry));

        var sizes = new Dictionary<uint, int>();
        foreach (string token in PlayerbotWorldBossOptions.Tokens(options.SizeOverrides))
            if (PlayerbotWorldBossOptions.TryParseSizeOverride(token, out uint entry, out int size)) sizes[entry] = size;
        for (int i = 0; i < entries.Count; i++)
            if (sizes.TryGetValue(entries[i].Entry, out int size)) entries[i] = entries[i] with { SizeOverride = size };

        return new PlayerbotWorldBossCatalogue(entries, warnings);
    }

    /// <summary>The first (lowest guid) continent spawn of <paramref name="entry"/>, map 0 before map 1; null when it has none.</summary>
    private static PlayerbotWorldBossLocator? SpawnLocator(CreatureContent content, uint entry)
    {
        for (uint map = 0; map <= 1; map++)
            if (content.GetSpawns(map, entry) is { Count: > 0 } spawns)
                return PlayerbotWorldBossLocator.Spawn(map, spawns[0].Guid, new Vector3(spawns[0].X, spawns[0].Y, spawns[0].Z));
        return null;
    }

    /// <summary>The creatures the derive rule keeps (see the class remarks), one per entry, by spawn guid.</summary>
    internal static IEnumerable<PlayerbotWorldBossEntry> Derive(CreatureContent content, FactionTemplateCatalog factions, IReadOnlySet<uint> eventSpawns)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(eventSpawns);
        var found = new List<PlayerbotWorldBossEntry>();
        if (factions.Find(HumanFactionTemplate) is not { } human || factions.Find(OrcFactionTemplate) is not { } orc) return found;
        var done = new HashSet<uint>();
        for (uint map = 0; map <= 1; map++)
            foreach (CreatureSpawn spawn in content.GetSpawns(map))
            {
                if (done.Contains(spawn.Entry) || eventSpawns.Contains(spawn.Guid) || content.FindTemplate(spawn.Entry) is not { } template) continue;
                if (template.Rank != RankWorldBoss || template.RacialLeader || (template.ExtraFlags & ExtraInstanceBind) != 0
                    || (template.UnitFlags & ExcludedUnitFlags) != 0) continue;
                if (factions.Find(template.Faction) is not { } faction || faction.IsFriendlyTo(human) || faction.IsFriendlyTo(orc)) continue;
                done.Add(spawn.Entry);
                found.Add(new PlayerbotWorldBossEntry(spawn.Entry, template.Name, template.MaxLevel,
                    PlayerbotWorldBossLocator.Spawn(map, spawn.Guid, new Vector3(spawn.X, spawn.Y, spawn.Z)), null, Derived: true));
            }

        return found;
    }
}

// --- status ------------------------------------------------------------------------------------------------------------------------

/// <summary>What the boss watch concludes about a boss.</summary>
internal enum PlayerbotWorldBossState : byte
{
    /// <summary>Standing in the world, alive.</summary>
    Up,

    /// <summary>Dead: until <see cref="PlayerbotWorldBossStatus.DeadUntilUnix"/> when the respawn ledger knows, else for an unknown time.</summary>
    Dead,

    /// <summary>Not seen and its grid is not loaded: nothing is known, and the bots never travel to it.</summary>
    Unknown,
}

/// <summary>What the watch saw of one boss (<see cref="PlayerbotWorldBossStatus.Resolve"/>).</summary>
internal sealed record PlayerbotWorldBossFacts
{
    /// <summary>A living creature of the boss's entry stands where it is looked for.</summary>
    public bool Living { get; init; }

    public Vector3 Position { get; init; }

    public bool InCombat { get; init; }

    /// <summary>The boss is tapped by a player, or fights one.</summary>
    public bool TappedOrFightingOthers { get; init; }

    public int RealPlayersNear { get; init; }

    /// <summary>The grid at the boss's place is loaded, so a boss that is not there is not there.</summary>
    public bool GridLoaded { get; init; }

    /// <summary>The respawn ledger's time for the boss's spawn (Unix seconds; 0 when it has none).</summary>
    public long PendingRespawnUnix { get; init; }
}

/// <summary>The watch's conclusion for one boss.</summary>
internal sealed record PlayerbotWorldBossStatus(PlayerbotWorldBossState State, Vector3 Position, bool InCombat, bool TappedOrFightingOthers, int RealPlayersNear, long? DeadUntilUnix)
{
    /// <summary>
    /// A living creature is Up (a stale ledger entry never beats it). Otherwise a respawn in the future is Dead until then, wherever the grid is; with none, a
    /// loaded grid means Dead with no known time, and an unloaded one Unknown: never Up on no evidence.
    /// </summary>
    internal static PlayerbotWorldBossStatus Resolve(PlayerbotWorldBossFacts facts, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Living)
            return new PlayerbotWorldBossStatus(PlayerbotWorldBossState.Up, facts.Position, facts.InCombat, facts.TappedOrFightingOthers, facts.RealPlayersNear, null);
        if (facts.PendingRespawnUnix > nowUnix)
            return new PlayerbotWorldBossStatus(PlayerbotWorldBossState.Dead, facts.Position, false, false, 0, facts.PendingRespawnUnix);
        return new PlayerbotWorldBossStatus(facts.GridLoaded ? PlayerbotWorldBossState.Dead : PlayerbotWorldBossState.Unknown, facts.Position, false, false, 0, null);
    }

    /// <summary>
    /// The entry a dragon portal asks for: the entry standing at the portal while the event runs and the portal's own killed bit is clear, else 0.
    /// </summary>
    internal static uint PortalEntry(bool active, int killedMask, int portal, uint entryAtPortal)
        => active && (killedMask & (1 << portal)) == 0 ? entryAtPortal : 0;
}

// --- the campaign ---------------------------------------------------------------------------------------------------------------

/// <summary>Where a boss's campaign stands.</summary>
internal enum PlayerbotWorldBossPhase : byte
{
    /// <summary>Watching the boss.</summary>
    Watch,

    /// <summary>A muster is open: the bots are being asked, and the window is running.</summary>
    Open,

    /// <summary>A raid exists for the boss.</summary>
    Formed,
}

/// <summary>Why a boss may or may not be opened for a muster (<see cref="PlayerbotWorldBossCampaign.MayOpen"/>).</summary>
public enum PlayerbotWorldBossOpening : byte
{
    Ok,
    Disabled,
    NotUp,
    Busy,
    Grace,
    Attempts,
    Cooldown,
    Concurrent,
    Groups,
    QuestSlot,
}

/// <summary>The server-wide load the caps look at: raids under way, groups the bots own, whether a quest-group slot is free.</summary>
internal readonly record struct PlayerbotWorldBossLoad(int ActiveRaids, int OwnGroupCount, bool QuestGroupSlotFree);

/// <summary>
/// One boss's campaign: when it was first seen up, its phase, the attempts of the UTC day and the retry cooldown. Only a raid that reached the
/// walk to the boss (Travelling) is an attempt; a muster that never formed or a raid that never left forming only sets the cooldown. World
/// thread; times are Unix seconds.
/// </summary>
internal sealed class PlayerbotWorldBossCampaign(uint entry)
{
    private const long Day = 86_400;

    private long _upSince = -1;
    private long _openedAt;
    private long _attemptDay = -1;
    private int _attempts;

    internal uint Entry { get; } = entry;

    internal PlayerbotWorldBossPhase Phase { get; private set; }

    /// <summary>No muster is opened before this time (0: no cooldown).</summary>
    internal long CooldownUntilUnix { get; private set; }

    /// <summary>The boss is seen up now; the first sighting of a run of sightings starts the player grace.</summary>
    internal void NoteUp(long nowUnix)
    {
        if (_upSince < 0) _upSince = nowUnix;
    }

    /// <summary>The boss is dead, gone or unknown: the next sighting starts the grace afresh.</summary>
    internal void NoteNotUp() => _upSince = -1;

    internal int AttemptsToday(long nowUnix) => nowUnix / Day == _attemptDay ? _attempts : 0;

    /// <summary>Whether a muster for the boss may open now; the first reason that forbids it otherwise.</summary>
    internal PlayerbotWorldBossOpening MayOpen(PlayerbotWorldBossOptions options, int maxGroups, in PlayerbotWorldBossLoad load, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return PlayerbotWorldBossOpening.Disabled;
        if (_upSince < 0) return PlayerbotWorldBossOpening.NotUp;
        if (Phase != PlayerbotWorldBossPhase.Watch) return PlayerbotWorldBossOpening.Busy;
        if (nowUnix - _upSince < options.PlayerGraceMinutes * 60L) return PlayerbotWorldBossOpening.Grace;
        if (AttemptsToday(nowUnix) >= options.MaxAttemptsPerDay) return PlayerbotWorldBossOpening.Attempts;
        if (nowUnix < CooldownUntilUnix) return PlayerbotWorldBossOpening.Cooldown;
        if (load.ActiveRaids >= options.MaxConcurrentRaids) return PlayerbotWorldBossOpening.Concurrent;
        if (load.OwnGroupCount >= maxGroups) return PlayerbotWorldBossOpening.Groups;
        if (!load.QuestGroupSlotFree) return PlayerbotWorldBossOpening.QuestSlot;
        return PlayerbotWorldBossOpening.Ok;
    }

    internal void Open(long nowUnix)
    {
        Phase = PlayerbotWorldBossPhase.Open;
        _openedAt = nowUnix;
    }

    internal void Formed() => Phase = PlayerbotWorldBossPhase.Formed;

    /// <summary>The open muster has run past its window.</summary>
    internal bool MusterExpired(PlayerbotWorldBossOptions options, long nowUnix)
        => Phase == PlayerbotWorldBossPhase.Open && nowUnix - _openedAt >= options.MusterWindowSeconds;

    /// <summary>The window ran out without a raid: back to watching, behind the retry cooldown, no attempt used.</summary>
    internal void MusterFailed(PlayerbotWorldBossOptions options, long nowUnix)
    {
        Phase = PlayerbotWorldBossPhase.Watch;
        CooldownUntilUnix = nowUnix + options.RetrySeconds;
    }

    /// <summary>The raid ended: back to watching; an attempt when it had reached Travelling; the retry cooldown either way.</summary>
    internal void Ended(PlayerbotWorldBossOptions options, long nowUnix, bool reachedTravelling, bool killed)
    {
        Phase = PlayerbotWorldBossPhase.Watch;
        CooldownUntilUnix = nowUnix + options.RetrySeconds;
        if (!reachedTravelling && !killed) return;
        long day = nowUnix / Day;
        if (day != _attemptDay)
        {
            _attemptDay = day;
            _attempts = 0;
        }

        _attempts++;
    }
}

// --- the muster -----------------------------------------------------------------------------------------------------------------

/// <summary>How a muster came out.</summary>
internal enum PlayerbotWorldBossMusterVerdict : byte
{
    /// <summary>A raid plan exists.</summary>
    Formed,

    /// <summary>No bot of the team is eligible near the boss.</summary>
    NoCandidates,

    /// <summary>Fewer bots agreed than the minimum raid size.</summary>
    TooFew,

    /// <summary>Enough agreed, but not with the tanks and healers a raid of that size needs.</summary>
    Roles,
}

/// <summary>The outcome of one muster: the caller (the nearest eligible bot, who leads), the plan, and the size asked for.</summary>
internal sealed record PlayerbotWorldBossMusterResult(PlayerbotWorldBossMusterVerdict Verdict, PlayerbotGroupCandidate? Caller, PlayerbotGroupPlan? Plan, int Size);

/// <summary>
/// The pure muster rules (docs/areas/playbots-groups.md, World-boss raids): who is asked, who agrees and which raid that makes. The nearest eligible bot to the
/// boss is the caller, chosen before any roll, and leads whatever its own roll would have said. Every other eligible bot rolls once per boss, UTC day and
/// team (<see cref="Seed"/>), with the cooperation chance, so a refusal holds for the day and a bot is not asked afresh every scan. The raid is as big as
/// agreed up to the target and the cooperation raid cap, matched with the raid tank and healer quotas, favouring the bots nearest the boss.
/// </summary>
internal static class PlayerbotWorldBossMuster
{
    /// <summary>The level players reach (PlayerbotPopulationOptions.MaxLevel's default): a boss above it asks no more of the bots than this.</summary>
    internal const int PlayerLevelCap = 60;

    /// <summary>The lowest level a bot may have: the lower of the boss's level and the player cap, less <see cref="PlayerbotWorldBossOptions.LevelGap"/>.</summary>
    internal static byte LevelFloor(PlayerbotWorldBossOptions options, byte bossLevel)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (byte)Math.Max(1, Math.Min((int)bossLevel, PlayerLevelCap) - options.LevelGap);
    }

    /// <summary>The willingness seed of a (boss, UTC day, team): fixed for the day.</summary>
    internal static int Seed(uint bossEntry, long utcDay, uint team)
        => (int)PlayerbotContention.Mix(PlayerbotContention.Mix(bossEntry, (ulong)utcDay), team);

    /// <summary>The candidate nearest to <paramref name="bossAt"/>; on a tie the lower bot id. Null for none.</summary>
    internal static PlayerbotGroupCandidate? Caller(IReadOnlyList<PlayerbotGroupCandidate> candidates, Vector3 bossAt)
    {
        PlayerbotGroupCandidate? best = null;
        float bestYards = float.MaxValue;
        foreach (PlayerbotGroupCandidate candidate in candidates)
        {
            float yards = Vector3.DistanceSquared(candidate.Position, bossAt);
            if (best is null || yards < bestYards || yards == bestYards && candidate.BotId.CompareTo(best.BotId) < 0)
            {
                best = candidate;
                bestYards = yards;
            }
        }

        return best;
    }

    /// <summary>
    /// Muster for <paramref name="boss"/>: the eligible candidates are the free bots of <paramref name="team"/> on the boss's map, at or above the level floor,
    /// within <see cref="PlayerbotWorldBossOptions.MusterRadiusYards"/>. <paramref name="facts"/> gives the recruiting facts of a (caller, candidate) pair.
    /// </summary>
    internal static PlayerbotWorldBossMusterResult Plan(PlayerbotWorldBossOptions options, PlayerbotGroupOptions groups, PlayerbotWorldBossEntry boss, uint team,
        long utcDay, IReadOnlyList<PlayerbotGroupCandidate> candidates, Func<PlayerbotGroupCandidate, PlayerbotGroupCandidate, PlayerbotRecruitFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(boss);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(facts);
        byte floor = LevelFloor(options, boss.Level);
        Vector3 bossAt = boss.Locator.Position;
        float radiusSquared = options.MusterRadiusYards * options.MusterRadiusYards;
        var eligible = new List<PlayerbotGroupCandidate>();
        foreach (PlayerbotGroupCandidate candidate in candidates)
            if (candidate.Team == team && candidate.MapId == boss.Locator.MapId && candidate.Level >= floor
                && Vector3.DistanceSquared(candidate.Position, bossAt) <= radiusSquared)
                eligible.Add(candidate);

        if (Caller(eligible, bossAt) is not { } caller)
            return new PlayerbotWorldBossMusterResult(PlayerbotWorldBossMusterVerdict.NoCandidates, null, null, 0);

        int seed = Seed(boss.Entry, utcDay, team);
        var agreed = new List<PlayerbotGroupCandidate> { caller };
        foreach (PlayerbotGroupCandidate candidate in eligible)
            if (candidate.BotId != caller.BotId && PlayerbotRecruitment.Accepts(groups.Cooperation, candidate.Guid, seed, facts(caller, candidate)))
                agreed.Add(candidate);

        int target = boss.SizeOverride ?? options.DefaultRaidSize;
        int minimum = Math.Min(options.MinRaidSize, target);
        int size = Math.Min(Math.Min(target, PlayerbotRecruitment.SignalLimit(groups)), agreed.Count);
        if (size < minimum || size < 2)
            return new PlayerbotWorldBossMusterResult(PlayerbotWorldBossMusterVerdict.TooFew, caller, null, size);

        // The nearest bots to the boss come first: Match takes the longest waiting first among equals, so the order of the rank is the order of the need.
        var ranked = new List<PlayerbotGroupCandidate>(agreed.Count);
        foreach (PlayerbotGroupCandidate candidate in agreed.OrderBy(c => c.BotId == caller.BotId ? 0f : Vector3.DistanceSquared(c.Position, bossAt)).ThenBy(c => c.BotId))
            ranked.Add(candidate with { NeedSinceMs = (uint)ranked.Count });

        (int tanks, int healers) = PlayerbotGroupContent.RunRoleNeeds(size, size > PlayerbotGroupContent.PartySize, groups);
        PlayerbotGroupPlan? plan = PlayerbotGroupContent.Match(ranked, size, groups, (tanks, healers));
        if (plan is null || plan.Members.All(m => m.Bot.BotId != caller.BotId))
            return new PlayerbotWorldBossMusterResult(PlayerbotWorldBossMusterVerdict.Roles, caller, null, size);

        PlayerbotGroupCandidate leader = plan.Members.First(m => m.Bot.BotId == caller.BotId).Bot;
        return new PlayerbotWorldBossMusterResult(PlayerbotWorldBossMusterVerdict.Formed, caller, new PlayerbotGroupPlan(leader, plan.Members), size);
    }
}

// --- the engage decision --------------------------------------------------------------------------------------------------------

/// <summary>What the raid does next about the boss (<see cref="PlayerbotWorldBossEngage.Decide"/>).</summary>
internal enum PlayerbotWorldBossAction : byte
{
    /// <summary>The raid's kill: settle the loot and finish.</summary>
    Kill,

    /// <summary>End the attempt with <see cref="PlayerbotWorldBossDecision.Reason"/>.</summary>
    Finish,

    /// <summary>Do not pull: wait where the raid is.</summary>
    Hold,

    /// <summary>The gate opens: the raid pulls.</summary>
    Release,

    /// <summary>A fight the raid is in.</summary>
    Fight,

    /// <summary>Released and not yet fighting: walk to the boss.</summary>
    Approach,
}

/// <summary>What holds the pull.</summary>
internal enum PlayerbotWorldBossHold : byte
{
    None,

    /// <summary>Another player or group holds the boss's tap or fights it.</summary>
    OthersClaim,

    /// <summary>Real players stand at the boss.</summary>
    PlayersNear,

    /// <summary>The raid waits for its own members (the pull gate).</summary>
    Gate,
}

/// <summary>What the raid saw of the boss at one tick.</summary>
internal sealed record PlayerbotWorldBossEngageFacts
{
    /// <summary>The boss creature (alive or a corpse) is on the map.</summary>
    public bool BossFound { get; init; }

    public bool BossAlive { get; init; }

    /// <summary>The raid's joined members.</summary>
    public int Members { get; init; }

    /// <summary>Members at the rally.</summary>
    public int Present { get; init; }

    public bool AnyResting { get; init; }

    /// <summary>The corpse carries the tap of a raid member (or its pet).</summary>
    public bool CorpseTapByRaid { get; init; }

    /// <summary>The raid held the boss's tap at some earlier tick (sticky).</summary>
    public bool TapSeen { get; init; }

    /// <summary>The respawn ledger holds a respawn for the boss's spawn.</summary>
    public bool LedgerPending { get; init; }

    /// <summary>Another player or group holds the living boss's tap or fights it.</summary>
    public bool OthersClaim { get; init; }

    /// <summary>How long somebody else's claim (a tap or fight, or players near) has lasted without a break.</summary>
    public long OthersClaimMs { get; init; }

    public int RealPlayersNear { get; init; }

    /// <summary>How long the raid has waited at the gate.</summary>
    public long WaitMs { get; init; }

    /// <summary>The raid is in the boss's fight.</summary>
    public bool FightingUs { get; init; }

    /// <summary>The living boss's tap is a raid member's.</summary>
    public bool TappedByRaid { get; init; }

    /// <summary>The gate has opened (the raid is approaching or fighting).</summary>
    public bool Released { get; init; }
}

/// <summary>The decision: the action, its reason when it ends the attempt, what holds the pull, and the flags the leader reads.</summary>
internal sealed record PlayerbotWorldBossDecision(PlayerbotWorldBossAction Action, string? Reason, PlayerbotWorldBossHold Hold, bool PullHeld, bool Waiting, bool SetTapSeen);

/// <summary>
/// The engage decision of a world-boss raid, in a fixed order: the boss dead or gone (the corpse's tap, the ledger and the tap seen decide whose kill it was),
/// a fight the raid is in, the holds that put players first (another's tap or fight, real players near; both end the attempt as <c>boss-taken</c> after
/// <see cref="PlayerbotWorldBossOptions.HoldSeconds"/>), the approach after the release, and the pull gate (<see cref="PlayerbotWorldBossOptions.PresentFraction"/>
/// of the raid at the rally, or <see cref="PlayerbotWorldBossOptions.PullWaitSeconds"/> and the minimum size).
/// </summary>
internal static class PlayerbotWorldBossEngage
{
    internal static PlayerbotWorldBossDecision Decide(PlayerbotWorldBossEngageFacts facts, PlayerbotWorldBossOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        if (!facts.BossAlive)
        {
            if (facts.BossFound) return facts.CorpseTapByRaid ? Of(PlayerbotWorldBossAction.Kill) : Finish("boss-taken");
            if (!facts.LedgerPending) return Finish("boss-gone");
            return facts.TapSeen ? Of(PlayerbotWorldBossAction.Kill) : Finish("boss-taken");
        }

        if (facts.FightingUs)
            return new PlayerbotWorldBossDecision(PlayerbotWorldBossAction.Fight, null, PlayerbotWorldBossHold.None, false, false, facts.TappedByRaid);

        bool playersHold = options.PlayersNearYards > 0f && facts.RealPlayersNear >= options.PlayersNearCount;
        if (facts.OthersClaim || playersHold)
        {
            if (facts.OthersClaimMs >= options.HoldSeconds * 1000L) return Finish("boss-taken");
            return Held(facts.OthersClaim ? PlayerbotWorldBossHold.OthersClaim : PlayerbotWorldBossHold.PlayersNear);
        }

        if (facts.Released) return Of(PlayerbotWorldBossAction.Approach);

        int needed = (int)Math.Ceiling((facts.Members * (double)options.PresentFraction) - 1e-6);
        if (facts.Present >= needed && !facts.AnyResting) return Of(PlayerbotWorldBossAction.Release);
        if (facts.WaitMs >= options.PullWaitSeconds * 1000L)
            return facts.Present >= options.MinRaidSize ? Of(PlayerbotWorldBossAction.Release) : Finish("not-enough");
        return Held(PlayerbotWorldBossHold.Gate);
    }

    private static PlayerbotWorldBossDecision Of(PlayerbotWorldBossAction action) => new(action, null, PlayerbotWorldBossHold.None, false, false, false);

    private static PlayerbotWorldBossDecision Finish(string reason) => new(PlayerbotWorldBossAction.Finish, reason, PlayerbotWorldBossHold.None, false, false, false);

    private static PlayerbotWorldBossDecision Held(PlayerbotWorldBossHold hold) => new(PlayerbotWorldBossAction.Hold, null, hold, true, true, false);
}

// --- the raid ---------------------------------------------------------------------------------------------------------------------

/// <summary>The raid warnings a world-boss raid's leader gives.</summary>
public enum PlayerbotWorldBossWarning : byte
{
    /// <summary>The raid has formed (names the rally zone).</summary>
    Form,

    /// <summary>The gate opened: pulling.</summary>
    Pull,

    /// <summary>The boss is down.</summary>
    Kill,

    /// <summary>The attempt is given up.</summary>
    GiveUp,
}

/// <summary>The state of one world-boss raid, carried by its <see cref="PlayerbotGroupCoordinator.BotGroup"/> (world thread).</summary>
internal sealed class PlayerbotWorldBossRaid(PlayerbotWorldBossEntry boss, string zone)
{
    internal PlayerbotWorldBossEntry Boss { get; } = boss;

    /// <summary>The name of the zone the boss stands in (the map's name where the area is not known): what the forming warning names.</summary>
    internal string Zone { get; } = zone;

    /// <summary>The pull is held back (somebody else's claim, or the raid is not all there): the leader stands at the rally.</summary>
    internal bool PullHeld { get; set; }

    /// <summary>The raid is waiting for something (a hold or the gate): the stall watch leaves it alone.</summary>
    internal bool Waiting { get; set; }

    internal PlayerbotWorldBossHold Hold { get; set; }

    /// <summary>The gate has opened.</summary>
    internal bool Released { get; set; }

    /// <summary>The raid saw the boss alive at some scan.</summary>
    internal bool SawAlive { get; set; }

    /// <summary>The raid held the boss's tap at some tick.</summary>
    internal bool TapSeen { get; set; }

    /// <summary>The raid left forming for the walk to the boss: the attempt counts.</summary>
    internal bool ReachedTravelling { get; set; }

    internal int Wipes { get; set; }

    /// <summary>World ms since when somebody else's claim has lasted without a break (0: none).</summary>
    internal uint ClaimSinceMs { get; set; }

    /// <summary>World ms another player's claim on the boss was last seen (0: never).</summary>
    internal uint ClaimSeenMs { get; set; }

    /// <summary>World ms since when the raid has waited at the gate (0: not waiting).</summary>
    internal uint GateSinceMs { get; set; }

    /// <summary>World ms of the kill (0: not killed): the raid settles the loot before it disbands.</summary>
    internal uint KilledAtMs { get; set; }

    /// <summary>The warnings given, in order.</summary>
    internal List<PlayerbotWorldBossWarning> Warnings { get; } = [];

    /// <summary>The Unix time the raid formed.</summary>
    internal long FormedUnix { get; set; }

    /// <summary>How far from the boss the raid gathers (flat yards from the rally point to the boss).</summary>
    internal float RallyYards { get; set; }

    /// <summary>The boss creature last found (empty: none cached): every later look is one lookup by guid, dropped when it finds nothing or another entry.</summary>
    internal ObjectGuid BossGuid { get; set; }

    /// <summary>World ms of the last scan of the creature system for the boss (0: none yet); a scan runs at most once per watch interval while nothing is cached.</summary>
    internal uint BossScanMs { get; set; }

    /// <summary>How many times the raid scanned the creature system for its boss (tests: the scan runs at most once per watch interval while nothing is cached).</summary>
    internal int BossScans { get; set; }

    /// <summary>World ms of the last engage decision (the boss is looked at a few times a second, not every tick; 0: never).</summary>
    internal uint CheckedMs { get; set; }
}

/// <summary>The wording of the raid warnings.</summary>
internal static class PlayerbotWorldBoss
{
    /// <summary>The longest chat message the client accepts.</summary>
    private const int MaxChat = 255;

    private static readonly string[] Forming =
    [
        "Raid forming for {0}. Rally at {1}.",
        "We are going after {0}. Everyone to {1}.",
        "{0} is up. Gathering at {1}.",
    ];

    private static readonly string[] Pulling =
    [
        "Pulling {0}. Stay on your roles.",
        "{0} in thirty yards. Pulling now.",
        "Engaging {0}. Healers ready.",
    ];

    private static readonly string[] Killed =
    [
        "{0} is down. Well done.",
        "That is {0} dead. Loot up.",
        "{0} killed. Nice work, raid.",
    ];

    private static readonly string[] GivingUp =
    [
        "Standing down from {0}.",
        "We are leaving {0}. Not this time.",
        "Calling off {0}.",
    ];

    /// <summary>The text of a warning: a wording picked by the group's id, always naming the boss, the forming one the zone.</summary>
    internal static string WarningText(PlayerbotWorldBossWarning kind, int groupId, string boss, string zone)
    {
        string[] words = kind switch
        {
            PlayerbotWorldBossWarning.Form => Forming,
            PlayerbotWorldBossWarning.Pull => Pulling,
            PlayerbotWorldBossWarning.Kill => Killed,
            _ => GivingUp,
        };
        string text = string.Format(CultureInfo.InvariantCulture, words[(int)((uint)groupId % (uint)words.Length)], boss, zone);
        return text.Length <= MaxChat ? text : text[..MaxChat];
    }
}

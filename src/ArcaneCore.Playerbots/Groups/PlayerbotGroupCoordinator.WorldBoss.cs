using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// World-boss raids (docs/areas/playbots-groups.md, World-boss raids): the bots watch the catalogued world bosses of the open world
/// (<see cref="PlayerbotWorldBossCatalogue"/>); when one has stood for <see cref="PlayerbotWorldBossOptions.PlayerGraceMinutes"/> and the
/// caps allow it, a muster opens (<see cref="PlayerbotWorldBossCampaign"/>), the free bots near it agree or not on a hash of (boss, UTC day,
/// team) and the nearest of them calls the raid (<see cref="PlayerbotWorldBossMuster"/>). The raid is an ordinary bot-led group of the
/// <see cref="PlayerbotGroupGoalKind.WorldBoss"/> goal: it gathers, walks to a rally <see cref="PlayerbotWorldBossOptions.RallyYards"/> short of the boss,
/// and there the engage decision (<see cref="PlayerbotWorldBossEngage"/>) puts the players first: the raid never pulls a boss another player or group
/// holds, nor one real players stand at, and gives it up as <c>boss-taken</c> after <see cref="PlayerbotWorldBossOptions.HoldSeconds"/>. Everything
/// here runs on the world thread; the only state read from other threads is the frozen entry snapshot behind <see cref="IsWorldBossEntry"/>.
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>How far past the rally a member may stand and still count as present at the boss.</summary>
    internal const float WorldBossPresentSlackYards = 20f;

    /// <summary>The least time between two engage decisions of one raid (the boss is looked at a few times a second, not every tick).</summary>
    internal const uint WorldBossCheckMs = 200;

    /// <summary>How long another player's claim on the boss lingers after it was last seen: a boss that evades and is tapped again a moment later is one claim, not a free pull.</summary>
    internal const uint WorldBossClaimLingerMs = 5_000;

    /// <summary>The least time between two muster attempts of one boss while its muster is open.</summary>
    internal const uint WorldBossMusterRetryMs = 10_000;

    private const long SecondsPerDay = 86_400;

    /// <summary>The fault sites of the world-boss code (the fault ledger and the log): the watch, the muster and a raid's advance.</summary>
    internal const string WorldBossWatchSite = "playerbot.groups.worldboss.watch", WorldBossMusterSite = "playerbot.groups.worldboss.muster", WorldBossAdvanceSite = "playerbot.groups.worldboss.advance";

    private PlayerbotWorldBossOptions Wb => _options.Groups.WorldBosses;

    /// <summary>The campaigns' clock, in Unix seconds (tests set it to a manual clock).</summary>
    internal Func<long> UnixNow { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private readonly record struct WorldBossKey(CreatureContent Content, int Version, FactionTemplateCatalog Factions, ISpawnGate? Gate, bool Derive, string Included, string Excluded, string Sizes);

    /// <summary>What the last watch saw of a boss: its status and the catalogue entry with the place it stands at.</summary>
    private sealed record WorldBossSeen(PlayerbotWorldBossStatus Status, PlayerbotWorldBossEntry Live);

    private volatile PlayerbotWorldBossCatalogue _wbCatalogue = PlayerbotWorldBossCatalogue.Empty;
    private WorldBossKey? _wbKey;
    private readonly Dictionary<uint, PlayerbotWorldBossCampaign> _wbCampaigns = [];
    private readonly Dictionary<uint, WorldBossSeen> _wbSeen = [];
    private readonly Dictionary<uint, uint> _wbMusterTriedMs = [];
    private readonly Dictionary<uint, PlayerbotWorldBossOpening> _wbOpening = [];
    private readonly Dictionary<uint, ObjectGuid> _wbBossGuid = [];
    private uint _wbWatchMs;
    private bool _wbWatched;

    /// <summary>
    /// Whether <paramref name="entry"/> is a catalogued world boss. Reads a volatile snapshot of the catalogue, so the map threads (the kill talk)
    /// may ask; false until the catalogue is built.
    /// </summary>
    internal bool IsWorldBossEntry(uint entry) => _wbCatalogue.IsWorldBossEntry(entry);

    /// <summary>The campaign of a catalogued boss (tests and status); null for an entry that is not one.</summary>
    internal PlayerbotWorldBossCampaign? WorldBossCampaign(uint entry) => _wbCampaigns.GetValueOrDefault(entry);

    private PlayerbotWorldBossCampaign CampaignOf(uint entry)
    {
        if (!_wbCampaigns.TryGetValue(entry, out PlayerbotWorldBossCampaign? campaign)) _wbCampaigns[entry] = campaign = new PlayerbotWorldBossCampaign(entry);
        return campaign;
    }

    private void ResetWorldBosses()
    {
        _wbCatalogue = PlayerbotWorldBossCatalogue.Empty;
        _wbKey = null;
        _wbCampaigns.Clear();
        _wbSeen.Clear();
        _wbMusterTriedMs.Clear();
        _wbOpening.Clear();
        _wbBossGuid.Clear();
        _wbWatchMs = 0;
        _wbWatched = false;
        PlayerbotWorldBossMeters.SetActive(0);
    }

    // --- the catalogue --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Build the catalogue once the creature content is installed, and again when the content, the faction table, the event gate or the options that
    /// shape it change. With the content derivation on it waits for the event spawn gate (the game events decide which rank-3 spawns are event bosses).
    /// </summary>
    private void EnsureWorldBossCatalogue()
    {
        if (_world is not { } world || CatalogueCreatures(services.GetService<CreatureWorldFeature>()) is not { } content) return;
        PlayerbotWorldBossOptions o = Wb;
        ISpawnGate? gate = world.FindMap(0)?.FindUpdater<CreatureMapSystem>()?.SpawnGate;
        if (o.DeriveFromContent && gate is null) return;
        FactionTemplateCatalog factions = services.GetService<WorldCombatHooksFeature>()?.Catalog ?? FactionTemplateCatalog.Empty;
        var key = new WorldBossKey(content, content.DefinitionsVersion, factions, gate, o.DeriveFromContent, o.IncludedEntries, o.ExcludedEntries, o.SizeOverrides);
        if (_wbKey == key) return;
        HashSet<uint> events = gate is null ? [] : [.. gate.GatedCreatures];
        PlayerbotWorldBossCatalogue catalogue = PlayerbotWorldBossCatalogue.Build(o, content, factions, events);
        _wbCatalogue = catalogue;
        _wbKey = key;
        foreach (uint stale in _wbCampaigns.Keys.Where(e => !catalogue.IsWorldBossEntry(e)).ToArray())
        {
            _wbCampaigns.Remove(stale);
            _wbSeen.Remove(stale);
            _wbMusterTriedMs.Remove(stale);
            _wbOpening.Remove(stale);
            _wbBossGuid.Remove(stale);
        }

        foreach (PlayerbotWorldBossEntry entry in catalogue.Entries) CampaignOf(entry.Entry);
        logger.LogInformation("Playerbot world bosses: {Count} in the catalogue ({Names})", catalogue.Entries.Count, string.Join(", ", catalogue.Entries.Select(e => $"{e.Name} {e.Entry}")));
        foreach (string warning in catalogue.Warnings) logger.LogWarning("Playerbot world bosses: {Warning}", warning);
    }

    // --- the watch and the muster ---------------------------------------------------------------------------------------

    private void ScanWorldBossesSafely()
    {
        try
        {
            ScanWorldBosses();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogFault(WorldBossWatchSite, ex, "Playerbot world-boss scan failed");
        }
    }

    private void ScanWorldBosses()
    {
        EnsureWorldBossCatalogue();
        PlayerbotWorldBossCatalogue catalogue = _wbCatalogue;
        if (catalogue.Entries.Count == 0 || _world is null || !_options.Groups.Enabled || !_options.Groups.RaidsEnabled) return;
        PlayerbotWorldBossOptions o = Wb;
        long unix = UnixNow();
        uint now = Now;
        if (!o.Enabled)
        {
            foreach (PlayerbotWorldBossCampaign open in _wbCampaigns.Values)
                if (open.Phase == PlayerbotWorldBossPhase.Open) open.MusterFailed(o, unix);
            return;
        }

        if (!_wbWatched || unchecked(now - _wbWatchMs) >= (uint)Math.Max(1, o.WatchSeconds) * 1000u)
        {
            _wbWatched = true;
            _wbWatchMs = now;
            foreach (PlayerbotWorldBossEntry entry in catalogue.Entries)
            {
                PlayerbotWorldBossStatus status = ResolveWorldBoss(entry, unix, out PlayerbotWorldBossEntry live);
                _wbSeen[entry.Entry] = new WorldBossSeen(status, live);
                PlayerbotWorldBossCampaign campaign = CampaignOf(entry.Entry);
                if (status.State == PlayerbotWorldBossState.Up) campaign.NoteUp(unix);
                else
                {
                    campaign.NoteNotUp();
                    if (campaign.Phase == PlayerbotWorldBossPhase.Open) campaign.MusterFailed(o, unix); // the boss is gone: the muster has nothing to go for
                }
            }
        }

        int active = -1; // counted on the first boss that may open a muster: most passes have none
        foreach (PlayerbotWorldBossEntry entry in catalogue.Entries)
        {
            if (!_wbSeen.TryGetValue(entry.Entry, out WorldBossSeen? seen) || seen.Status.State != PlayerbotWorldBossState.Up) continue;
            PlayerbotWorldBossCampaign campaign = CampaignOf(entry.Entry);
            if (campaign.Phase != PlayerbotWorldBossPhase.Open)
            {
                if (active < 0) active = ActiveWorldBossCount();
                var load = new PlayerbotWorldBossLoad(active, OwnGroupCount, QuestGroupSlotFree());
                PlayerbotWorldBossOpening verdict = campaign.MayOpen(o, _options.Groups.MaxGroups, load, unix);
                _wbOpening[entry.Entry] = verdict;
                if (verdict != PlayerbotWorldBossOpening.Ok) continue;
                campaign.Open(unix);
                active++;
                Note($"world boss {entry.Name} is up: a muster opens");
                logger.LogInformation("Playerbot world boss {Boss} ({Entry}) is up: a muster opens", entry.Name, entry.Entry);
            }

            try
            {
                MusterWorldBoss(seen.Live, campaign, o, unix);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogFault(WorldBossMusterSite, ex, "Playerbot world-boss muster failed for {Boss}", entry.Name);
                _wbMusterTriedMs[entry.Entry] = Now; // the next try waits its interval: a fault does not repeat every scan
            }
        }
    }

    /// <summary>The world-boss raids standing plus the musters open: the load a new muster is weighed against.</summary>
    private int ActiveWorldBossCount()
    {
        int active = 0;
        foreach (BotGroup group in _groups)
            if (group.WorldBoss is not null) active++;
        foreach (PlayerbotWorldBossCampaign campaign in _wbCampaigns.Values)
            if (campaign.Phase == PlayerbotWorldBossPhase.Open) active++;
        return active;
    }

    /// <summary>One muster step of a boss whose campaign is open: try for a raid now and then, give the muster up when its window has run out.</summary>
    private void MusterWorldBoss(PlayerbotWorldBossEntry live, PlayerbotWorldBossCampaign campaign, PlayerbotWorldBossOptions o, long unix)
    {
        uint now = Now;
        if (!_wbMusterTriedMs.TryGetValue(live.Entry, out uint triedMs) || unchecked(now - triedMs) >= WorldBossMusterRetryMs)
        {
            _wbMusterTriedMs[live.Entry] = now;
            if (TryFormWorldBoss(live, campaign, o, unix)) return;
        }

        if (!campaign.MusterExpired(o, unix)) return;
        campaign.MusterFailed(o, unix);
        _wbMusterTriedMs.Remove(live.Entry);
        PlayerbotWorldBossMeters.Count(PlayerbotWorldBossOutcomes.MusterFailed);
        Note($"world boss {live.Name}: no raid came together in {o.MusterWindowSeconds}s");
        logger.LogInformation("Playerbot world boss {Boss} ({Entry}): the muster ran out without a raid", live.Name, live.Entry);
    }

    private bool TryFormWorldBoss(PlayerbotWorldBossEntry live, PlayerbotWorldBossCampaign campaign, PlayerbotWorldBossOptions o, long unix)
    {
        if (GroupManagerOrNull() is not { } groups) return false;
        byte floor = PlayerbotWorldBossMuster.LevelFloor(o, live.Level);
        float radiusSq = o.MusterRadiusYards * o.MusterRadiusYards;
        Vector3 bossAt = live.Locator.Position;
        var goalKey = (PlayerbotGroupGoalKind.WorldBoss, live.Entry);
        var byTeam = new Dictionary<uint, List<PlayerbotGroupCandidate>>();
        foreach (PlayerbotGroupBot bot in _orderedBots)
        {
            if (!bot.Free || bot.Scripted || bot.Brain.Stay is not null || _memberOf.ContainsKey(bot.BotId) || _waiting.ContainsKey(bot.BotId)) continue;
            if (bot.Session.Player is not { IsInWorld: true, IsAlive: true } player || (player.Flags & PlayerFlags.Ghost) != 0 || player.Combat.IsInCombat) continue;
            if (player.MapId != live.Locator.MapId || player.Level < floor) continue;
            float dx = player.X - bossAt.X, dy = player.Y - bossAt.Y;
            if ((dx * dx) + (dy * dy) > radiusSq) continue;
            if (PlayerbotMageService.IsServing(player) || PlayerbotMageService.IsActive(player) || IsSetAside(bot.BotId, goalKey)) continue;
            if (!PlayerbotRecruitment.GoalYields(bot.Brain.Goal, bot.Brain.Goal == PlayerbotGoalKind.Quest ? RemainingObjectives(player, bot.Brain.QuestId) : 0)) continue;
            if (groups.GetGroup(player.Guid) is not null || groups.GetInvite(player.Guid) is not null) continue;
            if (!byTeam.TryGetValue((uint)player.Team, out List<PlayerbotGroupCandidate>? list)) byTeam[(uint)player.Team] = list = [];
            list.Add(Candidate(bot.BotId, player, Now));
        }

        GuildManager? guilds = GuildsOrNull();
        foreach (var team in byTeam.OrderByDescending(t => t.Value.Count).ThenBy(t => t.Key))
        {
            PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(o, _options.Groups, live, team.Key, unix / SecondsPerDay, team.Value,
                (caller, candidate) => WorldBossRecruitFacts(caller, candidate, guilds));
            if (result.Verdict != PlayerbotWorldBossMusterVerdict.Formed || result.Plan is not { } plan) continue;
            FormWorldBoss(live, campaign, plan, unix);
            return true;
        }

        return false;
    }

    /// <summary>What a candidate thinks of the caller, for the willingness roll: the same facts as recruiting a waiting group, with no call answered.</summary>
    private PlayerbotRecruitFacts WorldBossRecruitFacts(PlayerbotGroupCandidate caller, PlayerbotGroupCandidate candidate, GuildManager? guilds)
    {
        int affinity = 0;
        bool rival = false;
        if (Social is { Enabled: true } recorder)
        {
            PlayerbotSocialMemory memory = recorder.Memory;
            if (memory.TryGet(candidate.BotId, caller.Guid, out PlayerbotRelation toCaller))
            {
                affinity = memory.EffectiveAffinity(toCaller);
                rival = memory.IsRival(toCaller);
            }

            if (memory.TryGet(caller.BotId, candidate.Guid, out PlayerbotRelation fromCaller) && (memory.IsRival(fromCaller) || memory.EffectiveAffinity(fromCaller) <= Coop.RefuseAffinity))
                rival = true;
        }

        bool guildmate = guilds is not null && TryPlayer(candidate.BotId, out Player? mate) && TryPlayer(caller.BotId, out Player? leader)
            && guilds.GetGuildOf(mate) is { } mateGuild && guilds.GetGuildOf(leader)?.Id == mateGuild.Id;
        bool fillsRole = PlayerbotGroupContent.CanTank(candidate.Class) || PlayerbotGroupContent.CanHeal(candidate.Class);
        return new PlayerbotRecruitFacts(PlayerbotTraits.For(candidate.Guid).Sociability, affinity, affinity, rival, fillsRole, guildmate, AnsweredCall: false);
    }

    private void FormWorldBoss(PlayerbotWorldBossEntry live, PlayerbotWorldBossCampaign campaign, PlayerbotGroupPlan plan, long unix)
    {
        PlayerbotWorldBossOptions o = Wb;
        Vector3 bossAt = live.Locator.Position;
        foreach ((PlayerbotGroupCandidate bot, PlayerbotGroupRole _, byte _) in plan.Members)
            Unhold(bot.BotId, null);
        var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.WorldBoss, 0, live.Entry, live.Locator.MapId, live.Locator.MapId, bossAt, 0, bossAt, plan.Members.Count,
            PlayerbotWorldBossMuster.LevelFloor(o, live.Level), "worldboss");
        BotGroup group = Form(goal, plan);

        // The raid gathers RallyYards short of the boss, on the side it comes from.
        Vector3 rally = SnapToMesh(_world is not null ? WorldCollision.Of(_world).Pathfinder : null, goal.MeetingMap, plan.Leader.Position,
            ApproachPoint(goal, plan.Leader.Position, o.RallyYards));
        group.Approach = rally;
        Map? map = _world?.FindMap(live.Locator.MapId);
        group.WorldBoss = new PlayerbotWorldBossRaid(live, WorldBossZone(map, bossAt, live.Locator.MapId))
        {
            BossGuid = _wbBossGuid.GetValueOrDefault(live.Entry),
            PullHeld = true,
            FormedUnix = unix,
            RallyYards = MathF.Sqrt(((rally.X - bossAt.X) * (rally.X - bossAt.X)) + ((rally.Y - bossAt.Y) * (rally.Y - bossAt.Y))),
        };
        campaign.Formed();
        PlayerbotWorldBossMeters.Count(PlayerbotWorldBossOutcomes.Formed);
        PlayerbotWorldBossMeters.SetActive(_groups.Count(g => g.WorldBoss is not null));
        Note($"group {group.Id} raids {live.Name}: {plan.Members.Count} bots, rally {group.WorldBoss.RallyYards:F0} yards from the boss");
        logger.LogInformation("Playerbot group {Group} raids world boss {Boss} ({Entry}) with {Count} bots", group.Id, live.Name, live.Entry, plan.Members.Count);
    }

    // --- what the watch sees ----------------------------------------------------------------------------------------------

    /// <summary>The place name of <paramref name="at"/> on <paramref name="map"/> from the area table; the map's name where the area is not known.</summary>
    private string WorldBossZone(Map? map, Vector3 at, uint mapId)
    {
        if (_world is not { } world) return "the open world";
        try
        {
            if (map is not null)
            {
                (uint zoneId, _) = map.GetZoneAndAreaId(at.X, at.Y, at.Z);
                if (zoneId != 0 && WorldMaps.Of(world).Areas.GetById(zoneId)?.Name is { Length: > 0 } zone) return zone;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            FaultLedger.Shared.Record(WorldBossMusterSite, error);
        }

        return WorldMaps.Of(world).Registry.Find(mapId)?.Name is { Length: > 0 } name ? name : "the open world";
    }

    /// <summary>The portal whose dragon is <paramref name="entry"/> right now (the event runs, the portal's dragon is not killed), -1 for none.</summary>
    private int DragonPortalOf(uint entry)
    {
        NightmareDragonsFeature? dragons = services.GetServices<IWorldFeature>().OfType<NightmareDragonsFeature>().FirstOrDefault();
        if (dragons is null) return -1;
        NightmareDragonsState state = dragons.State;
        for (int portal = 0; portal < NightmareDragonsFeature.Portals.Length; portal++)
            if (PlayerbotWorldBossStatus.PortalEntry(state.Active, state.KilledMask, portal, dragons.EntryAt(portal)) == entry) return portal;
        return -1;
    }

    /// <summary>
    /// What the world says of one boss now. A database spawn is found by its spawn guid; a placed boss and a dragon by entry on the map. A living
    /// creature is Up; the respawn ledger gives a dead one's time; a boss whose grid is not loaded is Unknown, never Up on no evidence.
    /// <paramref name="live"/> is the entry with the place the boss stands at now (a dragon's portal, a placed boss's position).
    /// </summary>
    private PlayerbotWorldBossStatus ResolveWorldBoss(PlayerbotWorldBossEntry entry, long unix, out PlayerbotWorldBossEntry live)
    {
        live = entry;
        PlayerbotWorldBossLocator locator = entry.Locator;
        uint mapId = locator.MapId;
        Vector3 position = locator.Position;
        if (locator.Kind == PlayerbotWorldBossLocatorKind.Portal)
        {
            int portal = DragonPortalOf(entry.Entry);
            if (portal < 0) return PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts { Position = position, GridLoaded = true }, unix); // not placed: dead
            NightmareDragonsFeature.Portal at = NightmareDragonsFeature.Portals[portal];
            mapId = at.MapId;
            position = new Vector3(at.X, at.Y, at.Z);
            live = entry with { Locator = PlayerbotWorldBossLocator.Portal(portal, mapId, position) };
        }

        Map? map = _world?.FindMap(mapId);
        if (map?.FindUpdater<CreatureMapSystem>() is not { } system)
            return PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts { Position = position }, unix);

        Creature? creature = null;
        if (locator.Kind == PlayerbotWorldBossLocatorKind.Spawn)
        {
            if (system.FindBySpawnGuid(locator.SpawnGuid) is { } bySpawn && bySpawn.Entry == entry.Entry) creature = bySpawn;
        }
        else
        {
            // The cached guid first (one lookup); a scan of the map's creatures only while nothing is cached, and this watch runs once per WatchSeconds.
            if (_wbBossGuid.TryGetValue(entry.Entry, out ObjectGuid cachedGuid))
            {
                if (system.FindCreature(cachedGuid) is { IsAlive: true } known && known.Entry == entry.Entry) creature = known;
                else _wbBossGuid.Remove(entry.Entry);
            }

            if (creature is null && (locator.Kind != PlayerbotWorldBossLocatorKind.Portal || system.IsGridLoaded(position.X, position.Y)))
            {
                foreach (Creature candidate in system.Creatures)
                    if (candidate.Entry == entry.Entry && candidate.IsAlive)
                    {
                        creature = candidate;
                        _wbBossGuid[entry.Entry] = candidate.Guid;
                        break;
                    }
            }
        }

        if (creature is { IsAlive: true } living)
        {
            var at = new Vector3(living.X, living.Y, living.Z);
            live = live with { Locator = live.Locator with { Position = at } };
            return PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts
            {
                Living = true,
                Position = at,
                TappedOrFightingOthers = !living.LootTapper.IsEmpty && !IsAnyRaidTap(map, living.LootTapper) || living.Combat.Victim is Player victim && !IsAnyRaidTap(map, victim.Guid),
                GridLoaded = true,
            }, unix);
        }

        long pending = 0; // the ledger is read only for a boss that is not alive: a point lookup, never the copy of the map's dictionary
        if (locator.Kind == PlayerbotWorldBossLocatorKind.Spawn && RespawnLedger()?.TryGetPending(mapId, 0, locator.SpawnGuid, out long due) == true) pending = due;
        return PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts
        {
            Position = position,
            GridLoaded = locator.Kind != PlayerbotWorldBossLocatorKind.Placed && system.IsGridLoaded(position.X, position.Y),
            PendingRespawnUnix = pending,
        }, unix);
    }

    /// <summary>The respawn ledger the world-boss code reads (tests set it to count the reads; null: the world's own).</summary>
    internal ICreatureRespawnPersistence? RespawnLedgerOverride { get; set; }

    private ICreatureRespawnPersistence? RespawnLedger() => RespawnLedgerOverride ?? services.GetService<CreatureRespawnFeature>()?.Persistence;

    /// <summary>What the last watch of the boss <paramref name="entry"/> saw of it (tests); null before the first watch.</summary>
    internal PlayerbotWorldBossStatus? WorldBossStatus(uint entry) => _wbSeen.TryGetValue(entry, out WorldBossSeen? seen) ? seen.Status : null;

    // --- the raid's life --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Forming a world-boss raid: a bot that does not answer its invitation, or refuses, is dropped and forming goes on while at least
    /// <see cref="PlayerbotWorldBossOptions.MinRaidSize"/> remain (the any-unanswered abort of a quest group would end a raid of 34 on one silent bot).
    /// False when too few would remain: the caller ends the forming.
    /// </summary>
    private bool DropUnansweredInvitees(BotGroup group, uint now)
    {
        var silent = new List<Member>();
        foreach (Member member in group.Members)
            if (!member.Real && !member.Joined && (member.Refused || member.Invited && unchecked(now - member.InvitedMs) > InviteTimeoutMs)) silent.Add(member);
        if (group.Members.Count - silent.Count < Math.Max(2, Wb.MinRaidSize)) return false;
        foreach (Member member in silent) DropMember(group, member.BotId, "did not answer the call");
        return true;
    }

    /// <summary>
    /// The gathering of a world-boss raid with the catch-up teleport on is the ordinary one (stragglers are brought to the leader). With it off a
    /// straggler walks: the gathering goes on once <see cref="GatherCatchUpMs"/> have passed and <see cref="PlayerbotWorldBossOptions.PresentFraction"/>
    /// of the raid stands with the leader.
    /// </summary>
    private bool WorldBossGathered(BotGroup group, Player leader, uint now)
    {
        if (Wb.CatchUpTeleport) return CatchUpGathering(group, leader, now);
        if (_world is not { } world || unchecked(now - group.StateSinceMs) <= GatherCatchUpMs) return false;
        int joined = 0, near = 0;
        foreach (Member member in group.Members)
        {
            if (!member.Joined) continue;
            joined++;
            if (member.Guid == leader.Guid || world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && ReferenceEquals(player.Map, leader.Map) && Distance(player, leader) <= GatherYards)
                near++;
        }

        return near >= (int)Math.Ceiling((joined * (double)Wb.PresentFraction) - 1e-6);
    }

    private void OnWorldBossState(BotGroup group, PlayerbotGroupState state)
    {
        if (group.WorldBoss is not { } raid) return;
        if (state == PlayerbotGroupState.Gathering && !raid.Warnings.Contains(PlayerbotWorldBossWarning.Form)) WorldBossWarn(group, PlayerbotWorldBossWarning.Form);
        if (state == PlayerbotGroupState.Travelling) raid.ReachedTravelling = true;
    }

    private static void ResetWorldBossPull(PlayerbotWorldBossRaid raid)
    {
        raid.Released = false;
        raid.PullHeld = true;
        raid.Waiting = false;
        raid.Hold = PlayerbotWorldBossHold.None;
        raid.ClaimSinceMs = 0;
        raid.ClaimSeenMs = 0;
        raid.GateSinceMs = 0;
        raid.CheckedMs = 0;
    }

    /// <summary>The leader's warning to the raid (the raid's warning chat; a party of five, its party chat), recorded in the raid even when the leader cannot send it.</summary>
    private void WorldBossWarn(BotGroup group, PlayerbotWorldBossWarning kind)
    {
        if (_shuttingDown || group.WorldBoss is not { } raid || _world is null) return;
        raid.Warnings.Add(kind);
        if (group.LeaderPlayer(_world) is not { } leader) return;
        Language language = leader.Team == Team.Horde ? Language.Orcish : Language.Common;
        string text = PlayerbotWorldBoss.WarningText(kind, group.Id, raid.Boss.Name, raid.Zone);
        LeaderAct(group, leader, WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(group.Raid ? ChatType.RaidWarning : ChatType.Party, language, text));
    }

    private static bool IsMember(BotGroup group, ObjectGuid guid)
    {
        foreach (Member member in group.Members)
            if (member.Guid == guid) return true;
        return false;
    }

    /// <summary>The unit is a raid member, or a pet or charm of one.</summary>
    private static bool BelongsToRaid(BotGroup group, Unit unit) => IsMember(group, unit.Guid) || DuelRules.ControllingPlayer(unit) is { } owner && IsMember(group, owner.Guid);

    private static bool TapIsRaid(Map map, BotGroup group, ObjectGuid tap)
        => !tap.IsEmpty && (IsMember(group, tap) || map.FindObject(tap) is Unit pet && DuelRules.ControllingPlayer(pet) is { } owner && IsMember(group, owner.Guid));

    /// <summary>The guid is a member (or a member's pet) of any world-boss raid: our own tap is not somebody else's claim.</summary>
    private bool IsAnyRaidTap(Map map, ObjectGuid guid)
    {
        foreach (BotGroup group in _groups)
            if (group.WorldBoss is not null && TapIsRaid(map, group, guid)) return true;
        return false;
    }

    private static bool FightsRaid(BotGroup group, Creature boss)
    {
        if (boss.Combat.Victim is { } victim && BelongsToRaid(group, victim)) return true;
        if (!boss.Combat.HasThreatList) return false;
        IReadOnlyList<ThreatEntry> entries = boss.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++)
            if (entries[index].Target is { } target && BelongsToRaid(group, target)) return true;
        return false;
    }

    /// <summary>The boss creature (alive, or its corpse) the raid is after: by spawn guid for a database spawn, else the nearest of the entry to <paramref name="home"/>.</summary>
    internal Creature? FindRaidBoss(CreatureMapSystem system, PlayerbotWorldBossRaid raid, Vector3 home, uint now)
    {
        // The cached guid is one lookup; it is dropped when it finds nothing or a creature of another entry.
        if (!raid.BossGuid.IsEmpty)
        {
            if (system.FindCreature(raid.BossGuid) is { } cached && cached.Entry == raid.Boss.Entry) return cached;
            raid.BossGuid = ObjectGuid.Empty;
        }

        PlayerbotWorldBossLocator locator = raid.Boss.Locator;
        if (locator.Kind == PlayerbotWorldBossLocatorKind.Spawn && system.FindBySpawnGuid(locator.SpawnGuid) is { } bySpawn && bySpawn.Entry == raid.Boss.Entry)
        {
            raid.BossGuid = bySpawn.Guid;
            return bySpawn;
        }

        // Nothing cached: scan the map's creatures at most once per watch interval (a miss is looked at again then).
        if (raid.BossScanMs != 0 && unchecked(now - raid.BossScanMs) < (uint)Math.Max(1, Wb.WatchSeconds) * 1000u) return null;
        raid.BossScanMs = Math.Max(1u, now);
        raid.BossScans++;
        Creature? best = null;
        float bestYards = float.MaxValue;
        foreach (Creature creature in system.Creatures)
        {
            if (creature.Entry != raid.Boss.Entry) continue;
            float dx = creature.X - home.X, dy = creature.Y - home.Y;
            float yards = (dx * dx) + (dy * dy);
            bool better = best is null || creature.IsAlive && !best.IsAlive || creature.IsAlive == best.IsAlive && yards < bestYards;
            if (!better) continue;
            best = creature;
            bestYards = yards;
        }

        if (best is not null) raid.BossGuid = best.Guid;
        return best;
    }

    /// <summary>
    /// A world-boss raid's step in Travelling and Engaging: what the raid sees of the boss goes to <see cref="PlayerbotWorldBossEngage.Decide"/>, and
    /// the decision is carried out. While travelling only the boss's death or disappearance acts (the others wait for the rally). True when the group ended.
    /// </summary>
    private bool AdvanceWorldBoss(BotGroup group, Player leader, uint now)
    {
        try
        {
            return AdvanceWorldBossCore(group, leader, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogFault(WorldBossAdvanceSite, ex, "Playerbot world-boss raid {Group} failed", group.Id);
            Finish(group, success: false, "worldboss-fault", setAside: false);
            return true;
        }
    }

    private bool AdvanceWorldBossCore(BotGroup group, Player leader, uint now)
    {
        if (_world is not { } world || group.WorldBoss is not { } raid) return false;
        PlayerbotWorldBossOptions o = Wb;
        if (raid.KilledAtMs != 0)
        {
            if (unchecked(now - raid.KilledAtMs) < LootSettleMs) return false;
            Totals = (Totals.Formed, Totals.Completed + 1, Totals.Failed);
            Finish(group, success: true, "killed", setAside: false);
            return true;
        }

        if (raid.CheckedMs != 0 && unchecked(now - raid.CheckedMs) < WorldBossCheckMs) return false;
        raid.CheckedMs = Math.Max(1u, now);
        Map? map = world.FindMap(raid.Boss.Locator.MapId);
        if (map?.FindUpdater<CreatureMapSystem>() is not { } system) return false;
        Vector3 home = group.Goal.Objective;
        Creature? boss = FindRaidBoss(system, raid, home, now);
        // Not in the creature system and its grid not loaded: nothing is known of the boss (the raid has not come near enough yet).
        if (boss is null && !system.IsGridLoaded(home.X, home.Y)) return false;

        bool engaging = group.State == PlayerbotGroupState.Engaging;
        bool alive = boss is { IsAlive: true };
        Vector3 at = boss is not null ? new Vector3(boss.X, boss.Y, boss.Z) : home;
        float presentYards = raid.RallyYards + WorldBossPresentSlackYards;
        int members = 0, present = 0;
        foreach (Member member in group.Members)
        {
            if (!member.Joined) continue;
            members++;
            if (world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && ReferenceEquals(player.Map, map))
            {
                float dx = player.X - at.X, dy = player.Y - at.Y;
                if ((dx * dx) + (dy * dy) <= presentYards * presentYards) present++;
            }
        }

        bool fightingUs = false, othersClaim = false, tappedByRaid = false, corpseTapByRaid = false;
        int playersNear = 0;
        if (boss is not null)
        {
            bool tapIsRaid = TapIsRaid(map, group, boss.LootTapper);
            if (alive)
            {
                raid.SawAlive = true;
                fightingUs = FightsRaid(group, boss);
                tappedByRaid = tapIsRaid;
                bool tapped = !fightingUs && PlayerbotBrain.IsSomeoneElses(leader, boss, GroupManagerOrNull());
                // A claim that blinks (the boss evades and its player taps it again a moment later) is one claim: it lingers for WorldBossClaimLingerMs after it was last seen.
                if (tapped) raid.ClaimSeenMs = Math.Max(1u, now);
                othersClaim = tapped || !fightingUs && raid.ClaimSeenMs != 0 && unchecked(now - raid.ClaimSeenMs) < WorldBossClaimLingerMs;
                if (engaging && o.PlayersNearYards > 0f) playersNear = map.CountPlayersWithin(at.X, at.Y, o.PlayersNearYards, includeBots: false);
            }
            else corpseTapByRaid = tapIsRaid;
        }

        bool claim = alive && !fightingUs && (othersClaim || o.PlayersNearYards > 0f && playersNear >= o.PlayersNearCount);
        if (engaging && claim)
        {
            if (raid.ClaimSinceMs == 0) raid.ClaimSinceMs = Math.Max(1u, now);
        }
        else raid.ClaimSinceMs = 0;

        if (engaging && !raid.Released && raid.GateSinceMs == 0) raid.GateSinceMs = Math.Max(1u, now);
        if (!engaging && !raid.Released) raid.GateSinceMs = 0;
        PlayerbotWorldBossDecision decision = PlayerbotWorldBossEngage.Decide(new PlayerbotWorldBossEngageFacts
        {
            BossFound = boss is not null,
            BossAlive = alive,
            Members = members,
            Present = present,
            AnyResting = group.AnyResting(world),
            CorpseTapByRaid = corpseTapByRaid,
            TapSeen = raid.TapSeen,
            LedgerPending = boss is null && LedgerHoldsBoss(raid), // read by Decide only when no boss was found: no ledger lookup on the 200 ms path while it is alive
            OthersClaim = othersClaim,
            OthersClaimMs = raid.ClaimSinceMs == 0 ? 0 : unchecked(now - raid.ClaimSinceMs),
            RealPlayersNear = playersNear,
            WaitMs = raid.GateSinceMs == 0 ? 0 : unchecked(now - raid.GateSinceMs),
            FightingUs = fightingUs,
            TappedByRaid = tappedByRaid,
            Released = raid.Released,
        }, o);
        if (decision.SetTapSeen) raid.TapSeen = true;

        switch (decision.Action)
        {
            case PlayerbotWorldBossAction.Kill:
                if (corpseTapByRaid && !raid.SawAlive)
                    PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.WorldBossCorpseTapUnseen, GroupSubject(group),
                        string.Create(CultureInfo.InvariantCulture, $"group {group.Id} holds the tap of {raid.Boss.Name} ({raid.Boss.Entry}), a corpse it never saw alive: the boss died before the raid's first look at it"),
                        world.Identity, AnomalySeverity.Info);
                raid.KilledAtMs = Math.Max(1u, now);
                raid.PullHeld = false;
                raid.Waiting = false;
                raid.Hold = PlayerbotWorldBossHold.None;
                WorldBossWarn(group, PlayerbotWorldBossWarning.Kill);
                Note($"group {group.Id}: {raid.Boss.Name} is down");
                return false;
            case PlayerbotWorldBossAction.Finish:
                if (decision.Reason is "boss-taken" or "not-enough" or "boss-gone") WorldBossWarn(group, PlayerbotWorldBossWarning.GiveUp);
                Finish(group, success: false, decision.Reason ?? "boss-gone", setAside: false);
                return true;
        }

        if (!engaging) return false;
        switch (decision.Action)
        {
            case PlayerbotWorldBossAction.Hold:
                if (raid.Hold != decision.Hold) Note($"group {group.Id}: holds the pull of {raid.Boss.Name} ({decision.Hold.ToString().ToLowerInvariant()})");
                raid.PullHeld = true;
                raid.Waiting = true;
                raid.Hold = decision.Hold;
                break;
            case PlayerbotWorldBossAction.Release:
                if (playersNear > 0)
                    PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.WorldBossPullNearPlayers, GroupSubject(group),
                        string.Create(CultureInfo.InvariantCulture, $"group {group.Id} pulls {raid.Boss.Name} ({raid.Boss.Entry}) in {raid.Zone} with {playersNear} real player(s) within {o.PlayersNearYards:F0} yards"),
                        world.Identity);
                raid.Released = true;
                raid.PullHeld = false;
                raid.Waiting = false;
                raid.Hold = PlayerbotWorldBossHold.None;
                WorldBossWarn(group, PlayerbotWorldBossWarning.Pull);
                Note($"group {group.Id}: pulls {raid.Boss.Name} with {present} of {members} at the rally");
                break;
            default:
                raid.PullHeld = false;
                raid.Waiting = false;
                raid.Hold = PlayerbotWorldBossHold.None;
                break;
        }

        return false;
    }

    /// <summary>The respawn ledger holds a respawn for the boss's database spawn (it died and is waiting to come back).</summary>
    private bool LedgerHoldsBoss(PlayerbotWorldBossRaid raid)
    {
        PlayerbotWorldBossLocator locator = raid.Boss.Locator;
        return locator.Kind == PlayerbotWorldBossLocatorKind.Spawn
            && RespawnLedger()?.TryGetPending(locator.MapId, 0, locator.SpawnGuid, out long due) == true && due > UnixNow();
    }

    /// <summary>The raid is over: count its outcome, set its campaign's cooldown (and the attempt, when it reached the walk to the boss) and the gauge.</summary>
    private void NoteWorldBossEnded(BotGroup group)
    {
        if (group.WorldBoss is not { } raid) return;
        bool killed = group.Succeeded && group.Reason == "killed";
        PlayerbotWorldBossMeters.Count(PlayerbotWorldBossOutcomes.Of(group.Reason, raid.ReachedTravelling));
        CampaignOf(raid.Boss.Entry).Ended(Wb, UnixNow(), raid.ReachedTravelling, killed);
        PlayerbotWorldBossMeters.SetActive(_groups.Count(g => g.WorldBoss is not null));
        ReportWorldBossFailed(group, raid);
    }

    private static string GroupSubject(BotGroup group) => string.Create(CultureInfo.InvariantCulture, $"group {group.Id} (leader {group.LeaderGuid.Value:X})");

    /// <summary>The raid wiped as often as it may: at full size that is an anomaly (the boss is out of its reach), at a smaller size the raid was short to begin with.</summary>
    private void NoteWorldBossWipedOut(BotGroup group, PlayerbotWorldBossRaid raid)
    {
        int joined = group.Members.Count(m => m.Joined);
        if (joined < group.Goal.Size) return;
        PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.WorldBossWipedAtFullSize, GroupSubject(group),
            string.Create(CultureInfo.InvariantCulture, $"group {group.Id} wiped {raid.Wipes} times on {raid.Boss.Name} ({raid.Boss.Entry}) in {raid.Zone} at its full size of {joined}"), _world?.Identity);
    }

    /// <summary>
    /// A raid that ended for a content or code reason (<see cref="PointsAtContentOrCode"/>: a timeout, a wipe-out, a stall, no path) goes to bot QA like a failed run,
    /// straight to the sink (docs/areas/playbots-qa.md): the map, the boss and the reason; no group, bot or character is named. Once per raid; nothing is built while QA
    /// is off; a failure of the report is swallowed so it cannot reach the world tick.
    /// </summary>
    private void ReportWorldBossFailed(BotGroup group, PlayerbotWorldBossRaid raid)
    {
        try
        {
            if (group.Reason is not { } reason || group.Succeeded || reason == "objective-missing" || !PointsAtContentOrCode(reason)) return;
            if (services.GetService<IPlayerbotQaSink>() is not { Enabled: true } sink) return;
            string stage = raid.ReachedTravelling ? "raid" : "forming";
            sink.Report(new QaSignal
            {
                Kind = QaSignalKind.RunFailed, AtUtc = DateTime.UtcNow, Map = raid.Boss.Locator.MapId, Segment = 0, Stage = stage, Target = raid.Boss.Entry, Reason = reason,
                Detail = raid.Zone,
                Message = string.Create(CultureInfo.InvariantCulture, $"world-boss raid ended: {reason} on {raid.Boss.Name} ({raid.Boss.Entry}) in {raid.Zone} (map {raid.Boss.Locator.MapId}), {raid.Wipes} wipes"),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(WorldBossAdvanceSite, ex);
            logger.LogDebug(ex, "Playerbot group {Group}: the world-boss failure could not be reported to bot QA", group.Id);
        }
    }

    // --- status ---------------------------------------------------------------------------------------------------------------

    /// <summary>One <c>worldboss:</c> line per catalogued boss for the status report: what the watch last saw, the campaign's phase, the attempts and the last verdict of the caps.</summary>
    private List<string> WorldBossReport()
    {
        var lines = new List<string>();
        PlayerbotWorldBossCatalogue catalogue = _wbCatalogue;
        if (catalogue.Entries.Count == 0) return lines;
        long unix = UnixNow();
        foreach (PlayerbotWorldBossEntry entry in catalogue.Entries)
        {
            PlayerbotWorldBossCampaign campaign = CampaignOf(entry.Entry);
            string state = !_wbSeen.TryGetValue(entry.Entry, out WorldBossSeen? seen) ? "unwatched"
                : seen.Status.State == PlayerbotWorldBossState.Dead && seen.Status.DeadUntilUnix is { } until ? string.Create(CultureInfo.InvariantCulture, $"dead for {Math.Max(0, until - unix)}s")
                : seen.Status.State.ToString().ToLowerInvariant();
            string cooldown = campaign.CooldownUntilUnix > unix ? string.Create(CultureInfo.InvariantCulture, $" cooldown={campaign.CooldownUntilUnix - unix}s") : string.Empty;
            string opening = _wbOpening.TryGetValue(entry.Entry, out PlayerbotWorldBossOpening verdict) ? " opening=" + verdict.ToString().ToLowerInvariant() : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"worldboss: {entry.Name} ({entry.Entry}) {state} phase={campaign.Phase.ToString().ToLowerInvariant()} attempts={campaign.AttemptsToday(unix)}/{Wb.MaxAttemptsPerDay}{cooldown}{opening}{(Wb.Enabled ? string.Empty : " disabled")}"));
        }

        return lines;
    }
}

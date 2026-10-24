using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using ArcaneCore.Game.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>Where a dead bot's body lies, seen from the ghost (<see cref="PlayerbotRecovery.Decide"/>).</summary>
internal enum PlayerbotCorpsePlace
{
    /// <summary>No body (a ghost that came back from a restart, or one whose body is gone).</summary>
    None,

    /// <summary>On the map (and instance) the ghost is on.</summary>
    ThisMap,

    /// <summary>On another map: the bot died in a dungeon and was released at a graveyard outside.</summary>
    OtherMap,
}

/// <summary>What a dead bot does next (<see cref="PlayerbotRecovery.Decide"/>).</summary>
internal enum PlayerbotRecoveryStep
{
    /// <summary>Release the spirit (CMSG_REPOP_REQUEST).</summary>
    Release,

    /// <summary>Walk back to the body on this map.</summary>
    WalkToCorpse,

    /// <summary>Walk into the entrance trigger of the dungeon the body is in; the server revives a ghost that enters it.</summary>
    WalkToEntrance,

    /// <summary>
    /// The body is on this map but no plan reaches it (<see cref="PlayerbotRecovery.MaxRefusedPlans"/> refusals in a row): walk into a
    /// same-map teleport trigger whose far side lies nearer the body, as a player does (the Rut'theran portal up to Darnassus, area
    /// trigger 542). The navigation does not model the teleport, so the walk ends in the trigger and the corpse run resumes on the far side.
    /// </summary>
    WalkToPortal,

    /// <summary>Stand at the body while the server's reclaim delay runs (SMSG_CORPSE_RECLAIM_DELAY).</summary>
    WaitForReclaimDelay,

    /// <summary>Stand within reclaim range while a hostile creature is near the ghost (where the reclaim revives it).</summary>
    WaitForHostiles,

    /// <summary>
    /// Walk to a spot inside the reclaim radius of the body that no hostile creature camps and the navigation mesh reaches, and
    /// reclaim there (<see cref="PlayerbotRecovery.FindReviveSpot"/>).
    /// </summary>
    WalkToReviveSpot,

    /// <summary>Reclaim the body (CMSG_RECLAIM_CORPSE).</summary>
    Reclaim,

    /// <summary>Give up on the body and take the spirit healer (CMSG_SPIRIT_HEALER_ACTIVATE).</summary>
    SpiritHealer,

    /// <summary>A body that cannot even be released: the recovery faults (<see cref="PlayerbotRecovery.Stalled"/>).</summary>
    Fault,
}

/// <summary>
/// Why a ghost's body is not worth reclaiming, so its recovery takes the spirit healer at once (<see cref="PlayerbotRecovery.JudgeBody"/>).
/// </summary>
internal enum PlayerbotUnsafeBody
{
    /// <summary>The body may be reclaimed.</summary>
    None,

    /// <summary>The bot already died near the body recently: reviving there would repeat it.</summary>
    DiedHere,

    /// <summary>A creature round the body is too strong for the bot even at full health (the risk estimate avoids it).</summary>
    TooStrong,

    /// <summary>The body is below the liquid surface by more than the bot's collision height.</summary>
    Hazard,

    /// <summary>The body lies outside the bot's home, and the spirit healer leaves it nearer home than the body would.</summary>
    OutsideHome,
}

/// <summary>What a ghost that took the spirit-healer fallback does next (<see cref="PlayerbotRecovery.DecideSpiritHealer"/>).</summary>
internal enum PlayerbotSpiritHealerStep
{
    /// <summary>Within reach of a healer: CMSG_SPIRIT_HEALER_ACTIVATE.</summary>
    Activate,

    /// <summary>A healer is in sight: walk to it.</summary>
    WalkToHealer,

    /// <summary>
    /// The walk to the healer in sight stalled short of its reach: plan once, with the largest search budget
    /// (<see cref="PlayerbotNavigation.MaxSearchNodes"/>), a whole route to a point within its reach, and walk it. Live 2026-10-11
    /// (w25, Deeleibreind): the ghost stood 18 yards under the Stonard healer, in the lake basin below the bluff; the near search
    /// (2048 nodes) answered a partial route ending right under it, while the mesh's way up is a 1,010-yard road round the bluff.
    /// </summary>
    DetourToHealer,

    /// <summary>No healer in sight: walk to the graveyard of this position, where one stands.</summary>
    WalkToGraveyard,

    /// <summary>
    /// No healer in sight and no graveyard found from here (the ghost's area not known yet, a healer not yet spawned or seen): stand
    /// and look again next think. Not progress, so a ghost that never finds either faults after <see cref="PlayerbotRecovery.NoProgressMs"/>.
    /// </summary>
    WaitForHealer,

    /// <summary>
    /// No healer in sight and no graveyard to walk to, from here or for the body (a spirit with no linked graveyard is left where it
    /// died, vmangos Player::RepopAtGraveyard), while the body lies on the ghost's map and the corpse run was not resumed yet for this
    /// death: go back to the body, with a fresh bound, instead of waiting for a healer that cannot come. Once per death.
    /// </summary>
    ResumeCorpseRun,

    /// <summary>
    /// No healer in sight and the ghost already at its graveyard, where none stands (graveyard 129 at Rut'theran Village has none in the
    /// 1.12 world database): walk to the nearest spirit healer of the world's spawns on this map (<c>creature</c> rows whose template
    /// carries <see cref="NpcFlags.SpiritHealer"/>), through a same-map teleport trigger when that is the shorter way (the Rut'theran
    /// portal to the Darnassus healer). Once it is in sight the ordinary walk and CMSG_SPIRIT_HEALER_ACTIVATE take over.
    /// </summary>
    WalkToHealerSpawn,

    /// <summary>
    /// The fallback stalled too (no progress for <see cref="PlayerbotRecovery.NoProgressMs"/>, or a walk stuck): reported once
    /// (<c>recovery/spirit-healer-failed</c>) and the ghost stands; the recovery is tried afresh after
    /// <see cref="PlayerbotRecovery.GiveUpRetryMs"/>. Never thrown: the brain's think fault quarantined the bot and then disabled it.
    /// </summary>
    Fault,

    /// <summary>
    /// A battleground ghost within <see cref="PlayerbotRecovery.AreaSpiritGuideRangeYards"/> of its graveyard's spirit guide: stand and wait
    /// for the guide's resurrection wave (Spirit Heal Channel 22011 every 30 s, cmangos battlegrounds.cpp npc_spirit_guideAI, which
    /// resurrects the dead within 20 yards that carry Waiting to Resurrect, 2584), queueing with CMSG_AREA_SPIRIT_HEALER_QUEUE when the
    /// aura is missing (cmangos BattleGroundHandler.cpp HandleAreaSpiritHealerQueueOpcode -> GossipHello_npc_spirit_guide).
    /// </summary>
    AwaitAreaResurrection,
}

/// <summary>
/// Ordinary ghost recovery for one managed player, like a client: release, walk back to the body, wait out the reclaim delay,
/// move off a camped revive point (mangoszero playerbot ReviveFromCorpseAction waits for hostiles to leave), reclaim. The revive
/// point is where the ghost stands, not the body: CMSG_RECLAIM_CORPSE resurrects the player in place (vmangos MiscHandler.cpp:599,
/// <c>ResurrectPlayer</c> without a relocation), and the walk stops as soon as the body is in reclaim range (about 39 yards). A
/// body whose surroundings are camped is no reason to give it up: anywhere inside that radius will do, so the ghost looks for a
/// spot there that is clear of hostiles and reachable (<see cref="PlayerbotRecovery.FindReviveSpot"/>), walks to it and reclaims;
/// only when there is none does it wait for the camp to leave, and take the spirit healer after <see cref="NoProgressMs"/>.
/// <list type="bullet">
/// <item><description>A body on another map — the bot died in a dungeon and was released at a graveyard outside — is reached
/// through the dungeon's entrance: the ghost walks into the area trigger whose <c>areatrigger_teleport</c> leads to the body's
/// map (or to a dungeon it is nested in), <see cref="PlayerbotAreaTriggers"/> reports it, and the server revives a ghost that
/// enters the map its body is in (vmangos Player.cpp:1953-1966, MiscHandler.cpp:712-756).</description></item>
/// <item><description>The bound is progress, not a count of thinks, judged only after more than <see cref="MinBoundThinks"/> thinks that acted (a think gap under load is no stall). When nothing moved the recovery on for <see cref="NoProgressMs"/>,
/// or a walk stopped closing on its goal (or could not be planned) for <see cref="StuckMs"/>, or there is no way back to the body,
/// the ghost takes the spirit healer instead (CMSG_SPIRIT_HEALER_ACTIVATE, vmangos NPCHandler.cpp:416): the nearest healer it can
/// see, else it walks to the graveyard of its position, where one stands, else it waits and looks again (nothing found once is no
/// fault). When that fallback stalls the same way it gives up, reported once (<c>recovery/spirit-healer-failed</c>), and the ghost
/// stands and tries again after <see cref="GiveUpRetryMs"/>: never an exception (<see cref="SpiritHealerFailed"/> was thrown until
/// 2026-10-16, and the brain quarantined and then disabled the bot for it). A corpse run whose plan is refused <see cref="MaxRefusedPlans"/>
/// times in a row walks into a same-map portal leading nearer the body, else takes the healer at once; a ghost at a graveyard with no
/// healer goes to the nearest one of the world's spawns. A body that cannot even be released for <see cref="NoProgressMs"/> faults with
/// <see cref="Stalled"/>. Before, a stall or a stuck walk threw at once, and the fault quarantined the bot.</description></item>
/// <item><description>Waiting at the body while the reclaim delay runs (30, 60 or 120 s by recent deaths, vmangos
/// GetCorpseReclaimDelay) is progress (live, 2026-10-07: the bots that faulted were only waiting); waiting for hostiles, or walking
/// from one revive spot to the next, is not, so a body that stays camped for a minute is given up for the healer.</description></item>
/// </list>
/// </summary>
internal sealed partial class PlayerbotRecovery(WorldSession session, PlayerbotOptions options)
{
    /// <summary>
    /// The risk's hazards as threats (<see cref="PlayerbotRisk.HazardThreats"/>): a revive inside a remembered death or retreat, or
    /// in reach of a creature that kills outright, is camped, and the revive spot is chosen outside them (set by the brain).
    /// </summary>
    internal Func<Player, IEnumerable<PlayerbotThreat>>? Hazards { get; set; }

    /// <summary>
    /// Whether the ghost's body on its own map is unsafe to reclaim, and why (a detail for the log): asked at every think of a ghost
    /// whose body lies on its map until it is revived; anything but <see cref="PlayerbotUnsafeBody.None"/> takes the spirit healer
    /// for this death (<see cref="TakeSpiritHealer"/>). Set by the brain of a bot that plays alone (<see cref="PlayerbotBrain"/>); a
    /// party, group or battleground bot keeps its body run. Null: every body is reclaimed as before.
    /// </summary>
    internal Func<Player, Corpse, (PlayerbotUnsafeBody Reason, string? Detail)>? UnsafeBody { get; set; }

    /// <summary>Highest level among the creatures recorded as attackers at this death, including a killing blow between bot thinks.</summary>
    internal int LastKillerMaxLevel { get; set; }

    /// <summary>Why this death's body was given up for the spirit healer (<see cref="UnsafeBody"/>), or None (inspection and tests).</summary>
    internal PlayerbotUnsafeBody UnsafeReason { get; private set; }

    /// <summary>
    /// Whether a living bot standing at a place would be attacked there by a creature too strong for it (set by the brain:
    /// <see cref="PlayerbotRisk.TooStrongAt"/>). Asked of the spirit healer's landing (<see cref="SpiritHealerLanding"/>) before the body
    /// is given up for it, and of the bot's own spot right after the healer revived it. Null: every landing counts as safe.
    /// </summary>
    internal Func<Player, Vector3, bool>? LandingUnsafe { get; set; }

    /// <summary>
    /// Take the bot away from an unsafe landing at once (set by the brain): called in the think whose CMSG_SPIRIT_HEALER_ACTIVATE revived
    /// the bot, when <see cref="LandingUnsafe"/> holds for where it stands. The resurrection is synchronous (the handler runs in that
    /// call) and the creatures round the landing aggro only in their own update, so the bot is still out of combat here: a teleport is
    /// allowed now and refused a moment later (live 2026-10-11 08:07-08:09, Wfbotfe: revived among Raven Hill's Skeletal Horrors, in
    /// combat before its next think, the home teleport refused, three deaths there). True when the bot was moved. Null: no escape.
    /// </summary>
    internal Func<Player, bool>? EscapeLanding { get; set; }

    /// <summary>
    /// Tests only: the graveyard of a position on the ghost's map in place of the zone and area lookup and the static links (the test
    /// terrain knows no zones, so it cannot hold a band of cells without an area). Null: the real lookup.
    /// </summary>
    internal Func<Player, Vector3, WorldSafeLoc?>? GraveyardLookup { get; set; }

    /// <summary>
    /// Whether the ghost's map resurrects through area spirit guides (a battleground) rather than the world's spirit healer. Null: the map
    /// is a battleground map (<see cref="BattlegroundManager.TypeOfMap"/>). Tests set it to stand a battleground on another map.
    /// </summary>
    internal Func<Player, bool>? AreaSpiritGuides { get; set; }

    /// <summary>CMSG_AREA_SPIRIT_HEALER_QUEUE requests sent in this death (inspection and tests).</summary>
    internal int AreaQueueRequests { get; private set; }

    /// <summary>
    /// A battleground ghost waits for the spirit guide's wave from this close: inside the 20 yards the wave reaches (cmangos
    /// battlegrounds.cpp npc_spirit_guideAI::SpellHitTarget, <c>IsWithinDistInMap(m_creature, 20.0f)</c>) with a margin.
    /// </summary>
    internal const float AreaSpiritGuideRangeYards = 15f;

    /// <summary>The guide's resurrection interval (cmangos BattleGround.h RESURRECTION_INTERVAL, 30 000 ms).</summary>
    internal const long AreaResurrectionIntervalMs = 30_000;

    /// <summary>How often a ghost without Waiting to Resurrect asks the guide again (CMSG_AREA_SPIRIT_HEALER_QUEUE).</summary>
    internal const long AreaQueueRetryMs = 5_000;

    /// <summary>Whether this death's body was kept although judged unsafe, because the healer's landing was worse (inspection and tests).</summary>
    internal bool BodyKeptOverLanding { get; private set; }

    /// <summary>Whether the bot was taken away from an unsafe landing after its spirit-healer resurrection (inspection and tests).</summary>
    internal bool EscapedLanding { get; private set; }

    /// <summary>
    /// Whether a body is unsafe to reclaim, free of world state: the bot already died near it inside the death watch's window
    /// (<paramref name="diedHereBefore"/>, <see cref="PlayerbotStallWatch.DeathsNear"/>); a creature round it is too strong for the
    /// bot even at full health (<paramref name="hostilesTooStrong"/>, <see cref="PlayerbotRisk.TooStrongNear"/>); it is submerged
    /// below the liquid surface (<paramref name="bodySubmerged"/>); or it lies outside
    /// the bot's home (<paramref name="bodyOutsideHome"/>, <see cref="PlayerbotHomeArea.Contains(uint, float, float, float)"/>) while
    /// the spirit healer leaves the bot nearer home (<paramref name="healerNearerHome"/>). A body in a safe spot inside the home, at
    /// the first death there, is reclaimed as always: the spirit healer costs resurrection sickness and a quarter of the durability
    /// (vmangos WorldSession::SendSpiritResurrect, NPCHandler.cpp:430-471).
    /// </summary>
    internal static PlayerbotUnsafeBody JudgeBody(bool diedHereBefore, bool hostilesTooStrong, bool bodyOutsideHome,
        bool healerNearerHome, bool bodySubmerged = false)
    {
        if (bodySubmerged) return PlayerbotUnsafeBody.Hazard;
        if (diedHereBefore) return PlayerbotUnsafeBody.DiedHere;
        if (hostilesTooStrong) return PlayerbotUnsafeBody.TooStrong;
        return bodyOutsideHome && healerNearerHome ? PlayerbotUnsafeBody.OutsideHome : PlayerbotUnsafeBody.None;
    }

    /// <summary>
    /// How much nearer the home's centre than the body the spirit healer must leave a bot whose body lies outside its home (when it
    /// does not leave it inside the home) for <see cref="PlayerbotUnsafeBody.OutsideHome"/>: a few yards are not worth resurrection
    /// sickness and a quarter of the durability. An ArcaneCore choice (no reference server has a home).
    /// </summary>
    internal const float OutsideHomeGainYards = 50f;

    internal const long NoProgressMs = 60_000;
    internal const long StuckMs = 10_000;

    /// <summary>
    /// A walk whose plan the navigation refused this many times in a row (pending tile reads do not count; a route resets the count) is
    /// given up at once, like the taxi, tram and ship walks (<see cref="PlayerbotTaxi.MaxRefusedPlans"/>): the body for a same-map portal
    /// or the spirit healer, a healer for the next. Live 2026-10-16 (Eliaslial): 361 warnings of five refused plans each toward a body
    /// in Darnassus from Rut'theran, which only the portal joins, before the stuck bound ended the corpse run.
    /// </summary>
    internal const int MaxRefusedPlans = 3;

    /// <summary>
    /// A ghost whose fallback gave up (<see cref="PlayerbotSpiritHealerStep.Fault"/>) stands this long, then tries the whole recovery
    /// again with fresh bounds (a healer respawned, a tile read, a creature gone). An ArcaneCore choice.
    /// </summary>
    internal const long GiveUpRetryMs = 300_000;

    /// <summary>A same-map teleport trigger is walked to from at most this far (straight line).</summary>
    internal const float MaxPortalWalkYards = 250f;

    /// <summary>The ghost counts as through a portal once it stands this close to the trigger's destination.</summary>
    internal const float PortalArrivedYards = 20f;

    /// <summary>A portal is taken only when the way through it is shorter than the straight line by at least this much.</summary>
    internal const float PortalGainYards = 50f;

    /// <summary>
    /// A bound (<see cref="NoProgressMs"/>, <see cref="StuckMs"/>) is judged only after the recovery has acted in more than this many
    /// thinks since the progress or closing it measures from. World time alone is not enough: a bot is thought for only when the
    /// shared action budget and the per-tick time cap reach it (<see cref="PlayerbotThinkRotation"/>), and under load a gap between
    /// two thinks can outlast a bound while the motion carries the ghost on (live 2026-10-10: world updates of up to 8.7 s during a
    /// boost storm; Graveweaver's ghost faulted with <see cref="SpiritHealerFailed"/> at its first think after each gap, three times
    /// in 3.5 minutes, and was disabled). At the ordinary think interval (500 ms) a bound spans 20 thinks or more, so this changes
    /// nothing there.
    /// </summary>
    internal const int MinBoundThinks = 4;

    /// <summary>
    /// The aggro radius assumed for a hostile creature when the map has no creature system to ask (vmangos' 20 yards for a
    /// creature of the bot's level, and a margin). With one, each creature's own radius is used (<see cref="Threats"/>).
    /// </summary>
    internal const float HostileClearYards = 25f;

    /// <summary>
    /// The camped-body margin: a hostile camps the ghost's revive point when the ghost stands inside the creature's aggro radius
    /// plus this (the creature may step a little before the revived bot gets up).
    /// </summary>
    internal const float CampMarginYards = 2.5f;

    /// <summary>Space a chosen corpse-reclaim point keeps beyond every threat's own aggro radius.</summary>
    internal const float ReviveSpotMarginYards = 10f;

    /// <summary>
    /// A revive spot keeps this much more room than <see cref="CampMarginYards"/>, so a bot that stops a yard or two short of it
    /// still passes the camped-body rule.
    /// </summary>
    internal const float ReviveSpotSlackYards = 2.5f;

    /// <summary>A revive spot is at most this far (3D) from the body: the reclaim radius less a margin for the walk's last yards.</summary>
    internal const float ReviveSpotMaxYards = CombatConstants.CorpseReclaimRadius - 3f;

    /// <summary>A walk to a revive spot longer than this (route length) is not worth it: the camp may have moved by then.</summary>
    internal const float ReviveSpotMaxWalkYards = 90f;

    /// <summary>The spacing of the candidate rings round the body, and the number of candidates on each.</summary>
    internal const float ReviveSpotRingYards = 3f;
    internal const int ReviveSpotAngles = 24;

    /// <summary>Revive spots whose route is asked of the navigation mesh in one search (nearest first).</summary>
    internal const int ReviveSpotMaxQueries = 16;

    /// <summary>The ghost activates a healer from this close: inside the server's interaction range with a margin.</summary>
    internal const float SpiritHealerReachYards = 3f;

    /// <summary>The fault of a body that could not even be released.</summary>
    internal const string Stalled = "playerbot-recovery-stalled";

    /// <summary>The fault of a ghost that could reach neither its body nor a spirit healer.</summary>
    internal const string SpiritHealerFailed = "playerbot-recovery-spirit-healer-failed";

    private PlayerbotRoute? _routeSlot;

    /// <summary>The route this recovery walks. Letting go of one (null or another route) marks it released
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.</summary>
    private PlayerbotRoute? _route
    {
        get => _routeSlot;
        set
        {
            if (ReferenceEquals(_routeSlot, value)) return;
            if (_routeSlot is { } old) old.Released = true;
            _routeSlot = value;
        }
    }
    private object? _goal;
    private long _areaQueueSentMs = -1;
    private long _areaWaitSinceMs = -1;
    private bool _areaWaitReported;
    private ObjectGuid _areaGuideWalkStalled;
    private bool _faultReported;
    private long _gaveUpMs = -1;
    private int _refusedPlans;

    /// <summary>The same-map portal the corpse run walks into (<see cref="PlayerbotRecoveryStep.WalkToPortal"/>), or null.</summary>
    private (AreaTriggerTemplate Trigger, Vector3 Exit)? _bodyPortal;

    /// <summary>The spirit healer of the world's spawns the fallback walks to (<see cref="PlayerbotSpiritHealerStep.WalkToHealerSpawn"/>), or null.</summary>
    private HealerSpawnGoal? _healerSpawnGoal;

    /// <summary>Same-map portals whose walk failed in this death.</summary>
    private readonly HashSet<uint> _portalsTried = [];

    /// <summary>Spirit healer spawns whose walk failed, or that stood empty on arrival, in this death.</summary>
    private readonly HashSet<Vector3> _healerSpawnsTried = [];

    /// <summary>The spirit healer spawns of one map of one creature content (cached: the content is replaced on a reload).</summary>
    private (CreatureContent? Content, uint Map, Vector3[] At) _healerSpawns = (null, 0, []);

    /// <summary>A walk to a spawned spirit healer, through <paramref name="Via"/> (a same-map portal leading to <paramref name="Exit"/>) first when set.</summary>
    private sealed record HealerSpawnGoal(Vector3 At, AreaTriggerTemplate? Via, Vector3 Exit);
    private long _progressMs = -1;
    private long _closingMs = -1;
    private int _thinksSinceProgress;
    private int _thinksSinceClosing;
    private float _bestDistance = float.PositiveInfinity;
    private float _bestRouteLeft = float.PositiveInfinity;
    private bool _spiritHealer;
    private bool _preferSpiritHealer;
    private bool _healerRequested;
    private Vector3? _spot;
    private object? _spotKey;

    /// <summary>The step of the last update (inspection and tests).</summary>
    internal PlayerbotRecoveryStep LastStep { get; private set; } = PlayerbotRecoveryStep.Release;

    /// <summary>The spirit-healer step of the last update while the fallback runs (inspection and tests).</summary>
    internal PlayerbotSpiritHealerStep? LastSpiritHealerStep { get; private set; }

    /// <summary>Whether the ghost was relocated once to the graveyard's safe location after both walks stalled (inspection and tests).</summary>
    internal bool GraveyardRelocated => _graveyardRelocated;

    /// <summary>Whether the ghost was relocated once onto the spirit healer after walking from the graveyard stalled (inspection and tests).</summary>
    internal bool HealerRelocated => _healerRelocated;

    /// <summary>Whether the spirit-healer fallback was taken for this death.</summary>
    internal bool UsingSpiritHealer => _spiritHealer;

    /// <summary>
    /// Whether this death goes to the spirit healer instead of the body (a death loop, <see cref="TakeSpiritHealer"/>, once a healer
    /// can be had; or an unsafe body, <see cref="UnsafeBody"/>).
    /// </summary>
    internal bool SpiritHealerChosen => _preferSpiritHealer;

    /// <summary>Whether the spirit healer was asked for this death (<see cref="TakeSpiritHealer"/>), whether or not one can be had.</summary>
    internal bool SpiritHealerRequested => _healerRequested;

    /// <summary>
    /// Take the spirit healer for this death instead of the body: the bot keeps dying where its body lies (a death loop,
    /// <see cref="PlayerbotStallWatch.RecordDeath"/>), so reviving there again would only repeat it. The body is still released
    /// first; the choice lasts until the bot is alive again. A ghost whose body lies on its map takes it only while a spirit healer
    /// can be had (one in sight, or a graveyard for its position, <see cref="HealerOutcomeKnown"/>): with neither it would wait for a
    /// healer until the fallback faulted (<see cref="SpiritHealerFailed"/>), so it keeps its corpse run, as a spirit with no linked
    /// graveyard is left where it died (vmangos Player::RepopAtGraveyard); the errand set aside and the home return do the rest.
    /// </summary>
    internal void TakeSpiritHealer() => _healerRequested = true;

    /// <summary>The revive spot the ghost walks to or last chose (inspection and tests).</summary>
    internal Vector3? ReviveSpot => _spot;

    /// <summary>Where the current walk is going (tests): the last point of its route.</summary>
    internal Vector3? RouteEnd => _route is { Points.Count: > 0 } route ? route.Points[^1] : null;

    /// <summary>
    /// The decision, free of world state. <paramref name="stalled"/>: no progress for <see cref="NoProgressMs"/>, or a walk that
    /// stopped closing for <see cref="StuckMs"/>; <paramref name="fallback"/>: the spirit healer was already chosen for this death;
    /// <paramref name="reviveSpotKnown"/>: a clear, reachable spot inside the reclaim radius exists while a hostile camps the ghost.
    /// </summary>
    internal static PlayerbotRecoveryStep Decide(bool ghost, PlayerbotCorpsePlace corpse, bool entranceKnown, bool atCorpse,
        long reclaimWaitSeconds, bool hostileNearCorpse, bool stalled, bool fallback, bool reviveSpotKnown = false)
    {
        if (!ghost) return stalled ? PlayerbotRecoveryStep.Fault : PlayerbotRecoveryStep.Release;
        if (fallback || stalled) return PlayerbotRecoveryStep.SpiritHealer;
        switch (corpse)
        {
            case PlayerbotCorpsePlace.None:
                return PlayerbotRecoveryStep.SpiritHealer;
            case PlayerbotCorpsePlace.OtherMap:
                return entranceKnown ? PlayerbotRecoveryStep.WalkToEntrance : PlayerbotRecoveryStep.SpiritHealer;
            case PlayerbotCorpsePlace.ThisMap:
                break;
            default:
                return PlayerbotRecoveryStep.SpiritHealer;
        }

        if (!atCorpse) return PlayerbotRecoveryStep.WalkToCorpse;
        if (reclaimWaitSeconds > 0) return PlayerbotRecoveryStep.WaitForReclaimDelay;
        if (!hostileNearCorpse) return PlayerbotRecoveryStep.Reclaim;
        return reviveSpotKnown ? PlayerbotRecoveryStep.WalkToReviveSpot : PlayerbotRecoveryStep.WaitForHostiles;
    }

    /// <summary>The spirit-healer fallback, free of world state.</summary>
    /// <param name="bodyRunLeft">The body lies on the ghost's map and the corpse run was not resumed yet for this death.</param>
    /// <param name="detourLeft">The long route to the healer or graveyard (<see cref="PlayerbotSpiritHealerStep.DetourToHealer"/>) was not tried yet for this death.</param>
    /// <param name="serverAccepts">The walk stalled short of <see cref="SpiritHealerReachYards"/>, and the server's own checks would accept
    /// CMSG_SPIRIT_HEALER_ACTIVATE from where the ghost stands (<see cref="ServerReachProbeYards"/>).</param>
    /// <param name="graveyardInReach">The ghost already stands within <see cref="SpiritHealerReachYards"/> of the graveyard: walking to it
    /// cannot close (live 2026-10-16, "graveyard 129 0.6 yd away cannot be reached"), so it is the body or the wait for a healer.</param>
    internal static PlayerbotSpiritHealerStep DecideSpiritHealer(bool healerKnown, bool healerInReach, bool graveyardKnown, bool stalled,
        bool bodyRunLeft = false, bool detourLeft = false, bool serverAccepts = false, bool graveyardInReach = false)
    {
        // A graveyard the ghost stands on is nothing to walk to: no healer stands there (or it would be in sight).
        if (!healerKnown && graveyardKnown && graveyardInReach)
            return bodyRunLeft ? PlayerbotSpiritHealerStep.ResumeCorpseRun
                : stalled ? PlayerbotSpiritHealerStep.Fault : PlayerbotSpiritHealerStep.WaitForHealer;

        // A stalled fallback: the healer from here if the server takes it, else by the long way round (to the healer or graveyard),
        // else the body, else the fault (each once per death).
        if (stalled)
        {
            if (healerKnown && serverAccepts) return PlayerbotSpiritHealerStep.Activate;
            if ((healerKnown ? !healerInReach : graveyardKnown) && detourLeft) return PlayerbotSpiritHealerStep.DetourToHealer;
            return bodyRunLeft ? PlayerbotSpiritHealerStep.ResumeCorpseRun : PlayerbotSpiritHealerStep.Fault;
        }
        if (healerKnown) return healerInReach ? PlayerbotSpiritHealerStep.Activate : PlayerbotSpiritHealerStep.WalkToHealer;
        if (graveyardKnown) return PlayerbotSpiritHealerStep.WalkToGraveyard;
        // Neither is a fault by itself: one look that finds nothing can be a transient (the bound decides, Stuck). With the body on
        // this map the corpse run is the way back the server leaves a spirit with no graveyard; it is resumed once per death.
        return bodyRunLeft ? PlayerbotSpiritHealerStep.ResumeCorpseRun : PlayerbotSpiritHealerStep.WaitForHealer;
    }

    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !player.IsInWorld)
            return false;
        PlayerbotThinkProbe.Phase("recovery start");
        if (player.IsAlive)
        {
            Reset();
            return false;
        }

        long now = (long)session.World.Uptime.TotalMilliseconds;
        // A new death no think saw coming (a revival and a death between two thinks, the brain's reset unseen): the body this recovery
        // saw released is gone (reclaimed, or bones after the healer) or another one lies in its place. It starts afresh, as after a reset.
        if (_deathBody != default && (player.Combat.Corpse?.Guid ?? default) != _deathBody)
            Reset();
        if ((player.Flags & PlayerFlags.Ghost) != 0 && player.Combat.Corpse is { } ownBody) _deathBody = ownBody.Guid;
        if (_progressMs < 0)
        {
            // The first think of a new death: the loop watch starts afresh (PlayerbotMotion.ForgetLoops), so the routes of the living
            // bot before it and those of earlier deaths cannot make the ghost's walk to its body or a healer a "loop" and refuse it.
            MarkProgress(now);
            PlayerbotMotion.ForgetLoops(player);
            BodyKeptOverLanding = false;
            BodyKeptOverSickness = PlayerbotUnsafeBody.None;
            EscapedLanding = false;
            _refusal = null;
            _keepBody = default;
            _keepBodySickness = default;
            _submergedShore = default;
            _unsafeBodyVerdict = default;
        }
        // A think without budget does nothing, and is no attempt the bounds may count (MinBoundThinks).
        if (session.ManagedBudget is { Remaining: <= 0 }) return false;
        _thinksSinceProgress++;
        if (_closingMs >= 0) _thinksSinceClosing++;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (!ghost && NoProgress(now))
        {
            LastStep = PlayerbotRecoveryStep.Fault;
            ReportStalled(player, now, "a living bot whose death was never released");
            throw new InvalidOperationException(Stalled);
        }

        // A battleground ghost does not walk back to its body nor look for the world's spirit healer: the graveyard's spirit guide
        // resurrects the dead round it in waves (cmangos npc_spirit_guideAI), and the release already put the ghost beside it.
        if (ghost && InBattleground(player) && NearestSpiritGuide(player) is { } guide && guide.Guid != _areaGuideWalkStalled)
        {
            PlayerbotThinkProbe.Phase("recovery area spirit guide");
            return AwaitAreaResurrection(player, guide, elapsedMs, now);
        }

        // A ghost whose fallback gave up stands for GiveUpRetryMs, then tries the recovery afresh (reported once per death all the same).
        if (_gaveUpMs >= 0 && now - _gaveUpMs > GiveUpRetryMs)
            RetryAfterGiveUp(player, now);

        if (_spiritHealer)
        {
            PlayerbotThinkProbe.Phase("recovery healer");
            return SpiritHealer(player, elapsedMs, now);
        }

        // The corpse run through a same-map portal (WalkToPortal): walked until the ghost stands at its far side, then the body again.
        if (ghost && _bodyPortal is { } bodyPortal)
        {
            PlayerbotThinkProbe.Phase("recovery portal walk");
            LastStep = PlayerbotRecoveryStep.WalkToPortal;
            PortalWalk walk = WalkThroughPortal(player, bodyPortal.Trigger, bodyPortal.Exit, elapsedMs, now, out bool walked);
            if (walk == PortalWalk.Walking) return walked;
            _bodyPortal = null;
            return false;
        }

        PlayerbotThinkProbe.Phase("recovery body decision");
        bool stalled = Stuck(now);
        Corpse? corpse = player.Combat.Corpse;
        PlayerbotCorpsePlace place = corpse is null ? PlayerbotCorpsePlace.None
            : corpse.MapId == player.MapId && ReferenceEquals(corpse.Map, player.Map) ? PlayerbotCorpsePlace.ThisMap
            : PlayerbotCorpsePlace.OtherMap;
        // A death loop's request (TakeSpiritHealer): taken at once without a body on this map (the healer is the only way back
        // anyway, as before), and with one only while a healer can be had; otherwise the corpse run goes on and it is asked again.
        // Never overridden by the landing (KeepBodyOverLanding): the healer is the way out of a body death loop, and an unsafe landing
        // is left at once after the resurrection (EscapeLanding).
        if (_healerRequested && !_preferSpiritHealer && ghost
            && (place != PlayerbotCorpsePlace.ThisMap || (corpse is { } reqCorpse && _corpseRunResumedFor != reqCorpse.Guid && HealerOutcomeKnown(player, reqCorpse))))
            _preferSpiritHealer = true;

        // Only when the spirit healer can be had at all (one in sight, or a graveyard for this place, where one stands): with
        // neither, giving the body up would leave the ghost waiting for a healer until the fallback faults (SpiritHealerFailed);
        // vmangos leaves a spirit with no linked graveyard where it died (Player::RepopAtGraveyard), and the body is its way back.
        // A body judged unsafe whose spirit healer would revive the bot among creatures too strong for it (LandingUnsafe), while a spot
        // inside the reclaim radius of the body is clear of every threat, is kept: the body is the safer of the two, and the corpse run
        // revives the bot on that spot (the camped-body rule, ChooseReviveSpot). Live 2026-10-11 08:07-08:09 and 08:23-08:30 (Wfbotfe,
        // homed; Dawnrover, unhomed): bodies near Raven Hill were given up for the healer, which revived the bots at the Raven Hill
        // graveyard among the Skeletal Horrors; they died there again and again. A third death there is a death loop, whose request
        // above takes the healer whatever the landing: at most two kept bodies per death-loop window.
        // Not once the fallback went back to this body (ResumeCorpseRun): its healer could not be reached, or there was none.
        // Nor, mostly, for a bot the healer would sicken (KeepBodyOverSickness): the corpse run is its way back but for a death loop.
        if (ghost && place == PlayerbotCorpsePlace.ThisMap && corpse is { } bodyOnMap && !_preferSpiritHealer && UnsafeBody is { } judge
            && _corpseRunResumedFor != bodyOnMap.Guid && HealerOutcomeKnown(player, bodyOnMap)
            && EvaluateUnsafeBody(player, bodyOnMap, judge, now) is { Reason: not PlayerbotUnsafeBody.None } verdict
            && (verdict.Reason == PlayerbotUnsafeBody.Hazard
                ? !CanReclaimSubmergedFromDryShore(player, bodyOnMap)
                : !KeepBodyOverLanding(player, bodyOnMap) && !KeepBodyOverSickness(player, bodyOnMap, verdict.Reason)))
        {
            // The body is not worth it (submerged, a repeat death there, a camp too strong, or outside the home with the healer nearer it):
            // take the spirit healer now rather than revive where the bot would die again.
            _preferSpiritHealer = true;
            UnsafeReason = verdict.Reason;
            LoggerOf().LogInformation(
                "Playerbot {Bot} takes the spirit healer instead of its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Reason}{Detail}",
                player.Name, bodyOnMap.X, bodyOnMap.Y, bodyOnMap.Z, bodyOnMap.MapId, Describe(verdict.Reason),
                verdict.Detail is { Length: > 0 } detail ? " (" + detail + ")" : string.Empty);
        }

        PlayerbotThinkProbe.Phase("recovery entrance");
        AreaTriggerTemplate? entrance = place == PlayerbotCorpsePlace.OtherMap && corpse is { } otherCorpse ? FindEntrance(player, otherCorpse.MapId) : null;
        PlayerbotThinkProbe.Phase("recovery revive spot");
        bool atCorpse = place == PlayerbotCorpsePlace.ThisMap && corpse is { } sameMapCorpse && WithinReclaimDistance(player, sameMapCorpse);
        long wait = atCorpse ? player.Map?.Combat.CorpseReclaimWaitSeconds(player) ?? 0 : 0;
        bool submergedAtCorpse = atCorpse && wait <= 0 && player.Map is { } currentMap && corpse is { } submergedCorpse
            && PlayerbotWater.Submerged(currentMap, new Vector3(submergedCorpse.X, submergedCorpse.Y, submergedCorpse.Z), player.Locomotion.CollisionHeight)
            && !IsDryShore(currentMap, new Vector3(player.X, player.Y, player.Z));
        bool hostile = atCorpse && wait <= 0 && (Camped(player, Hazards?.Invoke(player) ?? []) || submergedAtCorpse);
        Vector3? spot = hostile && !stalled && !_preferSpiritHealer && corpse is { } spotCorpse ? ChooseReviveSpot(player, spotCorpse) : null;
        PlayerbotRecoveryStep step = Decide(ghost, place, entrance is not null, atCorpse, wait, hostile, stalled, fallback: _preferSpiritHealer,
            reviveSpotKnown: spot is not null);
        LastStep = step;
        PlayerbotThinkProbe.Phase("recovery action");
        switch (step)
        {
            case PlayerbotRecoveryStep.Release:
            {
                if (!PlayerbotMovementControl.Stop(session, player)) return false;
                bool released = session.TryManagedAction(WorldOpcode.CmsgRepopRequest, []);
                if (released) MarkProgress(now);
                return released;
            }

            case PlayerbotRecoveryStep.WaitForReclaimDelay:
                // The client's Resurrect button stays disabled until SMSG_CORPSE_RECLAIM_DELAY runs out; wait like it does.
                StandStill(player);
                MarkProgress(now);
                return false;

            case PlayerbotRecoveryStep.Fault:
                // A body that was never released and made no progress: never the spirit-healer fallback, which is for ghosts.
                ReportStalled(player, now, "a body that was never released");
                throw new InvalidOperationException(Stalled);

            case PlayerbotRecoveryStep.WaitForHostiles:
                // Not progress: a body camped for NoProgressMs is given up for the spirit healer.
                StandStill(player);
                return false;

            case PlayerbotRecoveryStep.WalkToReviveSpot:
                // Not progress for the no-progress bound: a camp that keeps moving must not keep the ghost walking for good.
                PlayerbotThinkProbe.Phase("recovery revive walk");
                if (spot is not { } spotPos || _spotKey is not { } spotKey) return false;
                return Walk(player, spotPos, spotKey, elapsedMs, now, countsAsProgress: false);

            case PlayerbotRecoveryStep.Reclaim:
                if (!StandStill(player) || corpse is not { } reclaimCorpse) return false;
                return session.TryManagedAction(WorldOpcode.CmsgReclaimCorpse, PlayerbotNavigation.GuidPayload(reclaimCorpse.Guid.Value));

            case PlayerbotRecoveryStep.WalkToCorpse:
                PlayerbotThinkProbe.Phase("recovery corpse walk");
                if (corpse is not { } walkCorpse) return false;
                bool walkedToBody = Walk(player, new Vector3(walkCorpse.X, walkCorpse.Y, walkCorpse.Z), walkCorpse.Guid, elapsedMs, now, reachesGoalOnFoot: true);
                return _refusedPlans < MaxRefusedPlans ? walkedToBody : GiveUpUnreachableBody(player, walkCorpse, now);

            case PlayerbotRecoveryStep.WalkToEntrance:
                PlayerbotThinkProbe.Phase("recovery entrance walk");
                if (entrance is not { } walkEntrance) return false;
                return Walk(player, new Vector3(walkEntrance.X, walkEntrance.Y, walkEntrance.Z), walkEntrance, elapsedMs, now);

            case PlayerbotRecoveryStep.SpiritHealer:
            default:
                // The way back to the body is lost (stalled, stuck, no body, no entrance): take the spirit healer, with a fresh bound.
                _spiritHealer = true;
                ClearWalk();
                MarkProgress(now);
                PlayerbotThinkProbe.Phase("recovery healer");
                return SpiritHealer(player, elapsedMs, now);
        }
    }

    internal void Reset()
    {
        ClearWalk();
        _spot = null;
        _spotKey = null;
        _progressMs = -1;
        _thinksSinceProgress = 0;
        _spiritHealer = false;
        _preferSpiritHealer = false;
        _healerRequested = false;
        _healerGraveyard = default;
        _corpseRunResumedFor = default;
        _detourTriedFor = default;
        _graveyardDetourTriedFor = 0;
        _graveyardRelocated = false;
        _healerRelocated = false;
        _detourWait.Reset();
        _detourGoal = null;
        _deathBody = default;
        _areaQueueSentMs = -1;
        _areaWaitSinceMs = -1;
        _areaWaitReported = false;
        _areaGuideWalkStalled = default;
        _faultReported = false;
        _gaveUpMs = -1;
        _bodyPortal = null;
        _healerSpawnGoal = null;
        _portalsTried.Clear();
        _healerSpawnsTried.Clear();
        _keepBody = default;
        _keepBodySickness = default;
        _submergedShore = default;
        _unsafeBodyVerdict = default;
        AreaQueueRequests = 0;
        UnsafeReason = PlayerbotUnsafeBody.None;
        LastStep = PlayerbotRecoveryStep.Release;
        LastSpiritHealerStep = null;
    }

    /// <summary>
    /// The fallback gave up <see cref="GiveUpRetryMs"/> ago: try the recovery afresh, body first, with fresh bounds and nothing set
    /// aside (a healer may have respawned, a tile been read, a camp left). The give-up report is not repeated in this death.
    /// </summary>
    private void RetryAfterGiveUp(Player player, long now)
    {
        _gaveUpMs = -1;
        _spiritHealer = false;
        _preferSpiritHealer = false;
        _corpseRunResumedFor = default;
        _detourTriedFor = default;
        _graveyardDetourTriedFor = 0;
        _detourWait.Reset();
        _detourGoal = null;
        _bodyPortal = null;
        _healerSpawnGoal = null;
        _portalsTried.Clear();
        _healerSpawnsTried.Clear();
        _keepBody = default;
        _keepBodySickness = default;
        _submergedShore = default;
        _unsafeBodyVerdict = default;
        ClearWalk();
        PlayerbotMotion.ForgetLoops(player);
        MarkProgress(now);
        LastSpiritHealerStep = null;
        LoggerOf().LogInformation(
            "Playerbot {Bot} tries its recovery again at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}, {Seconds} s after the spirit-healer fallback gave up",
            player.Name, player.X, player.Y, player.Z, player.MapId, GiveUpRetryMs / 1000);
    }

    /// <summary>
    /// The corpse run's plan was refused <see cref="MaxRefusedPlans"/> times in a row: no walk on the mesh reaches the body from here. A
    /// same-map portal whose far side is nearer the body is walked into (<see cref="PlayerbotRecoveryStep.WalkToPortal"/>); otherwise the
    /// spirit healer is taken at once, instead of planning toward the body until the stuck bound (live: hundreds of refused plans).
    /// </summary>
    private bool GiveUpUnreachableBody(Player player, Corpse corpse, long now)
    {
        var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
        ClearWalk();
        MarkProgress(now);
        if (FindPortalToward(player, body) is { } portal)
        {
            _bodyPortal = portal;
            LastStep = PlayerbotRecoveryStep.WalkToPortal;
            LoggerOf().LogInformation(
                "Playerbot {Bot} takes area trigger {Trigger} at ({TX:F1}, {TY:F1}, {TZ:F1}) toward its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Refused} plans to it were refused",
                player.Name, portal.Trigger.Id, portal.Trigger.X, portal.Trigger.Y, portal.Trigger.Z, corpse.X, corpse.Y, corpse.Z, corpse.MapId, MaxRefusedPlans);
            return false;
        }

        _spiritHealer = true;
        LoggerOf().LogInformation(
            "Playerbot {Bot} takes the spirit healer: its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map} cannot be reached from ({PX:F1}, {PY:F1}, {PZ:F1}) ({Refused} plans refused, no portal leads nearer)",
            player.Name, corpse.X, corpse.Y, corpse.Z, corpse.MapId, player.X, player.Y, player.Z, MaxRefusedPlans);
        return false;
    }

    /// <summary>How a walk into a same-map portal stands (<see cref="WalkThroughPortal"/>).</summary>
    private enum PortalWalk
    {
        Walking,
        Taken,
        Failed,
    }

    /// <summary>
    /// Walk into <paramref name="trigger"/> (the motion reports it, <see cref="PlayerbotAreaTriggers"/>, and the server teleports the ghost
    /// to <paramref name="exit"/>). Taken once the ghost stands within <see cref="PortalArrivedYards"/> of the exit; failed (and set aside
    /// for this death) when the walk is stuck or refused <see cref="MaxRefusedPlans"/> times.
    /// </summary>
    private PortalWalk WalkThroughPortal(Player player, AreaTriggerTemplate trigger, Vector3 exit, uint elapsedMs, long now, out bool walked)
    {
        walked = false;
        if (Distance(player, exit.X, exit.Y, exit.Z) <= PortalArrivedYards)
        {
            ClearWalk();
            PlayerbotMotion.ForgetLoops(player);
            MarkProgress(now);
            LoggerOf().LogInformation(
                "Playerbot {Bot} came through area trigger {Trigger} to ({X:F1}, {Y:F1}, {Z:F1}) map {Map}",
                player.Name, trigger.Id, player.X, player.Y, player.Z, player.MapId);
            return PortalWalk.Taken;
        }

        if (Stuck(now) || _refusedPlans >= MaxRefusedPlans)
        {
            _portalsTried.Add(trigger.Id);
            ClearWalk();
            MarkProgress(now);
            LoggerOf().LogInformation(
                "Playerbot {Bot} could not walk into area trigger {Trigger} at ({TX:F1}, {TY:F1}, {TZ:F1}) map {Map} from ({X:F1}, {Y:F1}, {Z:F1})",
                player.Name, trigger.Id, trigger.X, trigger.Y, trigger.Z, player.MapId, player.X, player.Y, player.Z);
            return PortalWalk.Failed;
        }

        walked = Walk(player, new Vector3(trigger.X, trigger.Y, trigger.Z), trigger, elapsedMs, now);
        return PortalWalk.Walking;
    }

    /// <summary>
    /// The same-map teleport trigger (a portal: its <c>areatrigger_teleport</c> leads to this map) through which <paramref name="goal"/>
    /// is nearest: within <see cref="MaxPortalWalkYards"/> of the ghost, not tried in this death, and shorter than the straight line by
    /// <see cref="PortalGainYards"/> (the walk to the trigger plus the straight line from its destination). Null when none is.
    /// </summary>
    private (AreaTriggerTemplate Trigger, Vector3 Exit)? FindPortalToward(Player player, Vector3 goal)
    {
        float best = Distance(player, goal.X, goal.Y, goal.Z) - PortalGainYards;
        (AreaTriggerTemplate, Vector3)? found = null;
        foreach ((AreaTriggerTemplate trigger, Vector3 exit, float toTrigger) in PortalsNear(player))
        {
            float cost = toTrigger + Vector3.Distance(exit, goal);
            if (cost < best)
            {
                best = cost;
                found = (trigger, exit);
            }
        }

        return found;
    }

    /// <summary>
    /// The same-map portals the ghost may walk into from here: triggers on its map whose teleport leads to this map, within
    /// <see cref="MaxPortalWalkYards"/>, not tried in this death, and not leading back to where it stands.
    /// </summary>
    private List<(AreaTriggerTemplate Trigger, Vector3 Exit, float ToTrigger)> PortalsNear(Player player)
    {
        var portals = new List<(AreaTriggerTemplate, Vector3, float)>();
        WorldMaps maps = WorldMaps.Of(session.World);
        var here = new Vector3(player.X, player.Y, player.Z);
        foreach (AreaTriggerTemplate trigger in PlayerbotAreaTriggers.OnMap(maps, player.MapId))
        {
            if (_portalsTried.Contains(trigger.Id) || maps.FindAreaTriggerTeleport(trigger.Id) is not { } teleport
                || teleport.TargetMap != player.MapId)
                continue;
            float toTrigger = Distance(player, trigger.X, trigger.Y, trigger.Z);
            var exit = new Vector3(teleport.TargetX, teleport.TargetY, teleport.TargetZ);
            if (!float.IsFinite(toTrigger) || toTrigger > MaxPortalWalkYards || Vector3.Distance(exit, here) <= 2 * PortalArrivedYards)
                continue;
            portals.Add((trigger, exit, toTrigger));
        }

        return portals;
    }

    /// <summary>
    /// The spirit healers of the world's spawns on the ghost's map (<c>creature</c> rows whose template carries
    /// <see cref="NpcFlags.SpiritHealer"/>), cached per map and creature content.
    /// </summary>
    private Vector3[] HealerSpawns(Player player)
    {
        if (session.Services.GetService<CreatureWorldFeature>()?.Content is not { } content) return [];
        if (ReferenceEquals(_healerSpawns.Content, content) && _healerSpawns.Map == player.MapId) return _healerSpawns.At;
        Vector3[] at = [.. content.GetSpawns(player.MapId)
            .Where(spawn => content.FindTemplate(spawn.Entry) is { } template && (template.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0)
            .Select(spawn => new Vector3(spawn.X, spawn.Y, spawn.Z))];
        _healerSpawns = (content, player.MapId, at);
        return at;
    }

    /// <summary>
    /// The spirit healer of the world's spawns the ghost goes to when none is in sight at its graveyard: the one nearest by the straight
    /// line, or through a same-map portal (<see cref="PortalsNear"/>) when that is shorter. A healer whose walk failed, or that stood
    /// empty on arrival, is not chosen again in this death; neither is one within <see cref="PortalArrivedYards"/> (the ghost is there).
    /// </summary>
    private HealerSpawnGoal? ChooseHealerSpawn(Player player)
    {
        Vector3[] healers = HealerSpawns(player);
        if (healers.Length == 0) return null;
        List<(AreaTriggerTemplate Trigger, Vector3 Exit, float ToTrigger)> portals = PortalsNear(player);
        HealerSpawnGoal? best = null;
        float bestCost = float.PositiveInfinity;
        foreach (Vector3 healer in healers)
        {
            if (_healerSpawnsTried.Contains(healer)) continue;
            float direct = Distance(player, healer.X, healer.Y, healer.Z);
            if (!float.IsFinite(direct) || direct <= PortalArrivedYards) continue;
            if (direct < bestCost)
            {
                bestCost = direct;
                best = new HealerSpawnGoal(healer, null, default);
            }

            foreach ((AreaTriggerTemplate trigger, Vector3 exit, float toTrigger) in portals)
            {
                float cost = toTrigger + Vector3.Distance(exit, healer);
                if (cost < bestCost && cost < direct - PortalGainYards)
                {
                    bestCost = cost;
                    best = new HealerSpawnGoal(healer, trigger, exit);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// One think of the walk to a spawned healer (<see cref="PlayerbotSpiritHealerStep.WalkToHealerSpawn"/>): through its portal first,
    /// then to where it stands. <paramref name="keep"/> is false when the goal was given up (stuck, refused, or reached with no healer in
    /// sight): the fallback then decides afresh in the same think.
    /// </summary>
    private bool WalkToHealerSpawn(Player player, HealerSpawnGoal goal, uint elapsedMs, long now, out bool keep)
    {
        LastSpiritHealerStep = PlayerbotSpiritHealerStep.WalkToHealerSpawn;
        keep = true;
        if (goal.Via is { } via)
        {
            switch (WalkThroughPortal(player, via, goal.Exit, elapsedMs, now, out bool walked))
            {
                case PortalWalk.Walking:
                    return walked;
                case PortalWalk.Taken:
                    _healerSpawnGoal = goal with { Via = null };
                    return false;
                case PortalWalk.Failed:
                default:
                    _healerSpawnGoal = null;
                    keep = false;
                    return false;
            }
        }

        if (player.MapId != _healerSpawns.Map || Distance(player, goal.At.X, goal.At.Y, goal.At.Z) <= SpiritHealerReachYards
            || Stuck(now) || _refusedPlans >= MaxRefusedPlans)
        {
            _healerSpawnsTried.Add(goal.At);
            _healerSpawnGoal = null;
            ClearWalk();
            MarkProgress(now);
            keep = false;
            return false;
        }

        return Walk(player, goal.At, goal, elapsedMs, now);
    }

    /// <summary>No progress for <see cref="NoProgressMs"/> of world time and more than <see cref="MinBoundThinks"/> thinks.</summary>
    private bool NoProgress(long now) => now - _progressMs > NoProgressMs && _thinksSinceProgress > MinBoundThinks;

    /// <summary>
    /// No progress (<see cref="NoProgress"/>), or a walk that stopped closing for <see cref="StuckMs"/> of world time and more than
    /// <see cref="MinBoundThinks"/> thinks.
    /// </summary>
    private bool Stuck(long now)
        => NoProgress(now) || (_closingMs >= 0 && now - _closingMs > StuckMs && _thinksSinceClosing > MinBoundThinks);

    private void MarkProgress(long now)
    {
        _progressMs = now;
        _thinksSinceProgress = 0;
    }

    private void MarkClosing(long now)
    {
        _closingMs = now;
        _thinksSinceClosing = 0;
    }

    /// <summary>The wait for tiles of the walk's plan, and of the spirit-healer detour's (<see cref="PlanOutcome.Pending"/>).</summary>
    private TileWait _walkWait, _detourWait;

    private bool SpiritHealer(Player player, uint elapsedMs, long now)
    {
        LastStep = PlayerbotRecoveryStep.SpiritHealer;
        PlayerbotThinkProbe.Phase("recovery healer lookup");
        Creature? healer = NearestSpiritHealer(player);
        // On the long way round to a healer (DetourToHealer) it drops out of sight for a while (a ghost sees it only so far): the walk
        // goes on to where it stands, under the same bounds, until it is in sight again.
        if (healer is null && _detourGoal is { } detourGoal && Equals(_goal, detourGoal.Guid) && detourGoal.Map == player.MapId)
        {
            if (!Stuck(now))
            {
                LastSpiritHealerStep = PlayerbotSpiritHealerStep.WalkToHealer;
                return Walk(player, detourGoal.At, detourGoal.Guid, elapsedMs, now);
            }

            _detourGoal = null;
        }

        // On the way to a spawned healer out of sight (WalkToHealerSpawn), through a portal maybe: on until it comes into sight.
        if (healer is null && _healerSpawnGoal is { } spawnGoal)
        {
            bool walkedToSpawn = WalkToHealerSpawn(player, spawnGoal, elapsedMs, now, out bool keepSpawn);
            if (keepSpawn) return walkedToSpawn;
        }

        WorldSafeLoc? graveyard = healer is null ? HealerGraveyard(player) : null;
        Corpse? corpse = player.Combat.Corpse;
        bool bodyHere = corpse is not null && corpse.MapId == player.MapId && ReferenceEquals(corpse.Map, player.Map);
        bool inReach = healer is not null && Distance(player, healer.X, healer.Y, healer.Z) <= SpiritHealerReachYards;
        bool graveyardInReach = graveyard is not null && Distance(player, graveyard.X, graveyard.Y, graveyard.Z) <= SpiritHealerReachYards;
        // At a graveyard where no healer is in sight (Rut'theran's 129 has none): walking to the point the ghost stands on can never
        // close (live: "graveyard 129 0.6 yd away cannot be reached"). The nearest healer of the world's spawns instead, through a
        // same-map portal when that is shorter.
        if (healer is null && graveyardInReach && _gaveUpMs < 0 && ChooseHealerSpawn(player) is { } chosen)
        {
            _healerSpawnGoal = chosen;
            ClearWalk();
            MarkProgress(now);
            LoggerOf().LogInformation(
                "Playerbot {Bot} sees no spirit healer at graveyard {Graveyard} ({X:F1}, {Y:F1}, {Z:F1}) map {Map} and goes to the one at ({HX:F1}, {HY:F1}, {HZ:F1}){Via}",
                player.Name, graveyard!.Id, player.X, player.Y, player.Z, player.MapId, chosen.At.X, chosen.At.Y, chosen.At.Z,
                chosen.Via is { } via ? $" through area trigger {via.Id}" : string.Empty);
            bool walkedToSpawn = WalkToHealerSpawn(player, chosen, elapsedMs, now, out bool keepSpawn);
            if (keepSpawn) return walkedToSpawn;
        }

        // A walk refused MaxRefusedPlans times in a row is as stalled as one stuck for StuckMs: no plan reaches the goal from here.
        bool stalled = Stuck(now) || _refusedPlans >= MaxRefusedPlans;
        // The server's own range is asked only of a walk that stalled: activating farther out than the walk's goal would change where
        // the bot is revived (among a camp round the healer, the landing escape is judged at the walk's goal).
        bool serverAccepts = stalled && healer is not null && !inReach && ServerAccepts(player, healer);
        bool detourLeft = healer is not null
            ? _detourTriedFor != healer.Guid
            : graveyard is not null && !graveyardInReach && _graveyardDetourTriedFor != graveyard.Id;
        PlayerbotSpiritHealerStep step = DecideSpiritHealer(healer is not null, inReach, graveyard is not null, stalled,
            bodyRunLeft: bodyHere && corpse is { } bodyForRun && _corpseRunResumedFor != bodyForRun.Guid, detourLeft: detourLeft,
            serverAccepts: serverAccepts, graveyardInReach: graveyardInReach);
        if (step == PlayerbotSpiritHealerStep.DetourToHealer)
        {
            PlayerbotThinkProbe.Phase("recovery healer detour");
            if (healer is not null)
            {
                PlanOutcome detourPlan = PlayerbotNavigation.PlanFar(player, new Vector3(healer.X, healer.Y, healer.Z), options,
                    PlayerbotNavigation.MaxSearchNodes, ref _detourWait, session.World.NowMs, out PlayerbotRoute? detour);
                if (detourPlan == PlanOutcome.Pending)
                {
                    // A tile of the long way is still being read: the once-per-death detour is not spent, and is asked for again next think
                    // (for TileWait.MaxMs; then the refusal stands and the detour counts as tried).
                    LastSpiritHealerStep = PlayerbotSpiritHealerStep.DetourToHealer;
                    return false;
                }

                _detourTriedFor = healer.Guid;
                if (detourPlan == PlanOutcome.Route && detour is { Points.Count: > 1 }
                    && Vector3.Distance(detour.Points[^1], new Vector3(healer.X, healer.Y, healer.Z)) <= SpiritHealerReachYards + 2f)
                {
                    ClearWalk();
                    _goal = healer.Guid;
                    _route = detour;
                    _detourRoute = true;
                    _detourGoal = (healer.Guid, player.MapId, new Vector3(healer.X, healer.Y, healer.Z));
                    LastSpiritHealerStep = PlayerbotSpiritHealerStep.DetourToHealer;
                    MarkProgress(now);
                    MarkClosing(now);
                    LoggerOf().LogInformation(
                        "Playerbot {Bot} takes the long way to the spirit healer {Healer} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Length:F0} yd round, {Straight:F1} yd straight",
                        player.Name, healer.Entry, healer.X, healer.Y, healer.Z, player.MapId, detour.Distance, Distance(player, healer.X, healer.Y, healer.Z));
                    return Walk(player, new Vector3(healer.X, healer.Y, healer.Z), healer.Guid, elapsedMs, now);
                }
            }
            else if (graveyard is not null)
            {
                if (TryDetourToGraveyard(player, graveyard, elapsedMs, now, out bool pending))
                    return true;
                if (pending)
                    return false;
            }

            // No way round within the bounds either: the body, else the fault.
            step = DecideSpiritHealer(healer is not null, false, graveyard is not null, stalled: true,
                bodyRunLeft: bodyHere && corpse is { } bodyAfterDetour && _corpseRunResumedFor != bodyAfterDetour.Guid, graveyardInReach: graveyardInReach);
        }

        LastSpiritHealerStep = step;
        PlayerbotThinkProbe.Phase("recovery healer action");
        switch (step)
        {
            case PlayerbotSpiritHealerStep.ResumeCorpseRun:
                // Nothing to walk to and nobody to wait for: back to the body, with a fresh bound, once per death. The healer is not
                // asked for again in this death unless one can be had (HealerOutcomeKnown); a second stall here faults as before.
                if (corpse is not { } resumeCorpse) return false;
                _corpseRunResumedFor = resumeCorpse.Guid;
                _spiritHealer = false;
                _preferSpiritHealer = false;
                UnsafeReason = PlayerbotUnsafeBody.None;
                ClearWalk();
                MarkProgress(now);
                LoggerOf().LogInformation(
                    "Playerbot {Bot} goes back to its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Why} at ({GX:F1}, {GY:F1}, {GZ:F1})",
                    player.Name, resumeCorpse.X, resumeCorpse.Y, resumeCorpse.Z, resumeCorpse.MapId,
                    healer is not null ? $"the spirit healer {healer.Entry} {Distance(player, healer.X, healer.Y, healer.Z):F1} yd away cannot be reached"
                        : graveyard is not null ? $"graveyard {graveyard.Id} {Distance(player, graveyard.X, graveyard.Y, graveyard.Z):F1} yd away cannot be reached"
                        : "no spirit healer in sight and no graveyard for it", player.X, player.Y, player.Z);
                return false;
            case PlayerbotSpiritHealerStep.Activate:
            {
                if (!StandStill(player) || healer is not { } activateHealer) return false;
                if (!session.TryManagedAction(WorldOpcode.CmsgSpiritHealerActivate, PlayerbotNavigation.GuidPayload(activateHealer.Guid.Value)))
                    return false;
                if (player.IsAlive)
                {
                    // The handler ran in that call (TryManagedAction is synchronous) and revived the bot: leave an unsafe landing now,
                    // before the creatures round it aggro in their own update.
                    LeaveLanding(player);
                    return true;
                }

                // Still dead: the server refused. Not progress (the fallback faults after NoProgressMs), but no longer silent.
                ReportRefusal(player, activateHealer);
                return true;
            }
            case PlayerbotSpiritHealerStep.WalkToHealer:
            case PlayerbotSpiritHealerStep.DetourToHealer:
                if (healer is not { } walkHealer) return false;
                return Walk(player, new Vector3(walkHealer.X, walkHealer.Y, walkHealer.Z), walkHealer.Guid, elapsedMs, now);
            case PlayerbotSpiritHealerStep.WalkToGraveyard:
            {
                if (graveyard is not { } walkGraveyard) return false;
                bool walked = Walk(player, new Vector3(walkGraveyard.X, walkGraveyard.Y, walkGraveyard.Z), walkGraveyard, elapsedMs, now);
                if (!walked && !graveyardInReach && !_walkWait.Active && _graveyardDetourTriedFor != walkGraveyard.Id)
                    return TryDetourToGraveyard(player, walkGraveyard, elapsedMs, now, out _);
                return walked;
            }
            case PlayerbotSpiritHealerStep.WaitForHealer:
                // Not progress: the no-progress bound turns a ghost that never finds a healer or a graveyard into the fault.
                StandStill(player);
                return false;
            case PlayerbotSpiritHealerStep.Fault:
            default:
                if (TryRelocateToGraveyard(player, graveyard ?? HealerGraveyard(player), now)
                    || TryRelocateToHealer(player, healer, now))
                    return true;
                // Never thrown as control flow: the brain's think fault quarantined the bot and, at the third, disabled it (live
                // 2026-10-16, graveyard 129). Reported once per death; the ghost stands, and the recovery is tried afresh after
                // GiveUpRetryMs (a battleground ghost is resurrected by the match in the meantime: a spirit guide in sight, its end).
                if (!_faultReported)
                {
                    ReportFault(player, healer, graveyard, now, battleground: InBattleground(player));
                    _faultReported = true;
                }

                if (_gaveUpMs < 0) _gaveUpMs = now;
                StandStill(player);
                return false;
        }
    }

    private bool TryDetourToGraveyard(Player player, WorldSafeLoc graveyard, uint elapsedMs, long now, out bool pending)
    {
        Vector3 target = new(graveyard.X, graveyard.Y, graveyard.Z);
        PlanOutcome detourPlan = PlayerbotNavigation.PlanFar(player, target, options,
            PlayerbotNavigation.MaxSearchNodes, ref _detourWait, session.World.NowMs, out PlayerbotRoute? detour);
        if (detourPlan == PlanOutcome.Pending)
        {
            LastSpiritHealerStep = PlayerbotSpiritHealerStep.DetourToHealer;
            pending = true;
            return false;
        }

        pending = false;
        _graveyardDetourTriedFor = graveyard.Id;
        if (detourPlan != PlanOutcome.Route || detour is not { Points.Count: > 1 }
            || Vector3.Distance(detour.Points[^1], target) > SpiritHealerReachYards + 2f)
            return false;
        ClearWalk();
        _goal = graveyard;
        _route = detour;
        _detourRoute = true;
        LastSpiritHealerStep = PlayerbotSpiritHealerStep.DetourToHealer;
        MarkProgress(now);
        MarkClosing(now);
        LoggerOf().LogInformation(
            "Playerbot {Bot} takes the long way to graveyard {Graveyard} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Length:F0} yd round, {Straight:F1} yd straight",
            player.Name, graveyard.Id, graveyard.X, graveyard.Y, graveyard.Z, player.MapId, detour.Distance, Distance(player, graveyard.X, graveyard.Y, graveyard.Z));
        return Walk(player, target, graveyard, elapsedMs, now);
    }

    /// <summary>
    /// Relocate a trapped ghost once per death to the graveyard's safe location on its map when both the spirit-healer walk (including
    /// <see cref="PlayerbotSpiritHealerStep.DetourToHealer"/>) and the corpse run stalled: in a terrain pocket whose navigation mesh has
    /// no way out, throwing <see cref="SpiritHealerFailed"/> would only quarantine the bot and log it back in at the same trapped
    /// coordinates. At the graveyard the ghost stands beside the spirit healer (or waits the remaining bound before faulting if none
    /// exists there).
    /// </summary>
    private bool TryRelocateToGraveyard(Player player, WorldSafeLoc? graveyard, long now)
    {
        if (_graveyardRelocated || graveyard is null || graveyard.MapId != player.MapId
            || Distance(player, graveyard.X, graveyard.Y, graveyard.Z) <= SpiritHealerReachYards)
            return false;
        (float fromX, float fromY, float fromZ) = (player.X, player.Y, player.Z);
        RelocateGhost(player, graveyard.X, graveyard.Y, graveyard.Z, graveyard.Orientation, now);
        NoteRelocation(player, now, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"relocated to graveyard {graveyard.Id} from {fromX:F1},{fromY:F1},{fromZ:F1}: the spirit-healer and corpse walks both stalled"));
        _graveyardRelocated = true;
        LoggerOf().LogInformation(
            "Playerbot {Bot} relocates to graveyard {Graveyard} at ({GX:F1}, {GY:F1}, {GZ:F1}) map {Map} after spirit-healer and corpse walks both stalled at ({X:F1}, {Y:F1}, {Z:F1})",
            player.Name, graveyard.Id, graveyard.X, graveyard.Y, graveyard.Z, player.MapId, fromX, fromY, fromZ);
        return true;
    }

    /// <summary>
    /// Relocate a trapped ghost once per death directly onto the visible spirit healer when walking to it (including after relocating to
    /// the graveyard safe location, where a spirit healer such as Mulgore's graveyard 34 stands 12.13 yards away beyond
    /// <see cref="ServerReachProbeYards"/>) also stalled.
    /// </summary>
    private bool TryRelocateToHealer(Player player, Creature? healer, long now)
    {
        if (_healerRelocated || healer is null || Distance(player, healer.X, healer.Y, healer.Z) <= SpiritHealerReachYards)
            return false;
        (float fromX, float fromY, float fromZ) = (player.X, player.Y, player.Z);
        RelocateGhost(player, healer.X, healer.Y, healer.Z, player.Orientation, now);
        NoteRelocation(player, now, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"relocated onto spirit healer {healer.Entry} from {fromX:F1},{fromY:F1},{fromZ:F1}: the walk to the healer stalled"));
        _healerRelocated = true;
        _graveyardRelocated = true;
        LoggerOf().LogInformation(
            "Playerbot {Bot} relocates to spirit healer {Healer} at ({HX:F1}, {HY:F1}, {HZ:F1}) map {Map} after spirit-healer walk stalled at ({X:F1}, {Y:F1}, {Z:F1})",
            player.Name, healer.Entry, healer.X, healer.Y, healer.Z, player.MapId, fromX, fromY, fromZ);
        return true;
    }

    /// <summary>The recovery gave up on its walks and is about to throw <see cref="Stalled"/>: the stall the recovery's own bounds found.</summary>
    private void ReportStalled(Player player, long now, string what)
        => PlayerbotAnomalies.Report(player, "recovery", "stalled",
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{what}: no progress for {(now - _progressMs) / 1000} s over {_thinksSinceProgress} thinks (step {LastStep}); the recovery faults"));

    /// <summary>Relocations within this window (world ms) that make a recovery loop.</summary>
    internal const long RelocationLoopWindowMs = 30 * 60_000;

    /// <summary>Relocations inside <see cref="RelocationLoopWindowMs"/> that make a recovery loop.</summary>
    internal const int RelocationsForLoop = 3;

    private readonly List<long> _relocations = [];

    /// <summary>
    /// A ghost was relocated by the recovery (world uptime <paramref name="now"/>): an Info report that a teleport rescued it, and a Warning
    /// when it is the <see cref="RelocationsForLoop"/>th inside <see cref="RelocationLoopWindowMs"/> (the same recovery over and over; the
    /// history survives <see cref="Reset"/>, which every new death calls).
    /// </summary>
    internal void NoteRelocation(Player player, long now, string what)
    {
        PlayerbotAnomalies.Report(player, "recovery", "teleport-used", what, AnomalySeverity.Info);
        _relocations.RemoveAll(at => now - at > RelocationLoopWindowMs);
        _relocations.Add(now);
        if (_relocations.Count >= RelocationsForLoop)
            PlayerbotAnomalies.Report(player, "recovery", "loop",
                $"the recovery relocated this bot {_relocations.Count} times in {RelocationLoopWindowMs / 60_000} minutes; the last: {what}");
    }

    private void RelocateGhost(Player player, float x, float y, float z, float orientation, long now)
    {
        Invariant.Check(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z),
            $"Ghost relocation coordinates must be finite: ({x}, {y}, {z})");
        StandStill(player);
        float facing = float.IsFinite(orientation) ? orientation : player.Orientation;
        if (session.Services.GetService<TeleportFeature>()?.Teleports is { } teleports
            && teleports.TeleportTo(player, player.MapId, x, y, z, facing))
        {
            teleports.HandleTeleportAck(player, player.Guid.Value);
        }
        else
        {
            // With a teleport service present, falling back to a raw relocation means the teleport was refused (it may keep its state
            // pending); without one (a bare test world) there was nothing to refuse.
            if (session.Services.GetService<TeleportFeature>() is not null)
                PlayerbotAnomalies.Report(player, "recovery", "teleport-refused",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"the teleport to {x:F1},{y:F1},{z:F1} map {player.MapId} was refused; the ghost was relocated directly"));
            player.Relocate(x, y, z, facing, session.World.NowMs);
            player.NeedsVisibilityUpdate = true;
        }

        PlayerbotMotion.Reset(player);
        PlayerbotMotion.ForgetLoops(player);
        MarkProgress(now);
        _detourTriedFor = default;
        _graveyardDetourTriedFor = 0;
        _detourWait.Reset();
        _detourGoal = null;
    }

    private string? _refusal;

    /// <summary>
    /// A CMSG_SPIRIT_HEALER_ACTIVATE the server answered by leaving the bot dead, with the rule that refused it, at Information (once per
    /// reason and death): the handler's own checks (<see cref="QuestNpcServices.SpiritHealerRefusal"/>: vmangos
    /// HandleSpiritHealerActivateOpcode and Player::GetNPCIfCanInteractWith; a ghost, the healer's flag, alive, not hostile, not in
    /// combat, selectable, reputation, interaction range).
    /// </summary>
    private void ReportRefusal(Player player, Creature healer)
    {
        string reason = session.Services.GetService<QuestNpcFeature>()?.Services.SpiritHealerRefusal(player, healer.Guid)
            ?? "no reason found (the resurrection itself was refused)";
        if (reason == _refusal) return;
        _refusal = reason;
        PlayerbotAnomalies.Report(player, "recovery", "healer-refused", $"spirit healer {healer.Entry} refused the activation: {reason}");
        LoggerOf().LogInformation(
            "Playerbot {Bot} could not activate the spirit healer {Healer} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Reason}",
            player.Name, healer.Entry, healer.X, healer.Y, healer.Z, player.MapId, reason);
    }

    /// <summary>The spirit-healer fallback stalled: what it was doing, logged at Information before the fault.</summary>
    private void ReportFault(Player player, Creature? healer, WorldSafeLoc? graveyard, long now, bool battleground)
    {
        Vector3? goal = healer is not null ? new Vector3(healer.X, healer.Y, healer.Z)
            : graveyard is not null ? new Vector3(graveyard.X, graveyard.Y, graveyard.Z) : null;
        string why = NoProgress(now) ? $"no progress for {(now - _progressMs) / 1000} s"
            : _refusedPlans >= MaxRefusedPlans ? $"its plan was refused {_refusedPlans} times in a row"
            : $"its walk stopped closing for {(now - _closingMs) / 1000} s";
        PlayerbotAnomalies.Report(player, "recovery", "spirit-healer-failed",
            $"the spirit-healer fallback gave up: {why}; target {(healer is not null ? "healer " + healer.Entry : graveyard is not null ? "graveyard " + graveyard.Id : "none")}; {(battleground ? "the battleground ghost waits for the match" : $"the ghost stands and tries again in {GiveUpRetryMs / 1000} s")}");
        LoggerOf().LogInformation(
            "Playerbot {Bot} gives up the spirit healer at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Why}; goal {Goal}, {Distance} yd away, route {Route}{Refused}{Refusal}",
            player.Name, player.X, player.Y, player.Z, player.MapId, why,
            healer is not null ? $"healer {healer.Entry}" : graveyard is not null ? $"graveyard {graveyard.Id}" : "none",
            goal is { } g ? Distance(player, g.X, g.Y, g.Z).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "n/a",
            _route is null ? "none" : "planned",
            goal is { } at && PlayerbotMotion.IsBlacklisted(player, at) ? " (the goal is refused after a movement loop)" : string.Empty,
            _refusal is { } refusal ? "; last refusal: " + refusal : string.Empty);
    }

    /// <summary>
    /// Right after the spirit healer revived the bot: an unsafe landing is left at once (<see cref="EscapeLanding"/>). The landing is
    /// where the resurrection puts the bot, not where it stands: when the body's graveyard differs from the ghost's, vmangos
    /// <c>SendSpiritResurrect</c> teleports the revived bot there (<see cref="GraveyardRepopService.TeleportToCorpseGraveyard"/>), a near
    /// teleport pending until the bot acknowledges it (<see cref="TeleportService.DestinationOf"/>). The escape replaces that teleport.
    /// </summary>
    private void LeaveLanding(Player player)
    {
        if (LandingUnsafe is not { } unsafeAt || EscapeLanding is not { } escape) return;
        Vector3 at = new(player.X, player.Y, player.Z);
        if (session.Services.GetService<TeleportFeature>()?.Teleports.DestinationOf(player) is { } destination)
        {
            // A far teleport (another map) is no landing among these creatures; the next think judges the bot where it arrives.
            if (destination.MapId != player.MapId) return;
            at = new Vector3(destination.X, destination.Y, destination.Z);
        }

        if (!unsafeAt(player, at)) return;
        EscapedLanding = escape(player);
        if (!EscapedLanding)
            LoggerOf().LogInformation(
                "Playerbot {Bot} was revived by the spirit healer among creatures too strong for it at ({X:F1}, {Y:F1}, {Z:F1}) map {Map} and could not leave",
                player.Name, at.X, at.Y, at.Z, player.MapId);
    }

    /// <summary>How long a <see cref="KeepBodyOverLanding"/> answer is kept for one body (it is asked at every think of the ghost).</summary>
    private const long KeepBodyRecheckMs = 2_000;

    private (ObjectGuid Body, long AtMs, bool Keep) _keepBody;

    /// <summary>
    /// Whether to keep the corpse run although the body was judged unsafe: the healer's landing (<see cref="SpiritHealerLanding"/>) is
    /// unsafe (<see cref="LandingUnsafe"/>, which looks at the creatures round the landing itself, seen or not) and a spot inside the
    /// reclaim radius of the body is clear of every threat round it (<see cref="FindReviveSpot"/>, its geometry; the corpse run then asks
    /// the navigation mesh). Never asked for a death loop's request. Kept per body for <see cref="KeepBodyRecheckMs"/>; logged once per death.
    /// </summary>
    private bool KeepBodyOverLanding(Player player, Corpse corpse)
    {
        long now = (long)session.World.Uptime.TotalMilliseconds;
        if (_keepBody.Body == corpse.Guid && _keepBody.AtMs >= 0 && now - _keepBody.AtMs < KeepBodyRecheckMs) return _keepBody.Keep;
        bool keep = false;
        Vector3? landingAt = null;
        if (player.Map is { } playerMap && LandingUnsafe is { } unsafeAt && SpiritHealerLanding(player, corpse) is { } landing && unsafeAt(player, landing))
        {
            var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
            List<PlayerbotThreat> hostiles = [.. ThreatsAround(player, body, BodyThreatYards), .. Hazards?.Invoke(player) ?? []];
            keep = FindReviveSpot(body, body, hostiles, candidate => candidate,
                candidate => !PlayerbotWater.Submerged(playerMap, candidate, player.Locomotion.CollisionHeight)) is not null;
            landingAt = landing;
        }

        _keepBody = (corpse.Guid, now, keep);
        if (keep && !BodyKeptOverLanding && landingAt is { } shown)
        {
            BodyKeptOverLanding = true;
            LoggerOf().LogInformation(
                "Playerbot {Bot} keeps its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: the spirit healer would revive it among creatures too strong for it at ({LX:F1}, {LY:F1}, {LZ:F1}), and a spot near the body is clear",
                player.Name, corpse.X, corpse.Y, corpse.Z, corpse.MapId, shown.X, shown.Y, shown.Z);
        }

        return keep;
    }

    /// <summary>
    /// The unsafe-body verdict a body was kept over because the spirit healer would sicken the bot (<see cref="KeepBodyOverSickness"/>),
    /// or None (inspection and tests).
    /// </summary>
    internal PlayerbotUnsafeBody BodyKeptOverSickness { get; private set; }

    /// <summary>
    /// Whether to keep the corpse run although the body was judged unsafe, because the spirit healer would cost the bot resurrection
    /// sickness (its level is at least <see cref="PlayerbotSickness.SicknessLevel"/>, vmangos Player::ResurrectPlayer applySickness: every
    /// stat and all its damage -75% for up to ten minutes) and a quarter of its durability (SendSpiritResurrect, DurabilityLossAll(0.25)),
    /// while a corpse run gives neither. Kept: a repeat death there (<see cref="PlayerbotUnsafeBody.DiedHere"/>: the third is a death loop,
    /// whose request takes the healer anyway, <see cref="TakeSpiritHealer"/>), a body outside the home (<see cref="PlayerbotUnsafeBody.OutsideHome"/>:
    /// the living bot walks home), and a camp too strong for the bot (<see cref="PlayerbotUnsafeBody.TooStrong"/>, judged without its
    /// sickness, <see cref="PlayerbotRisk.TooStrongNear"/>) while a spot inside the reclaim radius is clear of every threat round the
    /// body (<see cref="FindReviveSpot"/>; the corpse run revives the bot there, out of their reach). A strong camp over the whole reclaim
    /// disc is a body that cannot be had: the healer then, as before. The waits of the corpse run keep their bound (<see cref="NoProgressMs"/>).
    /// <para>
    /// Live 2026-10-11 08:53-14:57 UTC (analysis bot-xp-analysis.md, 4.1): 5,789 spirit-healer revivals of population bots in six hours
    /// (7.5 per running bot-hour; 4,633 "too strong", 1,156 "already died there"), 61% of the running level 11+ bots sick at any moment
    /// and 45% of their worn items broken: each healer revival weakened the bot, the weakened bot died again, and took the healer again.
    /// </para>
    /// </summary>
    private (ObjectGuid Body, PlayerbotUnsafeBody Reason, long AtMs, bool Keep) _keepBodySickness;
    private (ObjectGuid Body, long AtMs, bool CanReclaim) _submergedShore;
    private (ObjectGuid Body, int VisibleCount, long AtMs, (PlayerbotUnsafeBody Reason, string? Detail) Verdict) _unsafeBodyVerdict;

    private (PlayerbotUnsafeBody Reason, string? Detail) EvaluateUnsafeBody(
        Player player,
        Corpse corpse,
        Func<Player, Corpse, (PlayerbotUnsafeBody Reason, string? Detail)> judge,
        long now)
    {
        if (WithinReclaimDistance(player, corpse))
            return judge(player, corpse);
        int visible = player.VisibleObjects.Count;
        if (_unsafeBodyVerdict.Body == corpse.Guid && _unsafeBodyVerdict.VisibleCount == visible
            && _unsafeBodyVerdict.AtMs >= 0 && now - _unsafeBodyVerdict.AtMs < KeepBodyRecheckMs)
            return _unsafeBodyVerdict.Verdict;
        (PlayerbotUnsafeBody Reason, string? Detail) verdict = judge(player, corpse);
        _unsafeBodyVerdict = (corpse.Guid, visible, now, verdict);
        return verdict;
    }

    internal bool KeepBodyOverSickness(Player player, Corpse corpse, PlayerbotUnsafeBody reason)
    {
        if (!PlayerbotSickness.HealerSickens(session, player)) return false;
        if (reason == PlayerbotUnsafeBody.DiedHere
            && (PlayerbotSickness.IsSick(session, player) || LastKillerMaxLevel >= (int)player.Level + 3)) return false;
        long now = (long)session.World.Uptime.TotalMilliseconds;
        if (_keepBodySickness.Body == corpse.Guid && _keepBodySickness.Reason == reason
            && _keepBodySickness.AtMs >= 0 && now - _keepBodySickness.AtMs < KeepBodyRecheckMs)
            return _keepBodySickness.Keep;
        bool keep = reason switch
        {
            PlayerbotUnsafeBody.DiedHere or PlayerbotUnsafeBody.OutsideHome => true,
            PlayerbotUnsafeBody.TooStrong => ClearSpotNear(player, corpse),
            PlayerbotUnsafeBody.Hazard => CanReclaimSubmergedFromDryShore(player, corpse),
            PlayerbotUnsafeBody.None => false,
            _ => false,
        };
        _keepBodySickness = (corpse.Guid, reason, now, keep);
        if (keep && BodyKeptOverSickness == PlayerbotUnsafeBody.None)
        {
            BodyKeptOverSickness = reason;
            LoggerOf().LogInformation(
                "Playerbot {Bot} keeps its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map} although {Reason}: the spirit healer would give level {Level} resurrection sickness and a quarter of its durability",
                player.Name, corpse.X, corpse.Y, corpse.Z, corpse.MapId, Describe(reason), player.Level);
        }

        return keep;
    }

    /// <summary>Maximum distance from the corpse at which a dry shoreline spot may be used to reclaim a submerged body.</summary>
    internal const float SafeReviveMaxRadius = ReviveSpotMaxYards - 1f;

    /// <summary>
    /// Whether <paramref name="at"/> lies on loaded terrain with no liquid (<see cref="LiquidStatus.NoWater"/>: dry ground above the
    /// water surface, not merely shallow water).
    /// </summary>
    internal static bool IsDryShore(Map map, Vector3 at)
    {
        TerrainTile tile = map.Terrain.GetTile(at.X, at.Y);
        if (!tile.HasData) return false;
        float ground = tile.GetHeight(at.X, at.Y);
        float z = ground > TerrainTile.InvalidHeight ? ground : at.Z;
        return map.GetLiquidStatus(at.X, at.Y, z, LiquidTypeFlags.AllLiquids, out _) == LiquidStatus.NoWater;
    }

    private static Vector3 GroundAt(Map map, Vector3 at)
    {
        float ground = map.GetHeight(at.X, at.Y, at.Z);
        return ground > TerrainTile.InvalidHeight ? new Vector3(at.X, at.Y, ground) : at;
    }

    /// <summary>
    /// Whether a submerged body (<see cref="PlayerbotUnsafeBody.Hazard"/>) has a dry shoreline revive spot within
    /// <see cref="SafeReviveMaxRadius"/> yards that is clear of threats around the body.
    /// </summary>
    internal bool CanReclaimSubmergedFromDryShore(Player player, Corpse corpse)
    {
        if (player.Map is not { } map) return false;
        long now = (long)session.World.Uptime.TotalMilliseconds;
        if (_submergedShore.Body == corpse.Guid && _submergedShore.AtMs >= 0 && now - _submergedShore.AtMs < KeepBodyRecheckMs)
            return _submergedShore.CanReclaim;
        var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
        List<PlayerbotThreat> hostiles = [.. ThreatsAround(player, body, BodyThreatYards), .. Hazards?.Invoke(player) ?? []];
        bool canReclaim = FindReviveSpot(body, body, hostiles, candidate => GroundAt(map, candidate),
            candidate => Vector2.Distance(new Vector2(candidate.X, candidate.Y), new Vector2(body.X, body.Y)) <= SafeReviveMaxRadius
                && IsDryShore(map, candidate)) is not null;
        _submergedShore = (corpse.Guid, now, canReclaim);
        return canReclaim;
    }

    /// <summary>A spot inside the reclaim radius of the body clear of every threat round it (the geometry of <see cref="FindReviveSpot"/>).</summary>
    private bool ClearSpotNear(Player player, Corpse corpse)
    {
        if (player.Map is not { } playerMap) return false;
        var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
        List<PlayerbotThreat> hostiles = [.. ThreatsAround(player, body, BodyThreatYards), .. Hazards?.Invoke(player) ?? []];
        return FindReviveSpot(body, body, hostiles, candidate => candidate,
            candidate => !PlayerbotWater.Submerged(playerMap, candidate, player.Locomotion.CollisionHeight)) is not null;
    }

    /// <summary>
    /// How far round a place the creatures are looked at by <see cref="ThreatsAround"/>: the reclaim radius and 50 yards more for the
    /// aggro reach of a creature standing outside it (an ArcaneCore bound: a reach is the detection range, 18 yards by default, plus the
    /// level difference, CreatureMapSystem.GetAttackDistance).
    /// </summary>
    internal const float BodyThreatYards = CombatConstants.CorpseReclaimRadius + 50f;
    /// <summary>
    /// Whether <paramref name="route"/> (the mesh's partial answer to a query toward the goal, <see cref="PlayerbotRoute.PartialToGoal"/>; never a region leg, a chunk the mesh answered whole or a hop, which end short on purpose) ends where the goal cannot be closed on foot: the
    /// goal is farther than <see cref="CombatConstants.CorpseReclaimRadius"/> from its end, more than that above or below it (a ghost
    /// reclaims within that radius, and no stretch of it is walkable straight up), and the route did not narrow that vertical gap. On the
    /// real Teldrassil mesh the corpse run from Rut'theran graveyard to a body 438 yards up over the sea planned a route 230 yards out to
    /// sea, flat-closing on the body, and the ghost walked it for 35 s before the no-progress bound ended it. A route that narrows the
    /// gap (a climb, a descent) stays a route. Such a plan is refused, and <see cref="MaxRefusedPlans"/> of them give the body up.
    /// </summary>
    internal static bool LeavesGoalOutOfVerticalReach(PlayerbotRoute route, Vector3 goal)
    {
        if (!route.PartialToGoal || route.Points.Count < 2) return false;
        Vector3 end = route.Points[^1];
        float gap = MathF.Abs(end.Z - goal.Z);
        if (gap <= CombatConstants.CorpseReclaimRadius || Vector3.Distance(end, goal) <= CombatConstants.CorpseReclaimRadius) return false;
        return gap >= MathF.Abs(route.Points[0].Z - goal.Z) - PlayerbotNavigation.PartialProgressYards;
    }

    /// <summary>
    /// Walk towards <paramref name="goal"/> (identified by <paramref name="key"/>) in bounded chunks. Two things count as closing for
    /// the stuck bound (<see cref="StuckMs"/>): a straight-line distance to the goal shorter than any before, and, on the route being
    /// followed, less way left along it (its remaining legs, then straight on to the goal). A route down a switchback leads away from
    /// the goal for a while: on the ridge above Kharanos the first leg from the graveyard runs north-east for 16 yards of distance
    /// and more than 10 seconds before it turns west to Ironwander's body, and the straight-line measure alone judged every such
    /// corpse run stuck and took the spirit healer. Only the straight-line measure is progress for <see cref="NoProgressMs"/>, so
    /// routes that keep leading nowhere still end in the spirit healer.
    /// </summary>
    private bool Walk(Player player, Vector3 goal, object key, uint elapsedMs, long now, bool countsAsProgress = true, bool reachesGoalOnFoot = false)
    {
        if (!Equals(_goal, key))
        {
            ClearWalk();
            _goal = key;
            MarkClosing(now); // a goal the bot cannot even plan a route to counts as stuck after StuckMs
        }

        if (_route is null || _route.Complete)
        {
            PlayerbotThinkProbe.Phase("recovery walk plan");
            PlanOutcome planned = PlayerbotNavigation.PlanToward(player, goal, options, ref _walkWait, session.World.NowMs, out PlayerbotRoute? walkRoute);
            _route = walkRoute;
            if (planned != PlanOutcome.Route)
            {
                _route = null;
                // A tile of the way is still being read: the walk is waiting, not stuck (the stuck clock is paused for the wait's bound).
                // A refusal counts toward MaxRefusedPlans, which the callers turn into giving the goal up.
                if (planned == PlanOutcome.Pending) MarkClosing(now);
                else _refusedPlans++;
                return false;
            }

            if (reachesGoalOnFoot && _route is { } plannedRoute && LeavesGoalOutOfVerticalReach(plannedRoute, goal))
            {
                _route = null;
                _refusedPlans++;
                return false;
            }

            _refusedPlans = 0;

            // A fresh route sets its own yardstick; only walking it down counts, so replanning on the spot is not closing.
            _bestRouteLeft = float.PositiveInfinity;
            _detourRoute = false;
        }

        if (_route is not { } route) return false;
        PlayerbotThinkProbe.Phase("recovery walk advance");
        bool advanced = PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, session.World.NowMs);
        float left = RouteLeft(player, route, goal);
        if (float.IsFinite(left))
        {
            if (float.IsFinite(_bestRouteLeft) && left < _bestRouteLeft - RouteClosingYards)
            {
                MarkClosing(now);
                // On the long way round to a healer (DetourToHealer) the straight line grows for a while: walking the one planned
                // route down is progress. It is planned once per death, so this cannot keep a ghost walking for good.
                if (_detourRoute && countsAsProgress) MarkProgress(now);
            }
            if (left < _bestRouteLeft - RouteClosingYards || !float.IsFinite(_bestRouteLeft)) _bestRouteLeft = left;
        }

        if (!advanced) _route = null;
        float after = Distance(player, goal.X, goal.Y, goal.Z);
        if (float.IsFinite(after) && after < _bestDistance - 0.01f)
        {
            _bestDistance = after;
            MarkClosing(now);
            if (countsAsProgress) MarkProgress(now);
        }

        return advanced;
    }

    /// <summary>Stop walking (the bot is where it needs to be, or waiting): true when it stands still.</summary>
    private bool StandStill(Player player)
    {
        ClearWalk();
        return PlayerbotMovementControl.Stop(session, player);
    }

    private void ClearWalk()
    {
        _walkWait.Reset();
        _refusedPlans = 0;
        _detourRoute = false;
        _route = null;
        _goal = null;
        _closingMs = -1;
        _thinksSinceClosing = 0;
        _bestDistance = float.PositiveInfinity;
        _bestRouteLeft = float.PositiveInfinity;
    }

    /// <summary>Way left along a route that counts as closing: more than a heartbeat's jitter.</summary>
    internal const float RouteClosingYards = 0.5f;

    /// <summary>
    /// The way left along <paramref name="route"/> from where the bot is now (its motion's current position): to the next corner,
    /// along the remaining legs, then straight on to <paramref name="goal"/> (a partial route ends short of it).
    /// </summary>
    internal static float RouteLeft(Player player, PlayerbotRoute route, Vector3 goal)
        => RouteLeft(PlayerbotMotion.CurrentPosition(player), route, goal);

    /// <summary>The way left along <paramref name="route"/> from <paramref name="from"/>, free of world state.</summary>
    internal static float RouteLeft(Vector3 from, PlayerbotRoute route, Vector3 goal)
    {
        float left = 0;
        Vector3 at = from;
        for (int index = Math.Max(1, route.NextPoint); index < route.Points.Count; index++)
        {
            left += Vector3.Distance(at, route.Points[index]);
            at = route.Points[index];
        }

        return left + Vector3.Distance(at, goal);
    }

    /// <summary>
    /// The entrance a ghost on its current map walks into to reach a body on <paramref name="corpseMap"/>: the nearest trigger here
    /// whose teleport leads to that map, else to a dungeon it is nested in (the server then sends the ghost on to the body's
    /// dungeon, <c>GhostEntryRules</c>). Null when there is none on this map.
    /// </summary>
    internal AreaTriggerTemplate? FindEntrance(Player player, uint corpseMap)
    {
        WorldMaps maps = WorldMaps.Of(session.World);
        IReadOnlyList<AreaTriggerTemplate> here = PlayerbotAreaTriggers.OnMap(maps, player.MapId);
        if (here.Count == 0) return null;
        var seen = new HashSet<uint>();
        for (uint target = corpseMap; target != 0 && seen.Add(target);)
        {
            AreaTriggerTemplate? nearest = here
                .Where(t => maps.FindAreaTriggerTeleport(t.Id)?.TargetMap == target)
                .OrderBy(t => Distance(player, t.X, t.Y, t.Z))
                .FirstOrDefault();
            if (nearest is not null) return nearest;
            target = maps.Registry.Find(target) is { IsDungeon: true } dungeon ? dungeon.Parent : 0;
        }

        return null;
    }

    /// <summary>
    /// Whether a hostile creature would attack the bot revived where the ghost stands now (CMSG_RECLAIM_CORPSE revives it in
    /// place, vmangos MiscHandler.cpp:599): the ghost is inside a threat's aggro radius plus <see cref="CampMarginYards"/>.
    /// </summary>
    internal static bool Camped(Player player) => Camped(player, []);

    /// <param name="extra">More places to keep out of: the risk's hazards (remembered deaths and retreats, creatures that kill outright).</param>
    internal static bool Camped(Player player, IEnumerable<PlayerbotThreat> extra)
    {
        var here = new Vector3(player.X, player.Y, player.Z);
        foreach (PlayerbotThreat threat in Threats(player).Concat(extra))
            if (threat.Reaches(here, CampMarginYards))
                return true;
        return false;
    }

    /// <summary>
    /// The creatures the ghost can see that would attack it once revived, each with its aggro radius against this player, by the
    /// server's own on-sight rules (<see cref="CreatureMapSystem.CanAggroOnSight"/>, vmangos BasicAI::MoveInLineOfSight): alive,
    /// hostile to the player, able to initiate an attack (react state aggressive: not a passive or NO_AGGRO creature, not
    /// stunned, pacified or unselectable), proximity aggro allowed for it (not a creature that only attacks PvP-flagged players),
    /// and a radius of <see cref="CreatureMapSystem.GetAttackDistance"/>: the template's detection range (18 by default) less the
    /// level difference, never under 5, times the aggro rate; a low-level creature reaches less far, a higher one further. Without
    /// a creature system on the map every hostile counts with <see cref="HostileClearYards"/>.
    /// </summary>
    internal static List<PlayerbotThreat> Threats(Player player)
    {
        if (player.Map is not { } map) return [];
        return Threats(player, map, player.VisibleObjects.Select(map.FindObject).OfType<Creature>());
    }

    /// <summary>
    /// <see cref="Threats(Player)"/> for the creatures of the map within <paramref name="radius"/> yards (2D) of <paramref name="at"/>,
    /// whether the bot sees them or not: a spirit healer's landing at a graveyard out of the ghost's sight, or the body from afar. The
    /// creature system's own spawns (a linear pass over the map's creatures: asked once per resurrection, and per body at most every
    /// <see cref="KeepBodyRecheckMs"/>); without a creature system, the visible creatures.
    /// </summary>
    internal static List<PlayerbotThreat> ThreatsAround(Player player, Vector3 at, float radius)
    {
        if (player.Map is not { } map) return [];
        if (map.FindUpdater<CreatureMapSystem>() is not { } system) return Threats(player);
        // The grid cells round the place, not every creature of the continent (a per-bot scan of ~10,000 creatures; Oct 17 live sample).
        List<Creature> near = t_threatCandidates ??= [];
        near.Clear();
        try
        {
            system.CollectCreaturesNear(at.X, at.Y, radius, near);
            return Threats(player, map, near);
        }
        finally
        {
            near.Clear();
        }
    }

    [ThreadStatic]
    private static List<Creature>? t_threatCandidates;

    private static List<PlayerbotThreat> Threats(Player player, Map map, IEnumerable<Creature> candidates)
    {
        var found = new List<PlayerbotThreat>();
        CreatureMapSystem? system = map.FindUpdater<CreatureMapSystem>();
        foreach (Creature creature in candidates) AddThreat(player, map, system, creature, found);
        return found;
    }

    /// <summary>
    /// <see cref="Threats(Player)"/> into <paramref name="found"/> (cleared first), without a new list or an enumerator allocation: the
    /// hazard scan of every bot every 2 seconds (<see cref="PlayerbotRisk.ScanHazards"/>). Adds the stealthed creatures of the map
    /// within <see cref="StealthedHazardYards"/> that the bot does not see; only this overload does (the hazard scan, not the target choice).
    /// </summary>
    internal static void Threats(Player player, List<PlayerbotThreat> found)
    {
        found.Clear();
        if (player.Map is not { } map) return;
        CreatureMapSystem? system = map.FindUpdater<CreatureMapSystem>();
        foreach (ObjectGuid guid in player.VisibleObjects)
            if (map.FindObject(guid) is Creature creature) AddThreat(player, map, system, creature, found);

        // The stealth visibility rule keeps undetected stealthed creatures out of the visible objects, yet they aggro whoever walks into
        // their reach: the ones of this map within StealthedHazardYards count as threats too. Hazards only: the target choice and every
        // other reader use Threats(Player) / the visible objects, so the bot never attacks what it cannot see.
        if (StealthServices.Find(map) is not { Registry.Count: > 0 } stealth) return;
        float radiusSq = StealthedHazardYards * StealthedHazardYards;
        HashSet<Unit>.Enumerator hidden = stealth.Registry.EnumerateStealthed();
        while (hidden.MoveNext())
        {
            if (hidden.Current is not Creature creature || !ReferenceEquals(creature.Map, map)) continue;
            float dx = creature.X - player.X, dy = creature.Y - player.Y;
            if (((dx * dx) + (dy * dy)) <= radiusSq && !player.VisibleObjects.Contains(creature.Guid))
                AddThreat(player, map, system, creature, found);
        }
    }

    /// <summary>
    /// How far round the bot (2D) the hazard scan looks for stealthed creatures (<see cref="Threats(Player, List{PlayerbotThreat})"/>):
    /// the stealth visibility rule hides them from the bot's visible objects, yet they attack whoever walks into their reach (Wildthorn
    /// Lurker 3821 Hide 6920, Ridge Stalker 2731 Sneak 22766). 50 yards covers the aggro reach of any ordinary creature (the 18-yard
    /// detection range plus the level difference) with the room a route needs to turn.
    /// </summary>
    internal const float StealthedHazardYards = 50f;

    /// <summary>The creature is attacking <paramref name="player"/> or holds it on its threat list: in combat with it, not merely near it.</summary>
    private static bool IsEngagedWith(Creature creature, Player player)
        => ReferenceEquals(creature.Combat.Victim, player)
            || player.Combat.Attackers.Contains(creature)
            || player.Combat.ThreatenedBy.Contains(creature);

    private static void AddThreat(Player player, Map map, CreatureMapSystem? system, Creature creature, List<PlayerbotThreat> found)
    {
        if (!creature.IsInWorld || !creature.IsAlive
            || !ReferenceEquals(creature.Map, map) || !map.Combat.Hooks.IsHostileTo(creature, player))
            return;

        // A civilian, or a creature that does not attack on sight, is no camp of a body or a hold on the bot's rest: it counts only once it
        // is actually attacking the bot or fighting it (a passive creature that was struck, a Reactor that fights back).
        bool engaged = IsEngagedWith(creature, player);
        if (!engaged && (PlayerbotTargetFilter.IsCivilian(creature) || !PlayerbotTargetFilter.AggroesOnSight(creature)))
            return;
        var at = new Vector3(creature.X, creature.Y, creature.Z);
        if (system is null)
        {
            found.Add(new PlayerbotThreat(at, HostileClearYards, IgnoresHeight: true, Radii: 0, creature));
            return;
        }

        if (engaged
            ? (creature.UnitFlags & (UnitFlags.Spawning | UnitFlags.NotSelectable)) != 0
            : !system.CanInitiateAttack(creature) || !system.IsProximityAggroAllowedFor(creature, player))
            return;
        float radii = creature.BoundingRadius + player.BoundingRadius;
        float radius = system.GetAttackDistance(creature, player) + (system.Options.AggroUsesBoundingRadius ? radii : 0f);
        if (radius <= 0) return;
        bool flyer = (creature.Template.InhabitType & 0x04) != 0; // INHABIT_AIR: no height limit
        found.Add(new PlayerbotThreat(at, radius, flyer, radii, creature));
    }

    /// <summary>
    /// The revive spot to walk to while a hostile camps the ghost: the one chosen before while it is still clear and inside the
    /// reclaim radius, else a new one (<see cref="FindReviveSpot"/>) found with the navigation mesh. Null when there is none.
    /// </summary>
    private Vector3? ChooseReviveSpot(Player player, Corpse corpse)
    {
        if (player.Map is null) return null;
        var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
        List<PlayerbotThreat> hostiles = [.. Threats(player), .. Hazards?.Invoke(player) ?? []];
        bool bodySubmerged = PlayerbotWater.Submerged(player.Map, body, player.Locomotion.CollisionHeight);
        bool Dry(Vector3 candidate) => bodySubmerged
            ? Vector2.Distance(new Vector2(candidate.X, candidate.Y), new Vector2(body.X, body.Y)) <= SafeReviveMaxRadius
                && IsDryShore(player.Map, candidate)
            : !PlayerbotWater.Submerged(player.Map, candidate, player.Locomotion.CollisionHeight);
        if (_spot is { } kept && IsReviveSpot(kept, body, hostiles, Dry)) return kept;

        Vector3? found = FindReviveSpot(new Vector3(player.X, player.Y, player.Z), body, hostiles, candidate =>
        {
            // The mesh finds the floor: a candidate is a place on the ring at the body's height, and the route ends on the
            // nearest walkable point (a candidate off the mesh ends short of it, which IsReviveSpot then re-checks). A candidate whose
            // tiles are still being read answers no point, as a refusal does: no spot is known yet (WaitForHostiles, not progress), the
            // search is made again next think, and nothing is set aside here that a tile arriving would not reopen.
            Vector3 target = bodySubmerged ? GroundAt(player.Map, candidate) : candidate;
            if (!PlayerbotNavigation.TryPlan(player, target, options, out PlayerbotRoute? route, nearestEnd: true) || route is null
                || route.Distance > ReviveSpotMaxWalkYards)
                return null;
            return route.Points[^1];
        }, Dry);
        if (found is null)
        {
            _spot = null;
            return null;
        }

        _spot = found;
        _spotKey = new object();
        return found;
    }

    private static bool IsReviveSpot(Vector3 spot, Vector3 body, IReadOnlyList<PlayerbotThreat> hostiles,
        Func<Vector3, bool>? safe = null)
    {
        if (safe is not null && !safe(spot)) return false;
        if (Vector3.Distance(spot, body) > ReviveSpotMaxYards) return false;
        for (int i = 0; i < hostiles.Count; i++) // indexed: a foreach over the interface boxed an enumerator per candidate
            if (hostiles[i].Reaches(spot, ReviveSpotMarginYards + ReviveSpotSlackYards)) return false;
        return true;
    }

    /// <summary>
    /// A place to reclaim a camped body from, free of world state: candidates on rings round the body, within
    /// <see cref="ReviveSpotMaxYards"/> of it (the server revives within <see cref="CombatConstants.CorpseReclaimRadius"/>), out of
    /// every threat's reach plus <see cref="ReviveSpotMarginYards"/> and <see cref="ReviveSpotSlackYards"/>, and passing
    /// <paramref name="safe"/> when given (dry ground), nearest to the ghost first. <paramref name="reach"/> maps a
    /// candidate to the point the ghost can actually walk to (the navigation mesh's, null when it cannot); the first reachable
    /// point that is still a revive spot is taken. At most <see cref="ReviveSpotMaxQueries"/> candidates are asked.
    /// </summary>
    internal static Vector3? FindReviveSpot(Vector3 ghost, Vector3 body, IReadOnlyList<PlayerbotThreat> hostileList,
        Func<Vector3, Vector3?> reach, Func<Vector3, bool>? safe = null)
    {
        // Pooled scratch (this runs for every camped ghost on every recovery step): candidates in ring order, then the passing
        // ones sorted nearest-to-ghost first, ties kept in ring order as the former stable OrderBy did.
        int max = 1 + ((int)((ReviveSpotMaxYards - 1f) / ReviveSpotRingYards) + 1) * ReviveSpotAngles;
        (float Distance, int Order, Vector3 Spot)[] passing = System.Buffers.ArrayPool<(float, int, Vector3)>.Shared.Rent(max);
        try
        {
            int count = 0;
            Vector2 ghostFlat = new(ghost.X, ghost.Y);
            void Consider(Vector3 c, ref int n)
            {
                if (IsReviveSpot(c, body, hostileList, safe))
                {
                    passing[n] = (Vector2.Distance(new Vector2(c.X, c.Y), ghostFlat), n, c);
                    n++;
                }
            }

            Consider(body, ref count);
            // Rings every few yards, finely spaced: a camp can leave only a thin slice of the reclaim disc clear.
            for (float radius = ReviveSpotRingYards; radius <= ReviveSpotMaxYards - 1f; radius += ReviveSpotRingYards)
                for (int step = 0; step < ReviveSpotAngles; step++)
                {
                    float angle = step * 2f * MathF.PI / ReviveSpotAngles;
                    if (count < passing.Length)
                        Consider(new Vector3(body.X + (MathF.Cos(angle) * radius), body.Y + (MathF.Sin(angle) * radius), body.Z), ref count);
                }

            Array.Sort(passing, 0, count, ReviveSpotOrder.Instance);
            int queries = 0;
            for (int i = 0; i < count; i++)
            {
                if (++queries > ReviveSpotMaxQueries) break;
                if (reach(passing[i].Spot) is { } point && IsReviveSpot(point, body, hostileList, safe)) return point;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<(float, int, Vector3)>.Shared.Return(passing);
        }

        return null;
    }

    private bool InBattleground(Player player)
        => AreaSpiritGuides?.Invoke(player) ?? BattlegroundManager.TypeOfMap(player.MapId) != BattlegroundType.None;

    /// <summary>
    /// The battleground ghost's way back: walk within <see cref="AreaSpiritGuideRangeYards"/> of the spirit guide, then stand there, queued
    /// for its wave. The release already gave a participant Waiting to Resurrect (vmangos Player::BuildPlayerRepop); a ghost without it
    /// asks the guide (CMSG_AREA_SPIRIT_HEALER_QUEUE). Waiting in range is progress: the wave comes every
    /// <see cref="AreaResurrectionIntervalMs"/>; a wait of three intervals is reported once, never thrown. A walk to the guide that stalls
    /// leaves that guide to the ordinary fallback for this death.
    /// </summary>
    private bool AwaitAreaResurrection(Player player, Creature guide, uint elapsedMs, long now)
    {
        LastStep = PlayerbotRecoveryStep.SpiritHealer;
        float distance = Distance(player, guide.X, guide.Y, guide.Z);
        if (distance > AreaSpiritGuideRangeYards)
        {
            _areaWaitSinceMs = -1;
            if (Stuck(now))
            {
                _areaGuideWalkStalled = guide.Guid;
                ClearWalk();
                MarkProgress(now);
                LoggerOf().LogInformation(
                    "Playerbot {Bot} cannot reach the spirit guide {Guide} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map} from {Distance:F1} yd: the ordinary fallback goes on",
                    player.Name, guide.Entry, guide.X, guide.Y, guide.Z, player.MapId, distance);
                return false;
            }

            LastSpiritHealerStep = PlayerbotSpiritHealerStep.WalkToHealer;
            return Walk(player, new Vector3(guide.X, guide.Y, guide.Z), guide.Guid, elapsedMs, now);
        }

        LastSpiritHealerStep = PlayerbotSpiritHealerStep.AwaitAreaResurrection;
        StandStill(player);
        MarkProgress(now);
        if (_areaWaitSinceMs < 0) _areaWaitSinceMs = now;
        ArcaneCore.Game.Spells.SpellSystem? spells = session.Services.GetService<ArcaneCore.World.Spells.SpellFeature>()?.System;
        if (AreaSpiritGuides is null && spells is not null)
        {
            if (!spells.IsChanneling(guide) && !spells.HasAura(guide, BattlegroundConstants.SpellSpiritHealChannel))
            {
                ArcaneCore.Game.Spells.SpellCastResult ch = spells.CastSpell(
                    guide,
                    BattlegroundConstants.SpellSpiritHealChannel,
                    ArcaneCore.Game.Spells.SpellCastTargets.ForSelf(),
                    triggered: true);
                if (ch != ArcaneCore.Game.Spells.SpellCastResult.CastOk && !spells.HasAura(guide, BattlegroundConstants.SpellSpiritHealChannel))
                    spells.AddAura(guide, BattlegroundConstants.SpellSpiritHealChannel, permanent: false);
            }
            bool hasSpiritHealWave = spells.Store.Get(SpiritGuideService.SpellSpiritHeal) is not null
                || session.Services.GetService<ArcaneCore.World.Battlegrounds.BattlegroundFeature>()?.BattlegroundOf(player.Guid) is null;
            if (hasSpiritHealWave
                && (WaitingToResurrect(player) || _areaQueueSentMs >= 0)
                && now - _areaWaitSinceMs >= AreaResurrectionIntervalMs)
            {
                spells.CastSpell(guide, SpiritGuideService.SpellSpiritHeal, ArcaneCore.Game.Spells.SpellCastTargets.ForSelf(), triggered: true);
                if (!player.IsAlive)
                {
                    player.Map?.Combat.ResurrectBySpiritGuide(
                        player,
                        healed => spells.RemoveAuras(healed, BattlegroundConstants.SpellWaitingToResurrect));
                }

                if (player.IsAlive)
                {
                    _areaWaitSinceMs = -1;
                    return true;
                }
            }
        }

        if (!_areaWaitReported && now - _areaWaitSinceMs > 3 * AreaResurrectionIntervalMs)
        {
            _areaWaitReported = true;
            PlayerbotAnomalies.Report(player, "recovery", "area-resurrection-late", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"waited {(now - _areaWaitSinceMs) / 1000} s within {distance:F1} yd of spirit guide {guide.Entry} without a resurrection wave"));
        }

        if (WaitingToResurrect(player) || (_areaQueueSentMs >= 0 && now - _areaQueueSentMs < AreaQueueRetryMs))
            return false;
        _areaQueueSentMs = now;
        if (!session.TryManagedAction(WorldOpcode.CmsgAreaSpiritHealerQueue, PlayerbotNavigation.GuidPayload(guide.Guid.Value)))
            return false;
        if (AreaSpiritGuides is null && spells is not null && !WaitingToResurrect(player))
        {
            ArcaneCore.Game.Spells.SpellCastResult q = spells.CastSpell(
                player,
                BattlegroundConstants.SpellWaitingToResurrect,
                ArcaneCore.Game.Spells.SpellCastTargets.ForSelf(),
                triggered: true);
            if (q != ArcaneCore.Game.Spells.SpellCastResult.CastOk && !WaitingToResurrect(player))
                spells.AddAura(player, BattlegroundConstants.SpellWaitingToResurrect, permanent: true);
        }
        AreaQueueRequests++;
        return true;
    }

    private bool WaitingToResurrect(Player player)
        => session.Services.GetService<ArcaneCore.World.Spells.SpellFeature>()?.System is { } spells
            && spells.HasAura(player, BattlegroundConstants.SpellWaitingToResurrect);

    /// <summary>The nearest living battleground spirit guide the ghost can see (<see cref="NpcFlags.SpiritGuide"/>).</summary>
    private static Creature? NearestSpiritGuide(Player player)
    {
        if (player.Map is not { } map) return null;
        Creature? nearest = null;
        float best = float.PositiveInfinity;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature || !creature.IsInWorld || !creature.IsAlive
                || !ReferenceEquals(creature.Map, map) || (creature.NpcFlags & (uint)NpcFlags.SpiritGuide) == 0
                || !ArcaneCore.Game.Death.Visibility.GhostVisibilityRule.IsSpiritGuideFriendlyTo(player, creature))
                continue;
            float distance = Distance(player, creature.X, creature.Y, creature.Z);
            if (distance < best)
            {
                best = distance;
                nearest = creature;
            }
        }

        return nearest;
    }

    /// <summary>The nearest living spirit healer the ghost can see (ghosts see the spirit services across the map's view range).</summary>
    private static Creature? NearestSpiritHealer(Player player)
    {
        if (player.Map is not { } map) return null;
        Creature? nearest = null;
        float best = float.PositiveInfinity;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature || !creature.IsInWorld || !creature.IsAlive
                || !ReferenceEquals(creature.Map, map) || (creature.NpcFlags & (uint)NpcFlags.SpiritHealer) == 0)
                continue;
            float distance = Distance(player, creature.X, creature.Y, creature.Z);
            if (distance < best)
            {
                best = distance;
                nearest = creature;
            }
        }

        return nearest;
    }

    /// <summary>The graveyard of the ghost's position on its own map (a spirit healer stands at it), or null.</summary>
    private WorldSafeLoc? Graveyard(Player player) => GraveyardAt(player, player.X, player.Y, player.Z);

    /// <summary>
    /// The graveyard the spirit-healer fallback walks to: the one of the ghost's position, else the one it was already walking to in
    /// this death, else the body's (where the resurrection sends the bot, vmangos SendSpiritResurrect), else null. The spirit healer
    /// stands at the graveyard whatever the ghost's own cell says: a ghost crossing cells with no area (zone 0, nothing linked, as on
    /// the hills between the Barrens and Ashenvale) keeps its goal instead of standing and faulting there.
    /// </summary>
    private WorldSafeLoc? HealerGraveyard(Player player)
    {
        Corpse? corpse = player.Combat.Corpse;
        ObjectGuid body = corpse?.Guid ?? default;
        WorldSafeLoc? graveyard = Graveyard(player);
        // Kept for this body only (a death and a revival can fall between two thinks, Reset unseen), and on this map only.
        if (graveyard is null && _healerGraveyard is { Loc: { } kept } held && held.Body == body && kept.MapId == player.MapId)
            graveyard = kept;
        if (graveyard is null && corpse is not null && corpse.MapId == player.MapId && ReferenceEquals(corpse.Map, player.Map))
            graveyard = GraveyardAt(player, corpse.X, corpse.Y, corpse.Z);
        if (graveyard is not null) _healerGraveyard = (body, graveyard);
        return graveyard;
    }

    /// <summary>The graveyard the spirit-healer fallback last walked to, and the body of the death it was for (<see cref="HealerGraveyard"/>).</summary>
    private (ObjectGuid Body, WorldSafeLoc? Loc) _healerGraveyard;

    /// <summary>The healer whose long route (<see cref="PlayerbotSpiritHealerStep.DetourToHealer"/>) was tried in this death.</summary>
    private ObjectGuid _detourTriedFor;

    /// <summary>The graveyard whose long route (<see cref="PlayerbotSpiritHealerStep.DetourToHealer"/>) was tried in this death.</summary>
    private uint _graveyardDetourTriedFor;

    /// <summary>Whether the ghost was already relocated once to the graveyard's safe location in this death (<see cref="TryRelocateToGraveyard"/>).</summary>
    private bool _graveyardRelocated;

    /// <summary>Whether the ghost was already relocated once onto the spirit healer in this death (<see cref="TryRelocateToHealer"/>).</summary>
    private bool _healerRelocated;

    /// <summary>The healer the long route leads to, kept while it is out of sight (<see cref="PlayerbotSpiritHealerStep.DetourToHealer"/>).</summary>
    private (ObjectGuid Guid, uint Map, Vector3 At)? _detourGoal;

    /// <summary>Whether the route being walked is the long one to a healer (<see cref="PlayerbotSpiritHealerStep.DetourToHealer"/>).</summary>
    private bool _detourRoute;

    /// <summary>The body this recovery saw released (a different one, or none while dead, is a new death the brain's reset missed).</summary>
    private ObjectGuid _deathBody;

    /// <summary>
    /// Closer than this the server itself is asked whether it would accept CMSG_SPIRIT_HEALER_ACTIVATE from here
    /// (<see cref="QuestNpcServices.SpiritHealerRefusal"/>): vmangos GetNPCIfCanInteractWith takes INTERACTION_DISTANCE (5 yards), in 3D,
    /// plus both bounding radii (Player.cpp:2524, Object.cpp IsWithinDist).
    /// </summary>
    internal const float ServerReachProbeYards = 8f;

    /// <summary>
    /// Whether the server's own checks would accept CMSG_SPIRIT_HEALER_ACTIVATE for <paramref name="healer"/> from where the ghost stands
    /// (a healer on a ledge the mesh does not quite reach): asked within <see cref="ServerReachProbeYards"/> only.
    /// </summary>
    private bool ServerAccepts(Player player, Creature healer)
        => Distance(player, healer.X, healer.Y, healer.Z) <= ServerReachProbeYards
            && session.Services.GetService<QuestNpcFeature>()?.Services.SpiritHealerRefusal(player, healer.Guid) is null;

    /// <summary>The body the spirit-healer fallback already went back to (<see cref="PlayerbotSpiritHealerStep.ResumeCorpseRun"/>), once per death.</summary>
    private ObjectGuid _corpseRunResumedFor;

    /// <summary>The graveyard of a position on the ghost's own map, or null.</summary>
    private WorldSafeLoc? GraveyardAt(Player player, float x, float y, float z)
    {
        if (player.Map is not { } map) return null;
        if (GraveyardLookup is { } lookup) return lookup(player, new Vector3(x, y, z)) is { } seen && seen.MapId == map.MapId ? seen : null;
        GraveyardCatalog catalog = WorldGraveyards.Of(session.World).Catalog;
        if (catalog.SafeLocCount == 0) return null;
        (uint zone, uint area) = map.GetZoneAndAreaId(x, y, z);
        uint team = player.Team == Team.Alliance ? GraveyardCatalog.TeamAlliance : GraveyardCatalog.TeamHorde;
        WorldSafeLoc? graveyard = GraveyardSelector.FindClosest(catalog, WorldMaps.Of(session.World).Registry, map.MapId,
            x, y, z, zone, area, team);
        return graveyard?.MapId == map.MapId ? graveyard : null;
    }

    /// <summary>
    /// Where the spirit healer would leave the bot whose body lies on its map: the graveyard of the body's position, which the
    /// resurrection teleports to (vmangos SendSpiritResurrect, NPCHandler.cpp:430-471: the corpse's graveyard when it differs from the
    /// ghost's; <see cref="GraveyardRepopService.TeleportToCorpseGraveyard"/>), else the nearest spirit healer the ghost sees (no
    /// graveyard: the bot stays at the healer). Null when neither is known. The body's graveyard is looked up once per body.
    /// <para>
    /// A prediction through <see cref="GraveyardSelector.FindClosest"/>, the selector the server's chooser starts from: the server
    /// may in addition take a battleground's own graveyards (no solo bot recovers there), a run-time outdoor PvP link nearer the body,
    /// or, with <c>World:Death:GraveyardFallbackToDefaults</c>, the faction's default graveyard when nothing is linked (here: no
    /// graveyard, so the healer in sight). Those can only move the landing; a landing off the home map is never judged nearer home.
    /// </para>
    /// </summary>
    internal Vector3? SpiritHealerLanding(Player player, Corpse corpse)
    {
        var body = new Vector2(corpse.X, corpse.Y);
        if (_landing.Body != corpse.Guid || _landing.At != body)
        {
            WorldSafeLoc? graveyard = GraveyardAt(player, corpse.X, corpse.Y, corpse.Z);
            _landing = (corpse.Guid, body, graveyard is null ? null : new Vector3(graveyard.X, graveyard.Y, graveyard.Z));
        }

        if (_landing.Landing is { } landing) return landing;
        return NearestSpiritHealer(player) is { } healer ? new Vector3(healer.X, healer.Y, healer.Z) : null;
    }

    /// <summary>The cell size the cached graveyard of the ghost's position is kept for (<see cref="HealerOutcomeKnown"/>).</summary>
    private const float GraveyardCellYards = 50f;

    private (ObjectGuid Body, Vector2 At, int CellX, int CellY, bool Known) _ghostGraveyard;
    private (ObjectGuid Body, Vector2 At, Vector3? Landing) _landing;

    /// <summary>
    /// Whether the spirit-healer fallback has something to go to from here: a healer in sight, or a graveyard for the ghost's position
    /// (the inputs of <see cref="DecideSpiritHealer"/>). The graveyard lookup (zone and area, then the selector) is kept per body and
    /// per <see cref="GraveyardCellYards"/> cell of the ghost's position, not repeated at every think.
    /// </summary>
    private bool HealerOutcomeKnown(Player player, Corpse corpse)
    {
        if (NearestSpiritHealer(player) is not null) return true;
        int cellX = (int)MathF.Floor(player.X / GraveyardCellYards), cellY = (int)MathF.Floor(player.Y / GraveyardCellYards);
        var body = new Vector2(corpse.X, corpse.Y);
        if (_ghostGraveyard.Body != corpse.Guid || _ghostGraveyard.At != body || _ghostGraveyard.CellX != cellX || _ghostGraveyard.CellY != cellY)
            _ghostGraveyard = (corpse.Guid, body, cellX, cellY, Graveyard(player) is not null);
        return _ghostGraveyard.Known;
    }

    private static string Describe(PlayerbotUnsafeBody reason) => reason switch
    {
        PlayerbotUnsafeBody.DiedHere => "it already died there",
        PlayerbotUnsafeBody.TooStrong => "a creature there is too strong for it",
        PlayerbotUnsafeBody.Hazard => "the body is submerged",
        PlayerbotUnsafeBody.OutsideHome => "the body lies outside its home and the healer is nearer home",
        PlayerbotUnsafeBody.None => "none",
        _ => "none",
    };

    private ILogger? _logger;

    private ILogger LoggerOf()
        => _logger ??= session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotRecovery>() ?? NullLogger<PlayerbotRecovery>.Instance;

    private static bool WithinReclaimDistance(Player player, Corpse corpse)
    {
        float max = CombatConstants.CorpseReclaimRadius + corpse.BoundingRadius + player.BoundingRadius;
        float distance = Distance(player, corpse.X, corpse.Y, corpse.Z);
        return float.IsFinite(distance) && distance < max;
    }

    private static float Distance(WorldObject from, float x, float y, float z)
        => MathF.Sqrt(MathF.Pow(from.X - x, 2) + MathF.Pow(from.Y - y, 2) + MathF.Pow(from.Z - z, 2));
}

/// <summary>
/// A creature that would attack a revived bot (<see cref="PlayerbotRecovery.Threats"/>): where it stands, its aggro radius against
/// the bot, whether it is free of the 3-yard height limit (a flyer), and the bounding radii the height limit takes off.
/// </summary>
internal readonly record struct PlayerbotThreat(Vector3 Position, float Radius, bool IgnoresHeight, float Radii, Creature? Source = null)
{
    /// <summary>
    /// Whether a bot standing at <paramref name="spot"/> is inside this creature's aggro reach plus <paramref name="margin"/>
    /// (vmangos BasicAI::MoveInLineOfSight via CreatureMapSystem.IsInAggroReach: plain 3D distance, strictly inside; a unit more
    /// than CREATURE_Z_ATTACK_RANGE above or below a creature that cannot fly is not aggroed).
    /// </summary>
    internal bool Reaches(Vector3 spot, float margin)
    {
        if (!IgnoresHeight && MathF.Max(0f, MathF.Abs(Position.Z - spot.Z) - Radii) > CreatureAggro.MaxZDistance) return false;
        return Vector3.Distance(Position, spot) < Radius + margin;
    }

    /// <summary>
    /// Whether this creature's aggro reach and <paramref name="other"/>'s share a zone, that is, some spot is inside both: the
    /// distance between them is under the sum of the radii (and, for two creatures bound by the height limit, within it).
    /// Neither creature has to stand inside the other's reach for a bot walking between them to be attacked by both.
    /// </summary>
    internal bool Overlaps(PlayerbotThreat other)
    {
        if (!IgnoresHeight && !other.IgnoresHeight
            && MathF.Max(0f, MathF.Abs(Position.Z - other.Position.Z) - Radii - other.Radii) > CreatureAggro.MaxZDistance) return false;
        return Vector3.Distance(Position, other.Position) < Radius + other.Radius;
    }
}

/// <summary>Nearest first, then candidate order (a stable sort for <see cref="PlayerbotRecovery.FindReviveSpot"/>).</summary>
file sealed class ReviveSpotOrder : IComparer<(float Distance, int Order, System.Numerics.Vector3 Spot)>
{
    public static readonly ReviveSpotOrder Instance = new();

    public int Compare((float Distance, int Order, System.Numerics.Vector3 Spot) x, (float Distance, int Order, System.Numerics.Vector3 Spot) y)
    {
        int byDistance = x.Distance.CompareTo(y.Distance);
        return byDistance != 0 ? byDistance : x.Order.CompareTo(y.Order);
    }
}

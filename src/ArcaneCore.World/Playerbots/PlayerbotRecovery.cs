using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
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

    /// <summary>The fallback stalled too (no progress for <see cref="PlayerbotRecovery.NoProgressMs"/>, or a walk stuck): the recovery faults.</summary>
    Fault,
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
/// fault). Only when that fallback stalls the same way does the recovery fault (<see cref="SpiritHealerFailed"/>); a body that cannot even be released for <see cref="NoProgressMs"/> faults with
/// <see cref="Stalled"/>. Before, a stall or a stuck walk threw at once, and the fault quarantined the bot.</description></item>
/// <item><description>Waiting at the body while the reclaim delay runs (30, 60 or 120 s by recent deaths, vmangos
/// GetCorpseReclaimDelay) is progress (live, 2026-10-07: the bots that faulted were only waiting); waiting for hostiles, or walking
/// from one revive spot to the next, is not, so a body that stays camped for a minute is given up for the healer.</description></item>
/// </list>
/// </summary>
internal sealed class PlayerbotRecovery(WorldSession session, PlayerbotOptions options)
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

    /// <summary>Whether this death's body was kept although judged unsafe, because the healer's landing was worse (inspection and tests).</summary>
    internal bool BodyKeptOverLanding { get; private set; }

    /// <summary>Whether the bot was taken away from an unsafe landing after its spirit-healer resurrection (inspection and tests).</summary>
    internal bool EscapedLanding { get; private set; }

    /// <summary>
    /// Whether a body is unsafe to reclaim, free of world state: the bot already died near it inside the death watch's window
    /// (<paramref name="diedHereBefore"/>, <see cref="PlayerbotStallWatch.DeathsNear"/>); a creature round it is too strong for the
    /// bot even at full health (<paramref name="hostilesTooStrong"/>, <see cref="PlayerbotRisk.TooStrongNear"/>); or it lies outside
    /// the bot's home (<paramref name="bodyOutsideHome"/>, <see cref="PlayerbotHomeArea.Contains(uint, float, float, float)"/>) while
    /// the spirit healer leaves the bot nearer home (<paramref name="healerNearerHome"/>). A body in a safe spot inside the home, at
    /// the first death there, is reclaimed as always: the spirit healer costs resurrection sickness and a quarter of the durability
    /// (vmangos WorldSession::SendSpiritResurrect, NPCHandler.cpp:430-471).
    /// </summary>
    internal static PlayerbotUnsafeBody JudgeBody(bool diedHereBefore, bool hostilesTooStrong, bool bodyOutsideHome, bool healerNearerHome)
    {
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

    private PlayerbotRoute? _route;
    private object? _goal;
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
        }

        if (!atCorpse) return PlayerbotRecoveryStep.WalkToCorpse;
        if (reclaimWaitSeconds > 0) return PlayerbotRecoveryStep.WaitForReclaimDelay;
        if (!hostileNearCorpse) return PlayerbotRecoveryStep.Reclaim;
        return reviveSpotKnown ? PlayerbotRecoveryStep.WalkToReviveSpot : PlayerbotRecoveryStep.WaitForHostiles;
    }

    /// <summary>The spirit-healer fallback, free of world state.</summary>
    /// <param name="bodyRunLeft">The body lies on the ghost's map and the corpse run was not resumed yet for this death.</param>
    internal static PlayerbotSpiritHealerStep DecideSpiritHealer(bool healerKnown, bool healerInReach, bool graveyardKnown, bool stalled,
        bool bodyRunLeft = false)
    {
        if (stalled) return PlayerbotSpiritHealerStep.Fault;
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
            EscapedLanding = false;
            _refusal = null;
            _keepBody = default;
        }
        // A think without budget does nothing, and is no attempt the bounds may count (MinBoundThinks).
        if (session.ManagedBudget is { Remaining: <= 0 }) return false;
        _thinksSinceProgress++;
        if (_closingMs >= 0) _thinksSinceClosing++;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (!ghost && NoProgress(now))
        {
            LastStep = PlayerbotRecoveryStep.Fault;
            throw new InvalidOperationException(Stalled);
        }

        if (_spiritHealer) return SpiritHealer(player, elapsedMs, now);

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
            && (place != PlayerbotCorpsePlace.ThisMap || HealerOutcomeKnown(player, corpse!)))
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
        if (ghost && place == PlayerbotCorpsePlace.ThisMap && !_preferSpiritHealer && UnsafeBody is { } judge
            && HealerOutcomeKnown(player, corpse!) && judge(player, corpse!) is { Reason: not PlayerbotUnsafeBody.None } verdict
            && !KeepBodyOverLanding(player, corpse!))
        {
            // The body is not worth it (a repeat death there, a camp too strong, or outside the home with the healer nearer it):
            // take the spirit healer now rather than revive where the bot would die again.
            _preferSpiritHealer = true;
            UnsafeReason = verdict.Reason;
            LoggerOf().LogInformation(
                "Playerbot {Bot} takes the spirit healer instead of its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Reason}{Detail}",
                player.Name, corpse!.X, corpse.Y, corpse.Z, corpse.MapId, Describe(verdict.Reason),
                verdict.Detail is { Length: > 0 } detail ? " (" + detail + ")" : string.Empty);
        }

        AreaTriggerTemplate? entrance = place == PlayerbotCorpsePlace.OtherMap ? FindEntrance(player, corpse!.MapId) : null;
        bool atCorpse = place == PlayerbotCorpsePlace.ThisMap && WithinReclaimDistance(player, corpse!);
        long wait = atCorpse ? player.Map?.Combat.CorpseReclaimWaitSeconds(player) ?? 0 : 0;
        bool hostile = atCorpse && wait <= 0 && Camped(player, Hazards?.Invoke(player) ?? []);
        Vector3? spot = hostile && !stalled && !_preferSpiritHealer ? ChooseReviveSpot(player, corpse!) : null;
        PlayerbotRecoveryStep step = Decide(ghost, place, entrance is not null, atCorpse, wait, hostile, stalled, fallback: _preferSpiritHealer,
            reviveSpotKnown: spot is not null);
        LastStep = step;
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
                throw new InvalidOperationException(Stalled);

            case PlayerbotRecoveryStep.WaitForHostiles:
                // Not progress: a body camped for NoProgressMs is given up for the spirit healer.
                StandStill(player);
                return false;

            case PlayerbotRecoveryStep.WalkToReviveSpot:
                // Not progress for the no-progress bound: a camp that keeps moving must not keep the ghost walking for good.
                return Walk(player, spot!.Value, _spotKey!, elapsedMs, now, countsAsProgress: false);

            case PlayerbotRecoveryStep.Reclaim:
                if (!StandStill(player)) return false;
                return session.TryManagedAction(WorldOpcode.CmsgReclaimCorpse, PlayerbotNavigation.GuidPayload(corpse!.Guid.Value));

            case PlayerbotRecoveryStep.WalkToCorpse:
                return Walk(player, new Vector3(corpse!.X, corpse.Y, corpse.Z), corpse.Guid, elapsedMs, now);

            case PlayerbotRecoveryStep.WalkToEntrance:
                return Walk(player, new Vector3(entrance!.X, entrance.Y, entrance.Z), entrance, elapsedMs, now);

            default:
                // The way back to the body is lost (stalled, stuck, no body, no entrance): take the spirit healer, with a fresh bound.
                _spiritHealer = true;
                ClearWalk();
                MarkProgress(now);
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
        _deathBody = default;
        UnsafeReason = PlayerbotUnsafeBody.None;
        LastStep = PlayerbotRecoveryStep.Release;
        LastSpiritHealerStep = null;
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

    private bool SpiritHealer(Player player, uint elapsedMs, long now)
    {
        LastStep = PlayerbotRecoveryStep.SpiritHealer;
        Creature? healer = NearestSpiritHealer(player);
        WorldSafeLoc? graveyard = healer is null ? HealerGraveyard(player) : null;
        Corpse? corpse = player.Combat.Corpse;
        bool bodyHere = corpse is not null && corpse.MapId == player.MapId && ReferenceEquals(corpse.Map, player.Map);
        bool inReach = healer is not null && Distance(player, healer.X, healer.Y, healer.Z) <= SpiritHealerReachYards;
        PlayerbotSpiritHealerStep step = DecideSpiritHealer(healer is not null, inReach, graveyard is not null, Stuck(now),
            bodyRunLeft: bodyHere && _corpseRunResumedFor != corpse!.Guid);
        LastSpiritHealerStep = step;
        switch (step)
        {
            case PlayerbotSpiritHealerStep.ResumeCorpseRun:
                // Nothing to walk to and nobody to wait for: back to the body, with a fresh bound, once per death. The healer is not
                // asked for again in this death unless one can be had (HealerOutcomeKnown); a second stall here faults as before.
                _corpseRunResumedFor = corpse!.Guid;
                _spiritHealer = false;
                _preferSpiritHealer = false;
                UnsafeReason = PlayerbotUnsafeBody.None;
                ClearWalk();
                MarkProgress(now);
                LoggerOf().LogInformation(
                    "Playerbot {Bot} goes back to its body at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: no spirit healer in sight and no graveyard for it at ({GX:F1}, {GY:F1}, {GZ:F1})",
                    player.Name, corpse.X, corpse.Y, corpse.Z, corpse.MapId, player.X, player.Y, player.Z);
                return false;
            case PlayerbotSpiritHealerStep.Activate:
            {
                if (!StandStill(player)) return false;
                if (!session.TryManagedAction(WorldOpcode.CmsgSpiritHealerActivate, PlayerbotNavigation.GuidPayload(healer!.Guid.Value)))
                    return false;
                if (player.IsAlive)
                {
                    // The handler ran in that call (TryManagedAction is synchronous) and revived the bot: leave an unsafe landing now,
                    // before the creatures round it aggro in their own update.
                    LeaveLanding(player);
                    return true;
                }

                // Still dead: the server refused. Not progress (the fallback faults after NoProgressMs), but no longer silent.
                ReportRefusal(player, healer);
                return true;
            }
            case PlayerbotSpiritHealerStep.WalkToHealer:
                return Walk(player, new Vector3(healer!.X, healer.Y, healer.Z), healer.Guid, elapsedMs, now);
            case PlayerbotSpiritHealerStep.WalkToGraveyard:
                return Walk(player, new Vector3(graveyard!.X, graveyard.Y, graveyard.Z), graveyard, elapsedMs, now);
            case PlayerbotSpiritHealerStep.WaitForHealer:
                // Not progress: the no-progress bound turns a ghost that never finds a healer or a graveyard into the fault.
                StandStill(player);
                return false;
            default:
                ReportFault(player, healer, graveyard, now);
                throw new InvalidOperationException(SpiritHealerFailed);
        }
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
        LoggerOf().LogInformation(
            "Playerbot {Bot} could not activate the spirit healer {Healer} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: {Reason}",
            player.Name, healer.Entry, healer.X, healer.Y, healer.Z, player.MapId, reason);
    }

    /// <summary>The spirit-healer fallback stalled: what it was doing, logged at Information before the fault.</summary>
    private void ReportFault(Player player, Creature? healer, WorldSafeLoc? graveyard, long now)
    {
        Vector3? goal = healer is not null ? new Vector3(healer.X, healer.Y, healer.Z)
            : graveyard is not null ? new Vector3(graveyard.X, graveyard.Y, graveyard.Z) : null;
        string why = NoProgress(now) ? $"no progress for {(now - _progressMs) / 1000} s" : $"its walk stopped closing for {(now - _closingMs) / 1000} s";
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
        if (LandingUnsafe is { } unsafeAt && SpiritHealerLanding(player, corpse) is { } landing && unsafeAt(player, landing))
        {
            var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
            List<PlayerbotThreat> hostiles = [.. ThreatsAround(player, body, BodyThreatYards), .. Hazards?.Invoke(player) ?? []];
            keep = FindReviveSpot(body, body, hostiles, candidate => candidate) is not null;
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
    /// How far round a place the creatures are looked at by <see cref="ThreatsAround"/>: the reclaim radius and 50 yards more for the
    /// aggro reach of a creature standing outside it (an ArcaneCore bound: a reach is the detection range, 18 yards by default, plus the
    /// level difference, CreatureMapSystem.GetAttackDistance).
    /// </summary>
    internal const float BodyThreatYards = CombatConstants.CorpseReclaimRadius + 50f;
    /// <summary>
    /// Walk towards <paramref name="goal"/> (identified by <paramref name="key"/>) in bounded chunks. Two things count as closing for
    /// the stuck bound (<see cref="StuckMs"/>): a straight-line distance to the goal shorter than any before, and, on the route being
    /// followed, less way left along it (its remaining legs, then straight on to the goal). A route down a switchback leads away from
    /// the goal for a while: on the ridge above Kharanos the first leg from the graveyard runs north-east for 16 yards of distance
    /// and more than 10 seconds before it turns west to Ironwander's body, and the straight-line measure alone judged every such
    /// corpse run stuck and took the spirit healer. Only the straight-line measure is progress for <see cref="NoProgressMs"/>, so
    /// routes that keep leading nowhere still end in the spirit healer.
    /// </summary>
    private bool Walk(Player player, Vector3 goal, object key, uint elapsedMs, long now, bool countsAsProgress = true)
    {
        if (!Equals(_goal, key))
        {
            ClearWalk();
            _goal = key;
            MarkClosing(now); // a goal the bot cannot even plan a route to counts as stuck after StuckMs
        }

        if (_route is null || _route.Complete)
        {
            if (!PlayerbotNavigation.TryPlanToward(player, goal, options, out _route))
            {
                _route = null;
                return false;
            }

            // A fresh route sets its own yardstick; only walking it down counts, so replanning on the spot is not closing.
            _bestRouteLeft = float.PositiveInfinity;
        }

        PlayerbotRoute route = _route!;
        bool advanced = PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, session.World.NowMs);
        float left = RouteLeft(player, route, goal);
        if (float.IsFinite(left))
        {
            if (float.IsFinite(_bestRouteLeft) && left < _bestRouteLeft - RouteClosingYards) MarkClosing(now);
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
        float radiusSq = radius * radius;
        return Threats(player, map, system.Creatures.Where(c =>
            ((c.X - at.X) * (c.X - at.X)) + ((c.Y - at.Y) * (c.Y - at.Y)) <= radiusSq));
    }

    private static List<PlayerbotThreat> Threats(Player player, Map map, IEnumerable<Creature> candidates)
    {
        var found = new List<PlayerbotThreat>();
        CreatureMapSystem? system = map.FindUpdater<CreatureMapSystem>();
        foreach (Creature creature in candidates)
        {
            if (!creature.IsInWorld || !creature.IsAlive
                || !ReferenceEquals(creature.Map, map) || !map.Combat.Hooks.IsHostileTo(creature, player))
                continue;
            var at = new Vector3(creature.X, creature.Y, creature.Z);
            if (system is null)
            {
                found.Add(new PlayerbotThreat(at, HostileClearYards, IgnoresHeight: true, Radii: 0, creature));
                continue;
            }

            if (!system.CanInitiateAttack(creature) || !system.IsProximityAggroAllowedFor(creature, player)) continue;
            float radii = creature.BoundingRadius + player.BoundingRadius;
            float radius = system.GetAttackDistance(creature, player) + (system.Options.AggroUsesBoundingRadius ? radii : 0f);
            if (radius <= 0) continue;
            bool flyer = (creature.Template.InhabitType & 0x04) != 0; // INHABIT_AIR: no height limit
            found.Add(new PlayerbotThreat(at, radius, flyer, radii, creature));
        }

        return found;
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
        if (_spot is { } kept && IsReviveSpot(kept, body, hostiles)) return kept;

        Vector3? found = FindReviveSpot(new Vector3(player.X, player.Y, player.Z), body, hostiles, candidate =>
        {
            // The mesh finds the floor: a candidate is a place on the ring at the body's height, and the route ends on the
            // nearest walkable point (a candidate off the mesh ends short of it, which IsReviveSpot then re-checks).
            if (!PlayerbotNavigation.TryPlan(player, candidate, options, out PlayerbotRoute? route) || route is null
                || route.Distance > ReviveSpotMaxWalkYards)
                return null;
            return route.Points[^1];
        });
        if (found is null)
        {
            _spot = null;
            return null;
        }

        _spot = found;
        _spotKey = new object();
        return found;
    }

    private static bool IsReviveSpot(Vector3 spot, Vector3 body, IReadOnlyList<PlayerbotThreat> hostiles)
    {
        if (Vector3.Distance(spot, body) > ReviveSpotMaxYards) return false;
        foreach (PlayerbotThreat hostile in hostiles)
            if (hostile.Reaches(spot, CampMarginYards + ReviveSpotSlackYards)) return false;
        return true;
    }

    /// <summary>
    /// A place to reclaim a camped body from, free of world state: candidates on rings round the body, within
    /// <see cref="ReviveSpotMaxYards"/> of it (the server revives within <see cref="CombatConstants.CorpseReclaimRadius"/>) and at
    /// out of every threat's reach with <see cref="CampMarginYards"/> and <see cref="ReviveSpotSlackYards"/>, nearest to the ghost first. <paramref name="reach"/> maps a
    /// candidate to the point the ghost can actually walk to (the navigation mesh's, null when it cannot); the first reachable
    /// point that is still a revive spot is taken. At most <see cref="ReviveSpotMaxQueries"/> candidates are asked.
    /// </summary>
    internal static Vector3? FindReviveSpot(Vector3 ghost, Vector3 body, IReadOnlyList<PlayerbotThreat> hostileList, Func<Vector3, Vector3?> reach)
    {
        var candidates = new List<Vector3> { body };
        // Rings every few yards, finely spaced: a camp can leave only a thin slice of the reclaim disc clear.
        for (float radius = ReviveSpotRingYards; radius <= ReviveSpotMaxYards - 1f; radius += ReviveSpotRingYards)
            for (int step = 0; step < ReviveSpotAngles; step++)
            {
                float angle = step * 2f * MathF.PI / ReviveSpotAngles;
                candidates.Add(new Vector3(body.X + (MathF.Cos(angle) * radius), body.Y + (MathF.Sin(angle) * radius), body.Z));
            }

        int queries = 0;
        foreach (Vector3 candidate in candidates
            .Where(c => IsReviveSpot(c, body, hostileList))
            .OrderBy(c => Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(ghost.X, ghost.Y))))
        {
            if (++queries > ReviveSpotMaxQueries) break;
            if (reach(candidate) is { } point && IsReviveSpot(point, body, hostileList)) return point;
        }

        return null;
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

    /// <summary>The body this recovery saw released (a different one, or none while dead, is a new death the brain's reset missed).</summary>
    private ObjectGuid _deathBody;

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
        PlayerbotUnsafeBody.OutsideHome => "the body lies outside its home and the healer is nearer home",
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
}

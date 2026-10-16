using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>The life of one portal request on the board (<see cref="PlayerbotPortalBoard"/>).</summary>
internal enum PortalRequestState : byte
{
    /// <summary>Posted; no mage took it yet.</summary>
    Open,

    /// <summary>A mage took it and serves it (rune, fee, cast).</summary>
    Matched,

    /// <summary>The mage's portal stands (<see cref="PortalRequest.PortalGuid"/>); the requester walks into it.</summary>
    PortalOpen,

    /// <summary>The requester arrived through the portal.</summary>
    Done,

    /// <summary>One side gave up (<see cref="PortalRequest.Failure"/> says why).</summary>
    Failed,

    /// <summary>No mage took it in time.</summary>
    Expired,
}

/// <summary>
/// One bot's request for a portal: who asks, from where, which portal (the mage's Portal spell names the capital), and how far it got. Owned by
/// the board; the requester and the matched mage read and advance it on their own thinks. Mutable, world thread only.
/// </summary>
internal sealed class PortalRequest(ulong requester, string requesterName, Team team, uint mapId, Vector3 position, uint portalSpellId,
    ulong preferredMage, uint feeCopper, uint nowMs)
{
    public ulong Requester { get; } = requester;
    public string RequesterName { get; } = requesterName;
    public Team Team { get; } = team;
    public uint MapId { get; } = mapId;

    /// <summary>Where the requester stood when it asked (refreshed by <see cref="PlayerbotPortalBoard.Match"/>).</summary>
    public Vector3 Position { get; set; } = position;

    public uint PortalSpellId { get; } = portalSpellId;

    /// <summary>The mage the planner's offer named (0: any); matched first while it is still free.</summary>
    public ulong PreferredMage { get; } = preferredMage;

    /// <summary>The copper the requester pays the mage by a trade before the cast (0: free).</summary>
    public uint FeeCopper { get; } = feeCopper;

    public uint PostedMs { get; } = nowMs;
    public PortalRequestState State { get; set; }

    /// <summary>When the request entered its state (world ms).</summary>
    public uint StateMs { get; set; } = nowMs;

    public ulong MageGuid { get; set; }
    public ObjectGuid PortalGuid { get; set; }

    /// <summary>The fee trade settled (always true for a free portal).</summary>
    public bool Paid { get; set; } = feeCopper == 0;

    public string? Failure { get; set; }

    public bool IsLive => State is PortalRequestState.Open or PortalRequestState.Matched or PortalRequestState.PortalOpen;
}

/// <summary>
/// One mage bot listed as available (<see cref="PlayerbotMageService.DriveMage"/> lists an idle mage at each think): where it stands and which
/// portals it can open. The masks are over <see cref="PlayerbotMageService.Portals"/> (bit i = <c>Portals[i]</c>), so a listing allocates
/// nothing. Owned by the board, world thread only.
/// </summary>
internal sealed class PortalMage(ulong guid)
{
    public ulong Guid { get; } = guid;
    public Team Team { get; set; }
    public uint MapId { get; set; }
    public Vector3 Position { get; set; }

    /// <summary>The Portal spells the mage knows.</summary>
    public byte KnownMask { get; set; }

    /// <summary>The known portals it can cast now: rune of portals carried, the spell off cooldown.</summary>
    public byte ReadyMask { get; set; }

    /// <summary>It carries no rune but a vendor that stocks it stands within reach on its map (it buys one when matched).</summary>
    public bool CanBuyRune { get; set; }

    /// <summary>The listing lapses at this time unless the mage lists itself again.</summary>
    public uint ExpiresMs { get; set; }

    /// <summary>The requester the mage serves (0: free).</summary>
    public ulong Serving { get; set; }
}

/// <summary>
/// The in-world portal request board (docs/areas/playbots.md, "Travel"): bots of one faction ask on it for a portal to a capital, mage bots of
/// that faction list themselves on it when idle, and a request is matched to the nearest available mage on the requester's map within
/// <see cref="PlayerbotTravelOptions.MageServiceRadiusYards"/> that can open that portal now or after buying the rune. Requests move
/// <see cref="PortalRequestState.Open"/> → <see cref="PortalRequestState.Matched"/> → <see cref="PortalRequestState.PortalOpen"/> →
/// <see cref="PortalRequestState.Done"/>, or end <see cref="PortalRequestState.Failed"/> / <see cref="PortalRequestState.Expired"/>. Nothing on
/// the board acts: the requester (<see cref="PlayerbotMageService.Drive"/>) and the mage (<see cref="PlayerbotMageService.DriveMage"/>) act
/// through their own sessions.
/// <para>
/// One board per <see cref="WorldRuntime"/> (<see cref="ConditionalWeakTable{TKey,TValue}"/>, so a world that goes takes its board with it).
/// Bounded: at most <see cref="MaxRequests"/> requests and <see cref="MaxMages"/> listings; a listing lapses after <see cref="ListingLifeMs"/>
/// without a refresh, an open request expires after <see cref="MatchWaitMs"/>, a matched one fails after <see cref="ServeDeadlineMs"/>, and an
/// ended one is dropped after <see cref="EndedKeepMs"/> (swept when the tables fill and at each post). Thread affinity: world thread only, no
/// lock. Cost: a match or an offer list walks the listings once (O(mages in the world)); everything else is one dictionary probe.
/// </para>
/// </summary>
internal static class PlayerbotPortalBoard
{
    /// <summary>An open request no mage took within this long expires.</summary>
    internal const uint MatchWaitMs = 30_000;

    /// <summary>A matched request whose portal did not open within this long (the mage's walk for a rune included) fails.</summary>
    internal const uint ServeDeadlineMs = 240_000;

    /// <summary>A mage listing lapses this long after the mage last listed itself (its brain stopped serving).</summary>
    internal const uint ListingLifeMs = 5_000;

    /// <summary>An ended request is kept this long so that the other side reads how it ended.</summary>
    internal const uint EndedKeepMs = 30_000;

    internal const int MaxRequests = 4096;
    internal const int MaxMages = 4096;

    private static readonly ConditionalWeakTable<WorldRuntime, Table> Boards = [];

    private sealed class Table
    {
        public readonly Dictionary<ulong, PortalRequest> Requests = [];
        public readonly Dictionary<ulong, PortalMage> Mages = [];
    }

    private static Table Of(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Boards.GetValue(session.World, static _ => new Table());
    }

    /// <summary>Requests on the board, ended ones not yet dropped included (tests).</summary>
    internal static int RequestCount(WorldSession session) => Of(session).Requests.Count;

    /// <summary>The listing of <paramref name="mage"/>, lapsed or not (tests and inspection).</summary>
    internal static PortalMage? ListingOf(WorldSession session, ulong mage) => Of(session).Mages.GetValueOrDefault(mage);

    /// <summary>The request <paramref name="requester"/> posted, live or ended, or null.</summary>
    internal static PortalRequest? RequestOf(WorldSession session, ulong requester) => Of(session).Requests.GetValueOrDefault(requester);

    /// <summary>The live request matched to <paramref name="mage"/>, or null.</summary>
    internal static PortalRequest? RequestFor(WorldSession session, ulong mage)
    {
        Table table = Of(session);
        return table.Mages.GetValueOrDefault(mage) is { Serving: not 0 } listing
            && table.Requests.GetValueOrDefault(listing.Serving) is { IsLive: true } request && request.MageGuid == mage ? request : null;
    }

    /// <summary>
    /// List (or refresh) a mage as available: where it stands, the portals it knows and can open now, and whether it could buy the rune. A mage
    /// that serves a request stays listed as busy. False when the table is full of live listings.
    /// </summary>
    internal static bool List(WorldSession session, Player mage, byte knownMask, byte readyMask, bool canBuyRune)
    {
        ArgumentNullException.ThrowIfNull(mage);
        Table table = Of(session);
        uint now = session.World.NowMs;
        if (!table.Mages.TryGetValue(mage.Guid.Value, out PortalMage? listing))
        {
            if (table.Mages.Count >= MaxMages)
            {
                Sweep(table, now);
                if (table.Mages.Count >= MaxMages) return false;
            }

            listing = new PortalMage(mage.Guid.Value);
            table.Mages[listing.Guid] = listing;
        }

        listing.Team = mage.Team;
        listing.MapId = mage.MapId;
        listing.Position = new Vector3(mage.X, mage.Y, mage.Z);
        listing.KnownMask = knownMask;
        listing.ReadyMask = readyMask;
        listing.CanBuyRune = canBuyRune;
        listing.ExpiresMs = unchecked(now + ListingLifeMs);
        return true;
    }

    /// <summary>Take a mage off the board (service off, dead, grouped, a controller took it over). A request it served is left to <see cref="Fail"/>.</summary>
    internal static void Unlist(WorldSession session, ulong mage) => Of(session).Mages.Remove(mage);

    /// <summary>
    /// The portals mages near <paramref name="player"/> can open now, for the planner: one offer per (mage, portal) of a listed, free mage of the
    /// bot's team on its map within <see cref="PlayerbotTravelOptions.MageServiceRadiusYards"/>, whose rune is carried and spell ready, with the
    /// destination the portal leads to (<see cref="PlayerbotMageService.PortalDestination"/>) and the fee
    /// <see cref="PlayerbotTravelOptions.MageServiceFeeCopper"/>. Empty with the service off. The bot itself is never offered.
    /// </summary>
    internal static IReadOnlyList<PortalOffer> Offers(WorldSession session, Player player, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Travel.MageService || !player.IsInWorld || player.Map is null) return [];
        Table table = Of(session);
        uint now = session.World.NowMs;
        float radius = Math.Clamp(options.Travel.MageServiceRadiusYards, 10f, 1000f);
        Vector3 here = new(player.X, player.Y, player.Z);
        List<PortalOffer>? offers = null;
        foreach (PortalMage mage in table.Mages.Values)
        {
            if (!Available(mage, player, here, radius, now) || mage.ReadyMask == 0) continue;
            for (int i = 0; i < PlayerbotMageService.Portals.Length; i++)
            {
                if ((mage.ReadyMask & (1 << i)) == 0) continue;
                uint spell = PlayerbotMageService.Portals[i].PortalSpell;
                if (PlayerbotMageService.PortalDestination(session, player, spell) is not { } destination) continue;
                (offers ??= []).Add(new PortalOffer(mage.Guid, spell, new TravelPlace(mage.MapId, mage.Position), destination,
                    options.Travel.MageServiceFeeCopper));
            }
        }

        return offers is null ? [] : offers;
    }

    /// <summary>
    /// Post <paramref name="requester"/>'s request for the portal <paramref name="portalSpellId"/> (replacing any earlier request of the bot,
    /// whose matched mage then sees it fail). Null when the board is full of live requests.
    /// </summary>
    internal static PortalRequest? Post(WorldSession session, Player requester, uint portalSpellId, ulong preferredMage, uint feeCopper)
    {
        ArgumentNullException.ThrowIfNull(requester);
        Table table = Of(session);
        uint now = session.World.NowMs;
        Sweep(table, now);
        if (table.Requests.TryGetValue(requester.Guid.Value, out PortalRequest? earlier))
            End(table, earlier, PortalRequestState.Failed, "replaced by a new request", now);
        else if (table.Requests.Count >= MaxRequests) return null;
        var request = new PortalRequest(requester.Guid.Value, requester.Name, requester.Team, requester.MapId,
            new Vector3(requester.X, requester.Y, requester.Z), portalSpellId, preferredMage, feeCopper, now);
        table.Requests[request.Requester] = request;
        return request;
    }

    /// <summary>
    /// Match <paramref name="requester"/>'s open request: the offer's mage when it is still available, else the nearest available mage of the
    /// same team on the same map within the radius that can open the portal now (rune carried) or after buying the rune. An open request past
    /// <see cref="MatchWaitMs"/> expires instead. The request as it stands afterwards (null: none).
    /// </summary>
    internal static PortalRequest? Match(WorldSession session, Player requester, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(options);
        Table table = Of(session);
        if (!table.Requests.TryGetValue(requester.Guid.Value, out PortalRequest? request)) return null;
        uint now = session.World.NowMs;
        if (request.State == PortalRequestState.Matched && unchecked(now - request.StateMs) > ServeDeadlineMs)
            End(table, request, PortalRequestState.Failed, "the matched mage did not open the portal in time", now);
        if (request.State != PortalRequestState.Open) return request;
        if (unchecked(now - request.PostedMs) > MatchWaitMs)
        {
            End(table, request, PortalRequestState.Expired, "no mage took the request", now);
            return request;
        }

        request.Position = new Vector3(requester.X, requester.Y, requester.Z);
        int bit = PlayerbotMageService.PortalIndex(request.PortalSpellId);
        if (bit < 0 || !options.Travel.MageService) return request;
        float radius = Math.Clamp(options.Travel.MageServiceRadiusYards, 10f, 1000f);
        PortalMage? best = null;
        float bestDistance = float.PositiveInfinity;
        foreach (PortalMage mage in table.Mages.Values)
        {
            if (!Available(mage, requester, request.Position, radius, now) || (mage.KnownMask & (1 << bit)) == 0) continue;
            if ((mage.ReadyMask & (1 << bit)) == 0 && !mage.CanBuyRune) continue;
            float distance = Vector3.Distance(mage.Position, request.Position);
            if (mage.Guid == request.PreferredMage) distance = -1f;
            if (distance < bestDistance)
            {
                best = mage;
                bestDistance = distance;
            }
        }

        if (best is null) return request;
        best.Serving = request.Requester;
        request.MageGuid = best.Guid;
        request.State = PortalRequestState.Matched;
        request.StateMs = now;
        return request;
    }

    /// <summary>The mage's portal stands: publish its guid to the requester.</summary>
    internal static void PortalOpened(WorldSession session, PortalRequest request, ObjectGuid portal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsLive) return;
        request.PortalGuid = portal;
        request.State = PortalRequestState.PortalOpen;
        request.StateMs = session.World.NowMs;
    }

    /// <summary>The requester arrived through the portal; the mage is free again.</summary>
    internal static void Complete(WorldSession session, PortalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        End(Of(session), request, PortalRequestState.Done, null, session.World.NowMs);
    }

    /// <summary>One side gave up <paramref name="request"/> (the reason is the other side's to read); the mage is free again.</summary>
    internal static void Fail(WorldSession session, PortalRequest request, string why)
    {
        ArgumentNullException.ThrowIfNull(request);
        End(Of(session), request, PortalRequestState.Failed, why, session.World.NowMs);
    }

    /// <summary>The requester withdraws its request (death, a controller, the trip given up): it fails as "cancelled" for the mage to see.</summary>
    internal static void Cancel(WorldSession session, Player requester)
    {
        ArgumentNullException.ThrowIfNull(requester);
        Table table = Of(session);
        if (table.Requests.TryGetValue(requester.Guid.Value, out PortalRequest? request))
            End(table, request, PortalRequestState.Failed, "cancelled by the requester", session.World.NowMs);
    }

    private static bool Available(PortalMage mage, Player asker, Vector3 here, float radius, uint now)
        => mage.Serving == 0 && mage.Guid != asker.Guid.Value && unchecked((int)(mage.ExpiresMs - now)) > 0 && mage.Team == asker.Team
            && mage.MapId == asker.MapId && Vector3.Distance(mage.Position, here) <= radius;

    private static void End(Table table, PortalRequest request, PortalRequestState state, string? why, uint now)
    {
        if (!request.IsLive) return;
        request.State = state;
        request.StateMs = now;
        request.Failure = why;
        if (request.MageGuid != 0 && table.Mages.GetValueOrDefault(request.MageGuid) is { } mage && mage.Serving == request.Requester) mage.Serving = 0;
    }

    /// <summary>Drop lapsed listings, expire overdue requests and drop ended ones kept long enough.</summary>
    private static void Sweep(Table table, uint now)
    {
        List<ulong>? drop = null;
        foreach (PortalMage mage in table.Mages.Values)
        {
            if (mage.Serving == 0 && unchecked((int)(mage.ExpiresMs - now)) <= 0) (drop ??= []).Add(mage.Guid);
        }

        if (drop is not null)
        {
            foreach (ulong guid in drop) table.Mages.Remove(guid);
            drop.Clear();
        }

        foreach (PortalRequest request in table.Requests.Values)
        {
            if (request.State == PortalRequestState.Open && unchecked(now - request.PostedMs) > MatchWaitMs)
                End(table, request, PortalRequestState.Expired, "no mage took the request", now);
            else if (request.State is PortalRequestState.Matched or PortalRequestState.PortalOpen && unchecked(now - request.StateMs) > ServeDeadlineMs)
                End(table, request, PortalRequestState.Failed, "the request was never finished", now);
            if (!request.IsLive && unchecked(now - request.StateMs) > EndedKeepMs) (drop ??= []).Add(request.Requester);
        }

        if (drop is not null)
        {
            foreach (ulong guid in drop) table.Requests.Remove(guid);
        }
    }
}

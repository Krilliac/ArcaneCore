using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>One capital a mage reaches: its Teleport spell (rune of teleportation), its Portal spell (rune of portals) and who may go.</summary>
internal readonly record struct MageCapital(uint TeleportSpell, uint PortalSpell, TravelAccess Access, string Name);

/// <summary>
/// A walk toward a point that may move (a mage, a portal, a vendor): planned with <see cref="PlayerbotNavigation.PlanToward"/>, followed with
/// <see cref="PlayerbotNavigation.TryAdvance"/>, given up after <see cref="MaxRefusedPlans"/> refused plans in a row or no
/// <see cref="ProgressYards"/> gained in <see cref="NoProgressMs"/>. Owned by one leg; mutable, world thread only.
/// </summary>
internal sealed class TravelWalk
{
    /// <summary>Refused path queries in a row that end the walk.</summary>
    internal const int MaxRefusedPlans = 3;

    internal const float ProgressYards = 5f;
    internal const uint NoProgressMs = 60_000;
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A target that moved farther than this from where the route leads is planned to again.</summary>
    internal const float RetargetYards = 5f;

    private PlayerbotRoute? _route;
    private TileWait _tiles;
    private Vector3 _target = new(float.NaN);
    private int _refused;
    private uint _nextPlanMs;
    private float _best = float.PositiveInfinity;
    private uint _bestMs;
    private bool _started;

    /// <summary>Refused plans in a row so far (tests).</summary>
    internal int Refused => _refused;

    internal void Reset()
    {
        _route = null;
        _tiles.Reset();
        _target = new Vector3(float.NaN);
        _refused = 0;
        _best = float.PositiveInfinity;
        _started = false;
    }

    /// <summary>One step toward <paramref name="target"/>; false when the walk is given up.</summary>
    internal bool Step(WorldSession session, Player player, PlayerbotOptions options, Vector3 target, uint elapsedMs, uint now)
    {
        if (!_started)
        {
            _started = true;
            _bestMs = now;
            _nextPlanMs = now;
        }

        if (!(Vector3.Distance(target, _target) <= RetargetYards))
        {
            _target = target;
            _route = null;
            _nextPlanMs = now;
        }

        float left = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), target);
        if (left + ProgressYards <= _best)
        {
            _best = left;
            _bestMs = now;
        }
        else if (unchecked(now - _bestMs) > NoProgressMs)
        {
            return false;
        }

        if (_route is { Complete: false } route && PlayerbotNavigation.TryAdvance(session, route, options, Math.Max(1u, elapsedMs), now)) return true;
        _route = null;
        if (unchecked((int)(_nextPlanMs - now)) > 0) return true;
        PlanOutcome planned = PlayerbotNavigation.PlanToward(player, target, options, ref _tiles, now, out PlayerbotRoute? next);
        if (planned == PlanOutcome.Route && next is not null)
        {
            _refused = 0;
            _route = next;
            PlayerbotNavigation.TryAdvance(session, next, options, Math.Max(1u, elapsedMs), now);
            return true;
        }

        _nextPlanMs = unchecked(now + PlanRetryMs);
        return planned == PlanOutcome.Pending || ++_refused < MaxRefusedPlans;
    }
}

/// <summary>The stages of a requester's (or a mage's own) leg in <see cref="PlayerbotMageService"/>.</summary>
internal enum MageLegStage : byte { Teleport, WaitMatch, ToMage, Paying, WaitPortal, ToPortal, Teleporting, Done, Failed }

/// <summary>A bot's mage leg: its own Teleport (<see cref="TravelMeans.MageTeleport"/>) or a portal asked for (<see cref="TravelMeans.MagePortal"/>).</summary>
internal sealed class MageLeg(TravelMeans means, uint spellId, TravelPlace destination, uint nowMs)
{
    public TravelMeans Means { get; } = means;

    /// <summary>The Teleport spell, or the Portal spell asked for.</summary>
    public uint SpellId { get; } = spellId;

    public TravelPlace Destination { get; } = destination;
    public MageLegStage Stage { get; set; }
    public uint StageMs { get; set; } = nowMs;

    /// <summary>The cast of the bot's own Teleport (<see cref="TravelMeans.MageTeleport"/> only).</summary>
    public TravelCastLeg? Cast { get; set; }

    public TravelWalk Walk { get; } = new();
    public ulong MageGuid { get; set; }

    /// <summary>The bot joined the mage's party for the portal (it leaves it at the end).</summary>
    public bool JoinedGroup { get; set; }

    /// <summary>The money before the fee trade, and when the last trade request went out.</summary>
    public uint MoneyBefore { get; set; }

    /// <summary>The fee this leg paid the mage (0 until the trade settled): the journey charges it to its spend cap.</summary>
    public uint PaidCopper { get; set; }

    /// <summary>
    /// The mage this bot already paid for this portal (a leg begun again after a fight dropped the paid one; 0: none): when that mage takes
    /// the request it is marked paid, and the fee is not traded again.
    /// </summary>
    public ulong PrepaidMage { get; init; }

    public uint LastTradeMs { get; set; }
    public string? Failure { get; set; }
}

/// <summary>The stages of a mage serving a matched portal request.</summary>
internal enum MageServeStage : byte { Idle, Prepare, ToVendor, WaitRequester, WaitPaid, Cast, Casting, WaitUse }

/// <summary>A mage's service of one request: where it stands in it, the vendor it walks to, the portal it opened. Mutable, world thread only.</summary>
internal sealed class MageServe
{
    public MageServeStage Stage { get; set; }
    public ulong Requester { get; set; }
    public uint StageMs { get; set; }
    public TravelWalk Walk { get; } = new();
    public uint VendorEntry { get; set; }
    public Vector3 VendorAt { get; set; }
    public TravelCastLeg? Cast { get; set; }

    /// <summary>The portals of this mage standing before the cast: the new one is the one not among them.</summary>
    public List<ObjectGuid> Before { get; } = [];

    public ObjectGuid Portal { get; set; }
    public bool FormedGroup { get; set; }
    public int Invites { get; set; }
    public uint LastInviteMs { get; set; }

    public void Reset()
    {
        Stage = MageServeStage.Idle;
        Requester = 0;
        Walk.Reset();
        VendorEntry = 0;
        Cast = null;
        Before.Clear();
        Portal = ObjectGuid.Empty;
        FormedGroup = false;
        Invites = 0;
    }
}

/// <summary>
/// Mage bots as a travel service, through the server's real spell, item, gameobject, group and trade paths (docs/areas/playbots.md, "Travel").
/// Three parts:
/// <list type="bullet">
/// <item>A mage's own Teleport (<see cref="OwnTeleports"/>, <see cref="TryBeginTeleport"/>, driven by <see cref="Drive"/>): CMSG_CAST_SPELL
/// at itself, the ten-second cast waited out standing still, the arrival at the spell's <c>spell_target_position</c> confirmed.</item>
/// <item>A mage serving the board (<see cref="DriveMage"/>, one call per think of every mage bot): an idle mage lists itself on
/// <see cref="PlayerbotPortalBoard"/>; a matched mage stops, buys the Rune of Portals from a vendor that stocks it when it has none, waits for the
/// requester, takes the fee by a real trade, casts the Portal (whose TRANS_DOOR summons a GAMEOBJECT_TYPE_SPELLCASTER owned by the mage),
/// publishes the portal's guid on the board, invites the requester into its party when the portal is party-only (vmangos GameObject::Use,
/// GameObject.cpp:1798-1834), and when the requester went through leaves the party.</item>
/// <item>A requester's portal leg (<see cref="TryBeginPortal(WorldSession, Player, PortalOffer, PlayerbotOptions, out string?)"/>, driven by
/// <see cref="Drive"/>): posts a request, waits for a match, walks to the mage, pays the fee, accepts the mage's party invitation, walks into
/// interaction range of the portal, CMSG_GAMEOBJ_USE, and confirms it stands where the portal leads.</item>
/// </list>
/// A refusal at the beginning of a leg (a spell not known, no rune) is a planner-checked refusal and is not reported; a failure after it is
/// reported once per leg (anomaly area <c>mage</c>), and nothing is reported on success. When the mage side fails a request the mage reports it
/// and the requester's leg fails without a second report. Thread affinity: world thread; per-bot state hangs off the <see cref="Player"/>.
/// </summary>
internal static class PlayerbotMageService
{
    /// <summary>The anomaly area of every report in this file (subsystem <c>bot-mage</c>).</summary>
    internal const string Area = "mage";

    /// <summary>Rune of Teleportation, the reagent of the Teleport spells (item 17031).</summary>
    internal const uint RuneOfTeleportation = 17031;

    /// <summary>Rune of Portals, the reagent of the Portal spells (item 17032).</summary>
    internal const uint RuneOfPortals = 17032;

    /// <summary>The six capitals of 1.12 (Spell.dbc Teleport and Portal spells; the spellbook decides which a bot knows).</summary>
    internal static readonly MageCapital[] Portals =
    [
        new(3561, 10059, TravelAccess.Alliance, "Stormwind"),
        new(3562, 11416, TravelAccess.Alliance, "Ironforge"),
        new(3565, 11419, TravelAccess.Alliance, "Darnassus"),
        new(3563, 11418, TravelAccess.Horde, "Undercity"),
        new(3566, 11420, TravelAccess.Horde, "Thunder Bluff"),
        new(3567, 11417, TravelAccess.Horde, "Orgrimmar"),
    ];

    /// <summary>The requester stops this close to the mage (inside trade and party range).</summary>
    internal const float MeetYards = 4f;

    /// <summary>The requester stops this close to the portal (inside the 5-yard interaction distance of a spell caster).</summary>
    internal const float UseYards = 3.5f;

    /// <summary>The mage casts once the requester stands this close.</summary>
    internal const float CastWhenNearYards = 10f;

    /// <summary>The farthest a mage walks to a vendor for a rune (straight line from where it stands).</summary>
    internal const float MaxVendorYards = 300f;

    /// <summary>A mage waits this long for its requester to come.</summary>
    internal const uint RequesterWaitMs = 90_000;

    /// <summary>The fee trade must settle within this long.</summary>
    internal const uint PayWaitMs = 30_000;

    /// <summary>A rune purchase (the walk to the vendor included) must be done within this long.</summary>
    internal const uint VendorTripMs = 120_000;

    /// <summary>A portal not used within this long after it opened ends the service.</summary>
    internal const uint PortalUseWaitMs = 60_000;

    /// <summary>A cast portal must stand within this long after the cast went off.</summary>
    internal const uint PortalSpawnWaitMs = 2_000;

    /// <summary>The requester waits this long at the portal for the mage's party invitation.</summary>
    internal const uint InviteWaitMs = 15_000;

    /// <summary>A mage invites again after this long, at most <see cref="MaxInvites"/> times.</summary>
    internal const uint InviteRetryMs = 5_000;

    internal const int MaxInvites = 3;

    /// <summary>The teleport after a portal click must end within this long.</summary>
    internal const uint TeleportWaitMs = 30_000;

    /// <summary>The trade request is sent again after this long while no trade is open.</summary>
    internal const uint TradeRetryMs = 3_000;

    private static readonly ConditionalWeakTable<Player, MageLeg> Legs = [];
    private static readonly ConditionalWeakTable<Player, MageServe> Serves = [];
    private static readonly ConditionalWeakTable<CreatureContent, VendorIndex> VendorIndexes = [];

    /// <summary>The vendors of each rune per map, built once per creature content and NPC store (rebuilt when either changes).</summary>
    private sealed class VendorIndex(NpcStore npcs, int version)
    {
        public NpcStore Npcs { get; } = npcs;
        public int Version { get; } = version;
        public Dictionary<(uint Map, uint Item), List<(uint Entry, uint Faction, CreatureSpawn Spawn)>> ByMapItem { get; } = [];
    }

    // --- shared look-ups -------------------------------------------------------------------

    /// <summary>The index of <paramref name="portalSpell"/> in <see cref="Portals"/>, or -1.</summary>
    internal static int PortalIndex(uint portalSpell)
    {
        for (int i = 0; i < Portals.Length; i++)
        {
            if (Portals[i].PortalSpell == portalSpell) return i;
        }

        return -1;
    }

    /// <summary>The index of <paramref name="teleportSpell"/> in <see cref="Portals"/>, or -1.</summary>
    internal static int TeleportIndex(uint teleportSpell)
    {
        for (int i = 0; i < Portals.Length; i++)
        {
            if (Portals[i].TeleportSpell == teleportSpell) return i;
        }

        return -1;
    }

    /// <summary>
    /// The gameobject template a Portal spell summons (its TRANS_DOOR effect's MiscValue, a GAMEOBJECT_TYPE_SPELLCASTER), or null when the spell,
    /// its effect or the template is unknown to the server.
    /// </summary>
    internal static GameObjectTemplate? PortalObject(WorldSession session, Player player, uint portalSpell)
    {
        if (session.Services.GetService<SpellFeature>()?.System.Store.Get(portalSpell) is not { } spell
            || player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return null;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect == SpellEffectName.TransDoor && effect.MiscValue > 0
                && objects.FindTemplate((uint)effect.MiscValue) is { } template && (GameObjectType)template.Type == GameObjectType.SpellCaster)
                return template;
        }

        return null;
    }

    /// <summary>
    /// Where a Portal spell leads: its portal object's spell (data0, the "Portal Effect" teleport) and that spell's <c>spell_target_position</c>.
    /// Null when any link of the chain is missing.
    /// </summary>
    internal static TravelPlace? PortalDestination(WorldSession session, Player player, uint portalSpell)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (PortalObject(session, player, portalSpell) is not { } template
            || session.Services.GetService<SpellFeature>()?.System.Store is not { } store
            || template.GetData(GameObjectMapSystem.SpellCasterSpellData) is not (var teleport and not 0)
            || store.GetTargetPosition(teleport) is not { } target) return null;
        return new TravelPlace(target.MapId, new Vector3(target.X, target.Y, target.Z));
    }

    /// <summary>Where a Teleport spell leads (its <c>spell_target_position</c>), or null.</summary>
    internal static TravelPlace? TeleportDestination(SpellSystem spells, uint teleportSpell)
        => spells.Store.GetTargetPosition(teleportSpell) is { } target ? new TravelPlace(target.MapId, new Vector3(target.X, target.Y, target.Z)) : null;

    private static bool MayUse(Player player, MageCapital capital)
        => capital.Access == TravelAccess.Both || (capital.Access == TravelAccess.Alliance) == (player.Team == Team.Alliance);

    private static bool HasReagents(Player player, SpellInfo spell)
    {
        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (reagent.IsPresent && player.Inventory.GetItemCount(reagent.ItemId) < reagent.Count) return false;
        }

        return true;
    }

    /// <summary>The first reagent of the spell (the rune), or 0.</summary>
    private static uint RuneOf(SpellInfo spell)
    {
        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (reagent.IsPresent) return reagent.ItemId;
        }

        return 0;
    }

    private static bool Ready(SpellSystem spells, Player player, SpellInfo spell) => !PlayerbotTravelCast.OnCooldown(spells, player, spell.Id, spell.Category);

    private static GroupManager? Groups(WorldSession session)
    {
        try
        {
            return session.Services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.mage-service", ex);
            return null; // the social feature is not attached (a world without it)
        }
    }

    private static Player? Online(WorldSession session, ulong guid) => session.World.FindOnlinePlayer(new ObjectGuid(guid));

    // --- a mage's own Teleport -------------------------------------------------------------

    /// <summary>
    /// The Teleport spells the bot can cast now (<see cref="TravelFacts.OwnTeleports"/>): the service on, alive and out of combat, the spell in
    /// its book, the rune carried, the spell off cooldown, and a destination in the spell data. Empty for a bot that knows none.
    /// </summary>
    internal static IReadOnlyList<OwnTeleport> OwnTeleports(WorldSession session, Player player, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Travel.MageService || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat
            || session.Services.GetService<SpellFeature>() is not { } feature) return [];
        List<OwnTeleport>? found = null;
        foreach (MageCapital capital in Portals)
        {
            if (TeleportRefusal(feature, player, capital.TeleportSpell, out TravelPlace destination) is null)
                (found ??= []).Add(new OwnTeleport(capital.TeleportSpell, destination));
        }

        return found is null ? [] : found;
    }

    /// <summary>Why the bot cannot cast the Teleport <paramref name="spellId"/> now, or null (with where it leads).</summary>
    private static string? TeleportRefusal(SpellFeature feature, Player player, uint spellId, out TravelPlace destination)
    {
        destination = default;
        if (TeleportIndex(spellId) < 0) return $"spell {spellId} is not a mage Teleport";
        if (!feature.Spellbook.HasSpell(player, spellId)) return $"the bot does not know Teleport {spellId}";
        if (feature.System.Store.Get(spellId) is not { } spell) return $"Teleport {spellId} is not in the spell data";
        if (!HasReagents(player, spell)) return $"no rune for Teleport {spellId}";
        if (!Ready(feature.System, player, spell)) return $"Teleport {spellId} is on cooldown";
        if (TeleportDestination(feature.System, spellId) is not { } place) return $"Teleport {spellId} has no target position";
        destination = place;
        return null;
    }

    /// <summary>
    /// Begin the bot's own Teleport <paramref name="spellId"/>; false (with why, and no report: the planner checked it) when the service is off,
    /// the bot is busy or dead, or the spell is not known, without rune, on cooldown or without a destination. A leg under way answers true.
    /// </summary>
    internal static bool TryBeginTeleport(WorldSession session, Player player, uint spellId, PlayerbotOptions options, out string? why)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        why = null;
        if (IsActive(player)) return true;
        if (!options.Travel.MageService) why = "the mage service is off";
        else if (!player.IsInWorld || player.Map is null || !player.IsAlive) why = "not alive in the world";
        else if (player.Combat.IsInCombat) why = "in combat";
        else if (session.Services.GetService<SpellFeature>() is not { } feature) why = "no spell system";
        else if (TeleportRefusal(feature, player, spellId, out TravelPlace destination) is { } refused) why = refused;
        else
        {
            if (PlayerbotTravel.JourneyOf(player) is { State: JourneyState.Active } journey && journey.Current?.Means != TravelMeans.MageTeleport)
                PlayerbotTravel.Cancel(player);
            PlayerbotShip.Cancel(player);
            PlayerbotHearthstone.Cancel(session, player);
            CancelMage(session, player);
            PlayerbotTaxi.Cancel(player);
            PlayerbotTram.Cancel(player);
            PlayerbotElevator.Cancel(player);
            uint now = session.World.NowMs;
            Legs.AddOrUpdate(player, new MageLeg(TravelMeans.MageTeleport, spellId, destination, now)
            {
                Stage = MageLegStage.Teleport,
                Cast = new TravelCastLeg(TravelMeans.MageTeleport, spellId, destination, now),
            });
            return true;
        }

        why = "mage: " + why;
        return false;
    }

    // --- the requester's portal --------------------------------------------------------------

    /// <summary>Ask for the portal of <paramref name="offer"/> (its mage preferred while it is free); see the other overload.</summary>
    internal static bool TryBeginPortal(WorldSession session, Player player, PortalOffer offer, PlayerbotOptions options, out string? why)
        => TryBeginPortal(session, player, offer.PortalSpellId, offer.MageGuid, options, 0, out why);

    /// <summary>
    /// <see cref="TryBeginPortal(WorldSession, Player, PortalOffer, PlayerbotOptions, out string?)"/> for a portal already paid to
    /// <paramref name="prepaidMage"/> (0: none; <see cref="MageLeg.PrepaidMage"/>): that mage is asked, and when it takes the request the fee
    /// is not traded again. The money check is skipped for it.
    /// </summary>
    internal static bool TryBeginPortal(WorldSession session, Player player, PortalOffer offer, PlayerbotOptions options, ulong prepaidMage, out string? why)
        => TryBeginPortal(session, player, offer.PortalSpellId, prepaidMage != 0 ? prepaidMage : offer.MageGuid, options, prepaidMage, out why);

    /// <summary>
    /// Post a request for the portal <paramref name="portalSpellId"/> on the board and begin the requester's leg; false (with why) when the
    /// service is off, the bot is busy or dead, the portal is not a known capital's or leads nowhere in the data, the fee cannot be paid (too
    /// little money, or no trade on this server), the portal is party-only and the bot is in a party already, or the board is full. A leg under
    /// way answers true.
    /// </summary>
    internal static bool TryBeginPortal(WorldSession session, Player player, uint portalSpellId, PlayerbotOptions options, out string? why)
        => TryBeginPortal(session, player, portalSpellId, 0, options, 0, out why);

    private static bool TryBeginPortal(WorldSession session, Player player, uint portalSpellId, ulong mage, PlayerbotOptions options, ulong prepaidMage,
        out string? why)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        why = null;
        if (IsActive(player)) return true;
        uint fee = options.Travel.MageServiceFeeCopper;
        int index = PortalIndex(portalSpellId);
        if (!options.Travel.MageService) why = "the mage service is off";
        else if (!player.IsInWorld || player.Map is null || !player.IsAlive) why = "not alive in the world";
        else if (player.Combat.IsInCombat) why = "in combat";
        else if (index < 0 || !MayUse(player, Portals[index])) why = $"spell {portalSpellId} is not a portal of the bot's faction";
        else if (PortalDestination(session, player, portalSpellId) is not { } destination) why = $"portal {portalSpellId} leads nowhere in the data";
        else if (fee > 0 && prepaidMage == 0 && player.Money < fee) why = $"cannot pay the {fee} copper fee";
        else if (fee > 0 && session.Services.GetService<EconomyFeature>() is not { Enabled: true }) why = "a fee is asked but trading is off on this server";
        else if (PortalObject(session, player, portalSpellId)!.GetData(GameObjectMapSystem.SpellCasterPartyOnlyData) != 0
                 && (Groups(session) is not { } groups || groups.GetGroup(player.Guid) is not null || groups.GetInvite(player.Guid) is not null))
            why = "the portal is party-only and the bot is in another party";
        else if (PlayerbotPortalBoard.Post(session, player, portalSpellId, mage, fee) is null) why = "the portal board is full";
        else
        {
            Legs.AddOrUpdate(player, new MageLeg(TravelMeans.MagePortal, portalSpellId, destination, session.World.NowMs)
            {
                Stage = MageLegStage.WaitMatch,
                PrepaidMage = prepaidMage,
            });
            return true;
        }

        why = "mage: " + why;
        return false;
    }

    /// <summary>The bot's leg, done or not (inspection and tests).</summary>
    internal static MageLeg? LegOf(Player player) => Legs.TryGetValue(player, out MageLeg? leg) ? leg : null;

    /// <summary>A mage's service state (inspection and tests).</summary>
    internal static MageServe? ServeOf(Player player) => Serves.TryGetValue(player, out MageServe? serve) ? serve : null;

    /// <summary>Whether a mage leg (own Teleport or a portal asked for) is under way.</summary>
    internal static bool IsActive(Player player)
        => Legs.TryGetValue(player, out MageLeg? leg) && leg.Stage is not (MageLegStage.Done or MageLegStage.Failed);

    /// <summary>Whether the mage serves a request now.</summary>
    internal static bool IsServing(Player player) => Serves.TryGetValue(player, out MageServe? serve) && serve.Stage != MageServeStage.Idle;

    /// <summary>The reason the bot's last leg failed (inspection), null after one that arrived.</summary>
    internal static string? LastFailure(Player player)
        => Legs.TryGetValue(player, out MageLeg? leg) && leg.Stage == MageLegStage.Failed ? leg.Failure : null;

    /// <summary>
    /// Drop the bot's leg (death, combat, a controller taking it over, the trip given up): a running Teleport cast is cancelled (CMSG_CANCEL_CAST),
    /// an open fee trade cancelled (CMSG_CANCEL_TRADE), the request withdrawn from the board, and a party joined for the portal left. No report.
    /// </summary>
    internal static void Cancel(WorldSession session, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (!Legs.TryGetValue(player, out MageLeg? leg)) return;
        if (leg.Stage is not (MageLegStage.Done or MageLegStage.Failed)) EndLeg(session, player, leg, "cancelled by the requester");
        Legs.Remove(player);
    }

    /// <summary>
    /// One think of the bot's mage leg, before anything else that moves it: its own Teleport, or the portal it asked for.
    /// <see cref="TravelLegStep.Idle"/>: no leg (or one dropped: the bot died, or the switch went off or combat began at a safe point).
    /// <see cref="TravelLegStep.Traveling"/>: under way, nothing else moves the bot. <see cref="TravelLegStep.Arrived"/> and
    /// <see cref="TravelLegStep.Failed"/> are answered once; <paramref name="failure"/> then says why.
    /// </summary>
    internal static TravelLegStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        failure = null;
        if (!Legs.TryGetValue(player, out MageLeg? leg) || leg.Stage is MageLegStage.Done or MageLegStage.Failed) return TravelLegStep.Idle;
        if (!player.IsInWorld || player.Map is null) return TravelLegStep.Traveling;
        if (!player.IsAlive)
        {
            Cancel(session, player);
            return TravelLegStep.Idle;
        }

        if (session.Services.GetService<SpellFeature>()?.System is not { } spells)
            return FailLeg(session, player, leg, "services-unavailable", "the spell system is not available", AnomalySeverity.Warning, out failure);
        uint now = session.World.NowMs;
        PlayerbotThinkProbe.Phase("mage");
        return leg.Means == TravelMeans.MageTeleport
            ? DriveTeleport(session, player, options, spells, leg, now, out failure)
            : DrivePortal(session, player, options, spells, leg, elapsedMs, now, out failure);
    }

    private static TravelLegStep DriveTeleport(WorldSession session, Player player, PlayerbotOptions options, SpellSystem spells, MageLeg leg, uint now,
        out string? failure)
    {
        failure = null;
        TravelCastLeg cast = leg.Cast!;
        if (cast.Stage == TravelCastStage.Start)
        {
            if (!options.Travel.MageService || player.Combat.IsInCombat)
            {
                Legs.Remove(player);
                return TravelLegStep.Idle;
            }

            if (cast.Attempts == 0) cast.BeganMs = now;
            if (unchecked(now - cast.BeganMs) > PlayerbotTravelCast.StartBoundMs)
                return FailLeg(session, player, leg, "teleport-cast-refused",
                    $"the Teleport cast was not taken within {PlayerbotTravelCast.StartBoundMs} ms ({cast.Attempts} attempts)", AnomalySeverity.Warning, out failure);

            SpellInfo? spell = spells.Store.Get(leg.SpellId);
            switch (PlayerbotTravelCast.Send(session, player, spells, cast, WorldOpcode.CmsgCastSpell,
                        PlayerbotTravelCast.CastPayload(leg.SpellId, SpellCastTargets.ForSelf()), out SpellCastResult? reason))
            {
                case PlayerbotTravelCast.Sent.Taken:
                    cast.Stage = TravelCastStage.Casting;
                    cast.CastMs = now;
                    cast.BudgetMs = PlayerbotTravelCast.BudgetOf(spell, player);
                    return TravelLegStep.Traveling;
                case PlayerbotTravelCast.Sent.Retry:
                    return TravelLegStep.Traveling;
                case PlayerbotTravelCast.Sent.BudgetStarved:
                    return FailLeg(session, player, leg, "teleport-cast-refused", "the action budget never allowed the Teleport cast", AnomalySeverity.Warning, out failure);
                case PlayerbotTravelCast.Sent.NotStill:
                    return FailLeg(session, player, leg, "teleport-cast-refused", "the bot could not be stood still for the Teleport cast", AnomalySeverity.Warning, out failure);
                case PlayerbotTravelCast.Sent.SessionRefused:
                    return FailLeg(session, player, leg, "teleport-cast-refused", "the session refused the Teleport cast", AnomalySeverity.Warning, out failure);
                default:
                    return FailLeg(session, player, leg, "teleport-cast-refused",
                        $"the server refused Teleport {leg.SpellId}{(reason is { } r ? ": " + r : string.Empty)}", AnomalySeverity.Warning, out failure);
            }
        }

        // Attacked during the cast: the cast is cancelled and the leg dropped (no report), so the brain fights; the journey begins it again
        // after the fight. A teleport the cast already began is the server's and goes on.
        if (player.Combat.IsInCombat && !PlayerbotTravelCast.IsTeleporting(session, player))
        {
            Cancel(session, player);
            return TravelLegStep.Idle;
        }

        switch (PlayerbotTravelCast.Watch(session, player, spells, cast, now))
        {
            case PlayerbotTravelCast.Seen.Arrived:
                cast.Stage = TravelCastStage.Done;
                leg.Stage = MageLegStage.Done;
                return TravelLegStep.Arrived;
            case PlayerbotTravelCast.Seen.Interrupted:
                return FailLeg(session, player, leg, "cast-interrupted",
                    $"Teleport {leg.SpellId} ended without a teleport{(cast.FailReason is { } why ? $" ({why})" : string.Empty)}",
                    player.Combat.IsInCombat ? AnomalySeverity.Info : AnomalySeverity.Warning, out failure);
            case PlayerbotTravelCast.Seen.NotArrived:
                return FailLeg(session, player, leg, "not-arrived",
                    $"Teleport {leg.SpellId} went off but the bot stands at map {player.MapId} ({player.X:F0},{player.Y:F0},{player.Z:F0}), not at {PlayerbotHearthstone.Describe(leg.Destination)}",
                    AnomalySeverity.Warning, out failure);
            case PlayerbotTravelCast.Seen.Overdue:
                PlayerbotTravelCast.CancelCast(session, player, leg.SpellId);
                return FailLeg(session, player, leg, "not-arrived", $"Teleport {leg.SpellId} or its teleport still runs {unchecked(now - cast.CastMs)} ms after the cast",
                    AnomalySeverity.Warning, out failure);
            default:
                return TravelLegStep.Traveling;
        }
    }

    private static TravelLegStep DrivePortal(WorldSession session, Player player, PlayerbotOptions options, SpellSystem spells, MageLeg leg, uint elapsedMs,
        uint now, out string? failure)
    {
        failure = null;
        _ = spells;
        if (leg.Stage != MageLegStage.Teleporting)
        {
            // Safe points: before the portal is clicked, the switch going off or a fight drops the leg without a report.
            if (!options.Travel.MageService || player.Combat.IsInCombat)
            {
                Cancel(session, player);
                return TravelLegStep.Idle;
            }

            if (PlayerbotPortalBoard.Match(session, player, options) is not { } request)
                return FailLeg(session, player, leg, "request-lost", "the portal request is no longer on the board", AnomalySeverity.Warning, out failure);
            switch (request.State)
            {
                case PortalRequestState.Expired:
                    return FailLeg(session, player, leg, "match-expired", $"no mage took the request for portal {leg.SpellId} within {PlayerbotPortalBoard.MatchWaitMs} ms",
                        AnomalySeverity.Info, out failure);
                case PortalRequestState.Failed:
                    // The mage's side said why (and reported it); a request the board timed out is the requester's to report.
                    return request.Failure == "the matched mage did not open the portal in time"
                        ? FailLeg(session, player, leg, "portal-not-opened", $"mage {request.MageGuid:X} matched the request but opened no portal in time", AnomalySeverity.Warning, out failure)
                        : Quit(session, player, leg, "the mage gave up: " + request.Failure, out failure);
                case PortalRequestState.Done:
                    return Quit(session, player, leg, "the request ended before the bot went through", out failure);
                case PortalRequestState.Open:
                    return TravelLegStep.Traveling;
            }

            leg.MageGuid = request.MageGuid;
            // The mage this bot paid already for this portal (the paid leg was dropped by a fight): its request is paid, no second trade.
            if (!request.Paid && leg.PrepaidMage != 0 && request.MageGuid == leg.PrepaidMage) request.Paid = true;
            // Another mage took the request of a portal already paid to the first: this one is not paid, and the fee is not traded a second
            // time here (the journey checked no spend for it). The leg ends unreported; the journey's replan prices a new portal against what
            // it may still spend.
            if (!request.Paid && leg.PrepaidMage != 0 && request.MageGuid != leg.PrepaidMage && request.FeeCopper > 0)
                return Quit(session, player, leg, $"mage {request.MageGuid:X} took the request, but the fee was paid to mage {leg.PrepaidMage:X}", out failure);
            if (Online(session, request.MageGuid) is not { IsInWorld: true } mage || mage.MapId != player.MapId)
                return FailLeg(session, player, leg, "mage-gone", $"matched mage {request.MageGuid:X} left the map", AnomalySeverity.Info, out failure);
            AcceptMageInvite(session, player, leg);
            if (leg.Stage == MageLegStage.WaitMatch) Enter(leg, MageLegStage.ToMage, now);
            if (request.State == PortalRequestState.PortalOpen && leg.Stage is MageLegStage.ToMage or MageLegStage.WaitPortal) Enter(leg, MageLegStage.ToPortal, now);
            return leg.Stage switch
            {
                MageLegStage.ToMage => ToMage(session, player, options, leg, request, mage, elapsedMs, now, out failure),
                MageLegStage.Paying => Pay(session, player, leg, request, mage, now, out failure),
                MageLegStage.WaitPortal => WaitPortal(session, player, leg, request, mage, now),
                MageLegStage.ToPortal => ToPortal(session, player, options, leg, request, elapsedMs, now, out failure),
                _ => TravelLegStep.Traveling,
            };
        }

        // Through the portal: the teleport the spell caster's spell began.
        bool teleporting = PlayerbotTravelCast.IsTeleporting(session, player);
        if (teleporting && session.Services.GetService<ArcaneCore.World.Teleport.TeleportFeature>()?.Teleports.IsBeingTeleportedNear(player) == true)
        {
            PlayerbotMovementControl.Update(session, player);
            teleporting = PlayerbotTravelCast.IsTeleporting(session, player);
        }

        if (!teleporting && PlayerbotTravelCast.IsAt(player, leg.Destination))
        {
            if (PlayerbotPortalBoard.RequestOf(session, player.Guid.Value) is { } done) PlayerbotPortalBoard.Complete(session, done);
            LeaveJoinedGroup(session, player, leg);
            leg.Stage = MageLegStage.Done;
            return TravelLegStep.Arrived;
        }

        if (!teleporting || unchecked(now - leg.StageMs) > TeleportWaitMs)
            return FailLeg(session, player, leg, "not-arrived",
                $"the bot used portal {leg.SpellId} but stands at map {player.MapId} ({player.X:F0},{player.Y:F0},{player.Z:F0}), not at {PlayerbotHearthstone.Describe(leg.Destination)}",
                AnomalySeverity.Warning, out failure);
        return TravelLegStep.Traveling;
    }

    private static TravelLegStep ToMage(WorldSession session, Player player, PlayerbotOptions options, MageLeg leg, PortalRequest request, Player mage,
        uint elapsedMs, uint now, out string? failure)
    {
        failure = null;
        Vector3 at = new(mage.X, mage.Y, mage.Z);
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), at) <= MeetYards)
        {
            PlayerbotMovementControl.Stop(session, player);
            leg.Walk.Reset();
            if (!request.Paid)
            {
                leg.MoneyBefore = player.Money;
                Enter(leg, MageLegStage.Paying, now);
                return Pay(session, player, leg, request, mage, now, out failure);
            }

            Enter(leg, MageLegStage.WaitPortal, now);
            return TravelLegStep.Traveling;
        }

        return leg.Walk.Step(session, player, options, at, elapsedMs, now)
            ? TravelLegStep.Traveling
            : FailLeg(session, player, leg, "mage-unreachable", $"no way to mage {mage.Name} ({TravelWalk.MaxRefusedPlans} refused plans or no progress)", AnomalySeverity.Warning, out failure);
    }

    /// <summary>
    /// The fee by a real trade (EconomyHandlers): CMSG_INITIATE_TRADE to the mage, CMSG_SET_TRADE_GOLD with the fee once the mage opened the
    /// window, CMSG_ACCEPT_TRADE (again after a scam-prevention bounce); paid when the trade closed and the bot's money fell by the fee.
    /// </summary>
    private static TravelLegStep Pay(WorldSession session, Player player, MageLeg leg, PortalRequest request, Player mage, uint now, out string? failure)
    {
        failure = null;
        if (session.Services.GetService<EconomyFeature>() is not { Enabled: true } economy)
            return FailLeg(session, player, leg, "fee-trade-failed", "trading is off on this server", AnomalySeverity.Warning, out failure);
        if (BotSocial.PlayerbotTradeDesk.OwnsWindow(session, player, mage.Guid))
            return TravelLegStep.Traveling; // the trade desk's window or deal with the mage: no fee window beside it, nothing set, accepted or cancelled
        TradeSession? trade = economy.TradeOf(player);
        if (trade is null && leg.MoneyBefore >= player.Money && leg.MoneyBefore - player.Money >= request.FeeCopper)
        {
            request.Paid = true;
            leg.PaidCopper = request.FeeCopper;
            Enter(leg, MageLegStage.WaitPortal, now);
            return TravelLegStep.Traveling;
        }

        if (unchecked(now - leg.StageMs) > PayWaitMs)
        {
            if (trade is { Settling: false }) session.TryManagedAction(WorldOpcode.CmsgCancelTrade, []);
            return FailLeg(session, player, leg, "fee-trade-failed", $"the {request.FeeCopper} copper fee to mage {mage.Name} did not settle within {PayWaitMs} ms",
                AnomalySeverity.Warning, out failure);
        }

        if (trade is null)
        {
            if (leg.LastTradeMs == 0 || unchecked(now - leg.LastTradeMs) > TradeRetryMs)
            {
                leg.LastTradeMs = now;
                session.TryManagedAction(WorldOpcode.CmsgInitiateTrade, PlayerbotNavigation.GuidPayload(mage.Guid.Value));
            }

            return TravelLegStep.Traveling;
        }

        if (!trade.Involves(mage))
        {
            session.TryManagedAction(WorldOpcode.CmsgCancelTrade, []); // a trade with somebody else: not this fee
            return TravelLegStep.Traveling;
        }

        if (!trade.Opened || trade.Settling) return TravelLegStep.Traveling;
        TradeSide mine = trade.SideOf(player);
        if (mine.Gold != request.FeeCopper) session.TryManagedAction(WorldOpcode.CmsgSetTradeGold, BitConverter.GetBytes(request.FeeCopper));
        else if (!mine.Accepted) session.TryManagedAction(WorldOpcode.CmsgAcceptTrade, []);
        return TravelLegStep.Traveling;
    }

    private static TravelLegStep WaitPortal(WorldSession session, Player player, MageLeg leg, PortalRequest request, Player mage, uint now)
    {
        _ = session;
        _ = request;
        // The mage walks off for a rune: follow it again.
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), new Vector3(mage.X, mage.Y, mage.Z)) > CastWhenNearYards) Enter(leg, MageLegStage.ToMage, now);
        return TravelLegStep.Traveling;
    }

    private static TravelLegStep ToPortal(WorldSession session, Player player, PlayerbotOptions options, MageLeg leg, PortalRequest request, uint elapsedMs,
        uint now, out string? failure)
    {
        failure = null;
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects || objects.Find(request.PortalGuid) is not { IsSpawned: true } portal)
            return FailLeg(session, player, leg, "portal-vanished", $"the portal {request.PortalGuid} mage {request.MageGuid:X} opened is gone", AnomalySeverity.Warning, out failure);
        Vector3 at = new(portal.X, portal.Y, portal.Z);
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), at) > UseYards)
        {
            return leg.Walk.Step(session, player, options, at, elapsedMs, now)
                ? TravelLegStep.Traveling
                : FailLeg(session, player, leg, "portal-unreachable", $"no way to the portal {request.PortalGuid}", AnomalySeverity.Warning, out failure);
        }

        if (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)) return TravelLegStep.Traveling;
        if (portal.Template.GetData(GameObjectMapSystem.SpellCasterPartyOnlyData) != 0
            && Groups(session)?.AreInSameGroup(player.Guid, new ObjectGuid(request.MageGuid)) != true)
        {
            // Party-only: the mage invites; the bot accepts it (AcceptMageInvite) and clicks on a later think.
            return unchecked(now - leg.StageMs) > InviteWaitMs
                ? FailLeg(session, player, leg, "portal-use-refused", $"the portal is party-only and the bot is not in mage {request.MageGuid:X}'s party after {InviteWaitMs} ms",
                    AnomalySeverity.Warning, out failure)
                : TravelLegStep.Traveling;
        }

        if (!session.TryManagedAction(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(portal.Guid.Value)))
        {
            return session.ManagedBudget is { Remaining: <= 0 }
                ? TravelLegStep.Traveling
                : FailLeg(session, player, leg, "portal-use-refused", "the session refused the portal click", AnomalySeverity.Warning, out failure);
        }

        if (!PlayerbotTravelCast.IsTeleporting(session, player) && !PlayerbotTravelCast.IsAt(player, leg.Destination))
            return FailLeg(session, player, leg, "portal-use-refused", $"the server did not teleport the bot through portal {portal.Guid}", AnomalySeverity.Warning, out failure);
        Enter(leg, MageLegStage.Teleporting, now);
        return TravelLegStep.Traveling;
    }

    /// <summary>A party invitation from the matched mage is accepted (CMSG_GROUP_ACCEPT); an invitation from anyone else is left alone.</summary>
    private static void AcceptMageInvite(WorldSession session, Player player, MageLeg leg)
    {
        if (Groups(session) is not { } groups || groups.GetInvite(player.Guid) is not { } group || group.LeaderGuid.Value != leg.MageGuid) return;
        session.TryManagedAction(WorldOpcode.CmsgGroupAccept, []);
        if (groups.AreInSameGroup(player.Guid, group.LeaderGuid)) leg.JoinedGroup = true;
    }

    private static void LeaveJoinedGroup(WorldSession session, Player player, MageLeg leg)
    {
        if (!leg.JoinedGroup) return;
        leg.JoinedGroup = false;
        if (Groups(session)?.GetGroup(player.Guid) is not null) session.TryManagedAction(WorldOpcode.CmsgGroupDisband, []);
    }

    private static void Enter(MageLeg leg, MageLegStage stage, uint now)
    {
        leg.Stage = stage;
        leg.StageMs = now;
        leg.Walk.Reset();
    }

    /// <summary>The leg's cleanup: a running cast cancelled, an open trade cancelled, the request withdrawn, a joined party left.</summary>
    private static void EndLeg(WorldSession session, Player player, MageLeg leg, string why)
    {
        if (leg.Cast is { Stage: TravelCastStage.Casting }) PlayerbotTravelCast.CancelCast(session, player, leg.SpellId);
        if (leg.Means == TravelMeans.MagePortal)
        {
            if (session.Services.GetService<EconomyFeature>()?.TradeOf(player) is { Settling: false } open
                && !BotSocial.PlayerbotTradeDesk.OwnsWindow(session, player, open.OtherSide(player).Player.Guid)) session.TryManagedAction(WorldOpcode.CmsgCancelTrade, []);
            if (PlayerbotPortalBoard.RequestOf(session, player.Guid.Value) is { IsLive: true } request) PlayerbotPortalBoard.Fail(session, request, why);
            LeaveJoinedGroup(session, player, leg);
        }
    }

    /// <summary>The leg fails with a report (once: the leg ends here).</summary>
    private static TravelLegStep FailLeg(WorldSession session, Player player, MageLeg leg, string code, string why, AnomalySeverity severity, out string? failure)
    {
        PlayerbotAnomalies.Report(player, Area, code, why, severity);
        return Quit(session, player, leg, why, out failure);
    }

    /// <summary>The leg fails without a report (the other side reported it).</summary>
    private static TravelLegStep Quit(WorldSession session, Player player, MageLeg leg, string why, out string? failure)
    {
        EndLeg(session, player, leg, why);
        if (leg.Means == TravelMeans.MagePortal) PlayerbotMovementControl.Stop(session, player);
        leg.Stage = MageLegStage.Failed;
        leg.Failure = "mage: " + why;
        failure = leg.Failure;
        return TravelLegStep.Failed;
    }

    // --- the mage serving the board ----------------------------------------------------------

    /// <summary>
    /// One think of a mage bot's service, called at every think of a bot that may be a mage (cheap when it is not: a few spellbook probes).
    /// <see cref="TravelLegStep.Idle"/>: nothing to serve (an idle mage was listed on the board; the brain goes on).
    /// <see cref="TravelLegStep.Traveling"/>: serving a request, nothing else moves the bot this think. <see cref="TravelLegStep.Arrived"/>:
    /// the requester went through (once). <see cref="TravelLegStep.Failed"/>: the service ended without it (once; reported when the failure is
    /// the mage's).
    /// </summary>
    internal static TravelLegStep DriveMage(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        MageServe serve = Serves.GetOrCreateValue(player);
        if (!player.IsInWorld || player.Map is null) return serve.Stage == MageServeStage.Idle ? TravelLegStep.Idle : TravelLegStep.Traveling;
        ulong me = player.Guid.Value;
        if (!player.IsAlive || !options.Travel.MageService || session.Services.GetService<SpellFeature>() is not { } feature)
        {
            StopServing(session, player, serve, !player.IsAlive ? "the mage died" : "the mage service is off");
            PlayerbotPortalBoard.Unlist(session, me);
            return TravelLegStep.Idle;
        }

        uint now = session.World.NowMs;
        if (serve.Stage == MageServeStage.Idle)
        {
            if (PlayerbotPortalBoard.RequestFor(session, me) is not { } matched)
            {
                if (Listing(session, player, feature, out byte known, out byte ready, out bool canBuy)) PlayerbotPortalBoard.List(session, player, known, ready, canBuy);
                else PlayerbotPortalBoard.Unlist(session, me);
                return TravelLegStep.Idle;
            }

            serve.Reset();
            serve.Requester = matched.Requester;
            serve.Stage = MageServeStage.Prepare;
            serve.StageMs = now;
        }

        if (PlayerbotPortalBoard.RequestOf(session, serve.Requester) is not { } request || request.MageGuid != me)
        {
            serve.Reset();
            return TravelLegStep.Idle;
        }

        if (request.State == PortalRequestState.Done)
        {
            LeaveFormedGroup(session, player, serve);
            serve.Reset();
            return TravelLegStep.Arrived;
        }

        if (!request.IsLive)
        {
            // The requester gave up (it reported its own failure, if any): nothing for the mage to report.
            LeaveFormedGroup(session, player, serve);
            if (serve.Cast is { Stage: TravelCastStage.Casting }) PlayerbotTravelCast.CancelCast(session, player, request.PortalSpellId);
            serve.Reset();
            return TravelLegStep.Idle;
        }

        if (player.Combat.IsInCombat)
            return ServeFail(session, player, serve, request, null, "the mage was attacked while serving", AnomalySeverity.Info);
        PlayerbotThinkProbe.Phase("mage-serve");
        Player? requester = Online(session, serve.Requester) is { IsInWorld: true } found && found.MapId == player.MapId ? found : null;
        if (serve.Stage == MageServeStage.WaitUse) return WaitUse(session, player, serve, request, requester, now);
        if (requester is null) return ServeFail(session, player, serve, request, null, "the requester left the map", AnomalySeverity.Info);
        return serve.Stage switch
        {
            MageServeStage.Prepare => Prepare(session, player, feature, serve, request, now),
            MageServeStage.ToVendor => BuyRune(session, player, options, feature, serve, request, elapsedMs, now),
            MageServeStage.WaitRequester => WaitRequester(session, player, serve, request, requester, now),
            MageServeStage.WaitPaid => WaitPaid(session, player, serve, request, requester, now),
            MageServeStage.Cast => CastPortal(session, player, feature.System, serve, request, now),
            MageServeStage.Casting => WatchPortalCast(session, player, feature.System, serve, request, now),
            _ => TravelLegStep.Traveling,
        };
    }

    /// <summary>
    /// Drop the mage's service (death, a controller taking it over): a running portal cast is cancelled, the request fails for the requester
    /// to see, a party formed for it is left, the mage is taken off the board. No report.
    /// </summary>
    internal static void CancelMage(WorldSession session, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        if (Serves.TryGetValue(player, out MageServe? serve)) StopServing(session, player, serve, "the mage was taken over");
        PlayerbotPortalBoard.Unlist(session, player.Guid.Value);
    }

    /// <summary>Whether the mage is idle (alive, out of combat, not grouped or invited, on no trip of its own) and what it can offer.</summary>
    private static bool Listing(WorldSession session, Player player, SpellFeature feature, out byte known, out byte ready, out bool canBuy)
    {
        known = 0;
        ready = 0;
        canBuy = false;
        if (player.Combat.IsInCombat || IsActive(player) || PlayerbotHearthstone.IsActive(player) || PlayerbotTaxi.IsActive(player)
            || PlayerbotTaxi.IsFlying(player) || PlayerbotTravelCast.IsTeleporting(session, player)) return false;
        if (Groups(session) is { } groups && (groups.GetGroup(player.Guid) is not null || groups.GetInvite(player.Guid) is not null)) return false;
        uint rune = 0;
        for (int i = 0; i < Portals.Length; i++)
        {
            MageCapital capital = Portals[i];
            if (!MayUse(player, capital) || !feature.Spellbook.HasSpell(player, capital.PortalSpell) || feature.System.Store.Get(capital.PortalSpell) is not { } spell) continue;
            known |= (byte)(1 << i);
            if (HasReagents(player, spell) && Ready(feature.System, player, spell)) ready |= (byte)(1 << i);
            else if (rune == 0) rune = RuneOf(spell);
        }

        if (known == 0) return false;
        canBuy = rune != 0 && FindRuneVendor(session, player, rune) is not null;
        return true;
    }

    private static TravelLegStep Prepare(WorldSession session, Player player, SpellFeature feature, MageServe serve, PortalRequest request, uint now)
    {
        PlayerbotMovementControl.Stop(session, player);
        if (!feature.Spellbook.HasSpell(player, request.PortalSpellId) || feature.System.Store.Get(request.PortalSpellId) is not { } spell)
            return ServeFail(session, player, serve, request, null, $"the mage no longer knows portal {request.PortalSpellId}", AnomalySeverity.Info);
        if (HasReagents(player, spell))
        {
            Enter(serve, MageServeStage.WaitRequester, now);
            return TravelLegStep.Traveling;
        }

        uint rune = RuneOf(spell);
        if (FindRuneVendor(session, player, rune) is not { } vendor)
            return ServeFail(session, player, serve, request, "rune-purchase-failed", $"no rune {rune} for portal {request.PortalSpellId} and no vendor that stocks it within {MaxVendorYards} yards",
                AnomalySeverity.Warning);
        serve.VendorEntry = vendor.Entry;
        serve.VendorAt = new Vector3(vendor.Spawn.X, vendor.Spawn.Y, vendor.Spawn.Z);
        Enter(serve, MageServeStage.ToVendor, now);
        return TravelLegStep.Traveling;
    }

    /// <summary>
    /// Walk to the rune vendor's spawn; once a living vendor of that entry is in sight and in interaction range (the server's own check), buy one
    /// rune with CMSG_BUY_ITEM. A purchase that added no rune, no way to the vendor, or a trip past <see cref="VendorTripMs"/> fails the service.
    /// </summary>
    private static TravelLegStep BuyRune(WorldSession session, Player player, PlayerbotOptions options, SpellFeature feature, MageServe serve, PortalRequest request,
        uint elapsedMs, uint now)
    {
        if (feature.System.Store.Get(request.PortalSpellId) is not { } spell)
            return ServeFail(session, player, serve, request, null, $"portal {request.PortalSpellId} left the spell data", AnomalySeverity.Info);
        if (HasReagents(player, spell))
        {
            PlayerbotMovementControl.Stop(session, player);
            Enter(serve, MageServeStage.WaitRequester, now);
            return TravelLegStep.Traveling;
        }

        uint rune = RuneOf(spell);
        if (unchecked(now - serve.StageMs) > VendorTripMs)
            return ServeFail(session, player, serve, request, "rune-purchase-failed", $"no rune {rune} bought within {VendorTripMs} ms", AnomalySeverity.Warning);
        if (VisibleVendor(session, player, serve.VendorEntry) is { } vendor)
        {
            if (!PlayerbotMovementControl.Stop(session, player) || player.Movement.HasFlag(MovementFlags.MaskMoving)) return TravelLegStep.Traveling;
            uint before = player.Inventory.GetItemCount(rune);
            if (!session.TryManagedAction(WorldOpcode.CmsgBuyItem, BuyPayload(vendor.Guid, rune)))
            {
                return session.ManagedBudget is { Remaining: <= 0 }
                    ? TravelLegStep.Traveling
                    : ServeFail(session, player, serve, request, "rune-purchase-failed", "the session refused the rune purchase", AnomalySeverity.Warning);
            }

            return player.Inventory.GetItemCount(rune) > before
                ? TravelLegStep.Traveling // the next think goes back to the requester
                : ServeFail(session, player, serve, request, "rune-purchase-failed", $"vendor {vendor.Entry} sold no rune {rune} (money {player.Money})", AnomalySeverity.Warning);
        }

        return serve.Walk.Step(session, player, options, serve.VendorAt, elapsedMs, now)
            ? TravelLegStep.Traveling
            : ServeFail(session, player, serve, request, "rune-purchase-failed", $"no way to rune vendor {serve.VendorEntry}", AnomalySeverity.Warning);
    }

    private static TravelLegStep WaitRequester(WorldSession session, Player player, MageServe serve, PortalRequest request, Player requester, uint now)
    {
        PlayerbotMovementControl.Stop(session, player);
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), new Vector3(requester.X, requester.Y, requester.Z)) <= CastWhenNearYards)
        {
            Enter(serve, request.Paid ? MageServeStage.Cast : MageServeStage.WaitPaid, now);
            return TravelLegStep.Traveling;
        }

        return unchecked(now - serve.StageMs) > RequesterWaitMs
            ? ServeFail(session, player, serve, request, null, $"the requester did not come within {RequesterWaitMs} ms", AnomalySeverity.Info)
            : TravelLegStep.Traveling;
    }

    /// <summary>The mage's side of the fee trade: CMSG_BEGIN_TRADE on the requester's invitation, CMSG_ACCEPT_TRADE once the fee is offered and accepted.</summary>
    private static TravelLegStep WaitPaid(WorldSession session, Player player, MageServe serve, PortalRequest request, Player requester, uint now)
    {
        if (request.Paid)
        {
            Enter(serve, MageServeStage.Cast, now);
            return TravelLegStep.Traveling;
        }

        if (unchecked(now - serve.StageMs) > PayWaitMs + 5_000)
            return ServeFail(session, player, serve, request, null, "the fee was not paid", AnomalySeverity.Info);
        if (session.Services.GetService<EconomyFeature>()?.TradeOf(player) is { Settling: false } trade && trade.Involves(requester)
            && !BotSocial.PlayerbotTradeDesk.OwnsWindow(session, player, requester.Guid))
        {
            if (!trade.Opened && ReferenceEquals(trade.Target.Player, player)) session.TryManagedAction(WorldOpcode.CmsgBeginTrade, []);
            else if (trade.Opened && trade.OtherSide(player) is { Accepted: true } theirs && theirs.Gold >= request.FeeCopper && !trade.SideOf(player).Accepted)
                session.TryManagedAction(WorldOpcode.CmsgAcceptTrade, []);
        }

        return TravelLegStep.Traveling;
    }

    private static TravelLegStep CastPortal(WorldSession session, Player player, SpellSystem spells, MageServe serve, PortalRequest request, uint now)
    {
        serve.Cast ??= new TravelCastLeg(TravelMeans.MagePortal, request.PortalSpellId, default, now);
        if (unchecked(now - serve.Cast.BeganMs) > PlayerbotTravelCast.StartBoundMs)
            return ServeFail(session, player, serve, request, "portal-cast-refused",
                $"the portal {request.PortalSpellId} cast was not taken within {PlayerbotTravelCast.StartBoundMs} ms ({serve.Cast.Attempts} attempts)", AnomalySeverity.Warning);
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is { } objects && serve.Before.Count == 0) CollectPortals(objects, player, request.PortalSpellId, serve.Before);
        switch (PlayerbotTravelCast.Send(session, player, spells, serve.Cast, WorldOpcode.CmsgCastSpell,
                    PlayerbotTravelCast.CastPayload(request.PortalSpellId, SpellCastTargets.ForSelf()), out SpellCastResult? reason))
        {
            case PlayerbotTravelCast.Sent.Taken:
                serve.Cast.Stage = TravelCastStage.Casting;
                serve.Cast.CastMs = now;
                serve.Cast.BudgetMs = PlayerbotTravelCast.BudgetOf(spells.Store.Get(request.PortalSpellId), player);
                Enter(serve, MageServeStage.Casting, now);
                return TravelLegStep.Traveling;
            case PlayerbotTravelCast.Sent.Retry:
                return TravelLegStep.Traveling;
            default:
                return ServeFail(session, player, serve, request, "portal-cast-refused",
                    $"the server refused portal {request.PortalSpellId}{(reason is { } r ? ": " + r : string.Empty)}", AnomalySeverity.Warning);
        }
    }

    private static TravelLegStep WatchPortalCast(WorldSession session, Player player, SpellSystem spells, MageServe serve, PortalRequest request, uint now)
    {
        TravelCastLeg cast = serve.Cast!;
        if (PlayerbotTravelCast.IsCasting(spells, player, request.PortalSpellId))
        {
            if (unchecked(now - cast.CastMs) <= cast.BudgetMs) return TravelLegStep.Traveling;
            PlayerbotTravelCast.CancelCast(session, player, request.PortalSpellId);
            return ServeFail(session, player, serve, request, "cast-interrupted", $"portal {request.PortalSpellId} still casting {unchecked(now - cast.CastMs)} ms after it began",
                AnomalySeverity.Warning);
        }

        // The cast is over: the new portal is the mage's portal of this spell that did not stand before the cast.
        PlayerbotTravelCast.Observe(session, cast);
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            var standing = new List<ObjectGuid>(2);
            CollectPortals(objects, player, request.PortalSpellId, standing);
            foreach (ObjectGuid guid in standing)
            {
                if (serve.Before.Contains(guid)) continue;
                serve.Portal = guid;
                PlayerbotPortalBoard.PortalOpened(session, request, guid);
                Enter(serve, MageServeStage.WaitUse, now);
                return TravelLegStep.Traveling;
            }
        }

        if (!cast.SawGo)
            return ServeFail(session, player, serve, request, "cast-interrupted",
                $"portal {request.PortalSpellId} ended without going off{(cast.FailReason is { } why ? $" ({why})" : string.Empty)}",
                player.Combat.IsInCombat ? AnomalySeverity.Info : AnomalySeverity.Warning);
        return unchecked(now - serve.StageMs) > PortalSpawnWaitMs
            ? ServeFail(session, player, serve, request, "portal-not-spawned", $"portal {request.PortalSpellId} went off but no portal object of the mage stands", AnomalySeverity.Warning)
            : TravelLegStep.Traveling;
    }

    /// <summary>
    /// The portal stands: a party-only portal needs the requester in the mage's party (CMSG_GROUP_INVITE by name, again after
    /// <see cref="InviteRetryMs"/>, at most <see cref="MaxInvites"/> times); then wait for the requester to go through (the board's Done is
    /// handled by <see cref="DriveMage"/>), at most <see cref="PortalUseWaitMs"/>.
    /// </summary>
    private static TravelLegStep WaitUse(WorldSession session, Player player, MageServe serve, PortalRequest request, Player? requester, uint now)
    {
        if (player.Map?.FindUpdater<GameObjectMapSystem>()?.Find(serve.Portal) is not { IsSpawned: true } portal)
        {
            // The requester went through and the portal lapsed before the board said so; or it vanished first.
            return request.State == PortalRequestState.PortalOpen && unchecked(now - serve.StageMs) > PortalUseWaitMs
                ? ServeFail(session, player, serve, request, "portal-not-used", "the portal lapsed without the requester going through", AnomalySeverity.Info)
                : TravelLegStep.Traveling;
        }

        GroupManager? groups = Groups(session);
        if (requester is null)
        {
            // Between maps, or gone: the board says how it ends.
            return unchecked(now - serve.StageMs) > PortalUseWaitMs
                ? ServeFail(session, player, serve, request, "portal-not-used", $"the requester did not go through within {PortalUseWaitMs} ms", AnomalySeverity.Info)
                : TravelLegStep.Traveling;
        }

        if (portal.Template.GetData(GameObjectMapSystem.SpellCasterPartyOnlyData) != 0 && groups is not null
            && !groups.AreInSameGroup(player.Guid, requester.Guid) && groups.GetInvite(requester.Guid) is null
            && (serve.Invites == 0 || unchecked(now - serve.LastInviteMs) > InviteRetryMs))
        {
            if (serve.Invites >= MaxInvites)
                return ServeFail(session, player, serve, request, "party-invite-refused", $"{MaxInvites} party invitations to {requester.Name} for the party-only portal were not taken",
                    AnomalySeverity.Warning);
            serve.Invites++;
            serve.LastInviteMs = now;
            var writer = new PacketWriter(16);
            writer.WriteCString(requester.Name);
            session.TryManagedAction(WorldOpcode.CmsgGroupInvite, writer.ToArray());
            if (groups.GetInvite(requester.Guid) is not null || groups.AreInSameGroup(player.Guid, requester.Guid)) serve.FormedGroup = true;
        }

        if (groups is not null && groups.AreInSameGroup(player.Guid, requester.Guid)) serve.FormedGroup = true;
        return unchecked(now - serve.StageMs) > PortalUseWaitMs
            ? ServeFail(session, player, serve, request, "portal-not-used", $"the requester did not go through within {PortalUseWaitMs} ms", AnomalySeverity.Info)
            : TravelLegStep.Traveling;
    }

    /// <summary>The portals of <paramref name="portalSpell"/> the mage owns and that stand now (one walk over the map's objects).</summary>
    private static void CollectPortals(GameObjectMapSystem objects, Player mage, uint portalSpell, List<ObjectGuid> into)
    {
        foreach (GameObject go in objects.GameObjects)
        {
            if (go.IsSpawned && go.SpellId == portalSpell && go.OwnerGuid == mage.Guid) into.Add(go.Guid);
        }
    }

    private static void Enter(MageServe serve, MageServeStage stage, uint now)
    {
        serve.Stage = stage;
        serve.StageMs = now;
        serve.Walk.Reset();
    }

    private static void LeaveFormedGroup(WorldSession session, Player player, MageServe serve)
    {
        if (!serve.FormedGroup) return;
        serve.FormedGroup = false;
        if (Groups(session)?.GetGroup(player.Guid) is not null) session.TryManagedAction(WorldOpcode.CmsgGroupDisband, []);
    }

    private static void StopServing(WorldSession session, Player player, MageServe serve, string why)
    {
        if (serve.Stage == MageServeStage.Idle) return;
        if (serve.Cast is { Stage: TravelCastStage.Casting } && PlayerbotPortalBoard.RequestOf(session, serve.Requester) is { } running)
            PlayerbotTravelCast.CancelCast(session, player, running.PortalSpellId);
        if (PlayerbotPortalBoard.RequestOf(session, serve.Requester) is { IsLive: true } request && request.MageGuid == player.Guid.Value)
            PlayerbotPortalBoard.Fail(session, request, why);
        LeaveFormedGroup(session, player, serve);
        serve.Reset();
    }

    /// <summary>The service ends without the requester going through: reported when <paramref name="code"/> is given (a failure of the mage's own).</summary>
    private static TravelLegStep ServeFail(WorldSession session, Player player, MageServe serve, PortalRequest request, string? code, string why, AnomalySeverity severity)
    {
        if (code is not null) PlayerbotAnomalies.Report(player, Area, code, why, severity);
        PlayerbotMovementControl.Stop(session, player);
        StopServing(session, player, serve, code ?? why);
        _ = request;
        return TravelLegStep.Failed;
    }

    // --- rune vendors ------------------------------------------------------------------------

    /// <summary>
    /// The nearest spawn on the mage's map, within <see cref="MaxVendorYards"/>, of a creature whose vendor list stocks <paramref name="rune"/>
    /// (there is no reagent vendor flag: the stock says it) and that the mage's faction may approach. Null when none.
    /// </summary>
    private static (uint Entry, CreatureSpawn Spawn)? FindRuneVendor(WorldSession session, Player player, uint rune)
    {
        if (rune == 0 || session.Services.GetService<QuestNpcFeature>() is not { Services: { } services } npcFeature
            || session.Services.GetService<CreatureWorldFeature>()?.Content is not { } content) return null;
        VendorIndex index = VendorIndexes.TryGetValue(content, out VendorIndex? cached) && cached.Version == content.DefinitionsVersion
            && ReferenceEquals(cached.Npcs, services.Npcs) ? cached : Build(content, services.Npcs);
        if (!index.ByMapItem.TryGetValue((player.MapId, rune), out List<(uint Entry, uint Faction, CreatureSpawn Spawn)>? spawns)) return null;
        Vector3 here = new(player.X, player.Y, player.Z);
        (uint, CreatureSpawn)? best = null;
        float bestDistance = MaxVendorYards;
        foreach ((uint entry, uint faction, CreatureSpawn spawn) in spawns)
        {
            float distance = Vector3.Distance(here, new Vector3(spawn.X, spawn.Y, spawn.Z));
            if (distance > bestDistance || !PlayerbotTrainerDestinations.CanApproachTrainer(player, services, npcFeature.FactionTemplates, faction)) continue;
            best = (entry, spawn);
            bestDistance = distance;
        }

        return best;
    }

    private static VendorIndex Build(CreatureContent content, NpcStore npcs)
    {
        var index = new VendorIndex(npcs, content.DefinitionsVersion);
        foreach (CreatureTemplate template in content.Templates)
        {
            if (((NpcFlags)template.NpcFlags & NpcFlags.Vendor) == 0) continue;
            IReadOnlyList<VendorItem> stock = npcs.VendorItems(template.Entry);
            if (stock.Count == 0) continue;
            foreach (VendorItem row in stock)
            {
                if (row.Item is not (RuneOfPortals or RuneOfTeleportation)) continue;
                foreach (uint mapId in content.MapsWithSpawns)
                {
                    foreach (CreatureSpawn spawn in content.GetSpawns(mapId, template.Entry))
                    {
                        if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
                        if (!index.ByMapItem.TryGetValue((mapId, row.Item), out List<(uint, uint, CreatureSpawn)>? list))
                            index.ByMapItem[(mapId, row.Item)] = list = [];
                        list.Add((template.Entry, template.Faction, spawn));
                    }
                }
            }
        }

        VendorIndexes.AddOrUpdate(content, index);
        return index;
    }

    /// <summary>A visible, living vendor of <paramref name="entry"/> the mage is in interaction range of and may trade with (the server's own check).</summary>
    private static NpcInfo? VisibleVendor(WorldSession session, Player player, uint entry)
    {
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || services.Deps.Creatures is not { } lookup) return null;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (guid.High != HighGuid.Unit || guid.Entry != entry) continue;
            if (lookup.Find(player, guid) is { IsAlive: true, IsGameObject: false } && services.InteractableNpc(player, guid, NpcFlags.Vendor) is { } npc) return npc;
        }

        return null;
    }

    /// <summary>CMSG_BUY_ITEM: u64 vendor, u32 item, u8 count, u8 bag slot (as PlayerbotTownGoals' purchase).</summary>
    private static byte[] BuyPayload(ObjectGuid vendor, uint item)
    {
        var writer = new PacketWriter(14);
        writer.WriteUInt64(vendor.Value);
        writer.WriteUInt32(item);
        writer.WriteByte(1);
        writer.WriteByte(0);
        return writer.ToArray();
    }
}

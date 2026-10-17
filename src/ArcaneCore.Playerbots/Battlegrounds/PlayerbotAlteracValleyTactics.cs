using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>The road an Alterac Valley attacker pushes along (<see cref="PlayerbotAlteracValleyTactics.LaneOf"/>).</summary>
internal enum AvLane : byte
{
    /// <summary>The main road: the enemy captain, then the towers or bunkers, then the graveyards behind them.</summary>
    Main,

    /// <summary>The flank: Snowfall and the enemy graveyards first (forward spawn points), then the towers.</summary>
    Flank,
}

/// <summary>What an Alterac Valley bot does next (<see cref="PlayerbotAlteracValleyTactics.Decide"/>).</summary>
internal enum AvObjectiveKind : byte
{
    /// <summary>Nothing to do: only when the match offers no objective at all (no node, no boss, no enemy base).</summary>
    None,

    /// <summary>Click (21651) the banner of an enemy-held or neutral node: the assault.</summary>
    Assault,

    /// <summary>Click the banner of an own node the enemy assaults: the defence (it turns back to the team at once).</summary>
    Defend,

    /// <summary>Stand guard at an own node: a pending capture of the team, or a held graveyard or tower (a limited count).</summary>
    Hold,

    /// <summary>Kill the enemy captain (Galvangar for the Alliance, Balinda for the Horde): 100 enemy reinforcements.</summary>
    KillCaptain,

    /// <summary>Kill the enemy general (Drek'Thar, Vanndar): the match.</summary>
    KillGeneral,

    /// <summary>Walk towards the enemy general's keep (every lane step is done or blocked for now).</summary>
    Advance,
}

/// <summary>One node as a bot of one team sees it: the match's state (<see cref="AlteracValley"/>) and the banner the bot would click.</summary>
/// <param name="Owner">0 Alliance, 1 Horde, 2 neutral (<see cref="AlteracValley.NodeOwner"/>).</param>
/// <param name="TotalOwner">The owner before the running assault (<see cref="AlteracValley.NodeTotalOwner"/>).</param>
/// <param name="State"><see cref="AlteracValley.PointAssaulted"/> or <see cref="AlteracValley.PointControlled"/>.</param>
/// <param name="HasBanner">A clickable banner of the node is spawned (a destroyed tower has flames instead).</param>
/// <param name="At">Where the node's banner stands (known once one was seen); null: never seen.</param>
internal readonly record struct AvNode(int Owner, int TotalOwner, int State, bool Tower, bool HasBanner, Vector3? At)
{
    /// <summary>
    /// The team may assault it: controlled by the enemy (or neutral Snowfall) and not the team's before (vmangos
    /// BattleGroundAV::EventPlayerAssaultsPoint, BattleGroundAV.cpp:1330-1368), or a neutral-total node the enemy is assaulting (Snowfall's
    /// first capture: a click there is an assault, :1319-1326).
    /// </summary>
    public bool AssaultableBy(int team)
        => HasBanner && Owner != team
            && (State == AlteracValley.PointControlled ? TotalOwner != team : TotalOwner == AlteracValley.TeamNeutral);

    /// <summary>The team's own node the enemy assaults: a click defends it (vmangos EventPlayerDefendsPoint, BattleGroundAV.cpp:1288-1318).</summary>
    public bool DefendableBy(int team) => HasBanner && State == AlteracValley.PointAssaulted && Owner != team && TotalOwner == team;

    /// <summary>The team's own pending capture: assaulted by the team, not yet taken (five minutes, <see cref="AlteracValley.CaptureTimeMs"/>).</summary>
    public bool CapturingBy(int team) => State == AlteracValley.PointAssaulted && Owner == team && TotalOwner != team;

    /// <summary>The team holds it (a graveyard it spawns at, or a standing tower of its own).</summary>
    public bool HeldBy(int team) => State == AlteracValley.PointControlled && Owner == team && HasBanner;
}

/// <summary>A decided objective: its kind, the node (or -1) and where to go.</summary>
internal readonly record struct AvObjective(AvObjectiveKind Kind, int Node, Vector3? At);

/// <summary>Everything one decision reads, free of world state (unit tested).</summary>
internal sealed class AvDecision
{
    public required int TeamIndex { get; init; }
    public required PlayerbotBattlegroundRole Role { get; init; }
    public required AvLane Lane { get; init; }
    public required Vector3 Position { get; init; }

    /// <summary>The 15 nodes (<see cref="AlteracValley.NodeCount"/>).</summary>
    public required IReadOnlyList<AvNode> Nodes { get; init; }

    /// <summary>The enemy captain, when alive and known.</summary>
    public Vector3? EnemyCaptain { get; init; }

    /// <summary>The enemy general, when alive and known.</summary>
    public Vector3? EnemyGeneral { get; init; }

    /// <summary>Where the enemy keep is (the enemy start): the advance goes there when nothing else is to be done.</summary>
    public Vector3? EnemyBase { get; init; }

    /// <summary>The other bots' defence and guard claims per node (<see cref="PlayerbotAlteracValleyState.ClaimsAt"/>); null: none.</summary>
    public Func<int, int>? OthersClaiming { get; init; }

    /// <summary>A teammate already casting the banner spell at the node's banner (vmangos AtFlag, BattleBotWaypoints.cpp:181-189); null: never.</summary>
    public Func<int, bool>? Blocked { get; init; }
}

/// <summary>
/// The Alterac Valley objective flow of a bot, free of world state (unit tested): a push towards the enemy general through the enemy captain,
/// towers or bunkers and graveyards, with a limited count of defenders on the team's own nodes.
/// <para>
/// Reference: mangoszero playerbot <c>BGTactics::selectObjective</c> (BattleGroundTactics.cpp, the <c>BATTLEGROUND_AV</c> case: the attackers go
/// for the enemy captain and the enemy towers and graveyards nearest the road, a few defenders hold or retake the own nodes, the general
/// last) and vmangos BattleBotWaypoints.cpp:1680-1730 (<c>vPaths_AV</c>: the cave-to-flag paths through Iceblood Garrison to Captain Galvangar and
/// through Stonehearth Outpost to Captain Balinda, <c>AV_AtFlag</c> at every banner, BattleBotWaypoints.cpp:219-222). The server side is
/// vmangos BattleGroundAV.cpp (EventPlayerClickedOnFlag :1288-1309, HandleKillUnit :277-431).
/// </para>
/// <list type="bullet">
/// <item>An own node under enemy assault is defended (clicked back) by the defenders from anywhere and by an attacker within
/// <see cref="RespondYards"/>, at most <see cref="MaxDefendersPerNode"/> bots per node.</item>
/// <item>A defender with nothing to retake guards the team's pending captures first, then its held nodes nearest to it, at most
/// <see cref="MaxGuardsPerNode"/> per node; with every place full it attacks.</item>
/// <item>An attacker takes an assaultable banner within <see cref="OpportunityYards"/> (it never runs past one), then the first actionable step of
/// its lane (<see cref="MainRoad"/> or <see cref="FlankRoad"/>), then the other lane's, then the general; with nothing actionable it advances on the
/// enemy keep. A bot never idles while the match has an objective.</item>
/// </list>
/// </summary>
internal static class PlayerbotAlteracValleyTactics
{
    /// <summary>The captain step of a lane (any other step is a node).</summary>
    internal const int CaptainStep = -1;

    /// <summary>At most this many bots click one own assaulted banner back (the first one's click is enough; the rest fight there).</summary>
    internal const int MaxDefendersPerNode = 3;

    /// <summary>At most this many bots stand guard at one own node.</summary>
    internal const int MaxGuardsPerNode = 2;

    /// <summary>An attacker turns to defend an own assaulted node within this distance.</summary>
    internal const float RespondYards = 60f;

    /// <summary>An attacker takes an assaultable banner within this distance before its lane's next step.</summary>
    internal const float OpportunityYards = 35f;

    /// <summary>One in this many attackers takes the flank (by guid).</summary>
    internal const int FlankOneIn = 4;

    // The lanes per team (index 0 Alliance pushing south on Frostwolf, 1 Horde pushing north on Dun Baldar).
    private static readonly int[][] s_main =
    [
        [CaptainStep, AlteracValley.NodeIcebloodTower, AlteracValley.NodeIcebloodGrave, AlteracValley.NodeTowerPoint, AlteracValley.NodeFrostwolfGrave,
         AlteracValley.NodeFrostwolfWestTower, AlteracValley.NodeFrostwolfEastTower, AlteracValley.NodeFrostwolfHut],
        [CaptainStep, AlteracValley.NodeStoneheartBunker, AlteracValley.NodeStoneheartGrave, AlteracValley.NodeIcewingBunker, AlteracValley.NodeStormpikeGrave,
         AlteracValley.NodeDunBaldarSouth, AlteracValley.NodeDunBaldarNorth, AlteracValley.NodeFirstAidStation],
    ];

    private static readonly int[][] s_flank =
    [
        [AlteracValley.NodeSnowfallGrave, AlteracValley.NodeIcebloodGrave, AlteracValley.NodeTowerPoint, AlteracValley.NodeIcebloodTower,
         AlteracValley.NodeFrostwolfGrave, AlteracValley.NodeFrostwolfEastTower, AlteracValley.NodeFrostwolfWestTower, AlteracValley.NodeFrostwolfHut, CaptainStep],
        [AlteracValley.NodeSnowfallGrave, AlteracValley.NodeStoneheartGrave, AlteracValley.NodeIcewingBunker, AlteracValley.NodeStoneheartBunker,
         AlteracValley.NodeStormpikeGrave, AlteracValley.NodeDunBaldarNorth, AlteracValley.NodeDunBaldarSouth, AlteracValley.NodeFirstAidStation, CaptainStep],
    ];

    /// <summary>The main road of a team (0 Alliance, 1 Horde): node indices, <see cref="CaptainStep"/> for the enemy captain.</summary>
    internal static IReadOnlyList<int> MainRoad(int team) => s_main[team];

    /// <summary>The flank of a team.</summary>
    internal static IReadOnlyList<int> FlankRoad(int team) => s_flank[team];

    /// <summary>The lane of an attacker: one in <see cref="FlankOneIn"/> flanks, spread by a hash of the guid (deterministic).</summary>
    internal static AvLane LaneOf(ObjectGuid guid)
        => unchecked((uint)(guid.Value * 0x9E3779B97F4A7C15UL >> 32)) % FlankOneIn == 0 ? AvLane.Flank : AvLane.Main;

    /// <summary>The next objective (see the class summary for the order).</summary>
    internal static AvObjective Decide(AvDecision d)
    {
        ArgumentNullException.ThrowIfNull(d);
        int team = d.TeamIndex;
        IReadOnlyList<AvNode> nodes = d.Nodes;

        // 1. Retake an own node the enemy assaults (limited count per node).
        float respond = d.Role == PlayerbotBattlegroundRole.Defender ? float.MaxValue : RespondYards;
        if (Nearest(d, n => nodes[n].DefendableBy(team) && HasRoom(d, n, MaxDefendersPerNode), respond) is { } defend)
            return new AvObjective(AvObjectiveKind.Defend, defend, nodes[defend].At);

        // 2. A defender guards: the team's pending captures, then its held nodes (limited count per node).
        if (d.Role == PlayerbotBattlegroundRole.Defender)
        {
            if (Nearest(d, n => nodes[n].CapturingBy(team) && HasRoom(d, n, MaxGuardsPerNode), float.MaxValue) is { } pending)
                return new AvObjective(AvObjectiveKind.Hold, pending, nodes[pending].At);
            if (Nearest(d, n => nodes[n].HeldBy(team) && HasRoom(d, n, MaxGuardsPerNode), float.MaxValue) is { } held)
                return new AvObjective(AvObjectiveKind.Hold, held, nodes[held].At);
        }

        // 3. An assaultable banner close by is never run past.
        if (Nearest(d, n => nodes[n].AssaultableBy(team) && !IsBlocked(d, n), OpportunityYards) is { } near)
            return new AvObjective(AvObjectiveKind.Assault, near, nodes[near].At);

        // 4. The lane, then the other lane: the first step that can be acted on.
        IReadOnlyList<int> own = d.Lane == AvLane.Flank ? s_flank[team] : s_main[team];
        IReadOnlyList<int> other = d.Lane == AvLane.Flank ? s_main[team] : s_flank[team];
        if (FirstStep(d, own) is { } step) return step;
        if (FirstStep(d, other) is { } fallback) return fallback;

        // 5. The general, then the enemy keep.
        if (d.EnemyGeneral is { } general) return new AvObjective(AvObjectiveKind.KillGeneral, -1, general);
        if (d.EnemyBase is { } keep) return new AvObjective(AvObjectiveKind.Advance, -1, keep);
        return new AvObjective(AvObjectiveKind.None, -1, null);
    }

    private static AvObjective? FirstStep(AvDecision d, IReadOnlyList<int> lane)
    {
        foreach (int step in lane)
        {
            if (step == CaptainStep)
            {
                if (d.EnemyCaptain is { } captain) return new AvObjective(AvObjectiveKind.KillCaptain, -1, captain);
                continue;
            }

            AvNode node = d.Nodes[step];
            if (node.AssaultableBy(d.TeamIndex) && node.At is not null && !IsBlocked(d, step))
                return new AvObjective(AvObjectiveKind.Assault, step, node.At);
        }

        return null;
    }

    private static bool HasRoom(AvDecision d, int node, int max) => (d.OthersClaiming?.Invoke(node) ?? 0) < max;

    private static bool IsBlocked(AvDecision d, int node) => d.Blocked?.Invoke(node) ?? false;

    /// <summary>The nearest node (with a known place within <paramref name="within"/>) that <paramref name="accept"/> takes.</summary>
    private static int? Nearest(AvDecision d, Func<int, bool> accept, float within)
    {
        int? best = null;
        float bestDistance = within;
        for (int n = 0; n < d.Nodes.Count; n++)
        {
            if (d.Nodes[n].At is not { } at) continue;
            float distance = Vector3.Distance(d.Position, at);
            if (distance > bestDistance || (best is not null && distance == bestDistance) || !accept(n)) continue;
            bestDistance = distance;
            best = n;
        }

        return best;
    }
}

/// <summary>
/// The objective state one Alterac Valley match shares among its bots (world thread only): the clickable banner of every node and where it
/// stands, the captains and generals, and the defence and guard claims. The map's objects are scanned at most once per
/// <see cref="RescanMs"/> for the whole match (sooner, but not more than once per <see cref="MinRescanMs"/>, when a node changed hands), and
/// the creatures once per <see cref="BossRescanMs"/>, however many bots think: a 40-against-40 match never scans per think.
/// </summary>
internal sealed class PlayerbotAlteracValleyState
{
    internal const long RescanMs = 2_000;
    internal const long MinRescanMs = 250;
    internal const long BossRescanMs = 5_000;

    /// <summary>A claim lapses unless renewed within this long (a bot that died or left stops counting).</summary>
    internal const long ClaimTtlMs = 3_000;

    private static readonly ConditionalWeakTable<Battleground, PlayerbotAlteracValleyState> s_states = new();

    private readonly Dictionary<ObjectGuid, (int Node, long At)> _claims = [];
    private readonly int[] _claimCounts = new int[AlteracValley.NodeCount];
    private long _claimCountsAt = long.MinValue;
    private long _scanAt = long.MinValue;
    private long _bossScanAt = long.MinValue;
    private long _signature = -1;

    /// <summary>The state of <paramref name="match"/> (one per match, collected with it).</summary>
    internal static PlayerbotAlteracValleyState For(Battleground match) => s_states.GetValue(match, _ => new PlayerbotAlteracValleyState());

    /// <summary>Object scans done (inspection and tests).</summary>
    internal int Scans { get; private set; }

    /// <summary>Creature scans done (inspection and tests).</summary>
    internal int BossScans { get; private set; }

    /// <summary>The clickable banner of each node from the last scan (or null).</summary>
    internal GameObject?[] Banners { get; } = new GameObject?[AlteracValley.NodeCount];

    /// <summary>Where each node's banner stands, kept once seen (banners of one node share their place).</summary>
    internal Vector3?[] NodeAt { get; } = new Vector3?[AlteracValley.NodeCount];

    /// <summary>The captains and generals per team index (Alliance 0, Horde 1), from the last creature scan.</summary>
    internal Creature?[] Captains { get; } = new Creature?[2];

    internal Creature?[] Generals { get; } = new Creature?[2];

    /// <summary>
    /// Scan the objects when due: <see cref="RescanMs"/> since the last scan, or a changed node <paramref name="signature"/> and
    /// <see cref="MinRescanMs"/> since the last scan, or a <paramref name="stale"/> banner. <paramref name="scan"/> fills <see cref="Banners"/>
    /// and <see cref="NodeAt"/>. True when it scanned.
    /// </summary>
    internal bool RefreshObjects(long now, long signature, bool stale, Action<PlayerbotAlteracValleyState> scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (_scanAt != long.MinValue)
        {
            long since = now - _scanAt;
            if (since < MinRescanMs || (since < RescanMs && signature == _signature && !stale)) return false;
        }

        _scanAt = now;
        _signature = signature;
        Array.Clear(Banners);
        scan(this);
        Scans++;
        return true;
    }

    /// <summary>Scan the creatures when due (<see cref="BossRescanMs"/>, or <paramref name="stale"/> and <see cref="MinRescanMs"/>).</summary>
    internal bool RefreshBosses(long now, bool stale, Action<PlayerbotAlteracValleyState> scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (_bossScanAt != long.MinValue)
        {
            long since = now - _bossScanAt;
            if (since < BossRescanMs && !(stale && since >= BossRescanMs / 5)) return false;
        }

        _bossScanAt = now;
        Array.Clear(Captains);
        Array.Clear(Generals);
        scan(this);
        BossScans++;
        return true;
    }

    /// <summary><paramref name="bot"/> defends or guards <paramref name="node"/> now (or nothing, -1).</summary>
    internal void Claim(ObjectGuid bot, int node, long now)
    {
        if (node < 0) _claims.Remove(bot);
        else _claims[bot] = (node, now);
        _claimCountsAt = long.MinValue;
    }

    /// <summary>The live claims on <paramref name="node"/> by bots other than <paramref name="bot"/>.</summary>
    internal int ClaimsAt(int node, ObjectGuid bot, long now)
    {
        if (_claimCountsAt != now)
        {
            Array.Clear(_claimCounts);
            List<ObjectGuid>? lapsed = null;
            foreach ((ObjectGuid guid, (int n, long at)) in _claims)
            {
                if (now - at > ClaimTtlMs) (lapsed ??= []).Add(guid);
                else _claimCounts[n]++;
            }

            if (lapsed is not null) foreach (ObjectGuid guid in lapsed) _claims.Remove(guid);
            _claimCountsAt = now;
        }

        int count = _claimCounts[node];
        return _claims.TryGetValue(bot, out (int Node, long At) own) && own.Node == node && now - own.At <= ClaimTtlMs ? count - 1 : count;
    }
}

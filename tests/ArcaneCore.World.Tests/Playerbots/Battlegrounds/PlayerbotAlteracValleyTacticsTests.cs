using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;
using AV = ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The Alterac Valley objective flow of the bots (<see cref="PlayerbotAlteracValleyTactics"/>) and the match's shared scan state
/// (<see cref="PlayerbotAlteracValleyState"/>), free of world state. The node places, captains, generals and starts are the live world
/// database's (gameobject_battleground / creature_battleground rows of map 30, read 2026-10-16).
/// </summary>
public sealed class PlayerbotAlteracValleyTacticsTests
{
    private const int Alliance = 0;
    private const int Horde = 1;

    private static readonly Vector3[] s_nodeAt =
    [
        new(638.6f, -32.4f, 46.1f), new(669.0f, -294.1f, 30.3f), new(77.8f, -404.7f, 46.8f), new(-202.6f, -112.7f, 78.5f),
        new(-612.0f, -396.2f, 60.8f), new(-1084.2f, -339.1f, 54.9f), new(-1402.2f, -307.4f, 89.4f), new(553.8f, -78.7f, 51.9f),
        new(674.0f, -143.1f, 63.7f), new(203.3f, -360.4f, 56.4f), new(-152.4f, -441.8f, 40.4f), new(-571.9f, -262.8f, 75.0f),
        new(-768.9f, -363.7f, 90.9f), new(-1302.9f, -317.0f, 113.9f), new(-1297.5f, -266.8f, 114.2f),
    ];

    private static readonly Vector3 s_galvangar = new(-544.1f, -167.9f, 57.0f);
    private static readonly Vector3 s_balinda = new(-57.8f, -286.6f, 15.6f);
    private static readonly Vector3 s_vanndar = new(722.4f, -11.0f, 50.7f);
    private static readonly Vector3 s_drekThar = new(-1370.9f, -219.8f, 98.4f);
    private static readonly Vector3 s_allianceStart = new(873.0f, -491.3f, 96.5f);
    private static readonly Vector3 s_hordeStart = new(-1437.67f, -610.09f, 51.16f);

    /// <summary>The nodes at the start (vmangos BattleGroundAV::Reset, BattleGroundAV.cpp:1594-1668; AlteracValley's constructor).</summary>
    private static AvNode[] Start()
    {
        var nodes = new AvNode[AV.NodeCount];
        for (int n = 0; n < AV.NodeCount; n++)
        {
            int owner = n is AV.NodeSnowfallGrave ? AV.TeamNeutral
                : n is <= AV.NodeStoneheartGrave or (>= AV.NodeDunBaldarSouth and <= AV.NodeStoneheartBunker) ? Alliance : Horde;
            nodes[n] = new AvNode(owner, owner, AV.PointControlled, n >= AV.NodeDunBaldarSouth, HasBanner: true, s_nodeAt[n]);
        }

        return nodes;
    }

    /// <summary>The node after <paramref name="team"/> clicked it: assaulted by it, the old owner kept as the total owner.</summary>
    private static AvNode Assaulted(AvNode node, int team) => node with { Owner = team, State = AV.PointAssaulted };

    private static AvDecision Decision(int team, AvNode[] nodes, PlayerbotBattlegroundRole role = PlayerbotBattlegroundRole.Attacker,
        AvLane lane = AvLane.Main, Vector3? at = null, bool captain = true, bool general = true, Func<int, int>? claims = null,
        Func<int, bool>? blocked = null) => new()
    {
        TeamIndex = team,
        Role = role,
        Lane = lane,
        Position = at ?? (team == Alliance ? s_allianceStart : s_hordeStart),
        Nodes = nodes,
        EnemyCaptain = captain ? (team == Alliance ? s_galvangar : s_balinda) : null,
        EnemyGeneral = general ? (team == Alliance ? s_drekThar : s_vanndar) : null,
        EnemyBase = team == Alliance ? s_hordeStart : s_allianceStart,
        OthersClaiming = claims,
        Blocked = blocked,
    };

    /// <summary>
    /// The push along the road: an attacker with nothing near it goes through its lane step by step as each step is done (the captain killed,
    /// each node assaulted by its team), then the other lane's last step, then the general. Every node it is sent to is one its team may click.
    /// </summary>
    [Theory]
    [InlineData(Alliance, false, new[] { -1, AV.NodeIcebloodTower, AV.NodeIcebloodGrave, AV.NodeTowerPoint, AV.NodeFrostwolfGrave, AV.NodeFrostwolfWestTower, AV.NodeFrostwolfEastTower, AV.NodeFrostwolfHut, AV.NodeSnowfallGrave })]
    [InlineData(Alliance, true, new[] { AV.NodeSnowfallGrave, AV.NodeIcebloodGrave, AV.NodeTowerPoint, AV.NodeIcebloodTower, AV.NodeFrostwolfGrave, AV.NodeFrostwolfEastTower, AV.NodeFrostwolfWestTower, AV.NodeFrostwolfHut, -1 })]
    [InlineData(Horde, false, new[] { -1, AV.NodeStoneheartBunker, AV.NodeStoneheartGrave, AV.NodeIcewingBunker, AV.NodeStormpikeGrave, AV.NodeDunBaldarSouth, AV.NodeDunBaldarNorth, AV.NodeFirstAidStation, AV.NodeSnowfallGrave })]
    [InlineData(Horde, true, new[] { AV.NodeSnowfallGrave, AV.NodeStoneheartGrave, AV.NodeIcewingBunker, AV.NodeStoneheartBunker, AV.NodeStormpikeGrave, AV.NodeDunBaldarNorth, AV.NodeDunBaldarSouth, AV.NodeFirstAidStation, -1 })]
    public void Attacker_WalksItsLane_StepByStep_ThenTheGeneral(int team, bool flank, int[] expected)
    {
        AvNode[] nodes = Start();
        bool captain = true;
        var walked = new List<int>();
        for (int guard = 0; guard < 20; guard++)
        {
            AvObjective objective = PlayerbotAlteracValleyTactics.Decide(Decision(team, nodes, lane: flank ? AvLane.Flank : AvLane.Main, captain: captain));
            if (objective.Kind == AvObjectiveKind.KillGeneral)
            {
                Assert.Equal(team == Alliance ? s_drekThar : s_vanndar, objective.At);
                break;
            }

            if (objective.Kind == AvObjectiveKind.KillCaptain)
            {
                Assert.Equal(team == Alliance ? s_galvangar : s_balinda, objective.At);
                walked.Add(-1);
                captain = false;
                continue;
            }

            Assert.Equal(AvObjectiveKind.Assault, objective.Kind);
            Assert.True(nodes[objective.Node].AssaultableBy(team), $"node {objective.Node} is not the team's to click");
            Assert.Equal(s_nodeAt[objective.Node], objective.At);
            walked.Add(objective.Node);
            nodes[objective.Node] = Assaulted(nodes[objective.Node], team);
        }

        Assert.Equal(expected, walked);
    }

    [Fact]
    public void Attacker_TakesAnAssaultableBannerBesideIt_BeforeItsLanesNextStep()
    {
        // An Alliance main-road attacker that walks past Snowfall (neutral, 20 yards away) clicks it instead of running on to Galvangar.
        AvObjective objective = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, Start(), at: s_nodeAt[AV.NodeSnowfallGrave] + new Vector3(20f, 0f, 0f)));
        Assert.Equal(new AvObjective(AvObjectiveKind.Assault, AV.NodeSnowfallGrave, s_nodeAt[AV.NodeSnowfallGrave]), objective);
    }

    [Fact]
    public void ADestroyedTower_WithNoBanner_IsNeverATarget_AndANodeATeammateClaimsIsSkipped()
    {
        AvNode[] nodes = Start();
        // Iceblood Tower burnt down (flames, no banner); the Horde's own Iceblood graveyard is being clicked by a teammate.
        nodes[AV.NodeIcebloodTower] = nodes[AV.NodeIcebloodTower] with { Owner = Alliance, TotalOwner = Alliance, HasBanner = false };
        AvObjective objective = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, captain: false, blocked: n => n == AV.NodeIcebloodGrave));
        Assert.Equal(AvObjectiveKind.Assault, objective.Kind);
        Assert.Equal(AV.NodeTowerPoint, objective.Node);

        // The Horde may not click the Alliance's burnt tower back either.
        Assert.False(nodes[AV.NodeIcebloodTower].AssaultableBy(Horde));
    }

    /// <summary>
    /// Own nodes under assault are clicked back, by a limited count: the defenders from anywhere, an attacker only near it; a fourth bot finds the
    /// node full and plays on (the defender then guards a held node, the attacker pushes).
    /// </summary>
    [Fact]
    public void AnOwnAssaultedNode_IsDefended_ByAtMostThreeBots_AndOnlyByNearbyAttackers()
    {
        AvNode[] nodes = Start();
        nodes[AV.NodeStoneheartGrave] = Assaulted(nodes[AV.NodeStoneheartGrave], Horde);
        Assert.True(nodes[AV.NodeStoneheartGrave].DefendableBy(Alliance));
        Assert.False(nodes[AV.NodeStoneheartGrave].AssaultableBy(Alliance));

        AvObjective defender = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, PlayerbotBattlegroundRole.Defender, claims: _ => 2));
        Assert.Equal(new AvObjective(AvObjectiveKind.Defend, AV.NodeStoneheartGrave, s_nodeAt[AV.NodeStoneheartGrave]), defender);

        AvObjective full = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, PlayerbotBattlegroundRole.Defender,
            claims: n => n == AV.NodeStoneheartGrave ? PlayerbotAlteracValleyTactics.MaxDefendersPerNode : 0));
        Assert.Equal(AvObjectiveKind.Hold, full.Kind);
        Assert.NotEqual(AV.NodeStoneheartGrave, full.Node);

        Vector3 near = s_nodeAt[AV.NodeStoneheartGrave] + new Vector3(0f, 40f, 0f);
        Assert.Equal(AvObjectiveKind.Defend, PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, at: near)).Kind);
        Assert.Equal(AvObjectiveKind.KillCaptain, PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes)).Kind);
    }

    [Fact]
    public void ADefender_WithNothingToRetake_GuardsAPendingCaptureFirst_ThenAHeldNode_TwoPerNode_ThenAttacks()
    {
        AvNode[] nodes = Start();
        nodes[AV.NodeIcebloodGrave] = Assaulted(nodes[AV.NodeIcebloodGrave], Alliance);
        AvObjective pending = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, PlayerbotBattlegroundRole.Defender));
        Assert.Equal(new AvObjective(AvObjectiveKind.Hold, AV.NodeIcebloodGrave, s_nodeAt[AV.NodeIcebloodGrave]), pending);

        AvObjective held = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, PlayerbotBattlegroundRole.Defender,
            claims: n => n == AV.NodeIcebloodGrave ? PlayerbotAlteracValleyTactics.MaxGuardsPerNode : 0));
        Assert.Equal(AvObjectiveKind.Hold, held.Kind);
        Assert.True(nodes[held.Node].HeldBy(Alliance));

        AvObjective attacks = PlayerbotAlteracValleyTactics.Decide(Decision(Alliance, nodes, PlayerbotBattlegroundRole.Defender,
            claims: _ => PlayerbotAlteracValleyTactics.MaxGuardsPerNode));
        Assert.Equal(AvObjectiveKind.KillCaptain, attacks.Kind);
    }

    /// <summary>
    /// No idling while objectives exist: over many random states, roles, lanes and places, a bot always has an objective, and it only advances
    /// on the keep (the fallback) when no node is the team's to click, no own node is to retake and neither enemy boss is alive.
    /// </summary>
    [Fact]
    public void NoState_LeavesABotWithoutAnObjective_AndTheAdvanceIsOnlyTheLastResort()
    {
        var random = new Random(20261016);
        for (int round = 0; round < 5_000; round++)
        {
            var nodes = new AvNode[AV.NodeCount];
            for (int n = 0; n < AV.NodeCount; n++)
                nodes[n] = new AvNode(random.Next(3), random.Next(3), random.Next(2), n >= AV.NodeDunBaldarSouth, random.Next(4) != 0,
                    random.Next(10) == 0 ? null : s_nodeAt[n]);
            int team = random.Next(2);
            bool captain = random.Next(2) == 0;
            bool general = random.Next(2) == 0;
            var role = (PlayerbotBattlegroundRole)random.Next(3);
            var at = new Vector3(random.Next(-1500, 900), random.Next(-600, 0), 50f);
            AvObjective objective = PlayerbotAlteracValleyTactics.Decide(Decision(team, nodes, role, (AvLane)random.Next(2), at, captain, general,
                claims: _ => random.Next(4)));
            Assert.NotEqual(AvObjectiveKind.None, objective.Kind);
            if (objective.Kind == AvObjectiveKind.Advance)
            {
                Assert.False(captain || general, $"round {round}: advanced with a boss alive");
                Assert.DoesNotContain(nodes, node => node.At is not null && node.AssaultableBy(team));
            }
        }
    }

    [Fact]
    public void Lanes_AreDeterministic_AndAboutOneInFourFlanks()
    {
        int flank = 0;
        for (uint low = 1; low <= 400; low++)
        {
            var guid = new ObjectGuid(low);
            Assert.Equal(PlayerbotAlteracValleyTactics.LaneOf(guid), PlayerbotAlteracValleyTactics.LaneOf(guid));
            flank += PlayerbotAlteracValleyTactics.LaneOf(guid) == AvLane.Flank ? 1 : 0;
        }

        Assert.InRange(flank, 60, 140);
    }

    /// <summary>
    /// The tick cost of a 40-against-40 match: eighty bots thinking in one tick scan the match's objects once, not eighty times; the next scan
    /// comes after <see cref="PlayerbotAlteracValleyState.RescanMs"/>, or after <see cref="PlayerbotAlteracValleyState.MinRescanMs"/> when a node
    /// changed hands. The creatures likewise, every <see cref="PlayerbotAlteracValleyState.BossRescanMs"/>.
    /// </summary>
    [Fact]
    public void EightyBots_InOneTick_ScanTheMatchOnce_AndTheScansAreRateLimited()
    {
        var state = new PlayerbotAlteracValleyState();
        int objectScans = 0, bossScans = 0;
        void Think(long now, long signature)
        {
            for (int bot = 0; bot < 80; bot++)
            {
                state.RefreshObjects(now, signature, stale: false, _ => objectScans++);
                state.RefreshBosses(now, stale: false, _ => bossScans++);
            }
        }

        Think(1_000, 7);
        Assert.Equal((1, 1), (objectScans, bossScans));
        for (long now = 1_200; now < 1_000 + PlayerbotAlteracValleyState.RescanMs; now += 200) Think(now, 7);
        Assert.Equal((1, 1), (objectScans, bossScans));
        Think(1_000 + PlayerbotAlteracValleyState.RescanMs, 7);
        Assert.Equal((2, 1), (objectScans, bossScans));

        // A node changed hands: a rescan once MinRescanMs passed, not at once.
        long changed = 1_000 + PlayerbotAlteracValleyState.RescanMs + 100;
        Think(changed, 8);
        Assert.Equal(2, objectScans);
        Think(1_000 + PlayerbotAlteracValleyState.RescanMs + PlayerbotAlteracValleyState.MinRescanMs, 8);
        Assert.Equal(3, objectScans);

        Think(1_000 + PlayerbotAlteracValleyState.BossRescanMs, 8);
        Assert.Equal(2, bossScans);
        Assert.Equal((objectScans, bossScans), (state.Scans, state.BossScans));
    }

    [Fact]
    public void Claims_CountOtherBots_AndLapseWhenNotRenewed()
    {
        var state = new PlayerbotAlteracValleyState();
        ObjectGuid a = new(1), b = new(2), c = new(3);
        state.Claim(a, AV.NodeStoneheartGrave, 0);
        state.Claim(b, AV.NodeStoneheartGrave, 0);
        Assert.Equal(1, state.ClaimsAt(AV.NodeStoneheartGrave, a, 100));
        Assert.Equal(2, state.ClaimsAt(AV.NodeStoneheartGrave, c, 100));
        state.Claim(b, -1, 200);
        Assert.Equal(1, state.ClaimsAt(AV.NodeStoneheartGrave, c, 200));
        Assert.Equal(0, state.ClaimsAt(AV.NodeStoneheartGrave, c, PlayerbotAlteracValleyState.ClaimTtlMs + 1));
    }
}

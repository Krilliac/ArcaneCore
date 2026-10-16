using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The Arathi Basin team plan (<see cref="PlayerbotArathiBasinTactics"/>), free of world state. The node positions are the real banner
/// positions (vmangos BattleGroundAB.h BG_AB_NodePositions); the starts are the template's.
/// </summary>
public sealed class PlayerbotArathiBasinTacticsTests
{
    private static readonly Vector3[] s_nodes =
    [
        new(1166.79f, 1200.13f, -56.70f),   // stables
        new(977.08f, 1046.54f, -44.80f),    // blacksmith
        new(806.18f, 874.27f, -55.99f),     // farm
        new(856.14f, 1148.90f, 11.18f),     // lumber mill
        new(1146.92f, 848.17f, -110.92f),   // gold mine
    ];

    private static readonly Vector3 s_allianceStart = new(1286.05f, 1282.50f, -62.53f);

    private static ObjectGuid G(ulong low) => new(low);

    private static PlayerbotAbPicture Picture(byte[] statuses, IReadOnlyList<PlayerbotAbMember> bots, IReadOnlyList<PlayerbotAbUnit>? enemies = null,
        Team team = Team.Alliance, IReadOnlyList<PlayerbotAbUnit>? friends = null) => new()
    {
        Team = team,
        Nodes = [.. statuses.Select((s, i) => new PlayerbotAbNode(i, s_nodes[i], s))],
        Bots = bots,
        Enemies = enemies ?? [],
        Friends = friends ?? [],
        Home = s_allianceStart,
    };

    private static List<PlayerbotAbMember> AtStart(int count) => [.. Enumerable.Range(1, count).Select(i => new PlayerbotAbMember(G((ulong)i), s_allianceStart, true))];

    private const byte N = ArathiBasin.StatusNeutral, AC = ArathiBasin.StatusAllianceContested, HC = ArathiBasin.StatusHordeContested;
    private const byte AO = ArathiBasin.StatusAllianceOccupied, HO = ArathiBasin.StatusHordeOccupied;

    [Fact]
    public void FiveBots_AllNodesNeutral_SpreadOverAllFiveNodes()
    {
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([N, N, N, N, N], AtStart(5)));
        Assert.Equal(5, orders.Count);
        Assert.All(orders.Values, o => Assert.Equal(PlayerbotAbDuty.Assault, o.Duty));
        Assert.Equal([0, 1, 2, 3, 4], orders.Values.Select(o => o.Node).Order());
    }

    [Fact]
    public void TwoBots_GoToDifferentNodes_EachTheOneNearestIt()
    {
        // Both are nearer the stables than any other node, so the old nearest-node rule sent both there: the plan leaves the stables to the
        // one beside it and sends the other to the next nearest, the blacksmith.
        var bots = new List<PlayerbotAbMember>
        {
            new(G(1), Vector3.Lerp(s_nodes[0], s_nodes[1], 0.45f), true),
            new(G(2), s_nodes[0] + new Vector3(10f, 0f, 0f), true),
        };
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([N, N, N, N, N], bots));
        Assert.Equal(ArathiBasin.NodeBlacksmith, orders[G(1)].Node);
        Assert.Equal(ArathiBasin.NodeStables, orders[G(2)].Node);
    }

    [Fact]
    public void ManyBots_FewNodes_ShareThemEvenly()
    {
        // 9 bots, three attackable nodes and two held: 2 defenders (30% of 9 = 2), 7 attackers at most ceil(7 / 3) = 3 per node.
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, N, HO, N], AtStart(9)));
        Assert.Equal(9, orders.Count);
        Assert.Equal(2, orders.Values.Count(o => o.Duty == PlayerbotAbDuty.Defend));
        Assert.All(orders.Values.Where(o => o.Duty == PlayerbotAbDuty.Defend), o => Assert.Contains(o.Node, new[] { 0, 1 }));
        foreach (IGrouping<int, PlayerbotAbOrder> node in orders.Values.Where(o => o.Duty == PlayerbotAbDuty.Assault).GroupBy(o => o.Node))
        {
            Assert.Contains(node.Key, new[] { 2, 3, 4 });
            Assert.InRange(node.Count(), 1, 3);
        }

        Assert.Equal(3, orders.Values.Where(o => o.Duty == PlayerbotAbDuty.Assault).Select(o => o.Node).Distinct().Count());
    }

    [Fact]
    public void EveryLivingBot_GetsAnOrder_AndSomeoneAssaultsWhileANodeIsAttackable_InEveryNodeState()
    {
        byte[] states = [N, AC, HC, AO, HO];
        int checkedPlans = 0;
        for (int code = 0; code < 3125; code++)
        {
            byte[] statuses = new byte[5];
            int c = code;
            for (int i = 0; i < 5; i++, c /= 5) statuses[i] = states[c % 5];
            foreach (int size in new[] { 1, 2, 3, 7, 15 })
            {
                List<PlayerbotAbMember> bots = [.. AtStart(size).Select((b, i) => b with { At = s_nodes[i % 5], Alive = i != 1 })];
                Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture(statuses, bots, [new PlayerbotAbUnit(G(900), s_nodes[code % 5])]));
                checkedPlans++;
                foreach (PlayerbotAbMember bot in bots)
                {
                    if (!bot.Alive)
                    {
                        Assert.False(orders.ContainsKey(bot.Guid), "a dead bot got an order");
                        continue;
                    }

                    Assert.True(orders.TryGetValue(bot.Guid, out PlayerbotAbOrder order), $"bot {bot.Guid} has no order in {string.Join(",", statuses)}");
                    Assert.InRange(order.Node, 0, 4);
                    Assert.Equal(s_nodes[order.Node], order.Goal);
                    byte status = statuses[order.Node];
                    if (order.Duty == PlayerbotAbDuty.Assault) Assert.True(PlayerbotArathiBasinTactics.Clickable(status, 0), "assault at a node the team may not click");
                    if (order.Duty == PlayerbotAbDuty.Defend) Assert.True(PlayerbotArathiBasinTactics.Held(status, 0), "defence of a node the team does not hold");
                }

                bool attackable = statuses.Any(s => PlayerbotArathiBasinTactics.Clickable(s, 0));
                if (attackable && orders.Count > 0) Assert.Contains(orders.Values, o => o.Duty == PlayerbotAbDuty.Assault);
            }
        }

        Assert.Equal(3125 * 5, checkedPlans);
    }

    [Fact]
    public void Defenders_AreLimited_GoToTheThreatenedNode_AndFightTheEnemyThere()
    {
        // 10 bots, the Alliance holds the stables and the blacksmith; one Horde player walks up to the blacksmith.
        List<PlayerbotAbMember> bots = [.. AtStart(10).Select((b, i) => b with { Defender = i >= 7 })];
        var intruder = new PlayerbotAbUnit(G(500), s_nodes[1] + new Vector3(15f, 0f, 0f));
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, N, HO, N], bots, [intruder]));

        List<KeyValuePair<ObjectGuid, PlayerbotAbOrder>> defenders = [.. orders.Where(o => o.Value.Duty == PlayerbotAbDuty.Defend)];
        Assert.Equal(PlayerbotArathiBasinTactics.DefenderCount(10), defenders.Count);
        Assert.All(defenders, d => Assert.True(d.Key.Value >= 8, "a defender-role bot defends first"));
        Assert.Contains(defenders, d => d.Value.Node == ArathiBasin.NodeBlacksmith && d.Value.Target == intruder.Guid);
        Assert.Contains(defenders, d => d.Value.Node == ArathiBasin.NodeStables);
    }

    [Fact]
    public void ThreeBots_NoNodeHeld_NoDefenders_EveryoneAssaults()
    {
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([N, HO, HO, N, HO], AtStart(3)));
        Assert.All(orders.Values, o => Assert.Equal(PlayerbotAbDuty.Assault, o.Duty));
    }

    [Fact]
    public void EveryNodeHeld_EveryoneDefends_SpreadOverTheNodes()
    {
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, AC, AO, AO], AtStart(5)));
        Assert.All(orders.Values, o => Assert.Equal(PlayerbotAbDuty.Defend, o.Duty));
        Assert.Equal(5, orders.Values.Select(o => o.Node).Distinct().Count());
    }

    [Fact]
    public void ANodeTheEnemyAssaults_IsRetakenBeforeANearerNeutralOne()
    {
        // The bot stands 30 yards nearer the neutral farm than the lumber mill the Horde contests (its capture timer runs): it retakes the mill.
        Vector3 at = Vector3.Lerp(s_nodes[2], s_nodes[3], 0.5f) - (Vector3.Normalize(s_nodes[3] - s_nodes[2]) * 15f);
        Dictionary<ObjectGuid, PlayerbotAbOrder> orders = PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, N, HC, AO], [new PlayerbotAbMember(G(1), at, true)]));
        Assert.Equal(PlayerbotAbDuty.Assault, orders[G(1)].Duty);
        Assert.Equal(ArathiBasin.NodeLumberMill, orders[G(1)].Node);
    }

    [Fact]
    public void HurtAndOutnumbered_RetreatsToTheNearestOccupiedNode_ElseHome()
    {
        Vector3 at = s_nodes[2] + new Vector3(5f, 0f, 0f);
        var hurt = new PlayerbotAbMember(G(1), at, true, Health: 0.1f);
        PlayerbotAbUnit[] enemies = [new(G(500), at + new Vector3(3f, 0f, 0f)), new(G(501), at + new Vector3(0f, 3f, 0f))];

        Dictionary<ObjectGuid, PlayerbotAbOrder> held = PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, HO, N, N], [hurt], enemies));
        Assert.Equal(new PlayerbotAbOrder(PlayerbotAbDuty.Retreat, ArathiBasin.NodeBlacksmith, s_nodes[1], default), held[G(1)]);

        Dictionary<ObjectGuid, PlayerbotAbOrder> none = PlayerbotArathiBasinTactics.Plan(Picture([N, N, HO, N, N], [hurt], enemies));
        Assert.Equal(PlayerbotAbDuty.Retreat, none[G(1)].Duty);
        Assert.Equal(s_allianceStart, none[G(1)].Goal);

        // Not outnumbered (two friends beside it), or healthy: no retreat.
        PlayerbotAbUnit[] friends = [new(G(600), at), new(G(601), at)];
        Assert.NotEqual(PlayerbotAbDuty.Retreat, PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, HO, N, N], [hurt], enemies, friends: friends))[G(1)].Duty);
        Assert.NotEqual(PlayerbotAbDuty.Retreat, PlayerbotArathiBasinTactics.Plan(Picture([AO, AO, HO, N, N], [hurt with { Health = 0.9f }], enemies))[G(1)].Duty);
    }

    [Fact]
    public void Buffs_RestorationOnlyWhenHurt_SpeedAndBerserkingAlways_NoneInCombatOrFar()
    {
        Vector3 at = s_nodes[0];
        var regen = new PlayerbotBgBuff(G(1), BattlegroundConstants.RegenBuffEntry, at + new Vector3(10f, 0f, 0f));
        var speed = new PlayerbotBgBuff(G(2), BattlegroundConstants.SpeedBuffEntry, at + new Vector3(20f, 0f, 0f));
        var berserk = new PlayerbotBgBuff(G(3), BattlegroundConstants.BerserkBuffEntry, at + new Vector3(0f, 25f, 0f));
        var far = new PlayerbotBgBuff(G(4), BattlegroundConstants.SpeedBuffEntry, at + new Vector3(200f, 0f, 0f));

        Assert.Null(PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: false, health: 1f, mana: 1f, [regen]));
        Assert.Equal(regen, PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: false, health: 0.5f, mana: 1f, [regen, speed]));
        Assert.Equal(regen, PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: false, health: 1f, mana: 0.3f, [regen]));
        Assert.Equal(speed, PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: false, health: 1f, mana: 1f, [regen, speed, far]));
        Assert.Equal(berserk, PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: false, health: 1f, mana: 1f, [berserk, far]));
        Assert.Null(PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: true, health: 0.2f, mana: 1f, [regen, speed]));
        Assert.Null(PlayerbotArathiBasinTactics.ChooseBuff(at, inCombat: false, health: 1f, mana: 1f, [far]));
    }
}

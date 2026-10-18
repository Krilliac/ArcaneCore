using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The Warsong Gulch plan (<see cref="PlayerbotWarsongTactics"/>), free of world state: every living bot gets a duty every think, the
/// carrier runs home, dropped flags are clicked, the enemy carrier is hunted, defenders engage intruders, and the rest go for the flag or
/// the middle. Positions are the real map 489 flag stands (classic-db gameobject 179830 at 1540.42, 1481.33, 351.83 and 179831 at
/// 916.01, 1434.41, 346.02) and the middle mangosbot's BGTactics patrols (1227.45, 1476.24, 307.48).
/// </summary>
public sealed class PlayerbotWarsongTacticsTests
{
    private static readonly Vector3 AllianceRoom = new(1540.42f, 1481.33f, 351.83f);
    private static readonly Vector3 HordeRoom = new(916.01f, 1434.41f, 346.02f);
    private static readonly Vector3 Middle = new(1227.45f, 1476.24f, 307.48f);

    private static ObjectGuid G(ulong low) => new(low);

    private static PlayerbotWsgMember Bot(ulong low, Vector3 at, bool alive = true, bool healer = false) => new(G(low), at, alive, healer);

    /// <summary>A Horde team's picture: both flags home and ready, no enemy, unless overridden.</summary>
    private static PlayerbotWsgPicture Horde(IReadOnlyList<PlayerbotWsgMember> bots, IReadOnlyList<PlayerbotWsgEnemy>? enemies = null,
        WsgFlagState ownFlag = WsgFlagState.OnBase, Vector3? ownDropped = null, WsgFlagState enemyFlag = WsgFlagState.OnBase, Vector3? enemyDropped = null,
        ObjectGuid enemyCarrier = default, Vector3? enemyCarrierAt = null, ObjectGuid ownCarrier = default, Vector3? ownCarrierAt = null) => new()
    {
        Team = Team.Horde,
        Bots = bots,
        Enemies = enemies ?? [],
        OwnFlag = ownFlag,
        OwnFlagDropped = ownDropped,
        EnemyFlag = enemyFlag,
        EnemyFlagReady = enemyFlag == WsgFlagState.OnBase,
        EnemyStandObject = enemyFlag == WsgFlagState.OnBase ? G(9000) : default,
        EnemyFlagDropped = enemyDropped,
        EnemyCarrier = enemyCarrier,
        EnemyCarrierAt = enemyCarrierAt,
        OwnCarrier = ownCarrier,
        OwnCarrierAt = ownCarrierAt,
        OwnRoom = HordeRoom,
        EnemyStand = AllianceRoom,
        Midfield = Middle,
    };

    /// <summary>Ten bots spread from the Horde room to the middle.</summary>
    private static List<PlayerbotWsgMember> Ten(params int[] healers)
        => [.. Enumerable.Range(1, 10).Select(i => Bot((ulong)i, Vector3.Lerp(HordeRoom, Middle, i / 10f), healer: healers.Contains(i)))];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void EveryLivingBot_GetsAnOrder_InEveryFlagSituation(int count)
    {
        List<PlayerbotWsgMember> bots = [.. Ten().Take(count), Bot(99, HordeRoom, alive: false)];
        PlayerbotWsgEnemy efc = new(G(500), Middle);
        foreach (WsgFlagState own in Enum.GetValues<WsgFlagState>())
        foreach (WsgFlagState theirs in Enum.GetValues<WsgFlagState>())
        {
            PlayerbotWsgPicture picture = Horde(bots, [efc],
                ownFlag: own, ownDropped: own == WsgFlagState.OnGround ? Middle : null,
                enemyFlag: theirs, enemyDropped: theirs == WsgFlagState.OnGround ? Middle : null,
                enemyCarrier: own == WsgFlagState.OnPlayer ? efc.Guid : default, enemyCarrierAt: own == WsgFlagState.OnPlayer ? efc.At : null,
                ownCarrier: theirs == WsgFlagState.OnPlayer ? G(1) : default, ownCarrierAt: theirs == WsgFlagState.OnPlayer ? bots[0].At : null);
            Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(picture);
            Assert.Equal(count, orders.Count);
            Assert.All(bots.Where(b => b.Alive), b => Assert.True(orders.ContainsKey(b.Guid), $"{b.Guid} has no order ({own}/{theirs})"));
            Assert.False(orders.ContainsKey(G(99)), "a dead bot is recovering, not ordered");
            Assert.Equal(orders, PlayerbotWarsongTactics.Plan(picture)); // the same plan for every bot that computes it
        }
    }

    [Fact]
    public void BothFlagsHome_OneDefender_HalfToTheEnemyFlag_RestMidfield()
    {
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(Ten()));
        Assert.Equal(PlayerbotWarsongTactics.DefenderCount(10), orders.Values.Count(o => o.Duty == PlayerbotWsgDuty.DefendFlagRoom));
        Assert.Equal(2, PlayerbotWarsongTactics.DefenderCount(10));
        // The defenders are the bots nearest the room.
        Assert.Equal(PlayerbotWsgDuty.DefendFlagRoom, orders[G(1)].Duty);
        Assert.Equal(PlayerbotWsgDuty.DefendFlagRoom, orders[G(2)].Duty);
        PlayerbotWsgOrder[] takers = [.. orders.Values.Where(o => o.Duty == PlayerbotWsgDuty.TakeEnemyFlag)];
        Assert.Equal(4, takers.Length);
        Assert.All(takers, o => Assert.Equal(G(9000), o.Target));
        Assert.Equal(4, orders.Values.Count(o => o.Duty == PlayerbotWsgDuty.Midfield));
    }

    [Fact]
    public void Carrier_RunsHome_AndHealersEscortItFirst()
    {
        List<PlayerbotWsgMember> bots = Ten(healers: [7, 9]);
        Vector3 carrierAt = bots[2].At;
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(bots, enemyFlag: WsgFlagState.OnPlayer,
            ownCarrier: G(3), ownCarrierAt: carrierAt));
        Assert.Equal(new PlayerbotWsgOrder(PlayerbotWsgDuty.CarryHome, default, HordeRoom), orders[G(3)]);
        Assert.Equal(PlayerbotWsgDuty.EscortCarrier, orders[G(7)].Duty);
        Assert.Equal(PlayerbotWsgDuty.EscortCarrier, orders[G(9)].Duty);
        Assert.All(orders.Values.Where(o => o.Duty == PlayerbotWsgDuty.EscortCarrier), o => Assert.Equal(G(3), o.Target));
        Assert.DoesNotContain(orders.Values, o => o.Duty is PlayerbotWsgDuty.TakeEnemyFlag or PlayerbotWsgDuty.Midfield);
    }

    [Fact]
    public void EnemyCarrier_IsHunted_ByTheNearestHalf_AndTheRestAfterTheOwnRoleIsSet()
    {
        Vector3 efcAt = Vector3.Lerp(HordeRoom, Middle, 0.85f);
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(Ten(), [new PlayerbotWsgEnemy(G(500), efcAt)],
            ownFlag: WsgFlagState.OnPlayer, enemyCarrier: G(500), enemyCarrierAt: efcAt));
        // No defenders while the own flag is away: everyone hunts (the EFC is the objective; mangosbot's non-supporters and supporters
        // without a team carrier both go after it).
        Assert.DoesNotContain(orders.Values, o => o.Duty == PlayerbotWsgDuty.DefendFlagRoom);
        Assert.All(orders.Values, o => Assert.Equal(new PlayerbotWsgOrder(PlayerbotWsgDuty.HuntCarrier, G(500), efcAt), o));
    }

    [Fact]
    public void EnemyCarrierAndOwnCarrier_SplitBetweenHuntersAndEscorts()
    {
        Vector3 efcAt = Middle;
        List<PlayerbotWsgMember> bots = Ten();
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(bots, [new PlayerbotWsgEnemy(G(500), efcAt)],
            ownFlag: WsgFlagState.OnPlayer, enemyCarrier: G(500), enemyCarrierAt: efcAt,
            enemyFlag: WsgFlagState.OnPlayer, ownCarrier: G(1), ownCarrierAt: bots[0].At));
        Assert.Equal(PlayerbotWsgDuty.CarryHome, orders[G(1)].Duty);
        int hunters = orders.Values.Count(o => o.Duty == PlayerbotWsgDuty.HuntCarrier);
        int escorts = orders.Values.Count(o => o.Duty == PlayerbotWsgDuty.EscortCarrier);
        Assert.True(hunters >= 4 && escorts >= 2, $"hunters {hunters}, escorts {escorts}");
        Assert.Equal(9, hunters + escorts);
    }

    [Fact]
    public void DroppedFlags_TheOwnIsReturned_AndTheEnemyIsPickedUp_ByTheNearestBots()
    {
        Vector3 ownDropped = Vector3.Lerp(HordeRoom, Middle, 0.62f);
        Vector3 enemyDropped = Vector3.Lerp(HordeRoom, Middle, 0.18f);
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(Ten(),
            ownFlag: WsgFlagState.OnGround, ownDropped: ownDropped, enemyFlag: WsgFlagState.OnGround, enemyDropped: enemyDropped));
        Assert.Equal(new PlayerbotWsgOrder(PlayerbotWsgDuty.ReturnOwnFlag, default, ownDropped), orders[G(6)]);
        Assert.Equal(new PlayerbotWsgOrder(PlayerbotWsgDuty.TakeDroppedEnemyFlag, default, enemyDropped), orders[G(2)]);
        Assert.Single(orders.Values, o => o.Duty == PlayerbotWsgDuty.ReturnOwnFlag);
        Assert.Single(orders.Values, o => o.Duty == PlayerbotWsgDuty.TakeDroppedEnemyFlag);
    }

    [Fact]
    public void IntrudersInTheFlagRoom_AreEngagedByAsManyDefendersAsThereAreIntruders()
    {
        PlayerbotWsgEnemy[] intruders = [new(G(501), HordeRoom + new Vector3(5f, 0f, 0f)), new(G(502), HordeRoom + new Vector3(-6f, 3f, 0f)),
            new(G(503), HordeRoom + new Vector3(0f, 12f, 0f))];
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(Ten(), intruders));
        PlayerbotWsgOrder[] defenders = [.. orders.Values.Where(o => o.Duty == PlayerbotWsgDuty.DefendFlagRoom)];
        Assert.Equal(3, defenders.Length);
        Assert.All(defenders, d => Assert.Contains(d.Target, intruders.Select(i => i.Guid)));
    }

    [Fact]
    public void TwoBots_AnIntruder_OneDefendsAndOneGoesForTheFlag()
    {
        List<PlayerbotWsgMember> bots = [Bot(1, HordeRoom), Bot(2, Middle)];
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde(bots, [new PlayerbotWsgEnemy(G(501), HordeRoom + Vector3.UnitX)]));
        Assert.Equal(new PlayerbotWsgOrder(PlayerbotWsgDuty.DefendFlagRoom, G(501), HordeRoom), orders[G(1)]);
        Assert.Equal(PlayerbotWsgDuty.TakeEnemyFlag, orders[G(2)].Duty);
    }

    [Fact]
    public void EnemyFlagRespawning_AttackersStillGoToTheStand_WithoutAnObjectToClick()
    {
        Dictionary<ObjectGuid, PlayerbotWsgOrder> orders = PlayerbotWarsongTactics.Plan(Horde([Bot(1, HordeRoom)], enemyFlag: WsgFlagState.WaitRespawn));
        Assert.Equal(new PlayerbotWsgOrder(PlayerbotWsgDuty.TakeEnemyFlag, default, AllianceRoom), orders[G(1)]);
    }

    [Theory]
    [InlineData(true, true, 400f, "taking-flag")]  // the stand is in the world: click it
    [InlineData(false, false, 400f, "waiting-flag")] // respawning: go wait at the stand
    [InlineData(true, false, 400f, "waiting-flag")] // ready, but the stand's grid is not loaded yet: still go to the flag, do not idle
    [InlineData(true, false, 60f, "waiting-flag")] // partway there (the live idle positions, ~60 yd out of the Horde base)
    [InlineData(true, false, 8f, "no-flag")] // arrived, and the stand is still not there: missing content, idle
    [InlineData(true, false, 8.1f, "waiting-flag")]
    public void EnemyStandActivity_NoFlagOnlyOnceTheBotHasArrivedAtAMissingStand(bool ready, bool standFound, float yards, string activity)
        => Assert.Equal(activity, PlayerbotWarsongTactics.EnemyStandActivity(ready, standFound, yards, arrivedYards: 8f));

    [Theory]
    [InlineData(true, false, 15_000u, 50f, 10f, float.MaxValue, true)]   // still with somewhere to go
    [InlineData(true, false, 15_000u, 5f, 10f, 20f, true)]              // still beside an enemy, out of combat
    [InlineData(true, false, 15_000u, 5f, 10f, float.MaxValue, false)]  // a defender at its post, nobody near
    [InlineData(true, true, 60_000u, 50f, 10f, 5f, false)]              // fighting
    [InlineData(false, false, 60_000u, 50f, 10f, 5f, false)]            // dead
    [InlineData(true, false, 14_000u, 50f, 10f, 5f, false)]             // not long enough
    public void IsIdle_AliveOutOfCombatStillWithAGoalOrAnEnemy(bool alive, bool inCombat, uint stillMs, float goal, float arrived, float enemy, bool idle)
        => Assert.Equal(idle, PlayerbotWarsongTactics.IsIdle(alive, inCombat, stillMs, goal, arrived, enemy));

    [Fact]
    public void BotsAtTheSamePlace_KeepGuidOrderedDuties_WhenStaggeredByAHeartbeatOrThinkStep()
    {
        Vector3 dir = Vector3.Normalize(AllianceRoom - HordeRoom);
        // Four bots leaving the gate or a fight in the flag room together, staggered by up to one 500 ms heartbeat (3.5 yd at 7 yd/s).
        List<PlayerbotWsgMember> together = [Bot(1, HordeRoom), Bot(2, HordeRoom), Bot(3, HordeRoom), Bot(4, HordeRoom)];
        List<PlayerbotWsgMember> staggered =
        [
            Bot(1, HordeRoom),
            Bot(2, HordeRoom + (dir * 1.4f)),
            Bot(3, HordeRoom + (dir * 2.1f)),
            Bot(4, HordeRoom + (dir * 3.5f)),
        ];
        Dictionary<ObjectGuid, PlayerbotWsgOrder> baseline = PlayerbotWarsongTactics.Plan(Horde(together));
        Dictionary<ObjectGuid, PlayerbotWsgOrder> afterStep = PlayerbotWarsongTactics.Plan(Horde(staggered));
        Assert.Equal(baseline, afterStep);
        Assert.Equal(PlayerbotWsgDuty.DefendFlagRoom, afterStep[G(1)].Duty);
        Assert.Equal(PlayerbotWsgDuty.TakeEnemyFlag, afterStep[G(2)].Duty);
        Assert.Equal(PlayerbotWsgDuty.TakeEnemyFlag, afterStep[G(3)].Duty);
        Assert.Equal(PlayerbotWsgDuty.Midfield, afterStep[G(4)].Duty);
    }
}

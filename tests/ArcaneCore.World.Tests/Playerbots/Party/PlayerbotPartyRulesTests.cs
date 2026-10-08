using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The pure rules of the party AI (<see cref="PlayerbotParty"/>, vmangos PartyBotAI.cpp): who the master is, the follow and teleport
/// decision, the attack target order and the auto-revive verdict.
/// </summary>
public sealed class PlayerbotPartyRulesTests
{
    private static readonly ObjectGuid Bot = ObjectGuid.Player(10);
    private static readonly ObjectGuid OtherBot = ObjectGuid.Player(11);
    private static readonly ObjectGuid Alice = ObjectGuid.Player(20);
    private static readonly ObjectGuid Bob = ObjectGuid.Player(21);

    // --- master resolution (vmangos GetPartyLeader :107) -------------------------------------------------------------

    [Fact]
    public void Master_IsTheLeader_WhenTheLeaderIsARealPlayerOnline()
        => Assert.Equal(Alice, PlayerbotParty.ResolveMaster(Alice, [Bob, Bot, Alice], Bot, Real(Alice, Bob)));

    [Fact]
    public void Master_IsTheFirstRealPlayerInMemberOrder_WhenTheLeaderIsABot()
        => Assert.Equal(Bob, PlayerbotParty.ResolveMaster(OtherBot, [OtherBot, Bot, Bob, Alice], Bot, Real(Alice, Bob)));

    [Fact]
    public void Master_SkipsAnOfflineLeader_ForTheNextRealPlayerOnline()
        => Assert.Equal(Bob, PlayerbotParty.ResolveMaster(Alice, [Alice, Bot, Bob], Bot, Real(Bob)));

    [Fact]
    public void Master_IsEmpty_ForAGroupOfBotsOnly_OrWithEveryRealPlayerOffline()
    {
        Assert.Equal(ObjectGuid.Empty, PlayerbotParty.ResolveMaster(OtherBot, [OtherBot, Bot], Bot, Real()));
        Assert.Equal(ObjectGuid.Empty, PlayerbotParty.ResolveMaster(Alice, [Alice, Bot], Bot, Real()));
    }

    [Fact]
    public void Master_IsNeverTheBotItself_EvenAsTheLeader()
        => Assert.Equal(Alice, PlayerbotParty.ResolveMaster(Bot, [Bot, Alice], Bot, guid => guid == Bot || guid == Alice));

    [Fact]
    public void Master_IgnoresALeaderWhoIsNoLongerAMember()
        => Assert.Equal(Bob, PlayerbotParty.ResolveMaster(Alice, [Bot, Bob], Bot, Real(Alice, Bob)));

    // --- follow distances and the teleport decision (UpdateAI :806-817, :884-891) -----------------------------------

    [Theory]
    [InlineData(0f, (int)PlayerbotFollowAction.Hold)]
    [InlineData(PlayerbotParty.MaxFollowDistance, (int)PlayerbotFollowAction.Hold)]
    [InlineData(PlayerbotParty.MaxFollowDistance + 0.1f, (int)PlayerbotFollowAction.Move)]
    [InlineData(40f, (int)PlayerbotFollowAction.Move)]
    [InlineData(PlayerbotParty.TeleportDistance, (int)PlayerbotFollowAction.Move)]
    [InlineData(PlayerbotParty.TeleportDistance + 0.1f, (int)PlayerbotFollowAction.Teleport)]
    [InlineData(2500f, (int)PlayerbotFollowAction.Teleport)]
    public void Follow_HoldsWithinFiveYards_WalksFarther_AndTeleportsBeyondOneHundred(float distance, int expected)
        => Assert.Equal((PlayerbotFollowAction)expected, PlayerbotParty.DecideFollow(Facts(distance)));

    [Fact]
    public void Follow_TeleportsToAMasterOnAnotherMapOrInstance()
        => Assert.Equal(PlayerbotFollowAction.Teleport, PlayerbotParty.DecideFollow(Facts(float.NaN) with { SameMap = false }));

    [Fact]
    public void Follow_WaitsForAMasterOnAMapTheBotMayNotEnter()
        => Assert.Equal(PlayerbotFollowAction.Wait, PlayerbotParty.DecideFollow(Facts(float.NaN) with { SameMap = false, MasterMapAllowed = false }));

    [Fact]
    public void Follow_WaitsForAMasterOnATaxiFlight_EvenFarAway()
    {
        Assert.Equal(PlayerbotFollowAction.Wait, PlayerbotParty.DecideFollow(Facts(500f) with { MasterFlying = true }));
        Assert.Equal(PlayerbotFollowAction.Wait, PlayerbotParty.DecideFollow(Facts(20f) with { MasterFlying = true }));
    }

    [Fact]
    public void Follow_WaitsForAMasterBetweenMaps()
        => Assert.Equal(PlayerbotFollowAction.Wait, PlayerbotParty.DecideFollow(Facts(3f) with { MasterInWorld = false }));

    [Fact]
    public void Follow_WithoutTeleportToLeader_WalksToAFarMaster_AndWaitsForOneOnAnotherMap()
    {
        Assert.Equal(PlayerbotFollowAction.Move, PlayerbotParty.DecideFollow(Facts(500f) with { TeleportToLeader = false }));
        Assert.Equal(PlayerbotFollowAction.Wait, PlayerbotParty.DecideFollow(Facts(float.NaN) with { TeleportToLeader = false, SameMap = false }));
    }

    [Theory]
    [InlineData(3f, true)]
    [InlineData(40f, true)]
    [InlineData(float.NaN, false)]
    public void Follow_StayHoldsWhateverTheMasterDoes(float distance, bool sameMap)
        => Assert.Equal(PlayerbotFollowAction.Hold, PlayerbotParty.DecideFollow(Facts(distance) with { Mode = PlayerbotPartyMode.Stay, SameMap = sameMap }));

    [Fact]
    public void Follow_PassiveStillFollows()
        => Assert.Equal(PlayerbotFollowAction.Move, PlayerbotParty.DecideFollow(Facts(20f) with { Mode = PlayerbotPartyMode.Passive }));

    /// <summary>
    /// vmangos UpdateAI :795-817: the teleport to the leader sits inside the !IsInCombat() block. A bot in combat (its attacker out of
    /// reach or no valid target) walks after a far master on its map, and waits for one on another map; out of combat it teleports.
    /// </summary>
    [Fact]
    public void Follow_ABotInCombat_NeverTeleports()
    {
        Assert.Equal(PlayerbotFollowAction.Move, PlayerbotParty.DecideFollow(Facts(500f) with { InCombat = true }));
        Assert.Equal(PlayerbotFollowAction.Wait, PlayerbotParty.DecideFollow(Facts(float.NaN) with { InCombat = true, SameMap = false }));
        Assert.Equal(PlayerbotFollowAction.Teleport, PlayerbotParty.DecideFollow(Facts(500f) with { InCombat = false }));
    }

    /// <summary>
    /// A master in another instance of the bot's own map id cannot be reached by the teleport service: within one map id it teleports
    /// near and keeps the bot's instance (it would return true and loop every think). The bot waits; another map id is teleported to.
    /// </summary>
    [Fact]
    public void Follow_AMasterInAnotherInstanceOfTheSameMap_IsWaitedFor()
    {
        Assert.Equal(PlayerbotFollowAction.Wait,
            PlayerbotParty.DecideFollow(Facts(float.NaN) with { SameMap = false, MasterInOtherInstance = true }));
        Assert.Equal(PlayerbotFollowAction.Teleport,
            PlayerbotParty.DecideFollow(Facts(float.NaN) with { SameMap = false, MasterInOtherInstance = false }));
    }

    [Fact]
    public void FollowPoint_IsTheChosenDistanceFromTheMaster_AtTheAngleFromItsFacing()
    {
        var master = new Vector3(100f, 200f, 30f);
        Vector3 point = PlayerbotParty.FollowPoint(master, MathF.PI / 2, MathF.PI / 2, 4f);

        Assert.Equal(4f, Vector3.Distance(master, point), 3);
        Assert.Equal(96f, point.X, 3); // facing north plus a quarter turn: west of the master
        Assert.Equal(200f, point.Y, 3);
        Assert.Equal(30f, point.Z);
    }

    // --- target selection (SelectAttackTarget :359, SelectPartyAttackTarget :414) -----------------------------------

    [Fact]
    public void Target_OrderedFirst_ThenTheMastersVictim_ThenOwnAttackers_ThenMembersAttackers()
    {
        Assert.Equal("ordered", PlayerbotParty.SelectAttackTarget("ordered", "victim", ["mine"], [("theirs", 10f)], _ => true));
        Assert.Equal("victim", PlayerbotParty.SelectAttackTarget(null, "victim", ["mine"], [("theirs", 10f)], _ => true));
        Assert.Equal("mine", PlayerbotParty.SelectAttackTarget(null, null, ["mine"], [("theirs", 10f)], _ => true));
        Assert.Equal("theirs", PlayerbotParty.SelectAttackTarget<string>(null, null, [], [("theirs", 10f)], _ => true));
    }

    [Fact]
    public void Target_SkipsInvalidCandidates_ForTheNextValidOne()
    {
        string? chosen = PlayerbotParty.SelectAttackTarget("dead-ordered", "dead-victim", ["dead-mine", "mine"], [("theirs", 1f)],
            unit => !unit.StartsWith("dead", StringComparison.Ordinal));
        Assert.Equal("mine", chosen);
    }

    [Fact]
    public void Target_AMembersAttackerCountsOnlyWithinFiftyYards()
    {
        Assert.Equal("near", PlayerbotParty.SelectAttackTarget<string>(null, null, [], [("far", 50.1f), ("near", 50f)], _ => true));
        Assert.Null(PlayerbotParty.SelectAttackTarget<string>(null, null, [], [("far", 50.1f), ("nowhere", float.NaN)], _ => true));
    }

    [Fact]
    public void Target_NoneWhenNothingIsValid()
        => Assert.Null(PlayerbotParty.SelectAttackTarget("a", "b", ["c"], [("d", 1f)], _ => false));

    // --- auto revive (ShouldAutoRevive :237) --------------------------------------------------------------------------

    [Fact]
    public void AutoRevive_AReleasedGhostRevivesAtOnce()
        => Assert.True(PlayerbotParty.ShouldAutoRevive(ghost: true, [new(InCombat: true, Alive: true, Healer: true, Distance: 1f)]));

    [Fact]
    public void AutoRevive_WaitsWhileAMemberFights()
        => Assert.False(PlayerbotParty.ShouldAutoRevive(false, [new(false, true, false, 5f), new(true, true, false, 80f)]));

    [Fact]
    public void AutoRevive_WaitsForALivingHealer()
        => Assert.False(PlayerbotParty.ShouldAutoRevive(false, [new(false, true, true, 5f)])); // company within 15 yards, but a healer could resurrect it

    [Fact]
    public void AutoRevive_RevivesWithALivingMemberWithinFifteenYards_AndNotWithoutOne()
    {
        Assert.True(PlayerbotParty.ShouldAutoRevive(false, [new(false, true, false, 15f)]));
        Assert.False(PlayerbotParty.ShouldAutoRevive(false, [new(false, true, false, 15.5f), new(false, true, false, null)]));
        Assert.False(PlayerbotParty.ShouldAutoRevive(false, [new(false, false, true, 1f)])); // a dead healer resurrects nobody, and is no company
    }

    [Theory]
    [InlineData(Class.Priest, true)]
    [InlineData(Class.Paladin, true)]
    [InlineData(Class.Shaman, true)]
    [InlineData(Class.Druid, true)]
    [InlineData(Class.Warrior, false)]
    [InlineData(Class.Mage, false)]
    public void HealerClasses_AreTheResurrectingOnes(Class @class, bool healer) => Assert.Equal(healer, PlayerbotParty.IsHealerClass(@class));

    private static PlayerbotFollowFacts Facts(float distance)
        => new(PlayerbotPartyMode.Follow, MasterInWorld: true, SameMap: true, distance, TeleportToLeader: true, MasterMapAllowed: true);

    private static Func<ObjectGuid, bool> Real(params ObjectGuid[] online) => guid => online.Contains(guid);
}

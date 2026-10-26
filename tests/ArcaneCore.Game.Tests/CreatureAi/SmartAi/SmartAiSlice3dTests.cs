using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// SmartAI slice 3d: events MANA_PCT (3), RANGE (9), RESPAWN (11), FRIENDLY_HEALTH (14); actions SET_REACT_STATE (8), RANDOM_EMOTE (10),
/// SET_UNIT_FLAG (18), REMOVE_UNIT_FLAG (19), FLEE_FOR_ASSIST (25), ATTACK_START (49); slice 3e: event REWARD_QUEST (20), actions SET_INST_DATA (34)
/// and SUMMON_GO (50). Rows use raw AzerothCore ids (SmartScriptMgr.h), so they state the numbering the ported data uses.
/// </summary>
public sealed class SmartAiSlice3dTests
{
    private const int Wolf = (int)WolfEntry;
    private const uint NotRepeatable = 1;

    private static void Pull(SmartRig rig, Creature wolf) => rig.Map.Combat.DealDamage(rig.Player, wolf, 1, direct: false);

    private static Creature BySpawn(SmartRig rig, uint guid) => rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == guid);

    private static List<CreatureAiTestKit.MonsterChat> Chats(SmartRig rig)
        => [.. Packets(rig.Session, WorldOpcode.SmsgMessagechat).Select(ParseMonsterChat)];

    // ---- actions ----

    [Fact]
    public void FleeForAssist_RunsToTheNearestHelper_AndWithEmoteSaysTheFleeLine()
    {
        // The usual ported row: HEALTH_PCT 0-15, FLEE_FOR_ASSIST withEmote 1, target none (644 of the 699 classic rows).
        using var rig = SmartRig.Start(
            [Make(Wolf, 0, 0, 2, 25, 0, e1: 0, e2: 100, a1: 1, flags: NotRepeatable)],
            creatureSpawns: [Spawn(2, WolfEntry, 25, 0)]);
        Creature wolf = BySpawn(rig, 1);
        Pull(rig, wolf);
        rig.World.RunTick(100);

        Assert.Equal(MovementGeneratorType.Point, wolf.Motion.CurrentType);
        CreatureAiTestKit.MonsterChat emote = Assert.Single(Chats(rig));
        Assert.Equal(ChatType.MonsterEmote, emote.Type);
        Assert.Equal("%s attempts to run away in fear!", emote.Message);
    }

    [Fact]
    public void FleeForAssist_WithoutEmote_SaysNothing_AndAStunnedCreatureDoesNotFlee()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 2, 25, 0, e1: 0, e2: 100, flags: NotRepeatable)]);
        Creature wolf = rig.Wolf;
        Pull(rig, wolf);
        wolf.UnitFlags |= UnitFlags.Stunned;
        rig.World.RunTick(100);

        Assert.NotEqual(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        Assert.NotEqual(MovementGeneratorType.Point, wolf.Motion.CurrentType);
        Assert.Empty(Chats(rig));
    }

    [Fact]
    public void FleeForAssist_WaitsWhileTheCreatureCasts()
    {
        // SmartScript.cpp UpdateTimer: a flee whose creature is casting is put off by 1200 ms.
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 2, 25, 0, e1: 0, e2: 100, flags: NotRepeatable)]);
        Creature wolf = rig.Wolf;
        Pull(rig, wolf);
        rig.Spells.Casting = true;
        rig.World.RunTick(100);
        Assert.NotEqual(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        Assert.Equal(1200u, rig.Script.Events[0].TimerMs);

        rig.Spells.Casting = false;
        rig.World.RunTick(1300);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
    }

    [Fact]
    public void AttackStart_TheCreatureAttacksTheTarget()
    {
        // The player stands 60 yards off, outside aggro range: only the action can start the fight.
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 1, 49, 21, flags: NotRepeatable)], playerX: 65);
        rig.World.RunTick(100);
        Assert.Same(rig.Player, rig.Wolf.Combat.Victim);
    }

    [Fact]
    public void SetReactState_ChangesTheCreaturesReactState()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 60, 8, 1, e1: 500, e2: 500, a1: 0, flags: NotRepeatable)], playerX: 65);
        Assert.Equal(CreatureReactState.Aggressive, rig.Wolf.ReactState);
        rig.World.RunTick(600);
        Assert.Equal(CreatureReactState.Passive, rig.Wolf.ReactState);
    }

    [Fact]
    public void RandomEmote_PlaysOneOfTheNonZeroEmotes()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 4, 10, 1, a1: 4, a2: 0, a3: 5)]);
        Pull(rig, rig.Wolf);
        uint emote = BitConverter.ToUInt32(Assert.Single(Packets(rig.Session, WorldOpcode.SmsgEmote)), 0);
        Assert.Contains(emote, new uint[] { 4, 5 });
    }

    [Fact]
    public void SetAndRemoveUnitFlag_ChangeUnitFieldFlags()
    {
        using var rig = SmartRig.Start(
        [
            Make(Wolf, 0, 0, 60, 18, 1, a1: 0x300, link: 1, flags: NotRepeatable),
            Make(Wolf, 0, 1, 61, 19, 1, a1: 0x100),
        ], playerX: 65);
        rig.World.RunTick(100);
        Assert.Equal((UnitFlags)0x200, rig.Wolf.UnitFlags & (UnitFlags)0x300);
    }

    // ---- events ----

    [Fact]
    public void ManaPct_RunsOnlyWhileTheManaIsInTheBand()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 3, 22, 1, e1: 0, e2: 30, a1: 5)]);
        Creature wolf = rig.Wolf;
        wolf.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        wolf.SetUInt32(UpdateFields.UnitFieldPower1, 600);
        Pull(rig, wolf);
        rig.World.RunTick(100);
        Assert.Equal(0u, rig.Script.Phase);

        wolf.SetUInt32(UpdateFields.UnitFieldPower1, 100);
        rig.World.RunTick(100);
        Assert.Equal(5u, rig.Script.Phase);
    }

    [Fact]
    public void Range_RunsWhenTheVictimIsInsideTheRange_ElseRechecksIn1200()
    {
        // AzerothCore's RANGE row: min/max the first timer, repeat, then rangeMin/rangeMax in params 5 and 6.
        using var near = SmartRig.Start([Make(Wolf, 0, 0, 9, 22, 1, a1: 6) with { EventParam6 = 10 }]);
        Pull(near, near.Wolf);
        near.World.RunTick(100);
        Assert.Equal(6u, near.Script.Phase);
        Assert.Same(near.Player, near.Script.LastInvoker);

        using var far = SmartRig.Start([Make(Wolf, 0, 0, 9, 22, 1, a1: 6) with { EventParam5 = 30, EventParam6 = 40 }]);
        Pull(far, far.Wolf);
        far.World.RunTick(100);
        Assert.Equal(0u, far.Script.Phase);
        Assert.Equal(1200u, far.Script.Events[0].TimerMs);
    }

    [Fact]
    public void Respawn_FiresWhenTheCreatureSpawns_AndTheMapConditionFilters()
    {
        using var any = SmartRig.Start([Make(Wolf, 0, 0, 11, 22, 1, a1: 8)]);
        Assert.Equal(8u, any.Script.Phase);

        using var thisMap = SmartRig.Start([Make(Wolf, 0, 0, 11, 22, 1, e1: 1, e2: 0, a1: 8)]);
        Assert.Equal(8u, thisMap.Script.Phase);

        using var otherMap = SmartRig.Start([Make(Wolf, 0, 0, 11, 22, 1, e1: 1, e2: 1, a1: 8)]);
        Assert.Equal(0u, otherMap.Script.Phase);
    }

    [Fact]
    public void FriendlyHealth_TargetsTheFriendMissingTheMostHealth()
    {
        // FRIENDLY_HEALTH: hpDeficit 10, radius 30; the hurt friend is the invoker (target 7).
        using var rig = SmartRig.Start(
            [Make(Wolf, 0, 0, 14, 22, 1, e1: 10, e2: 30, a1: 7)],
            creatureSpawns: [Spawn(2, WolfEntry, 12, 0)]);
        Creature first = BySpawn(rig, 1);
        Creature second = BySpawn(rig, 2);
        Pull(rig, first);
        rig.Map.Combat.DealDamage(rig.Player, second, second.MaxHealth / 2, direct: false);
        rig.World.RunTick(100);

        SmartScript script = SmartRig.AiOf(first).Script;
        Assert.Equal(7u, script.Phase);
        Assert.Same(second, script.LastInvoker);
    }

    // ---- load rules ----

    [Fact]
    public void Catalog_LoadsTheNewIds_AndRefusesTheirBadParameters()
    {
        var catalog = new SmartScriptCatalog(
        [
            Make(Wolf, 0, 0, 3, 22, 1, e1: 0, e2: 30, a1: 1),
            Make(Wolf, 0, 1, 9, 22, 1, a1: 1) with { EventParam6 = 5 },
            Make(Wolf, 0, 2, 11, 22, 1, a1: 1),
            Make(Wolf, 0, 3, 14, 22, 1, e1: 10, e2: 30, a1: 1),
            Make(Wolf, 0, 4, 4, 8, 1, a1: 2),
            Make(Wolf, 0, 5, 4, 10, 1, a1: 4),
            Make(Wolf, 0, 6, 4, 18, 1, a1: 0x100),
            Make(Wolf, 0, 7, 4, 19, 1, a1: 0x100),
            Make(Wolf, 0, 8, 2, 25, 0, e1: 0, e2: 15, a1: 1),
            Make(Wolf, 0, 9, 4, 49, 7),

            Make(Wolf, 0, 20, 4, 10, 1),
            Make(Wolf, 0, 21, 4, 8, 1, a1: 3),
            Make(Wolf, 0, 22, 4, 18, 1, a1: 0x100, a2: 1),
            Make(Wolf, 0, 23, 9, 22, 1, a1: 1) with { EventParam5 = 20, EventParam6 = 10 },
            Make(Wolf, 0, 24, 14, 22, 1, e1: 10, e2: 0, a1: 1),
            Make(Wolf, 0, 25, 3, 22, 1, e1: 50, e2: 30, a1: 1),
            Make(50000, 1, 0, 64, 25, 0),
            Make(50000, 1, 1, 64, 49, 7),
        ]);

        Assert.Equal(10, catalog.For(WolfEntry, 0).Count);
        string[] reasons = [.. catalog.Rejected.OrderBy(r => r.SourceType).ThenBy(r => r.Id).Select(r => r.Reason)];
        Assert.Equal(8, reasons.Length);
        Assert.Contains("no non-zero emote", reasons[0], StringComparison.Ordinal);
        Assert.Contains("react state 3", reasons[1], StringComparison.Ordinal);
        Assert.Contains("UNIT_FIELD_FLAGS_2", reasons[2], StringComparison.Ordinal);
        Assert.Contains("range 20..10", reasons[3], StringComparison.Ordinal);
        Assert.Contains("radius 0", reasons[4], StringComparison.Ordinal);
        Assert.Contains("mana 50..30", reasons[5], StringComparison.Ordinal);
        Assert.Contains("own creature", reasons[6], StringComparison.Ordinal);
        Assert.Contains("own creature", reasons[7], StringComparison.Ordinal);
    }

    // ---- slice 3e ----

    private sealed class RecordingInstance(Map map) : InstanceData(map)
    {
        public Dictionary<uint, uint> Data { get; } = [];

        public override void SetData(uint type, uint data) => Data[type] = data;
    }

    [Fact]
    public void RewardQuest_RunsForTheNamedQuestOnly_AndQuestZeroMatchesAny()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 20, 22, 1, e1: 100, a1: 3), Make(Wolf, 0, 1, 20, 23, 1, a1: 1)]);
        rig.WolfAi.OnQuestRewarded(rig.Player, 99);
        Assert.Equal(1u, rig.Script.Phase);

        rig.WolfAi.OnQuestRewarded(rig.Player, 100);
        Assert.Equal(4u, rig.Script.Phase);
    }

    [Fact]
    public void RewardQuest_Cooldown_SuppressesTheNextReward()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 20, 23, 1, e2: 60_000, e3: 60_000, a1: 1)]);
        rig.WolfAi.OnQuestRewarded(rig.Player, 7);
        rig.WolfAi.OnQuestRewarded(rig.Player, 7);
        Assert.Equal(1u, rig.Script.Phase);
    }

    [Fact]
    public void SetInstData_WritesTheMapsInstanceScript_AndWithoutOneDoesNothing()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 20, 34, 1, a1: 5, a2: 9)]);
        rig.WolfAi.OnQuestRewarded(rig.Player, 1);
        Assert.Empty(rig.Script.Unsupported);

        var instance = new RecordingInstance(rig.Map);
        rig.Map.AddUpdater(instance);
        rig.WolfAi.OnQuestRewarded(rig.Player, 1);
        Assert.Equal(9u, instance.Data[5]);
    }

    [Fact]
    public void SummonGo_AtEachTargetPlusOffset_AtTheBaseWithTargetSummon_AndAtAPositionTarget()
    {
        using var rig = SmartRig.Start(
        [
            Make(Wolf, 0, 0, 20, 50, 1, a1: SmartRig.ObjectEntry) with { TargetX = 2f, TargetO = 1f },
            Make(Wolf, 0, 1, 20, 50, 7, a1: SmartRig.ObjectEntry2, a3: 1),
            Make(Wolf, 0, 2, 20, 50, 8, a1: SmartRig.ObjectEntryDespawning, a2: 30) with { TargetX = 10f, TargetY = 20f },
        ], objectSpawns: []);
        rig.WolfAi.OnQuestRewarded(rig.Player, 1);

        GameObject self = Assert.Single(rig.Objects!.GameObjects, g => g.Entry == SmartRig.ObjectEntry);
        Assert.Equal((7f, 0f), (self.X, self.Y));
        Assert.Equal(rig.Wolf.Orientation + 1f, self.Orientation);
        GameObject atBase = Assert.Single(rig.Objects.GameObjects, g => g.Entry == SmartRig.ObjectEntry2);
        Assert.Equal((5f, 0f), (atBase.X, atBase.Y));
        GameObject atPosition = Assert.Single(rig.Objects.GameObjects, g => g.Entry == SmartRig.ObjectEntryDespawning);
        Assert.Equal((10f, 20f), (atPosition.X, atPosition.Y));
    }

    [Fact]
    public void Catalog_Slice3e_RefusesSetInstDataBossStateEscortReachedAndABadRewardCooldown_AcceptsObjectSummonGo()
    {
        var catalog = new SmartScriptCatalog(
        [
            Make(Wolf, 0, 0, 20, 34, 1, a1: 5, a2: 1),
            Make(Wolf, 0, 1, 20, 50, 1, a1: SmartRig.ObjectEntry),
            Make(Wolf, 0, 2, 20, 34, 1, a1: 5, a2: 1, a3: 1),
            Make(Wolf, 0, 3, 20, 34, 1, a3: 2),
            Make(Wolf, 0, 4, 40, 22, 1, a1: 1),
            Make(Wolf, 0, 5, 20, 22, 1, e2: 5, e3: 1, a1: 1),
            Make(50000, 1, 0, 64, 50, 1, a1: SmartRig.ObjectEntry2),
            Make(50000, 1, 1, 64, 34, 1, a1: 5, a2: 1),
        ]);

        Assert.Equal(2, catalog.For(WolfEntry, 0).Count);
        string[] reasons = [.. catalog.Rejected.OrderBy(r => r.SourceType).ThenBy(r => r.Id).Select(r => r.Reason)];
        Assert.Equal(4, reasons.Length);
        Assert.Contains("SET_INST_DATA type 1", reasons[0], StringComparison.Ordinal);
        Assert.Contains("SET_INST_DATA type 2", reasons[1], StringComparison.Ordinal);
        Assert.Contains("needs a SMART_ACTION_WP_START escort", reasons[2], StringComparison.Ordinal);
        Assert.Contains("cooldown 5..1", reasons[3], StringComparison.Ordinal);
    }
}

using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class DeadminesScriptTests
{
    [Fact]
    public void FortuneAwaitsChestRespawnsOnlyForAPlayerWithTheCompletedUnrewardedQuest()
    {
        using var without = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [], [], [(180024, GameObjectType.Chest)], objectSpawnTimes: new Dictionary<uint, int> { [180024] = -300 });
        Assert.False(without.Object(180024).IsSpawned);

        using var with = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [], [], [(180024, GameObjectType.Chest)], objectSpawnTimes: new Dictionary<uint, int> { [180024] = -300 },
            questReady: (_, quest) => quest == 7938);
        Assert.True(with.Object(180024).IsSpawned);
    }

    [Fact]
    public void RhahkzorPatrolIsAbsentBeforeHisDeath_AndUsesItsScriptOnlySpawnRowAfter()
    {
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [644, 634], [644], [], extraSpawns:
            [Spawn(3_600_200, 634, -13f, -383.07f, 61.78f, mapId: InstanceFixture.Dungeon)]);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == 634);
        run.Kill(644);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 634);
    }

    [Fact]
    public void MrSmite_StompsAtSixtySixPercent_ThenRunsToHisChestAndResumesInPhaseTwo()
    {
        var spells = new RecordingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [646], [646], [(144111, GameObjectType.Generic)],
            aiServices: new CreatureAiServices { Spells = spells });
        Creature smite = run.Creature(646);
        var ai = Assert.IsType<ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi>(smite.AI);
        Assert.Equal(7420u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        Assert.True(run.Creatures.AttackStart(smite, run.Player));
        smite.Health = smite.MaxHealth * 60 / 100;
        run.Tick();

        Assert.Contains(6432u, spells.Casts); // Stomp
        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Equipping, ai.Phase);
        run.Tick(2_500);
        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.MovingToChest, ai.Phase);
        for (int i = 0; i < 50 && ai.Phase != ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Kneeling; i++)
        {
            run.Tick(100);
        }

        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Kneeling, ai.Phase);
        Assert.Equal(StandState.Kneel, smite.StandState);
        Assert.Equal(0u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        run.Tick(3_000);
        run.Tick(1_000);
        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Second, ai.Phase);
        Assert.Contains(12787u, spells.Casts); // Thrash
        Assert.Equal(7427u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        Assert.Equal(7427u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 1));
    }

    [Fact]
    public void MrSmite_EvadeAfterTheAxePhase_ResetsToPhaseOneWithTheSword()
    {
        var spells = new RecordingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [646], [646], [(144111, GameObjectType.Generic)],
            aiServices: new CreatureAiServices { Spells = spells });
        Creature smite = run.Creature(646);
        var ai = Assert.IsType<ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi>(smite.AI);
        Assert.True(run.Creatures.AttackStart(smite, run.Player));
        smite.Health = smite.MaxHealth * 60 / 100;
        run.Tick();
        run.Tick(2_500);
        for (int i = 0; i < 50 && ai.Phase != ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Kneeling; i++)
        {
            run.Tick(100);
        }

        run.Tick(3_000);
        run.Tick(1_000);
        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Second, ai.Phase);
        Assert.Equal(7427u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));

        // boss_mr_smiteAI::Reset runs on every evade (CreatureAI::EnterEvadeMode).
        ai.EnterEvadeMode();
        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.First, ai.Phase);
        Assert.Equal(7420u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        Assert.Equal(0u, smite.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 1));
    }

    [Fact]
    public void BossDeathsOpenFactoryDoors_AndCannonOpensIroncladDoorAfterDelay()
    {
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [644, 643, 1763, 646], [644, 643, 1763, 646],
            [(13965, GameObjectType.Door), (16400, GameObjectType.Door), (16399, GameObjectType.Door),
                (16397, GameObjectType.Door), (16398, GameObjectType.Button)]);
        run.Kill(644);
        run.Kill(643);
        run.Kill(1763);
        Assert.Equal(GameObjectState.Active, run.Object(13965).State);
        Assert.Equal(GameObjectState.Active, run.Object(16400).State);
        Assert.Equal(GameObjectState.Active, run.Object(16399).State);

        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(16398).Guid));
        Assert.Equal(GameObjectState.Ready, run.Object(16397).State);
        run.Tick(500);
        Assert.Equal(GameObjectState.ActiveAlternative, run.Object(16397).State);
        Assert.Equal(EncounterState.Done, run.Script.GetData(3));
    }
}

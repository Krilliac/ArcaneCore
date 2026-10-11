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
    public void RhahkzorPatrolWaitsSixtySecondsAfterDeath()
    {
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [644, 634], [644], [], extraSpawns:
            [Spawn(3_600_200, 634, -13f, -383.07f, 61.78f, mapId: InstanceFixture.Dungeon)]);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == 634);
        run.Kill(644);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == 634);
        run.Tick(59_999);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == 634);
        run.Tick(1);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 634);
    }

    [Fact]
    public void GilnidPatrolWaitsThirtySecondsAfterDeath()
    {
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [1763, 4417], [1763], [], extraSpawns:
            [Spawn(3_600_204, 4417, -13f, -383.07f, 61.78f, mapId: InstanceFixture.Dungeon)]);
        run.Kill(1763);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == 4417);
        run.Tick(29_999);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Entry == 4417);
        run.Tick(1);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 4417);
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
        // vmangos boss_mr_smiteAI::UpdateAI casts 3391 on a 4 s initial timer
        // in phase two; its 12787 proc aura is explicitly not used there.
        Assert.DoesNotContain(12787u, spells.Casts);
        run.Tick(4_000);
        Assert.DoesNotContain(3391u, spells.Casts);
        run.Tick(1);
        Assert.Contains(3391u, spells.Casts);
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
    public void MrSmite_ThirdPhaseSlamUsesTheNineSecondInitialTimer()
    {
        var spells = new RecordingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [646], [646], [(144111, GameObjectType.Generic)],
            aiServices: new CreatureAiServices { Spells = spells });
        Creature smite = run.Creature(646);
        var ai = Assert.IsType<ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi>(smite.AI);
        Assert.True(run.Creatures.AttackStart(smite, run.Player));
        smite.Health = smite.MaxHealth / 5;
        run.Tick();
        run.Tick(2_500);
        for (int i = 0; i < 50 && ai.Phase != ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Kneeling; i++)
        {
            run.Tick(100);
        }

        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Kneeling, ai.Phase);
        run.Tick(3_000);
        run.Tick(1_000);
        Assert.Equal(ArcaneCore.Game.Instances.Scripts.Deadmines.MrSmiteAi.SmitePhase.Third, ai.Phase);
        run.Tick(9_000);
        Assert.DoesNotContain(6435u, spells.Casts);
        run.Tick(1);
        Assert.Contains(6435u, spells.Casts);
    }

    [Fact]
    public void BossDeathsOpenFactoryDoors_AndCannonOpensIroncladDoorAtUse()
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
        Assert.Equal(GameObjectState.Active, run.Object(16397).State);
        Assert.Equal(EncounterState.InProgress, run.Script.GetData(3));
    }

    [Fact]
    public void CannonOpensIroncladDoorAtUse_AndStartsThreeSecondAlarmDelay()
    {
        // vmangos deadmines.cpp GOHello_go_defias_cannon -> SetData(IN_PROGRESS),
        // instance_deadmines.cpp SetData opens the door now and Update waits 3000 ms for alarm 1.
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [646, 657], [646], [(16397, GameObjectType.Door), (16398, GameObjectType.Button)],
            extraSpawns: [Spawn(3_600_148, 657, -80f, -380f, 61.78f, mapId: InstanceFixture.Dungeon)]);
        var guard = run.Creature(657);
        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(16398).Guid));
        Assert.Equal(EncounterState.InProgress, run.Script.GetData(3));
        Assert.Equal(GameObjectState.Active, run.Object(16397).State);
        Assert.NotEqual(MovementGeneratorType.Point, guard.Motion.CurrentType);
        run.Script.Update(2_999);
        Assert.NotEqual(MovementGeneratorType.Point, guard.Motion.CurrentType);
        run.Script.Update(1);
        Assert.Equal(MovementGeneratorType.Point, guard.Motion.CurrentType);
    }

    [Fact]
    public void GunpowderUseStartsOneOverseerRoute()
    {
        // vmangos deadmines.cpp GOHello_go_defias_gunpowder: the first use summons
        // one Defias Overseer and records GUN_POWDER_EVENT; later uses do not summon.
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [634], [], [(17155, GameObjectType.Chest)]);
        var powder = run.Object(17155);
        var ai = run.Objects.AiFor(powder);
        Assert.NotNull(ai);
        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, powder.Guid));
        Assert.Equal(1u, run.Script.GetData(5000));
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 634);
        Assert.Equal(MovementGeneratorType.Point, run.Creature(634).Motion.CurrentType);
        ai.OnUse(run.Objects, powder, run.Player);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 634);
        var overseer = run.Creature(634);
        for (int i = 0; i < 60 && MathF.Abs(overseer.Home.X - (-115.263672f)) > 1f; i++)
        {
            run.Tick(1_000);
        }

        Assert.InRange(overseer.Home.X, -115.264f, -115.263f);
        Assert.InRange(overseer.Home.Y, -617.397f, -617.395f);
    }

    [Fact]
    public void LeverAllowsItsNormalUseOnlyAfterIroncladDoorOpens()
    {
        // vmangos deadmines.cpp GOHello_go_door_lever_dm checks the Ironclad door's GO state.
        using var run = new DungeonScriptTestKit(map => new ArcaneCore.Game.Instances.Scripts.Deadmines.DeadminesInstance(map),
            [], [], [(16397, GameObjectType.Door), (101833, GameObjectType.Button)]);
        var lever = run.Object(101833);
        var ai = run.Objects.AiFor(lever);
        Assert.NotNull(ai);
        Assert.True(ai.OnUse(run.Objects, lever, run.Player));
        run.Object(16397).State = GameObjectState.Active;
        Assert.False(ai.OnUse(run.Objects, lever, run.Player));
    }
}

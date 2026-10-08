using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.ScarletMonastery;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Tests.CreatureAi;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>The Mograine and Whitemane wipe and revival, aggro on sight and evade resets of the Scarlet Monastery boss ports.</summary>
public sealed class ScarletMonasteryEncounterTests
{
    private static DungeonScriptHarness MograineAndWhitemane(CreatureAiServices? services = null)
        => new(map => new ScarletMonasteryInstance(map), [3976, 3977], [3976, 3977], null, services,
            (ScarletMonasteryInstance.WhitemaneDoor, GameObjectType.Door));

    private static void Ticks(DungeonScriptHarness run, uint ms)
    {
        for (uint done = 0; done < ms; done += 50)
        {
            run.Tick(50);
        }
    }

    [Fact]
    public void Wipe_DespawnsBothBosses_ClosesWhitemanesDoor_ResetsTheEvent_AndBringsThemBackIn30Seconds()
    {
        // instance_scarlet_monastery.cpp SetData(TYPE_MOGRAINE_AND_WHITE_EVENT, FAIL) with both alive: SetRespawnDelay(30, true) and
        // ForcedDespawn on each, DoUseDoorOrButton(GO_WHITEMANE_DOOR), m_auiEncounter[0] = NOT_STARTED. The harness spawns respawn in 120 s.
        using DungeonScriptHarness run = MograineAndWhitemane();
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        Creature mograine = run.Creature(3976), whitemane = run.Creature(3977);
        run.Map.Combat.DealDamage(run.Player, mograine, mograine.Health, direct: false);
        Assert.Equal(GameObjectState.Active, run.Object(ScarletMonasteryInstance.WhitemaneDoor).State);

        run.Creatures.EnterEvadeMode(whitemane); // Whitemane's EnterEvadeMode fails the event

        Assert.Equal(EncounterState.NotStarted, data.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane));
        Assert.Equal(GameObjectState.Ready, run.Object(ScarletMonasteryInstance.WhitemaneDoor).State);
        Assert.False(mograine.IsAlive);
        Assert.False(whitemane.IsAlive);
        Ticks(run, 29_000);
        Assert.False(run.Creatures.FindCreature(mograine.Guid)?.IsAlive ?? false);
        Ticks(run, 2_000);
        Creature back = Assert.IsType<Creature>(run.Creatures.FindCreature(mograine.Guid));
        Assert.True(back.IsAlive);
        Assert.True(run.Creatures.FindCreature(whitemane.Guid)?.IsAlive);
        var ai = Assert.IsType<MograineAi>(back.AI);
        Assert.False(ai.IsFeigningDeath);
        Assert.Equal(1u, back.InvincibilityHpThreshold); // death prevention is on again for the next attempt
        Assert.Null(back.RespawnDelayOnceSeconds); // the 30 s delay was for that death only
    }

    [Fact]
    public void Wipe_AfterMograineDied_DespawnsWhitemaneForA30SecondRespawn_AndLeavesTheEventFailed()
    {
        using DungeonScriptHarness run = MograineAndWhitemane();
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        Creature mograine = run.Creature(3976), whitemane = run.Creature(3977);
        data.SetData(ScarletMonasteryInstance.TypeMograineAndWhitemane, EncounterState.InProgress);
        mograine.InvincibilityHpThreshold = 0;
        run.Kill(3976);

        data.SetData(ScarletMonasteryInstance.TypeMograineAndWhitemane, EncounterState.Fail);

        Assert.Equal(EncounterState.Fail, data.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane));
        Assert.False(whitemane.IsAlive);
        Assert.Equal(GameObjectState.Ready, run.Object(ScarletMonasteryInstance.WhitemaneDoor).State);
        Ticks(run, 31_000);
        Assert.True(run.Creatures.FindCreature(whitemane.Guid)?.IsAlive);
        Assert.False(run.Creatures.FindCreature(mograine.Guid)?.IsAlive ?? false);
    }

    [Fact]
    public void Mograine_AfterLayOnHands_TheKillingBlowKills_AndDoesNotRestartTheEvent()
    {
        // JustPreventedDeath only fires while SetDeathPrevention(true); HandleLayOnHandsTimer turns it off.
        using DungeonScriptHarness run = MograineAndWhitemane();
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        Creature mograine = run.Creature(3976);
        var ai = Assert.IsType<MograineAi>(mograine.AI);
        run.Map.Combat.DealDamage(run.Player, mograine, mograine.Health, direct: false);
        Assert.True(ai.IsFeigningDeath);

        ai.OnResurrected(); // Whitemane's Scarlet Resurrection landed
        Ticks(run, 3_050);
        Assert.True(ai.IsRevived);
        Assert.Equal(0u, mograine.InvincibilityHpThreshold);
        Ticks(run, 2_050);
        Assert.False(ai.IsFeigningDeath);
        Assert.Equal(EncounterState.Special, data.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane));

        mograine.Health = 10;
        run.Map.Combat.DealDamage(run.Player, mograine, 10, direct: false);

        Assert.False(mograine.IsAlive);
        Assert.False(ai.IsFeigningDeath);
        Assert.Equal(EncounterState.Special, data.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane));
        Assert.Equal(GameObjectState.Active, run.Object(ScarletMonasteryInstance.WhitemaneDoor).State);
    }

    [Theory]
    [InlineData(HerodAi.Entry)]
    [InlineData(DoanAi.Entry)]
    [InlineData(ScarletMonasteryInstance.Mograine)]
    public void ScriptedBosses_AttackAHostilePlayerOnSight(uint entry)
    {
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map), [entry], [entry], null,
            new CreatureAiServices { Hostility = new AlwaysHostile() });
        Creature boss = run.Creature(entry);
        Assert.IsAssignableFrom<ScriptedAI>(boss.AI);
        Ticks(run, 1_000);
        Assert.Same(run.Player, boss.Combat.Victim);
    }

    [Fact]
    public void Whitemane_StaysPassiveOnSight()
    {
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map), [3977], [3977], null,
            new CreatureAiServices { Hostility = new AlwaysHostile() });
        Ticks(run, 1_000);
        Assert.Null(run.Creature(3977).Combat.Victim);
    }

    [Fact]
    public void HerodAndDoan_ForgetTheirFrenzyAndBubbleWhenTheyEvade()
    {
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map), [HerodAi.Entry, DoanAi.Entry],
            [HerodAi.Entry, DoanAi.Entry], null, new CreatureAiServices { Spells = caster });
        Creature herod = run.Creature(HerodAi.Entry), doan = run.Creature(DoanAi.Entry);
        Assert.True(run.Creatures.AttackStart(herod, run.Player));
        Assert.True(run.Creatures.AttackStart(doan, run.Player));
        herod.Health = herod.MaxHealth / 5;
        doan.Health = doan.MaxHealth / 3;
        Ticks(run, 100);
        Assert.True(Assert.IsType<HerodAi>(herod.AI).IsEnraged);
        Assert.True(Assert.IsType<DoanAi>(doan.AI).HasShielded);

        run.Creatures.EnterEvadeMode(herod);
        run.Creatures.EnterEvadeMode(doan);

        Assert.False(Assert.IsType<HerodAi>(herod.AI).IsEnraged);
        Assert.False(Assert.IsType<DoanAi>(doan.AI).HasShielded);
    }

    [Fact]
    public void Doan_PolymorphsAnAttackerBelowTheTopOfHisThreatList_NeverABystander()
    {
        // boss_arcanist_doan.cpp: SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 1).
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map), [DoanAi.Entry], [DoanAi.Entry], null,
            new CreatureAiServices { Spells = caster });
        Player bystander = run.AddPlayer(2);
        Creature doan = run.Creature(DoanAi.Entry);
        Assert.True(run.Creatures.AttackStart(doan, run.Player));
        run.Map.Combat.DealDamage(run.Player, doan, 10, direct: true);
        Ticks(run, 15_100);
        Assert.DoesNotContain(caster.Casts, c => c.Spell == 13323); // only the tank is on the threat list, and it is the top entry

        run.Map.Combat.DealDamage(bystander, doan, 1, direct: true);
        Ticks(run, 100);
        Assert.Contains(caster.Casts, c => c.Spell == 13323 && ReferenceEquals(c.Target, bystander));
        Assert.DoesNotContain(caster.Casts, c => c.Spell == 13323 && ReferenceEquals(c.Target, run.Player));
    }
}

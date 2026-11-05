using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>The in-process world host on the manual clock: delivery barrier, game-time budgets, reproducible runs.</summary>
[Collection("World tick load")]
public sealed class WorldTestHostManualClockTests
{
    [Fact]
    public async Task ASentPacket_IsAnsweredByAStep_AndTheStepAdvancesExactlyOneStepOfGameTime()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        WorldTestClient client = await host.EnterWorldAsync("clockacct", "Clocky");
        await host.SettleAsync();
        long gameBefore = host.World.Clock.ElapsedMs;

        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await host.StepAsync(1); // delivery barrier, then one step. The reply would also come from the zero-diff wall wakes of the manual world, so this does not pin the barrier itself

        await client.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
        Assert.Equal(gameBefore + 50, host.World.Clock.ElapsedMs);
        Assert.Equal(0, host.InboundWorldQueueDepth());
    }

    [Fact]
    public async Task RunUntilAsync_FailsByGameBudget_WithGameAndWallInTheMessage()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        long gameBefore = host.World.Clock.ElapsedMs;

        GameTimeoutException ex = await Assert.ThrowsAsync<GameTimeoutException>(
            () => host.RunUntilAsync(() => false, "something that never happens", maxGameMs: 200));

        Assert.Contains("something that never happens", ex.Message);
        Assert.Contains("200 game ms", ex.Message);
        Assert.Contains(" ticks", ex.Message);
        Assert.Contains("wall ms", ex.Message);
        Assert.Equal(gameBefore + 200, host.World.Clock.ElapsedMs);
    }

    [Fact]
    public async Task TwoRunsWithTheSameSeedAndSteps_ProduceIdenticalOutboundBytes()
    {
        // A scripted login, chat and played-time interaction over fixed steps. The damage cast and the swing are in the next two tests, with seeded dice;
        // loot determinism is ChestGoldAndConditionTests.ChestGold_IsReproducibleFromTheLootServiceSeed (Game.Tests), through the seeded Random of LootService.
        List<(WorldOpcode Opcode, byte[] Payload)> first = await RunScriptedAsync();
        List<(WorldOpcode Opcode, byte[] Payload)> second = await RunScriptedAsync();

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Opcode, second[i].Opcode);
            Assert.True(first[i].Payload.AsSpan().SequenceEqual(second[i].Payload), $"packet {i} ({first[i].Opcode}) differs between the runs");
        }
    }

    [Fact]
    public async Task TwoRunsOfTheSameDamageCastOnTheManualClock_ProduceIdenticalDamageLogBytes()
    {
        (byte[] first, uint damageFirst) = await RunCastAsync();
        (byte[] second, uint damageSecond) = await RunCastAsync();

        Assert.NotEmpty(first);
        Assert.InRange(damageFirst, 11u, 60u); // BasePoints 10 plus a die of 1..50: the amount is a draw from SpellSystem.Random
        Assert.Equal(damageFirst, damageSecond);
        Assert.True(first.AsSpan().SequenceEqual(second), "SMSG_SPELLNONMELEEDAMAGELOG differs between two identical scripted casts");
    }

    [Fact]
    public async Task TwoRunsOfTheSameSwingOnTheManualClock_ProduceIdenticalAttackerStateBytes()
    {
        byte[] first = await RunSwingAsync();
        byte[] second = await RunSwingAsync();

        Assert.NotEmpty(first);
        Assert.True(first.AsSpan().SequenceEqual(second), "SMSG_ATTACKERSTATEUPDATE differs between two identical scripted swings");
    }

    private sealed class HostileHooks : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;
    }

    private static async Task<byte[]> RunSwingAsync()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual(startMs: 1000));
        await using WorldTestClient attacker = await host.EnterWorldAsync("swingone", "Swingone");
        await using WorldTestClient victim = await host.EnterWorldAsync("swingtwo", "Swingtwo", race: 2);
        uint window = await host.OnWorldAsync(() =>
        {
            host.World.GetMap(0).Combat.Hooks = new HostileHooks();
            // The same seeds in both runs: the hit table and the damage roll draw from the combat dice, the batch dither from the spell system's.
            host.World.GetMap(0).Combat.Random = new global::ArcaneCore.World.Tests.Playerbots.Risk.RiskTestWorld.SeededDice(7);
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            spells.Random = new Random(7);
            return spells.SpellBatchWindowMs;
        });
        await global::ArcaneCore.World.Tests.Spells.SpellTestServices.FlagForPvpAsync(host, "Swingtwo");
        ulong victimGuid = await host.PlayerStateAsync("Swingtwo", p => p.Guid.Value);

        await attacker.SendAsync(WorldOpcode.CmsgAttackswing, BitConverter.GetBytes(victimGuid));
        await host.StepAsync();
        // A swing is held to the spell batch boundary, which the manual clock only reaches when the test runs it there.
        await host.AdvanceAsync(window);
        return await victim.ReadUntilAsync(WorldOpcode.SmsgAttackerstateupdate);
    }

    private static async Task<(byte[] DamageLog, uint Damage)> RunCastAsync()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual(startMs: 1000));
        await using WorldTestClient caster = await host.EnterWorldAsync("castone", "Castone");
        await using WorldTestClient target = await host.EnterWorldAsync("casttwo", "Casttwo", race: 2);
        await global::ArcaneCore.World.Tests.Spells.SpellTestServices.FlagForPvpAsync(host, "Casttwo");
        ulong targetGuid = await host.PlayerStateAsync("Casttwo", p => p.Guid.Value);
        uint healthBefore = await host.PlayerStateAsync("Casttwo", p => p.Health);
        uint window = await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Random = new Random(7);
            feature.System.CombatRules = SpellCombatRules.Neutral; // deterministic hit and crit: the amount is the only seeded draw the test depends on
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 994011, Name = "Synthetic Random Damage", School = SpellSchool.Fire, RangeIndex = 4, Range = new SpellRange(0, 20),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, BasePoints = 10, BaseDice = 1, DieSides = 50,
                    TargetA = SpellImplicitTarget.UnitEnemy }],
            }], [], []);
            feature.Spellbook.LearnSpell(host.World.FindOnlinePlayer("Castone")!, 994011);
            return feature.System.SpellBatchWindowMs;
        });

        var writer = new PacketWriter(16);
        writer.WriteUInt32(994011);
        SpellCastTargets.ForUnit(new ObjectGuid(targetGuid)).Write(writer);
        await caster.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        await host.StepAsync();
        // With spell batching on the damage is held to the next batch boundary, which the manual clock only reaches when the test runs it there.
        await host.AdvanceAsync(window);
        byte[] log = await target.ReadUntilAsync(WorldOpcode.SmsgSpellnonmeleedamagelog);
        uint damage = healthBefore - await host.PlayerStateAsync("Casttwo", p => p.Health);
        return (log, damage);
    }

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> RunScriptedAsync()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual(startMs: 1000));
        WorldTestClient client = await host.EnterWorldAsync("repro", "Repro");
        await host.StepAsync(2);
        await client.SendChatAsync(ChatType.Say, Language.Common, "hello clock");
        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await host.StepAsync(3);
        await host.AdvanceAsync(120);
        byte[] chat = await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        byte[] played = await client.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
        return [(WorldOpcode.SmsgMessagechat, chat), (WorldOpcode.SmsgPlayedTime, played)];
    }

    [Fact]
    public async Task GameWait_OnARealClockHost_StillPollsWallTime()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        Assert.False(host.Clock.IsManual);
        int evaluations = 0;

        await GameWait.UntilAsync(host, () => ++evaluations >= 3, "three evaluations");
        await GameWait.UntilWorldAsync(host, () => evaluations >= 5 || ++evaluations >= 5, "five evaluations");

        Assert.True(evaluations >= 5);
        Assert.Equal(ArcaneCore.Game.Maps.WorldClockMode.Realtime, WorldClockModeOf(host));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync());
    }

    private static ArcaneCore.Game.Maps.WorldClockMode WorldClockModeOf(WorldTestHost host) => host.World.Clock.Mode;
}

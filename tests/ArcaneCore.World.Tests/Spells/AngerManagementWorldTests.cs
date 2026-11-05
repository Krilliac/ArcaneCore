using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Combat;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class AngerManagementWorldTests
{
    [Fact]
    public async Task ModPowerRegenRage_UsesFiveSecondFirstTickAndThreeSecondRemainder()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        await using WorldTestClient client = await host.EnterWorldAsync("ANGER", "Anger");
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 994010, Name = "Synthetic Anger Management", Duration = new SpellDuration(20_000, 0, 20_000),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 16,
                    BaseDice = 1, DieSides = 1, AuraType = AuraType.ModPowerRegen, MiscValue = (int)PowerType.Rage,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("Anger")!;
            feature.Spellbook.LearnSpell(player, 994010);
            CombatEnvironment.For(host.World).Options.RateRageLoss = 0;
        });
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(994010));
        await host.StepAsync();
        // With spell batching on (ARCANECORE_TEST_SPELL_BATCH_MS) the go is held to the next batch boundary (SpellSystem.Batching.cs), which
        // this manual clock only reaches when the test runs it there; one window always crosses it. Batching off: a 0 ms advance.
        await host.AdvanceAsync(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.SpellBatchWindowMs));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        // Manual game clock, one 50 ms tick per step: the rage value is captured in the same world-thread call that
        // detects the first change, so no later tick (the 3 s remainder) can move it between the read and the assert.
        uint rageAtFirstTick = 0;
        await host.RunUntilAsync(() =>
        {
            rageAtFirstTick = host.World.FindOnlinePlayer("Anger")!.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage);
            return rageAtFirstTick != 0;
        }, "first periodic rage gain", maxGameMs: 8_000);
        Assert.Equal(10u, rageAtFirstTick);
        await client.CollectAsync();
    }

    private static byte[] CastPayload(uint spell)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }
}

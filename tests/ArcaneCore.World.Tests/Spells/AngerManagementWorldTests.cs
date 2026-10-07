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
        await using WorldTestHost host = WorldTestHost.Start();
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
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Anger")!
            .GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage) >= 10, "first periodic rage gain");
        Assert.Equal(10u, await host.PlayerStateAsync("Anger", p => p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage)));
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

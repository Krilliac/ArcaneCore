using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class ManaRegenInterruptWorldTests
{
    [Fact]
    public async Task Aura134_AppliesThroughTheSocketCastAndExposesTheConfiguredPercentage()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MANAINT", "Manaint");
        uint manaAtAuraApplication = uint.MaxValue;
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 998106, Name = "Synthetic mana regen interruption", Duration = new SpellDuration(30_000, 0, 30_000),
                ManaCost = 20, PowerType = (int)PowerType.Mana,
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, BasePoints = 49, BaseDice = 1, DieSides = 1,
                    AuraType = AuraType.ModManaRegenInterrupt, TargetA = SpellImplicitTarget.UnitCaster,
                }, new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, BasePoints = 49, BaseDice = 1, DieSides = 1,
                    AuraType = AuraType.ModPowerRegenPercent, MiscValue = (int)PowerType.Mana, TargetA = SpellImplicitTarget.UnitCaster,
                }, new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, BasePoints = 24, BaseDice = 1, DieSides = 1,
                    AuraType = AuraType.ModPowerRegen, MiscValue = (int)PowerType.Mana, TargetA = SpellImplicitTarget.UnitCaster,
                }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("Manaint")!;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
            player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
            player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
            player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
            player.SetUInt32(UpdateFields.UnitFieldPower1, 30);
            // Suppress unrelated pre-cast spirit ticks through the public combat seam.
            player.Combat.NoteManaUsed();
            feature.System.HolderAdded += holder =>
            {
                if (holder.Spell.Id == 998106)
                    manaAtAuraApplication = holder.Target.GetUInt32(UpdateFields.UnitFieldPower1);
            };
            feature.Spellbook.LearnSpell(player, 998106);
        });

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(998106));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        Assert.Equal(10u, await host.OnWorldAsync(() => manaAtAuraApplication));
        Assert.InRange(await host.PlayerStateAsync("Manaint", player => player.Combat.LastManaUseTimer), 1u, 5000u);
        Assert.Equal(998106u, await host.PlayerStateAsync("Manaint", player => player.Combat.LastManaUseSpellId));
        await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(
            host.World.FindOnlinePlayer("Manaint")!, 998106), "mana interruption aura");
        Assert.Equal(50f, await host.PlayerStateAsync("Manaint", player =>
            Math.Min(100f, (float)new SpellSystemPowerAuras(host.WorldServices.GetRequiredService<SpellFeature>().System)
                .GetTotalAuraModifier(player, AuraType.ModManaRegenInterrupt))));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Manaint")!
            .GetUInt32(UpdateFields.UnitFieldPower1) >= 48, "interrupted mana tick");
        Assert.Equal(48u, await host.PlayerStateAsync("Manaint", player => player.GetUInt32(UpdateFields.UnitFieldPower1)));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Manaint")!
            .Combat.LastManaUseTimer == 0, "five-second mana interruption window");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Manaint")!;
            player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        });
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Manaint")!
            .GetUInt32(UpdateFields.UnitFieldPower1) >= 66, "normal mana tick");
        Assert.Equal(66u, await host.PlayerStateAsync("Manaint", player => player.GetUInt32(UpdateFields.UnitFieldPower1)));
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

using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class DruidFormPowerWorldTests
{
    private sealed class FixedRandom : Random
    {
        public override int Next(int minValue, int maxValue) => 100;
    }

    [Fact]
    public async Task CatFormSocketCast_UsesEnergyAndFurorThenRemovalRestoresMana()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(
            new ShapeshiftFormCatalog([
                new ShapeshiftFormInfo(DruidForms.Cat, 0, 0),
                new ShapeshiftFormInfo(DruidForms.Bear, 0, 0),
                new ShapeshiftFormInfo(DruidForms.DireBear, 0, 0)])));
        await using WorldTestClient client = await host.EnterWorldAsync("DRUIDFORM", "Druidform");
        uint catPowerAtApplication = uint.MaxValue;
        uint bearPowerAtApplication = uint.MaxValue;
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Random = new FixedRandom();
            feature.System.HolderAdded += holder =>
            {
                if (holder.Spell.Id == 998220) catPowerAtApplication = holder.Target.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy);
                if (holder.Spell.Id == 998222) bearPowerAtApplication = holder.Target.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage);
            };
            feature.System.Store = new SpellStore([.. feature.System.Store.All,
                Spell(998220, "Synthetic Cat Form", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = -1, BaseDice = 1, DieSides = 1, AuraType = AuraType.ModShapeshift, MiscValue = DruidForms.Cat, TargetA = SpellImplicitTarget.UnitCaster }),
                Spell(998222, "Synthetic Bear Form", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = -1, BaseDice = 1, DieSides = 1, AuraType = AuraType.ModShapeshift, MiscValue = DruidForms.Bear, TargetA = SpellImplicitTarget.UnitCaster }),
                Spell(998221, "Synthetic Furor", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 99, BaseDice = 1, DieSides = 1, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster }, icon: FurorRules.DummyIconId),
                Spell(FurorRules.CatEnergySpell, "Synthetic Cat Energy", new SpellEffectInfo { Effect = SpellEffectName.Energize, BasePoints = 39, BaseDice = 1, DieSides = 1, MiscValue = (int)PowerType.Energy }),
                Spell(FurorRules.BearRageSpell, "Synthetic Bear Rage", new SpellEffectInfo { Effect = SpellEffectName.Energize, BasePoints = 99, BaseDice = 1, DieSides = 1, MiscValue = (int)PowerType.Rage })], [], []);
            Player player = host.World.FindOnlinePlayer("Druidform")!;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Druid);
            player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
            feature.Spellbook.LearnSpell(player, 998220);
            feature.Spellbook.LearnSpell(player, 998222);
            feature.Spellbook.LearnSpell(player, 998221);
            feature.System.CastSpell(player, 998221, SpellCastTargets.ForSelf(), true);
        });

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(998220));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Druidform")!.PowerType == PowerType.Energy, "cat energy power");
        Assert.Equal(40u, await host.OnWorldAsync(() => catPowerAtApplication));
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(
            host.World.FindOnlinePlayer("Druidform")!, 998220));
        Assert.Equal(PowerType.Mana, await host.PlayerStateAsync("Druidform", p => p.PowerType));
        Assert.Equal(0u, await host.PlayerStateAsync("Druidform", p => p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage)));
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(998222));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Druidform")!.PowerType == PowerType.Rage, "bear rage power");
        Assert.Equal(100u, await host.OnWorldAsync(() => bearPowerAtApplication));
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(
            host.World.FindOnlinePlayer("Druidform")!, 998222));
        Assert.Equal(PowerType.Mana, await host.PlayerStateAsync("Druidform", p => p.PowerType));
        await client.CollectAsync();
    }

    private static SpellInfo Spell(uint id, string name, SpellEffectInfo effect, uint icon = 0) => new()
    {
        Id = id, Name = name, SpellIconId = icon, Duration = new SpellDuration(60_000, 0, 60_000),
        StartRecoveryCategory = 0, StartRecoveryTime = 0, Effects = [effect],
    };

    private static byte[] CastPayload(uint spell)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }
}

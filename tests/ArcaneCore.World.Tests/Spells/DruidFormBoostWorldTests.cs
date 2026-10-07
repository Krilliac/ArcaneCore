using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class DruidFormBoostWorldTests
{
    [Fact]
    public async Task CatFormSocketCast_AppliesLinkedAndCustomHeartAura_ThenExactRemoval()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(
            new ShapeshiftFormCatalog([new ShapeshiftFormInfo(DruidForms.Cat, 0, 0)])));
        await using WorldTestClient client = await host.EnterWorldAsync("DRUIDBOOST", "Druidboost");

        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All,
                Spell(998230, "Synthetic Cat Form", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, TargetA = SpellImplicitTarget.UnitCaster, AuraType = AuraType.ModShapeshift, MiscValue = DruidForms.Cat }),
                Spell(3025, "Synthetic Cat Boost", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, TargetA = SpellImplicitTarget.UnitCaster, AuraType = AuraType.Dummy }),
                Spell(990010, "Synthetic Heart Talent", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, TargetA = SpellImplicitTarget.UnitCaster, BasePoints = 24, AuraType = AuraType.ModTotalStatPercentage, MiscValue = FormBoostTable.HeartOfTheWildMiscValue }, FormBoostTable.HeartOfTheWildIconId),
                // 24900 is bound to Cat Form by its Stances in the DBC: HandleShapeshiftBoosts(false) removes it as a shape-lost aura.
                Spell(24900, "Synthetic Heart Effect", new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, TargetA = SpellImplicitTarget.UnitCaster, BasePoints = 1, AuraType = AuraType.ModTotalStatPercentage, MiscValue = FormBoostTable.HeartOfTheWildMiscValue }) with { Stances = 1u << (DruidForms.Cat - 1) }], [], []);
            Player player = host.World.FindOnlinePlayer("Druidboost")!;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Druid);
            player.SetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue, 10);
            feature.Spellbook.LearnSpell(player, 998230);
            feature.System.CastSpell(player, 990010, SpellCastTargets.ForSelf(), true);
        });

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(998230));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Druidboost")!.GetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue) == 15, "Heart of the Wild stat delta");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Druidboost")!;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            Assert.True(feature.System.HasAura(player, 3025));
            SpellAuraHolder heart = Assert.Single(feature.System.GetAuras(player), h => h.Spell.Id == 24900);
            Assert.Equal(25, heart.Auras[0]!.Amount);
            feature.System.RemoveAuras(player, 998230);
            Assert.False(feature.System.HasAura(player, 3025));
            Assert.Equal(12, player.GetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue));
        });
        await client.CollectAsync();
    }

    private static SpellInfo Spell(uint id, string name, SpellEffectInfo effect, uint icon = 0) => new()
    {
        Id = id, Name = name, SpellIconId = icon, Duration = new SpellDuration(60_000, 0, 60_000),
        StartRecoveryCategory = 0, StartRecoveryTime = 0, Effects = [effect with { BaseDice = 1, DieSides = 1 }],
    };

    private static byte[] CastPayload(uint spell)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }
}

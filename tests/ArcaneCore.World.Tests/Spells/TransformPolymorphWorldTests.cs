using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class TransformPolymorphWorldTests
{
    [Fact]
    public async Task MagePolymorph_RealSocketCastUsesFallbackDisplayAndCombatHealth()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("POLY", "Poly");
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 998010, Name = "Synthetic Polymorph", SpellFamilyName = 3,
                PreventionType = SpellConstants.PreventionTypeSilence,
                Duration = new SpellDuration(60_000, 0, 60_000),
                Effects = [
                    new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = -1,
                        BaseDice = 1, DieSides = 1, AuraType = AuraType.ModConfuse,
                        TargetA = SpellImplicitTarget.UnitCaster },
                    new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = -1,
                        BaseDice = 1, DieSides = 1, AuraType = AuraType.Transform, MiscValue = 999999,
                        TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("Poly")!;
            player.MaxHealth = 1000;
            player.Health = 100;
            player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
            player.Map!.Combat.SetInCombatState(player, 60_000);
            feature.Spellbook.LearnSpell(player, 998010);
        });

        var writer = new PacketWriter(16);
        writer.WriteUInt32(998010);
        SpellCastTargets.ForSelf().Write(writer);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Poly")!.DisplayId == 4, "transform fallback display");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Poly")!.Health >= 200, "polymorph health regeneration");
        Assert.True(await host.PlayerStateAsync("Poly", p => p.Health >= 200 && p.Combat.IsInCombat));
        await client.CollectAsync();
    }
}

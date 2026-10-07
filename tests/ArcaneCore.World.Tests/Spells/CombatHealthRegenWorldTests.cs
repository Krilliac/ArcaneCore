using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class CombatHealthRegenWorldTests
{
    [Fact]
    public async Task CastCombatRegenAura_AllowsHealthGainDuringCombat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("COMBATREGEN", "Combatregen");
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 995010, Name = "Synthetic Combat Regen", Duration = new SpellDuration(30_000, 0, 30_000),
                Effects = [new SpellEffectInfo
                { Effect = SpellEffectName.ApplyAura, BasePoints = 99, BaseDice = 1, DieSides = 1,
                    AuraType = AuraType.ModRegenDuringCombat,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("Combatregen")!;
            player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
            player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
            player.Health = 100;
            player.Map!.Combat.SetInCombatState(player, 60_000);
            feature.Spellbook.LearnSpell(player, 995010);
        });
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(995010));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        Assert.Equal(100, await host.PlayerStateAsync("Combatregen", player =>
            host.WorldServices.GetRequiredService<SpellFeature>().System.GetTotalAuraModifier(player, AuraType.ModRegenDuringCombat)));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Combatregen")!.Health > 100, "combat health regeneration");
        Assert.True(await host.PlayerStateAsync("Combatregen", player => player.Combat.IsInCombat));
    }

    private static byte[] CastPayload(uint spell)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }
}

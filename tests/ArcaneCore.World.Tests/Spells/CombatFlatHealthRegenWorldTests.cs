using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class CombatFlatHealthRegenWorldTests
{
    [Fact]
    public async Task Aura161_RealCastRegeneratesFlatHealthDuringCombat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("HEALTH_REGEN", "HealthRegen");
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 996010, Name = "Synthetic Combat Flat Health", Duration = new SpellDuration(60_000, 0, 60_000),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 4,
                    BaseDice = 1, DieSides = 1, AuraType = AuraType.ModHealthRegenInCombat,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("HealthRegen")!;
            player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
            player.Health = 100;
            player.Map!.Combat.SetInCombatState(player, 60_000);
            feature.Spellbook.LearnSpell(player, 996010);
            CombatEnvironment.For(host.World).Options.RateHealth = 1.0f;
        });

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(996010));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("HealthRegen")!.Health >= 102, "flat health regeneration");
        Assert.Equal((uint)5, await host.PlayerStateAsync("HealthRegen", p =>
            (uint)host.WorldServices.GetRequiredService<SpellFeature>().System.GetTotalAuraModifier(p, AuraType.ModHealthRegenInCombat)));
        Assert.True(await host.PlayerStateAsync("HealthRegen", p => p.Health >= 102 && p.Combat.IsInCombat));
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

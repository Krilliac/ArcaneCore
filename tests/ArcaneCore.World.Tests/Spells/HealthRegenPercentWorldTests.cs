using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class HealthRegenPercentWorldTests
{
    [Fact]
    public async Task Aura88_RealCastUsesOutOfCombatSpiritAndStopsInCombat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("HEALTH_PERCENT", "Hpercent");
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 997010, Name = "Synthetic Health Percent", Duration = new SpellDuration(60_000, 0, 60_000),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 49,
                    BaseDice = 1, DieSides = 1, AuraType = AuraType.ModHealthRegenPercent,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("Hpercent")!;
            player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
            player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
            player.Health = 100;
            feature.Spellbook.LearnSpell(player, 997010);
        });

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(997010));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Hpercent")!.Health >= 106, "out-of-combat percentage regeneration");
        Assert.Equal((uint)50, await host.PlayerStateAsync("Hpercent", p =>
            (uint)host.WorldServices.GetRequiredService<SpellFeature>().System.GetTotalAuraModifier(p, AuraType.ModHealthRegenPercent)));
        Assert.True(await host.PlayerStateAsync("Hpercent", p => p.Health >= 106));

        uint combatStart = 0;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Hpercent")!;
            player.Health = 100;
            player.Map!.Combat.SetInCombatState(player, 60_000);
            combatStart = host.World.NowMs;
        });
        await host.WaitForWorldAsync(() => unchecked(host.World.NowMs - combatStart) >= 2100, "combat regeneration tick");
        Assert.Equal((uint)100, await host.PlayerStateAsync("Hpercent", p => p.Health));
        Assert.True(await host.PlayerStateAsync("Hpercent", p => p.Combat.IsInCombat));
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

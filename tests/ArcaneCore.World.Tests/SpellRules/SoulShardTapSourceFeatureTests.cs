using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.SpellRules;

/// <summary>
/// The world installs a Soul Shard tap source, so a shard needs a victim the warlock tapped (vmangos Aura::HandleChannelDeathItem,
/// SpellAuras.cpp:2860-2861, via Creature::IsTappedBy, Creature.cpp:1615-1632).
/// </summary>
public sealed class SoulShardTapSourceFeatureTests
{
    [Fact]
    public async Task SoulShardFeature_InstallsTheLootTapSource()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SHARDPLAYER", "Shardplayer");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Shardplayer")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            Assert.IsType<ArcaneCore.Game.Loot.LootSoulShardTapSource>(SoulShardRules.TapSourceOf(spells.System));
        });
    }

    private const uint DrainSoul = 971_001;
    private const uint VictimEntry = 971_002;

    private enum Tap { None, Own, Other }

    /// <summary>
    /// Through the production wiring: the tap is made by real damage (DealDamage, which raises the loot tap), the warlock channels a
    /// channel-death-item aura on the creature, and the kill removes the aura. Returns the warlock's Soul Shards.
    /// </summary>
    private static async Task<uint> ShardsAsync(Tap tap, bool untappedCountsAsTapped)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SpellRules:SoulShardUntappedCountsAsTapped"] = untappedCountsAsTapped ? "true" : "false",
        }).Build();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(configuration));
        await using WorldTestClient warlockClient = await host.EnterWorldAsync("SHARDWARLOCK", "Shardwarlock");
        await using WorldTestClient otherClient = await host.EnterWorldAsync("SHARDOTHER", "Shardother");

        return await host.OnWorldAsync(() =>
        {
            Player warlock = host.World.FindOnlinePlayer("Shardwarlock")!;
            Player other = host.World.FindOnlinePlayer("Shardother")!;
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            warlock.Inventory.Templates = new ItemTemplateStore(
                [new ItemTemplate { Entry = SoulShardRules.SoulShard, Class = 12, Name = "Soul Shard", DisplayId = 1, Stackable = 20 }], []);
            spells.Store = new SpellStore(
            [
                .. spells.Store.All,
                new SpellInfo
                {
                    Id = DrainSoul, Name = "Test Drain Soul", SpellFamilyName = SoulShardRules.WarlockFamily, RangeIndex = 4,
                    Range = new SpellRange(0, 30), Duration = new SpellDuration(15_000, 0, 15_000), Attributes = SpellAttributes.AuraIsDebuff,
                    Effects = [new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ChannelDeathItem, BasePoints = 0, BaseDice = 1, DieSides = 1,
                        TargetA = SpellImplicitTarget.UnitEnemy, ItemType = SoulShardRules.SoulShard,
                    }],
                },
            ], [], []);

            var template = new CreatureTemplate
            {
                Entry = VictimEntry, Name = "Shard Victim", MinLevel = 5, MaxLevel = 5, DisplayIds = [903], Faction = 14,
                MinLevelHealth = 500, MaxLevelHealth = 500, AIName = "NullAI", CreatureType = 7,
            };
            Creature victim = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(warlock.Map!)
                .SpawnTemporary(template, warlock.X + 4, warlock.Y, warlock.Z, 0);
            MapCombat combat = warlock.Map!.Combat;
            switch (tap)
            {
                case Tap.Own:
                    combat.DealDamage(warlock, victim, 1);
                    break;
                case Tap.Other:
                    combat.DealDamage(other, victim, 1);
                    break;
            }

            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(warlock, DrainSoul, SpellCastTargets.ForUnit(victim.Guid), triggered: true));
            combat.Kill(warlock, victim);
            return warlock.Inventory.GetItemCount(SoulShardRules.SoulShard);
        });
    }

    [Fact]
    public async Task OwnTap_ThroughDealDamage_ThenDrainSoulAndTheKill_GivesAShard()
        => Assert.Equal(1u, await ShardsAsync(Tap.Own, untappedCountsAsTapped: false));

    [Fact]
    public async Task AnotherPlayersTap_GivesNoShard()
        => Assert.Equal(0u, await ShardsAsync(Tap.Other, untappedCountsAsTapped: false));

    [Fact]
    public async Task AnUntappedKill_GivesNoShardByDefault()
        => Assert.Equal(0u, await ShardsAsync(Tap.None, untappedCountsAsTapped: false));

    [Fact]
    public async Task AnUntappedKill_GivesAShard_WithTheOptionSetInConfiguration()
        => Assert.Equal(1u, await ShardsAsync(Tap.None, untappedCountsAsTapped: true));
}

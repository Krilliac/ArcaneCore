using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Stats;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Stats;

/// <summary>
/// The stat feature in the daemon (docs/areas/stats.md): imported player base data at login, the attached
/// system, and the configured refusal of an incomplete import. The numbers are hand-worked from the synthetic
/// rows below with the vmangos formulas.
/// </summary>
public sealed class StatsFeatureTests
{
    private const string Account = "STATS1";
    private const string Name = "Statsy";

    /// <summary>Synthetic human warrior rows: level 1 and 2, crit rate 4 → 20 and dodge rate 2 → 20 over levels 1-60.</summary>
    private static PlayerStatsContent Content() => new(
        [new ClassLevelStats(1, 1, 20, 0), new ClassLevelStats(1, 2, 29, 0)],
        [new LevelStats(1, 1, 1, 23, 20, 22, 20, 20), new LevelStats(1, 1, 2, 24, 21, 23, 20, 21)],
        Enumerable.Range(1, 59).Select(l => ((uint)l, (uint)(l * 100))),
        [new AgilityRateRow(1, 1, 4f), new AgilityRateRow(1, 60, 20f)],
        [new AgilityRateRow(1, 1, 2f), new AgilityRateRow(1, 60, 20f)]);

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = 39, Class = 4, SubClass = 1, Name = "Recruit's Pants", DisplayId = 9892, Quality = 1, InventoryType = 7, Armor = 2, MaxDurability = 25 },
        ]);
        items.Templates.StartingItems.AddRange([new StartingItem(1, 1, 25, 1), new StartingItem(1, 1, 39, 1)]);
        return items;
    }

    private static WorldTestHost Start(StatsTestContent? stats, ItemTestContent? items = null)
    {
        using (stats?.Use())
        using (items?.Use())
        {
            return WorldTestHost.Start();
        }
    }

    [Fact]
    public void StatsFeature_IsDiscovered_AndHooksCharacterLoading()
    {
        Assert.Contains(typeof(StatsFeature), WorldFeatures.FeatureTypes);
        Assert.True(typeof(ICharacterHooks).IsAssignableFrom(typeof(StatsFeature)));
    }

    [Fact]
    public async Task Login_AppliesTheImportedLevelStatsAndTheDerivedCombatValues()
    {
        var stats = new StatsTestContent(Content());
        await using WorldTestHost host = Start(stats, Items());
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);

        (uint[] baseStats, uint maxHealth, int attackPower, uint armor, float crit, float dodge, uint attackTime, float min, float max) =
            await host.PlayerStateAsync(Name, p => (
                Enumerable.Range(0, 5).Select(i => p.GetUInt32(UpdateFields.UnitFieldStat0 + i)).ToArray(),
                p.MaxHealth,
                p.GetInt32(UpdateFields.UnitFieldAttackPower),
                p.GetUInt32(UpdateFields.UnitFieldResistances),
                p.GetFloat(UpdateFields.PlayerCritPercentage),
                p.GetFloat(UpdateFields.PlayerDodgePercentage),
                p.GetUInt32(UpdateFields.UnitFieldBaseattacktime),
                p.GetFloat(UpdateFields.UnitFieldMindamage),
                p.GetFloat(UpdateFields.UnitFieldMaxdamage)));

        Assert.Equal([23u, 20u, 22u, 20u, 20u], baseStats);           // from the imported rows, not a file
        Assert.Equal(60u, maxHealth);                                  // 20 base + 40 from 22 stamina
        Assert.Equal(29, attackPower);                                 // 3 * 1 + 2 * 23 - 20
        Assert.Equal(42u, armor);                                      // 2 per agility (40) + the pants' 2
        Assert.Equal(5.0f, crit, 0.001f);                              // 20 agility / rate 4
        Assert.Equal(10.0f, dodge, 0.001f);                            // 20 agility / rate 2
        Assert.Equal(1900u, attackTime);                               // the worn sword's delay, not the constant 2000
        Assert.Equal(1f + (29f / 14f * 1.9f), min, 0.001f);            // sword 1-3 plus AP / 14 * 1.9
        Assert.Equal(3f + (29f / 14f * 1.9f), max, 0.001f);
        Assert.Equal(1, stats.Loads);
    }

    [Fact]
    public async Task EveryMapsCombatAsksTheSystemAboutTheOffhandAndShield()
    {
        var stats = new StatsTestContent(Content());
        await using WorldTestHost host = Start(stats, Items());
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);

        Assert.NotNull(await host.World.InvokeAsync(() => host.World.GetMap(0).Combat.Stats));
        Assert.True(await host.PlayerStateAsync(Name, p => p.StatState.Maintainer is not null));
        // No Parry ability yet: the system answers "cannot" instead of falling back to the hooks.
        Assert.False(await host.PlayerStateAsync(Name, p => p.Map!.Combat.Stats!.PlayerCanParry(p)));
    }

    [Fact]
    public async Task WithoutAContentStoreTheFeatureStillAttachesWithoutInventingData()
    {
        await using WorldTestHost host = Start(stats: null, Items());
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);

        (float crit, int attackPower, uint attackTime) = await host.PlayerStateAsync(Name, p => (
            p.GetFloat(UpdateFields.PlayerCritPercentage), p.GetInt32(UpdateFields.UnitFieldAttackPower), p.GetUInt32(UpdateFields.UnitFieldBaseattacktime)));
        Assert.Equal(0f, crit);                       // no rate data: the agility term is left out (warrior class base is 0)
        Assert.Equal(1900u, attackTime);              // the weapon part does not need data
        Assert.Equal(-17, attackPower);               // the formula runs on the stat fields as they are: 3 * 1 + 2 * 0 - 20
    }

    [Fact]
    public async Task RequireImportedData_RefusesAnIncompleteImport()
    {
        var empty = new StatsTestContent(PlayerStatsContent.Empty);
        StatsFeature feature = Feature(empty, required: true);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => feature.EnsureLoadedAsync());
        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
        Assert.Null(feature.System);                  // a failed load is not cached
        Assert.Equal(1, empty.Loads);

        // Level stats and XP but no rate tables: still refused.
        var noRates = new StatsTestContent(new PlayerStatsContent(
            [new ClassLevelStats(1, 1, 20, 0)], [new LevelStats(1, 1, 1, 1, 1, 1, 1, 1)], Enumerable.Range(1, 59).Select(l => ((uint)l, 100u)), [], []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Feature(noRates, required: true).EnsureLoadedAsync());
    }

    [Fact]
    public async Task RequireImportedData_AcceptsACompleteImport_AndTheDefaultAcceptsAnEmptyOne()
    {
        StatsFeature complete = Feature(new StatsTestContent(Content()), required: true);
        Assert.NotNull(await complete.EnsureLoadedAsync());
        Assert.Same(complete.System, await complete.EnsureLoadedAsync());

        StatsFeature lenient = Feature(new StatsTestContent(PlayerStatsContent.Empty), required: false);
        Assert.NotNull(await lenient.EnsureLoadedAsync());
    }

    private static StatsFeature Feature(StatsTestContent content, bool required)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Stats:RequireImportedData"] = required ? "true" : "false" }).Build());
        services.AddSingleton<IPlayerStatsContentStore>(content);
        ServiceProvider provider = services.BuildServiceProvider();
        return new StatsFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<StatsFeature>.Instance);
    }
}

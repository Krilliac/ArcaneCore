using ArcaneCore.Game;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// The features that read honor: the PvP_RANK condition (internal rank, mangos-classic / classic-db type 11) and the NPC services'
/// vendor gate. Both fall back to today's behaviour (undecidable condition, rank items refused) when honor is off or absent.
/// </summary>
public sealed class HonorConsumersTests
{
    private const uint ScoutToSergeantRanks = 1;   // internal ranks 6..9 (classic-db condition 3019)

    private sealed class RankConditions : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>([new(ScoutToSergeantRanks, (int)ConditionType.PvpRank, 6, 9, 0, 0, 0)]);
    }

    private static ServiceProvider Services(bool withHonor, bool honorEnabled = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["World:Honor:Enabled"] = honorEnabled ? "true" : "false" }).Build());
        services.AddSingleton<IConditionContentStore, RankConditions>();
        services.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConditionFeature>.Instance));
        services.AddSingleton(sp => new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance));
        if (withHonor)
        {
            services.AddSingleton(sp => new HonorFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance));
        }

        return services.BuildServiceProvider();
    }

    private static WorldRuntime NewWorld(IServiceProvider services)
        => new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);

    private static Player CreatePlayer()
    {
        var character = new CharacterRecord { Id = 1, AccountId = 1, Name = "Ranked", Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = 83.5f };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400);
        return new Player(character, appearance, new HonorNullSession());
    }

    private static void Rank(HonorFeature feature, Player player, float rankPoints)
        => feature.Service.Track(player, feature.Service.Create(player, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = rankPoints } }));

    [Theory]
    [InlineData(5500f, true)]    // internal rank 7
    [InlineData(2000f, true)]    // internal rank 6
    [InlineData(1000f, false)]   // internal rank 5
    [InlineData(0f, false)]
    [InlineData(70000f, false)]  // internal rank 18
    public async Task The_pvp_rank_condition_compares_the_internal_rank(float rankPoints, bool satisfied)
    {
        await using ServiceProvider services = Services(withHonor: true);
        using WorldRuntime world = NewWorld(services);
        services.GetRequiredService<HonorFeature>().Attach(world);
        services.GetRequiredService<ConditionFeature>().Attach(world);
        Player player = CreatePlayer();
        Rank(services.GetRequiredService<HonorFeature>(), player, rankPoints);

        Assert.Equal(satisfied, services.GetRequiredService<ConditionFeature>().IsSatisfied(ScoutToSergeantRanks, player, null));
    }

    [Fact]
    public async Task The_condition_stays_undecidable_without_honor_or_with_it_disabled()
    {
        foreach ((bool withHonor, bool enabled) in new[] { (false, true), (true, false) })
        {
            await using ServiceProvider services = Services(withHonor, enabled);
            using WorldRuntime world = NewWorld(services);
            services.GetService<HonorFeature>()?.Attach(world);
            ConditionFeature conditions = services.GetRequiredService<ConditionFeature>();
            conditions.Attach(world);

            Assert.False(conditions.IsSatisfied(ScoutToSergeantRanks, CreatePlayer(), null));
            Assert.Equal(1, conditions.Current.Summarize().UnavailableByType[(int)ConditionType.PvpRank]);
        }
    }

    [Fact]
    public async Task The_npc_services_get_the_honor_owner_unless_one_was_supplied_or_honor_is_off()
    {
        await using ServiceProvider on = Services(withHonor: true);
        NpcServicesFeature npcs = on.GetRequiredService<NpcServicesFeature>();
        Assert.Same(on.GetRequiredService<HonorFeature>().Service, npcs.Extend(new QuestNpcDependencies(), NpcStore.Empty).Honor);

        var other = new NoHonor();
        Assert.Same(other, npcs.Extend(new QuestNpcDependencies(Honor: other), NpcStore.Empty).Honor);

        await using ServiceProvider off = Services(withHonor: true, honorEnabled: false);
        Assert.Null(off.GetRequiredService<NpcServicesFeature>().Extend(new QuestNpcDependencies(), NpcStore.Empty).Honor);

        await using ServiceProvider absent = Services(withHonor: false);
        Assert.Null(absent.GetRequiredService<NpcServicesFeature>().Extend(new QuestNpcDependencies(), NpcStore.Empty).Honor);
    }

    [Fact]
    public void The_options_bind_from_configuration_with_retail_defaults_and_validation()
    {
        var settings = new HonorSettings();
        HonorOptions defaults = settings.ToOptions();
        Assert.Equal(new HonorOptions().Maintenance, defaults.Maintenance);
        Assert.Equal(3u, defaults.MaintenanceDay);
        Assert.Equal(HonorMaintenanceMode.Startup, defaults.MaintenanceMode); // vmangos flags the week and runs it after a restart (HonorMgr.cpp:617-633)

        IConfigurationSection section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Honor:RpDecay"] = "7",
            ["World:Honor:MaintenanceDay"] = "99",
            ["World:Honor:TimeZoneOffsetHours"] = "-40",
            ["World:Honor:RacialLeaderExcludedEntries:0"] = "15423",
            ["World:Honor:MaintenanceMode"] = "Startup",
            ["World:Honor:DishonorableKills"] = "false",
        }).Build().GetSection(HonorSettings.SectionName);
        section.Bind(settings);
        HonorOptions bound = settings.ToOptions();

        Assert.Equal(1f, bound.RpDecay);              // clamped to 0..1 like vmangos setConfigMinMax
        Assert.Equal(6u, bound.MaintenanceDay);       // 0..6
        Assert.Equal(-23, bound.TimeZoneOffsetHours);
        Assert.Equal([15423u], bound.RacialLeaderExcludedEntries);
        Assert.Equal(HonorMaintenanceMode.Startup, bound.MaintenanceMode);
        Assert.False(bound.DishonorableKills);
    }

    private sealed class NoHonor : IPlayerHonor
    {
        public byte CurrentRank(Player player) => 0;

        public byte HighestRank(Player player) => 0;

        public sbyte VisualRank(Player player) => 0;
    }
}

internal sealed class HonorNullSession : IPlayerSession
{
    public int AccountId => 1;

    public ArcaneCore.Kernel.Accounts.AccountSecurity Security => ArcaneCore.Kernel.Accounts.AccountSecurity.Player;

    public void Send(ArcaneCore.Protocol.WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
    }

    public void ProcessWorldPackets(Player player)
    {
    }

    public void Kick()
    {
    }

    public void OnLoggedOut()
    {
    }
}

using System.Reflection;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload config</c> (vmangos ServerCommands.cpp:1016 → World::LoadConfigSettings(true),
/// World.cpp:445): the configuration is re-read into a candidate; live options are applied on
/// the world thread, restart-only ones keep their value with the vmangos message
/// (World.cpp:3044-3055), and an unreadable or invalid file changes nothing.
/// </summary>
public sealed class ConfigReloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-reload-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntimeOptions _options = new() { TickIntervalMs = 5, AutosaveIntervalMs = 0, Motd = "original", ListenRangeSay = 25 };
    private readonly WorldRuntime _world;

    public ConfigReloadTests()
    {
        Directory.CreateDirectory(_directory);
        _world = new WorldRuntime(_options, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ReloadCoordinator Reloader(IConfiguration configuration, WorldOptions? live = null, SocialFeature? social = null, HotReloadOptions? policy = null,
        PlayerbotOptions? playerbots = null)
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(configuration);
        if (policy is not null)
        {
            services.AddSingleton(Options.Create(policy));
        }

        if (live is not null)
        {
            services.AddSingleton(Options.Create(live));
        }

        if (social is not null)
        {
            services.AddSingleton(social);
        }

        if (playerbots is not null)
        {
            services.AddSingleton(Options.Create(playerbots));
        }

        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services.BuildServiceProvider()));
        _world.Start();
        return coordinator;
    }

    private static ConfigurationManager FromFile(string path)
    {
        var manager = new ConfigurationManager();
        manager.AddJsonFile(path, optional: false, reloadOnChange: false);
        return manager;
    }

    [Fact]
    public async Task LiveOptions_AreAppliedInPlace_OnTheWorldThread()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "MaxCommandsPerTick": 12, "CommandTimeBudgetMs": 0, "Motd": "Reloaded", "ListenRangeSay": 40, "AllowTwoSideChat": true, "InstantLogoutSecurity": "GameMaster", "Maps": { "GridCleanUpDelayMs": 120000, "GridUnload": false, "GridActivationDistance": 50 } } }""");
        MapOptions maps = _options.Maps;
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Same(_options, _world.Options);
        Assert.Same(maps, _options.Maps);
        Assert.Equal(12, _options.MaxCommandsPerTick);
        Assert.Equal(0, _options.CommandTimeBudgetMs);
        Assert.Equal("Reloaded", _options.Motd);
        Assert.Equal(40f, _options.ListenRangeSay);
        Assert.True(_options.AllowTwoSideChat);
        Assert.Equal(AccountSecurity.GameMaster, _options.InstantLogoutSecurity);
        Assert.Equal(120000, _options.Maps.GridCleanUpDelayMs);
        Assert.False(_options.Maps.GridUnload);
        Assert.Equal(50f, _options.Maps.GridActivationDistance);
    }

    [Fact]
    public async Task ARestartOnlyOption_KeepsItsValue_AndSaysSo()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 77, "Motd": "Reloaded", "Maps": { "DataDirectory": "D:/other" } } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(5, _options.TickIntervalMs);
        Assert.Equal(string.Empty, _options.Maps.DataDirectory);
        Assert.Equal("Reloaded", _options.Motd);
        Assert.Contains("World:TickIntervalMs option can't be changed at reload, using current value (5).", result.Notes);
        Assert.Contains("World:Maps:DataDirectory option can't be changed at reload, using current value ().", result.Notes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidCommandLimitRejectsTheWholeReload(int limit)
    {
        string path = Write($$"""{ "World": { "Motd": "Rejected", "MaxCommandsPerTick": {{limit}} } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));
        int originalLimit = _options.MaxCommandsPerTick;

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Equal(originalLimit, _options.MaxCommandsPerTick);
        Assert.Equal("original", _options.Motd);
    }

    [Theory]
    [InlineData("PlayerThreshold", "-1")]
    [InlineData("PlayerThreshold", "10001")]
    [InlineData("ScalePerPlayer", "-0.5")]
    [InlineData("ScalePerPlayer", "11")]
    [InlineData("MinimumSeconds", "0")]
    [InlineData("MinimumSeconds", "86401")]
    [InlineData("MaxReduction", "1.5")]
    [InlineData("MaxReduction", "-0.1")]
    [InlineData("Radius", "4")]
    [InlineData("Radius", "501")]
    public async Task DynamicRespawn_OutOfRangeValue_RejectsTheWholeReload(string key, string value)
    {
        string path = Write($$"""{ "World": { "Motd": "Rejected", "Respawn": { "Dynamic": { "Enabled": true, "{{key}}": {{value}} } } } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Equal("original", _options.Motd);
        Assert.False(_options.Respawn.Dynamic.Enabled);
        Assert.Empty(_options.Respawn.Dynamic.Problems());
    }

    [Fact]
    public async Task DynamicRespawn_ValidValues_AreAppliedLive()
    {
        string path = Write("""{ "World": { "Respawn": { "Dynamic": { "Enabled": true, "PlayerThreshold": 3, "MinimumSeconds": 45, "Radius": 80, "CountBots": false } } } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.True(_options.Respawn.Dynamic.Active);
        Assert.Equal(3, _options.Respawn.Dynamic.PlayerThreshold);
        Assert.Equal(45u, _options.Respawn.Dynamic.MinimumSeconds);
        Assert.Equal(80f, _options.Respawn.Dynamic.Radius);
        Assert.False(_options.Respawn.Dynamic.CountBots);
    }

    [Fact]
    public async Task ARestartOnlyOption_ThatDidNotChange_IsNotMentioned()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.DoesNotContain(result.Notes, n => n.Contains("can't be changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AsyncNavigation_IsRestartOnly_AndKeepsStartupValue()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "AsyncNavigationEnabled": true } } }""");
        var playerbots = new PlayerbotOptions { AsyncNavigationEnabled = false };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        ReloadResult enabled = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, enabled.Status);
        Assert.False(playerbots.AsyncNavigationEnabled);
        Assert.Contains("World:Playerbots:AsyncNavigationEnabled option can't be changed at reload, using current value (False).", enabled.Notes);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "AsyncNavigationEnabled": false } } }""");
        ReloadResult disabled = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, disabled.Status);
        Assert.False(playerbots.AsyncNavigationEnabled);
        Assert.DoesNotContain(disabled.Notes, n => n.Contains("World:Playerbots:AsyncNavigationEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorldOptionsListener_CannotChange_AtReload()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Port": 9999, "BindAddress": "127.0.0.1" } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path), new WorldOptions { Port = 8085, BindAddress = "0.0.0.0" });

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Contains("World:Port option can't be changed at reload, using current value (8085).", result.Notes);
        Assert.Contains("World:BindAddress option can't be changed at reload, using current value (0.0.0.0).", result.Notes);
    }

    [Fact]
    public async Task ATruncatedFile_ChangesNothing()
    {
        string path = Write("""{ "World": { "Motd": "Good" } }""");
        ConfigurationManager configuration = FromFile(path);
        ReloadCoordinator coordinator = Reloader(configuration);
        File.WriteAllText(path, """{ "World": { "Motd": "Tru""");

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal("original", _options.Motd);
        Assert.Equal(25f, _options.ListenRangeSay);
        Assert.Equal(0, _options.AutosaveIntervalMs);
    }

    [Fact]
    public async Task AMissingFile_ChangesNothing()
    {
        string path = Write("""{ "World": { "Motd": "Good" } }""");
        ConfigurationManager configuration = FromFile(path);
        ReloadCoordinator coordinator = Reloader(configuration);
        File.Delete(path);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public async Task AValueOfTheWrongType_ChangesNothing()
    {
        string path = Write("""{ "World": { "Motd": "Changed", "ListenRangeSay": "far" } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal("original", _options.Motd);
        Assert.Equal(25f, _options.ListenRangeSay);
    }

    [Fact]
    public async Task NegativeIntervalsAndRanges_FallBackToTheirDefaults_AsVmangosSetConfigPosDoes()
    {
        // World.cpp:2949-2977: a negative value is logged, replaced by the default, and the reload goes on.
        string path = Write("""{ "World": { "Motd": "Changed", "AutosaveIntervalMs": -1, "ListenRangeYell": -5, "Maps": { "GridCleanUpDelayMs": -1 } } }""");
        _options.AutosaveIntervalMs = 0;
        _options.ListenRangeYell = 111;
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Changed", _options.Motd);
        Assert.Equal(new WorldRuntimeOptions().AutosaveIntervalMs, _options.AutosaveIntervalMs);
        Assert.Equal(new WorldRuntimeOptions().ListenRangeYell, _options.ListenRangeYell);
        Assert.Equal(new MapOptions().GridCleanUpDelayMs, _options.Maps.GridCleanUpDelayMs);
        Assert.Contains(result.Notes, n => n.Contains("World:ListenRangeYell (-5) can't be negative. Using", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NegativeIntervalsAndRanges_AreRejected_ByName_WhenTheOptionSaysReject()
    {
        string path = Write("""{ "World": { "Motd": "Changed", "AutosaveIntervalMs": -1, "ListenRangeYell": -5, "Maps": { "GridCleanUpDelayMs": -1 } } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path), policy: new HotReloadOptions { NegativeNumbers = InvalidNumberPolicy.Reject });

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("World:AutosaveIntervalMs", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("World:ListenRangeYell", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("World:Maps:GridCleanUpDelayMs", StringComparison.Ordinal));
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public async Task TheSocialRules_AreLive_AndMapToTheVmangosAllowTwoSideKeys()
    {
        // World:Social:* <-> vmangos AllowTwoSide.* (World.cpp:610-613, 618): Group = Interaction.Group,
        // Guild = Interaction.Guild, Channel = Interaction.Channel, AddFriend = AddFriend.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Social": { "AllowTwoSideGroup": true, "AllowTwoSideChannel": true } } }""");
        var social = new SocialFeature(new CharacterDirectory(), new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        ReloadCoordinator coordinator = Reloader(FromFile(path), social: social);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.True(social.Options.AllowTwoSideGroup);
        Assert.True(social.Options.AllowTwoSideChannel);
        Assert.False(social.Options.AllowTwoSideGuild);
        Assert.False(social.Options.AllowTwoSideAddFriend);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        await coordinator.ReloadAsync("config");

        Assert.False(social.Options.AllowTwoSideGroup);
        Assert.False(social.Options.AllowTwoSideChannel);
    }

    [Fact]
    public async Task PlayerbotMovementPackets_IsLive_OnTheRunningPlayerbotOptions()
    {
        // The running bots read World:Playerbots:MovementPackets from this options object at every movement packet
        // (PlayerbotMotion), so the change takes effect at the bots' next move.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "ThinkIntervalMs": 100, "MovementPackets": false } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.False(playerbots.MovementPackets);
        Assert.Equal(500, playerbots.ThinkIntervalMs); // the playerbot options outside the reload set are read at start

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        await coordinator.ReloadAsync("config");

        Assert.True(playerbots.MovementPackets);
    }

    [Fact]
    public async Task PlayerbotCaps_AreLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // ManagedPlayerbotFeature reads MaxBots at every start and MaxRegisteredBots at every create (the live stress test of
        // 2026-10-08 could not raise MaxBots without restarting the world its owner was playing on).
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "MaxBots": 400, "MaxRegisteredBots": 600 } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(400, playerbots.MaxBots);
        Assert.Equal(600, playerbots.MaxRegisteredBots);

        File.WriteAllText(path, $$"""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "MaxBots": {{PlayerbotOptions.MaxBotsCeiling + 1}} } } }""");
        ReloadResult refused = await coordinator.ReloadAsync("config");
        Assert.NotEqual(ReloadStatus.Applied, refused.Status);
        Assert.Equal(400, playerbots.MaxBots);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(8, playerbots.MaxBots);
        Assert.Equal(1000, playerbots.MaxRegisteredBots);
    }

    [Fact]
    public async Task PlayerbotSocialTrading_IsLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // The trade desk (a later P4 step) will read Trading:Enabled and the limits from this options object at every pass: switching off mid-trade will cancel the window.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Social": { "Trading": { "Enabled": false, "TradeTimeoutSeconds": 90, "Limits": { "MaxConcurrentTrades": 4, "DailyOutflowCopperServer": 5000 } } } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.False(playerbots.Social.Trading.Enabled);
        Assert.Equal(90, playerbots.Social.Trading.TradeTimeoutSeconds);
        Assert.Equal(4, playerbots.Social.Trading.Limits.MaxConcurrentTrades);
        Assert.Equal(5000, playerbots.Social.Trading.Limits.DailyOutflowCopperServer);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Social": { "Trading": { "Limits": { "MaxConcurrentTrades": 257 } } } } } }""");
        ReloadResult refused = await coordinator.ReloadAsync("config");
        Assert.NotEqual(ReloadStatus.Applied, refused.Status);
        Assert.Equal(4, playerbots.Social.Trading.Limits.MaxConcurrentTrades);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(playerbots.Social.Trading.Enabled);
        Assert.Equal(45, playerbots.Social.Trading.TradeTimeoutSeconds);
        Assert.Equal(16, playerbots.Social.Trading.Limits.MaxConcurrentTrades);
        Assert.Equal(2_000_000, playerbots.Social.Trading.Limits.DailyOutflowCopperServer);
    }

    [Fact]
    public async Task PlayerbotThinkLimits_AreLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // ManagedPlayerbotFeature reads SlowThinkLogMs and MaxThinkSearchNodes from this options object at every think.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "SlowThinkLogMs": 25, "MaxThinkSearchNodes": 65536 } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(25, playerbots.SlowThinkLogMs);
        Assert.Equal(65_536, playerbots.MaxThinkSearchNodes);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "MaxThinkSearchNodes": 1000 } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(65_536, playerbots.MaxThinkSearchNodes);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(50, playerbots.SlowThinkLogMs);
        Assert.Equal(3 * PlayerbotNavigation.MaxSearchNodes, playerbots.MaxThinkSearchNodes);
    }

    [Fact]
    public async Task PlayerbotRisk_IsLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // The brains and party AIs read World:Playerbots:Risk from this options object at every decision.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Risk": { "Enabled": false, "Tolerance": 2, "RetreatHealthPct": 20, "DangerMemorySeconds": 30, "PartyRetreatOnWipe": false } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotRiskOptions risk = playerbots.Risk;
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(risk, playerbots.Risk); // changed in place: the running bots hold this object
        Assert.False(risk.Enabled);
        Assert.Equal(2f, risk.Tolerance);
        Assert.Equal(20f, risk.RetreatHealthPct);
        Assert.Equal(30, risk.DangerMemorySeconds);
        Assert.False(risk.PartyRetreatOnWipe);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Risk": { "Tolerance": 9 } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(2f, risk.Tolerance);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(risk.Enabled);
        Assert.Equal(1f, risk.Tolerance);
        Assert.Equal(300, risk.DangerMemorySeconds);
    }

    [Fact]
    public async Task PlayerbotCombat_IsLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // The brains, party AIs and group AIs read World:Playerbots:Combat from this options object at every decision (PlayerbotKite).
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Combat": { "Kite": { "Enabled": false, "HealthPct": 30, "StepYards": 6, "MaxStepsPerFight": 3 }, "PetTank": { "Enabled": false, "OpenerHoldMs": 500, "MasterThreatPct": 100 } } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotKiteOptions kite = playerbots.Combat.Kite;
        PlayerbotPetTankOptions petTank = playerbots.Combat.PetTank;
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(kite, playerbots.Combat.Kite); // changed in place: the running bots hold these objects
        Assert.Same(petTank, playerbots.Combat.PetTank);
        Assert.False(kite.Enabled);
        Assert.Equal(30f, kite.HealthPct);
        Assert.Equal(6f, kite.StepYards);
        Assert.Equal(3, kite.MaxStepsPerFight);
        Assert.False(petTank.Enabled);
        Assert.Equal(500, petTank.OpenerHoldMs);
        Assert.Equal(100f, petTank.MasterThreatPct);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Combat": { "Kite": { "HealthPct": 1 } } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(30f, kite.HealthPct);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Combat": { "PetTank": { "MasterThreatPct": 130 } } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(100f, petTank.MasterThreatPct);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(kite.Enabled);
        Assert.Equal(50f, kite.HealthPct);
        Assert.Equal(10f, kite.StepYards);
        Assert.Equal(8, kite.MaxStepsPerFight);
        Assert.True(petTank.Enabled);
        Assert.Equal(1500, petTank.OpenerHoldMs);
        Assert.Equal(110f, petTank.MasterThreatPct);
    }

    [Fact]
    public async Task PlayerbotRivalry_IsLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // The rivalry lane and the party AIs read World:Playerbots:Social:Rivalry from this options object at every pass and every emote.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Social": { "Rivalry": { "Enabled": false, "AllowOptOut": false, "Emotes": { "RudeAtBots": true, "RangeYards": 10 }, "Safeguards": { "SafeAreas": "35,392" } }, "Memory": { "OrganisedPvpKills": true } } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotSocialRivalryOptions rivalry = playerbots.Social.Rivalry;
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(rivalry, playerbots.Social.Rivalry); // changed in place: the running lane holds this object
        Assert.False(rivalry.Enabled);
        Assert.False(rivalry.AllowOptOut);
        Assert.True(rivalry.Emotes.RudeAtBots);
        Assert.Equal(10, rivalry.Emotes.RangeYards);
        Assert.Equal("35,392", rivalry.Safeguards.SafeAreas);
        Assert.True(playerbots.Social.Memory.OrganisedPvpKills);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Social": { "Rivalry": { "Emotes": { "RangeYards": 99 } } } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(10, rivalry.Emotes.RangeYards);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(rivalry.Enabled);
        Assert.False(rivalry.Emotes.RudeAtBots);
        Assert.False(playerbots.Social.Memory.OrganisedPvpKills);
    }

    [Fact]
    public async Task PlayerbotChat_IsLive_ProvidersIncluded_AndABadProviderIsRefused()
    {
        // PlayerbotChat reads World:Playerbots:Chat from this options object at every line and every provider attempt.
        string path = Write("""
            { "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Chat": {
                "Enabled": false, "Channels": "Whisper, Say", "PerPlayerCooldownSeconds": 3, "MaxDailySpendUsd": 0.5,
                "Providers": [ { "Kind": "OpenAICompatible", "BaseUrl": "http://localhost:11434/v1", "Model": "qwen3:0.6b", "ApiKeyEnvironmentVariable": "" },
                               { "Kind": "Anthropic", "MaxRepliesPerHour": 30, "Headers": { "X-Title": "ArcaneCore" } } ] } } } }
            """);
        var playerbots = new PlayerbotOptions { Enabled = true };
        global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatOptions chat = playerbots.Chat;
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(chat, playerbots.Chat); // changed in place: the chat service holds this object
        Assert.False(chat.Enabled);
        Assert.Equal(global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatChannels.Whisper | global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatChannels.Say, chat.Channels);
        Assert.Equal(3, chat.PerPlayerCooldownSeconds);
        Assert.Equal(0.5, chat.MaxDailySpendUsd);
        Assert.Equal(2, chat.Providers.Length);
        Assert.Equal("qwen3:0.6b", chat.Providers[0].Model);
        Assert.Equal("", chat.Providers[0].ApiKeyEnvironmentVariable);
        Assert.Equal(30, chat.Providers[1].MaxRepliesPerHour);
        Assert.Equal("ArcaneCore", chat.Providers[1].Headers["X-Title"]);
        Assert.Equal(["OpenAICompatible", "Anthropic", "Builtin"], chat.EffectiveProviders().Select(p => p.Kind.ToString()));

        // The same file again changes nothing (the provider list compares by value).
        Assert.Contains("no changes", (await coordinator.ReloadAsync("config")).Message);

        // A key sent in clear text to another host is refused, and the running providers stay.
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Chat": { "Providers": [ { "Kind": "OpenAICompatible", "BaseUrl": "http://example.com/v1", "Model": "m", "ApiKeyEnvironmentVariable": "OPENAI_API_KEY" } ] } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(2, chat.Providers.Length);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(chat.Enabled);
        Assert.Empty(chat.Providers);
        Assert.Equal(8, chat.PerPlayerCooldownSeconds);
    }

    [Fact]
    public async Task PlayerbotEconomy_IsLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // PlayerbotEconomy reads World:Playerbots:Economy from this options object at every decision.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Economy": { "Enabled": false, "ListChancePerVisit": 80, "MaxListingsPerBot": 7, "BuyUpgradeBudgetFraction": 0.5, "VisitIntervalMinutes": 10 } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotEconomyOptions economy = playerbots.Economy;
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(economy, playerbots.Economy); // changed in place: the coordinator holds this object
        Assert.False(economy.Enabled);
        Assert.Equal(80, economy.ListChancePerVisit);
        Assert.Equal(7, economy.MaxListingsPerBot);
        Assert.Equal(0.5f, economy.BuyUpgradeBudgetFraction);
        Assert.Equal(10, economy.VisitIntervalMinutes);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Economy": { "ListChancePerVisit": 101 } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(80, economy.ListChancePerVisit);
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Economy": { "BuyUpgradeBudgetFraction": 1.5 } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(0.5f, economy.BuyUpgradeBudgetFraction);
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Economy": { "VisitIntervalMinutes": 0 } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(10, economy.VisitIntervalMinutes);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(economy.Enabled);
        Assert.Equal(50, economy.ListChancePerVisit);
        Assert.Equal(5, economy.MaxListingsPerBot);
        Assert.Equal(0.25f, economy.BuyUpgradeBudgetFraction);
        Assert.Equal(30, economy.VisitIntervalMinutes);
    }

    [Fact]
    public async Task PlayerbotGroups_AreLive_OnTheRunningPlayerbotOptions_AndAnOutOfRangeValueIsRefused()
    {
        // PlayerbotGroupCoordinator reads World:Playerbots:Groups from this options object at every decision.
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Groups": { "Enabled": false, "MaxGroups": 2, "LevelRange": 3, "MinTank": 0, "MinHealer": 2, "FormationTimeoutSeconds": 60, "RaidsEnabled": false, "InvitePlayers": true } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotGroupOptions groups = playerbots.Groups;
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(groups, playerbots.Groups); // changed in place: the coordinator holds this object
        Assert.False(groups.Enabled);
        Assert.Equal(2, groups.MaxGroups);
        Assert.Equal(3, groups.LevelRange);
        Assert.Equal(0, groups.MinTank);
        Assert.Equal(2, groups.MinHealer);
        Assert.Equal(60, groups.FormationTimeoutSeconds);
        Assert.False(groups.RaidsEnabled);
        Assert.True(groups.InvitePlayers);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Groups": { "FormationTimeoutSeconds": 5 } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(60, groups.FormationTimeoutSeconds);
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Groups": { "MinTank": 6 } } } }""");
        Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(0, groups.MinTank);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(groups.Enabled);
        Assert.Equal(16, groups.MaxGroups);
        Assert.Equal(5, groups.LevelRange);
        Assert.Equal(1, groups.MinTank);
        Assert.Equal(1, groups.MinHealer);
        Assert.Equal(300, groups.FormationTimeoutSeconds);
        Assert.True(groups.RaidsEnabled);
        Assert.True(groups.InvitePlayers);
    }

    [Fact]
    public async Task PlayerbotSocialLists_ARuleThatTiesTwoValuesTogether_IsEnforcedOnAReload()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Social": { "Lists": { "FriendAffinity": 40, "UnfriendAffinity": 10 } } } } }""");
        var playerbots = new PlayerbotOptions { Enabled = true };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(40, playerbots.Social.Lists.FriendAffinity);

        // Each value alone is in range; together UnfriendAffinity (50) is above FriendAffinity (40).
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Social": { "Lists": { "FriendAffinity": 40, "UnfriendAffinity": 50 } } } } }""");
        ReloadResult refused = await coordinator.ReloadAsync("config");
        Assert.NotEqual(ReloadStatus.Applied, refused.Status);
        Assert.Equal(10, playerbots.Social.Lists.UnfriendAffinity);
        Assert.Equal(40, playerbots.Social.Lists.FriendAffinity);
    }

    [Fact]
    public async Task PlayerbotSocialEvents_AreLive_AndABadExcludedEventsListIsRefusedKeepingTheOldOne()
    {
        // The festival board and the events lane read World:Playerbots:Social:Events from this object at every decision (P7a slice 1).
        string path = Write("""
            { "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Social": { "Events": {
                "Enabled": false, "Holidays": { "CelebrateChance": 25, "ExcludedEvents": "12, 24 25", "MaxPerServiceAnchor": 0 } } } } } }
            """);
        var playerbots = new PlayerbotOptions { Enabled = true };
        ReloadCoordinator coordinator = Reloader(FromFile(path), playerbots: playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);

        var events = playerbots.Social.Events;
        Assert.False(events.Enabled);
        Assert.Equal(25, events.Holidays.CelebrateChance);
        Assert.Equal("12, 24 25", events.Holidays.ExcludedEvents);
        Assert.Equal(0, events.Holidays.MaxPerServiceAnchor);
        Assert.Same(events, playerbots.Social.Events); // changed in place: what the board holds is what the reload wrote

        // A live string compares by value, so the same text again is no change, and a different one is applied.
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Social": { "Events": { "Holidays": { "ExcludedEvents": "12, 24 25" } } } } } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Social": { "Events": { "Holidays": { "ExcludedEvents": "2" } } } } } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal("2", events.Holidays.ExcludedEvents);
        Assert.True(events.Enabled); // a key removed from the file returns to its default
        Assert.Equal(60, events.Holidays.CelebrateChance);

        // Bad values: the reload is refused as a whole and the old values stay.
        foreach (string bad in new[]
        {
            "\"ExcludedEvents\": \"12, x\"", "\"ExcludedEvents\": \"0\"", "\"ExcludedEvents\": \"65536\"", "\"CelebrateChance\": 101",
            "\"SpotRadiusYards\": 19", "\"MaxPerTown\": 0", "\"EmotesPerListenerMinute\": 11", "\"TalkZoneGapSeconds\": 0",
            "\"EmoteMinSeconds\": 120, \"EmoteMaxSeconds\": 60",
        })
        {
            File.WriteAllText(path, $$"""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Social": { "Events": { "Holidays": { {{bad}} } } } } } }""");
            ReloadResult refused = await coordinator.ReloadAsync("config");
            Assert.NotEqual(ReloadStatus.Applied, refused.Status);
            Assert.Equal("2", events.Holidays.ExcludedEvents);
            Assert.Equal(60, events.Holidays.CelebrateChance);
            Assert.Equal(250, events.Holidays.SpotRadiusYards);
            Assert.Equal(24, events.Holidays.MaxPerTown);
            Assert.Equal((30, 90), (events.Holidays.EmoteMinSeconds, events.Holidays.EmoteMaxSeconds));
        }
    }

    [Fact]
    public async Task AKeyRemovedFromTheFile_ReturnsToItsDefault()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        await coordinator.ReloadAsync("config");

        Assert.Equal(new WorldRuntimeOptions().Motd, _options.Motd);
        Assert.Equal(new WorldRuntimeOptions().ListenRangeSay, _options.ListenRangeSay);
    }

    [Fact]
    public async Task AConfigurationThatCannotBeReRead_Fails_WithoutChangingAnything()
    {
        IConfiguration frozen = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["World:Motd"] = "x" }).Build();
        ReloadCoordinator coordinator = Reloader(frozen);

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("re-read", result.Message);
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public async Task ReloadingTwice_WithNoFileChange_ReportsNoChanges()
    {
        string path = Write("""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Motd": "original", "ListenRangeSay": 25 } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        ReloadResult result = await coordinator.ReloadAsync("config");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains("no changes", result.Message);
    }

    [Fact]
    public async Task ReloadAll_DoesNotReloadTheConfig()
    {
        string path = Write("""{ "World": { "Motd": "Changed" } }""");
        ReloadCoordinator coordinator = Reloader(FromFile(path));

        IReadOnlyList<ReloadResult> results = await coordinator.ReloadAllAsync();

        Assert.Empty(results);
        Assert.Equal("original", _options.Motd);
    }

    [Fact]
    public void EveryWorldOption_IsClassified_LiveOrRestartOnly()
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in typeof(WorldRuntimeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(MapOptions) && p.PropertyType != typeof(WorldRespawnOptions) && p.PropertyType != typeof(PerformanceLogOptions) && p.PropertyType != typeof(ParallelMapOptions))) // Perf is bound from the top-level PerformanceLog section (ops lane), not World:*
        {
            expected.Add($"World:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(ParallelMapOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:Parallel:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(MapOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:Maps:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(DynamicRespawnOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:Respawn:Dynamic:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(WorldOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(SocialOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{SocialOptions.SectionName}:{property.Name}");
        }

        // Of the playerbot options the caps, the movement transport and the whole risk, chat, groups and battlegrounds sections are in the reload set
        // (the rest are read once at start; the chat provider list is one key).
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MaxBots)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MaxRegisteredBots)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MaxBulkCommand)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MovementPackets)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.SlowThinkLogMs)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MaxThinkSearchNodes)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobs)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobsMaxPerBot)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobsMaxInFlight)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobsMaxInFlightNodes)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobSyncNodeThreshold)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobWorkerMaxMs)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.PathJobWaitMaxMs)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.SyncPathQueryMaxMs)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.AsyncNavigationEnabled)}");
        foreach (PropertyInfo property in typeof(PlayerbotRiskOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Risk)}:{property.Name}");
        }

        // Kiting and the pet holding aggro are live key by key (docs/areas/playbots-combat.md, Kiting).
        foreach (PropertyInfo property in typeof(PlayerbotKiteOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Combat)}:{nameof(PlayerbotCombatOptions.Kite)}:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(PlayerbotPetTankOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Combat)}:{nameof(PlayerbotCombatOptions.PetTank)}:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatOptions).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatSafetyOptions)
                && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotSpeechOptions)
                && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotConversationOptions)
                && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotVoiceOptions)
                // Chat:InProcess (the in-process model) is read when the model loads at startup: a change takes a restart (17d3a10e), so none of its keys is a reload key.
                && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatInProcessOptions)))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chat)}:{property.Name}");
        }

        // Bot speech, conversations and the bot voice are live key by key (docs/areas/playbots.md, Bot speech, Conversations, Bot voice).
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotSpeechOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chat)}:Speech:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotConversationOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chat)}:Conversation:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotVoiceOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chat)}:Voice:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(PlayerbotGroupOptions).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.PlayerbotRunOptions) && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.PlayerbotCooperationOptions)
                && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.PlayerbotWorldBossOptions)))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Groups)}:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.PlayerbotCooperationOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Groups)}:Cooperation:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialListsOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Lists:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Battlegrounds)}:{property.Name}");
        }

        // The dungeon-run section and its raid subsection are live key by key (docs/areas/playbots-groups.md, Dungeon and raid runs).
        string runsPath = $"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Groups)}:{nameof(PlayerbotGroupOptions.DungeonRuns)}";
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.PlayerbotRunOptions).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.PlayerbotRaidRunOptions)))
        {
            expected.Add($"{runsPath}:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.PlayerbotRaidRunOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{runsPath}:Raids:{property.Name}");
        }

        // World-boss raids are live key by key (docs/areas/playbots-groups.md, World-boss raids).
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.PlayerbotWorldBossOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Groups)}:{nameof(PlayerbotGroupOptions.WorldBosses)}:{property.Name}");
        }

        // Following and catch-up is live key by key (docs/areas/playbots.md, Following and catch-up).
        foreach (PropertyInfo property in typeof(PlayerbotFollowOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Follow)}:{property.Name}");
        }

        // The way home of a homed bot is live key by key (docs/areas/playbots.md, Home area).
        foreach (PropertyInfo property in typeof(PlayerbotHomeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Home)}:{property.Name}");
        }

        // Chests, nodes and rares weighed against their guards are live key by key (docs/areas/playbots-opportunities.md).
        foreach (PropertyInfo property in typeof(PlayerbotOpportunityOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Opportunities)}:{property.Name}");
        }

        // How bots share the mobs of one place is live key by key (docs/areas/playbots.md, Contention).
        foreach (PropertyInfo property in typeof(PlayerbotContentionOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Contention)}:{property.Name}");
        }

        // Autonomous flight-path use is live key by key (docs/areas/playbots.md, Flight paths).
        foreach (PropertyInfo property in typeof(PlayerbotTaxiOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Taxi)}:{property.Name}");
        }

        // Multi-leg travel is live key by key (docs/areas/playbots.md, Travel).
        foreach (PropertyInfo property in typeof(PlayerbotTravelOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Travel)}:{property.Name}");
        }

        // The population section is live key by key (docs/areas/playbots-population.md).
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Population.PlayerbotPopulationOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Population)}:{property.Name}");
        }

        // The bot social section (guilds, guild chat, greetings) is live key by key (docs/areas/playbots-social.md).
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialGuildOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Guilds:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialGuildChatOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:GuildChat:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmoteOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Emotes:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialMemoryOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Memory:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotWarModeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:WarMode:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialHelpingOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Helping:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialGratitudeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Gratitude:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialTradingOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType.IsValueType))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Trading:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialTradingDealsOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType.IsValueType))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Trading:Deals:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialTradingGiftsOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType.IsValueType))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Trading:Gifts:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialTradingLimitsOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType.IsValueType))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Trading:Limits:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialMoodOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Mood:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialInitiativeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Initiative:{property.Name}");
        }
        // Rivalry (P6a): the switches, the emote rules and the safeguards are live key by key (docs/areas/playbots-social.md, Rivalry).
        foreach (PropertyInfo property in typeof(PlayerbotSocialRivalryOptions).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(PlayerbotSocialRivalryEmoteOptions) && p.PropertyType != typeof(PlayerbotSocialRivalrySafeguardOptions)))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Rivalry:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(PlayerbotSocialRivalryEmoteOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Rivalry:Emotes:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(PlayerbotSocialRivalrySafeguardOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Rivalry:Safeguards:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEventsOptions).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotHolidayOptions)))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Events:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotHolidayOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:Social:Events:Holidays:{property.Name}");
        }

        // The social pass's per-tick time budget (a scalar of the Social section itself).
        expected.Add($"{PlayerbotOptions.SectionName}:Social:{nameof(global::ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialOptions.PassBudgetMs)}");

        // The bot economy section is live key by key (docs/areas/playbots-economy.md).
        foreach (PropertyInfo property in typeof(PlayerbotEconomyOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Economy)}:{property.Name}");
        }

        // The bot budget and mounts sections are live key by key (docs/areas/playbots-economy.md).
        foreach (PropertyInfo property in typeof(PlayerbotBudgetOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Budget)}:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(PlayerbotMountOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Mounts)}:{property.Name}");
        }

        // The chat safety section is live key by key (docs/areas/playbots.md, Safety and provider policies).
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatSafetyOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chat)}:Safety:{property.Name}");
        }

        // The chaos bot section is live key by key (docs/areas/playbots.md, Chaos bots).
        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chaos.PlayerbotChaosOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chaos)}:{property.Name}");
        }

        // Of the Locomotion section only the player speed rates are reload keys (the rest is read at start; docs/areas/rates.md).
        foreach (PropertyInfo property in typeof(ArcaneCore.Game.Locomotion.LocomotionOptions).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true } && p.Name.StartsWith("Player", StringComparison.Ordinal)))
        {
            expected.Add($"{ArcaneCore.Game.Locomotion.LocomotionOptions.SectionName}:{property.Name}");
        }

        var classified = new SortedSet<string>(WorldConfigKeys.All.Select(k => k.Path), StringComparer.Ordinal);

        Assert.Equal(expected, classified);
        Assert.Equal(classified.Count, WorldConfigKeys.All.Count);
    }

    [Fact]
    public void TheRestartOnlyKeys_AreTheOnesTheProcessCapturesAtStart()
    {
        string[] restartOnly = [.. WorldConfigKeys.All.Where(k => !k.Live).Select(k => k.Path).Order(StringComparer.Ordinal)];

        // TickIntervalMs: WorldRuntime.Run reads it once for the thread's sleep and SpellFeature.Attach
        // for its timer; Maps:DataDirectory: TerrainManager / CollisionServices load at attach (vmangos
        // DataDir, World.cpp:932-935); Port / BindAddress: the listener is bound at start (World.cpp:598);
        // MaxConnections / MaxConnectionsPerIp: the connection limiter is built at start (hardening lane);
        // TickTimer / TickLateToleranceMs: WorldRuntime.Run builds its waiter and scheduler once (perf-limits lane);
        // Playerbots:AsyncNavigationEnabled: the private navigation executor is created when the feature attaches.
        // Parallel:*: WorldRuntime builds the map threads and the stage gates once (docs/research/multithreading-plan-20261017.md).
        Assert.Equal(["World:BindAddress", "World:Maps:DataDirectory", "World:MaxConnections", "World:MaxConnectionsPerIp",
            "World:Parallel:CreatureSplinePass", "World:Parallel:GateAllStages", "World:Parallel:Gated", "World:Parallel:MapUpdateThreads", "World:Parallel:OwnerGuard",
            "World:Parallel:Ungated", "World:Playerbots:AsyncNavigationEnabled", "World:Port", "World:TickIntervalMs", "World:TickLateToleranceMs", "World:TickTimer"], restartOnly);
    }
}

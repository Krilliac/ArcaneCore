using System.Reflection;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Playerbots;
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

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "MaxBots": 1001 } } }""");
        ReloadResult refused = await coordinator.ReloadAsync("config");
        Assert.NotEqual(ReloadStatus.Applied, refused.Status);
        Assert.Equal(400, playerbots.MaxBots);

        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Equal(8, playerbots.MaxBots);
        Assert.Equal(1000, playerbots.MaxRegisteredBots);
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
        Assert.Equal(4, groups.MaxGroups);
        Assert.Equal(5, groups.LevelRange);
        Assert.Equal(1, groups.MinTank);
        Assert.Equal(1, groups.MinHealer);
        Assert.Equal(300, groups.FormationTimeoutSeconds);
        Assert.True(groups.RaidsEnabled);
        Assert.False(groups.InvitePlayers);
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
        foreach (PropertyInfo property in typeof(WorldRuntimeOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true } && p.PropertyType != typeof(MapOptions) && p.PropertyType != typeof(PerformanceLogOptions))) // Perf is bound from the top-level PerformanceLog section (ops lane), not World:*
        {
            expected.Add($"World:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(MapOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:Maps:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(WorldOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"World:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(SocialOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{SocialOptions.SectionName}:{property.Name}");
        }

        // Of the playerbot options the caps, the movement transport and the whole risk, chat and groups sections are in the reload set
        // (the rest are read once at start; the chat provider list is one key).
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MaxBots)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MaxRegisteredBots)}");
        expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.MovementPackets)}");
        foreach (PropertyInfo property in typeof(PlayerbotRiskOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Risk)}:{property.Name}");
        }

        foreach (PropertyInfo property in typeof(global::ArcaneCore.World.Playerbots.Chat.PlayerbotChatOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Chat)}:{property.Name}");
        }
        foreach (PropertyInfo property in typeof(PlayerbotGroupOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            expected.Add($"{PlayerbotOptions.SectionName}:{nameof(PlayerbotOptions.Groups)}:{property.Name}");
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
        // TickTimer / TickLateToleranceMs: WorldRuntime.Run builds its waiter and scheduler once (perf-limits lane).
        Assert.Equal(["World:BindAddress", "World:Maps:DataDirectory", "World:MaxConnections", "World:MaxConnectionsPerIp", "World:Port",
            "World:TickIntervalMs", "World:TickLateToleranceMs", "World:TickTimer"], restartOnly);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

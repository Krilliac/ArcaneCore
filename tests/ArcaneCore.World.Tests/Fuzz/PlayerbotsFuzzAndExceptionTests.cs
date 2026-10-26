using System.Numerics;
using System.Text;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Bots.Progression;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chaos;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Commands;
using ArcaneCore.World.Playerbots.Control;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// Exception coverage and deterministic seeded fuzz tests across <c>ArcaneCore.Playerbots</c>
/// and <c>ArcaneCore.World.Bots</c>.
/// </summary>
public sealed class PlayerbotsFuzzAndExceptionTests
{
    [Fact]
    public void WorldBots_Guards_ThrowExpectedExceptions()
    {
        var catalog = new InstanceEncounterCatalog();
        Assert.Throws<ArgumentNullException>(() => catalog.Replace(null!));
        catalog.Replace([]);
        Assert.True(catalog.IsLoaded);
        Assert.Empty(catalog.Current);

        Assert.Throws<ArgumentNullException>(() => new ManagedBot(null!, default, null!));

        var registry = new ManagedBotRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.TryAdd(null!));
        Assert.Throws<ArgumentNullException>(() => registry.TryRemove(null!));
        Assert.Throws<ArgumentNullException>(() => registry.UseSessionPolicy(null!));
        Assert.Throws<ArgumentNullException>(() => registry.UseCastRefusals(null!));
        Assert.Throws<ArgumentNullException>(() => registry.UseHangSink(null!));
        Assert.Throws<ArgumentNullException>(() => registry.UseRouteSource(null!));
    }

    [Fact]
    public void PlayerbotItemScore_NullArguments_ThrowArgumentNullException()
    {
        var template = new ItemTemplate { Entry = 100, Class = (uint)ItemClass.Weapon };
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.Score((Item)null!, PlayerbotStatWeights.Caster, null));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.Score((ItemTemplate)null!, PlayerbotStatWeights.Caster));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.Score(template, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.WeaponDps(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.StyleFactor(null!, PlayerbotStatWeights.Caster));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.StyleFactor(template, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.ChooseQuestReward(null!, [], [], PlayerbotStatWeights.Caster));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.IsUnique(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.HasEquippedEntry(null!, 100));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.UpgradeGain(null!, template, PlayerbotStatWeights.Caster));
        Assert.Throws<ArgumentNullException>(() => PlayerbotItemScore.WornScore(null!, 0, PlayerbotStatWeights.Caster, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotItemScore_And_Talents_SeededFuzz_RemainsFinite(int seed)
    {
        var rng = new Random(seed);
        PlayerbotStatWeights[] weightsList =
        [
            PlayerbotStatWeights.ShieldTank,
            PlayerbotStatWeights.TwoHandStrength,
            PlayerbotStatWeights.DualWieldStrength,
            PlayerbotStatWeights.DualWieldAgility,
            PlayerbotStatWeights.TwoHandShaman,
            PlayerbotStatWeights.Feral,
            PlayerbotStatWeights.Hunter,
            PlayerbotStatWeights.Caster,
            PlayerbotStatWeights.Healer,
        ];
        Class[] classes =
        [
            Class.Warrior,
            Class.Paladin,
            Class.Hunter,
            Class.Rogue,
            Class.Priest,
            Class.Shaman,
            Class.Mage,
            Class.Warlock,
            Class.Druid,
        ];

        for (var i = 0; i < 64; i++)
        {
            var weights = weightsList[rng.Next(weightsList.Length)];
            var statVal = weights.Stat((uint)rng.Next(0, 50));
            Assert.True(float.IsFinite(statVal));

            var template = new ItemTemplate
            {
                Entry = (uint)rng.Next(1, 50000),
                Class = (uint)rng.Next(0, 16),
                SubClass = (uint)rng.Next(0, 21),
                InventoryType = (uint)rng.Next(0, 29),
                ItemLevel = (uint)rng.Next(0, 120),
                Armor = rng.Next(0, 5000),
                Block = (uint)rng.Next(0, 500),
                Delay = (uint)rng.Next(0, 4000),
                Damages =
                [
                    new ItemDamage((float)(rng.NextDouble() * 200.0), (float)(rng.NextDouble() * 400.0), 0u),
                    new ItemDamage((float)(rng.NextDouble() * 50.0), (float)(rng.NextDouble() * 100.0), 1u),
                ],
                Stats =
                [
                    new ItemStat((uint)rng.Next(0, 45), rng.Next(-50, 100)),
                    new ItemStat((uint)rng.Next(0, 45), rng.Next(-50, 100)),
                ],
                MaxCount = (uint)rng.Next(0, 3),
                Flags = rng.Next(0, 2) == 0 ? 0u : 0x00080000u,
            };

            var score = PlayerbotItemScore.Score(template, weights);
            var dps = PlayerbotItemScore.WeaponDps(template);
            var style = PlayerbotItemScore.StyleFactor(template, weights);
            _ = PlayerbotItemScore.IsUnique(template);

            Assert.True(float.IsFinite(score));
            Assert.True(float.IsFinite(dps) && dps >= 0f);
            Assert.True(float.IsFinite(style) && style >= 0f);

            var cls = classes[rng.Next(classes.Length)];
            var build = PlayerbotTalentBuilds.Choose(cls, (uint)rng.Next(0, 10000));
            Assert.NotNull(build);
            Assert.NotEmpty(PlayerbotTalentBuilds.For(cls));
        }
    }

    [Fact]
    public void PlayerbotOptions_NullAndBoundsValidation_ThrowsExpectedExceptions()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => PlayerbotOptions.Bind(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotOptions.ApplyConfiguration(null!, emptyConfig));
        Assert.Throws<ArgumentNullException>(() => PlayerbotOptions.ApplyConfiguration(new PlayerbotOptions(), null!));

        var valid = new PlayerbotOptions();
        valid.Validate();

        static void AssertInvalid(Action<PlayerbotOptions> mutate)
        {
            var opts = new PlayerbotOptions();
            mutate(opts);
            Assert.Throws<InvalidOperationException>(() => opts.Validate());
        }

        AssertInvalid(o => o.MaxBots = -1);
        AssertInvalid(o => o.MaxBots = 5001);
        AssertInvalid(o => o.MaxRegisteredBots = -1);
        AssertInvalid(o => o.MaxRegisteredBots = 10001);
        AssertInvalid(o => o.MaxBulkCommand = 0);
        AssertInvalid(o => o.MaxBulkCommand = 5001);
        AssertInvalid(o => o.ThinkIntervalMs = 49);
        AssertInvalid(o => o.ThinkIntervalMs = 60001);
        AssertInvalid(o => o.MaxActionsPerTick = 0);
        AssertInvalid(o => o.MaxActionsPerTick = 513);
        AssertInvalid(o => o.ThinkBudgetMinMs = 0.4);
        AssertInvalid(o => o.ThinkBudgetMinMs = 201.0);
        AssertInvalid(o => o.ThinkBudgetMinMs = double.NaN);
        AssertInvalid(o => { o.ThinkBudgetMinMs = 10.0; o.ThinkBudgetMaxMs = 5.0; });
        AssertInvalid(o => o.ThinkBudgetMaxMs = 201.0);
        AssertInvalid(o => o.ThinkBudgetMaxMs = double.PositiveInfinity);
        AssertInvalid(o => o.ThinkBudgetTickShare = 0.04);
        AssertInvalid(o => o.ThinkBudgetTickShare = 0.96);
        AssertInvalid(o => o.CombatThinkIntervalMs = 49);
        AssertInvalid(o => o.CombatThinkIntervalMs = 60001);
        AssertInvalid(o => o.CombatLaneShare = 0.05);
        AssertInvalid(o => o.CombatLaneShare = 0.95);
        AssertInvalid(o => o.ThinkStarvedGapMs = -1);
        AssertInvalid(o => o.ThinkStarvedGapMs = 500);
        AssertInvalid(o => o.ThinkStarvedGapMs = 3600001);
        AssertInvalid(o => o.ChaseReplanYards = 0.4f);
        AssertInvalid(o => o.ChaseReplanYards = 101f);
        AssertInvalid(o => o.ChaseReplanYards = float.NaN);
        AssertInvalid(o => o.SlowThinkLogMs = -1);
        AssertInvalid(o => o.SlowThinkLogMs = 60001);
        AssertInvalid(o => o.MaxThinkSearchNodes = 500);
        AssertInvalid(o => o.MaxThinkSearchNodes = 10000001);
        AssertInvalid(o => o.MaxPathPoints = 0);
        AssertInvalid(o => o.MaxPathPoints = 4097);
        AssertInvalid(o => o.MaxRouteYards = 0f);
        AssertInvalid(o => o.MaxRouteYards = 100001f);
        AssertInvalid(o => o.MaxDeliveryYards = -1f);
        AssertInvalid(o => o.MaxDeliveryYards = 100001f);
        AssertInvalid(o => o.MoveSpeed = 0f);
        AssertInvalid(o => o.MoveSpeed = 101f);
        AssertInvalid(o => o.StallSeconds = 9);
        AssertInvalid(o => o.StallSeconds = 86401);
        AssertInvalid(o => o.CutRegionMaxPolygons = -1);
        AssertInvalid(o => o.CutRegionMaxPolygons = 1_000_001);
        AssertInvalid(o => o.ClientFidelity = (PlayerbotClientFidelity)99);
        AssertInvalid(o => o.AllowedMaps = null!);
        AssertInvalid(o => o.AllowedMaps = []);
        AssertInvalid(o => o.AllowedMaps = [0u, 0u]);
        AssertInvalid(o => o.FaultBackoffSeconds = 0);
        AssertInvalid(o => o.FaultBackoffSeconds = 3601);
        AssertInvalid(o => o.MaxFaults = 0);
        AssertInvalid(o => o.MaxFaults = 101);
        AssertInvalid(o => o.FaultWindowSeconds = 0);
        AssertInvalid(o => o.FaultWindowSeconds = 604801);
        AssertInvalid(o => o.Scenarios = null!);
        AssertInvalid(o => o.Scenarios.MaxDurationSeconds = 4);
        AssertInvalid(o => o.Scenarios.MaxDurationSeconds = 3601);
        AssertInvalid(o => o.Scenarios.StepTimeoutSeconds = 0);
        AssertInvalid(o => o.Scenarios.StepTimeoutSeconds = 601);
        AssertInvalid(o => o.Party = null!);
        AssertInvalid(o => o.Risk = null!);
        AssertInvalid(o => o.Combat = null!);
        AssertInvalid(o => o.Opportunities = null!);
        AssertInvalid(o => o.Chat = null!);
        AssertInvalid(o => o.Groups = null!);
        AssertInvalid(o => o.Follow = null!);
        AssertInvalid(o => o.Home = null!);
        AssertInvalid(o => o.Contention = null!);
        AssertInvalid(o => o.Taxi = null!);
        AssertInvalid(o => o.Travel = null!);
        AssertInvalid(o => o.Population = null!);
        AssertInvalid(o => o.Social = null!);
        AssertInvalid(o => o.Economy = null!);
        AssertInvalid(o => o.Budget = null!);
        AssertInvalid(o => o.Mounts = null!);
        AssertInvalid(o => o.Battlegrounds = null!);
        AssertInvalid(o => o.Chaos = null!);
        AssertInvalid(o => o.Policy = null!);
    }

    [Fact]
    public void PlayerbotOptions_CutRegionMaxPolygons_ValidationBoundaries()
    {
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { CutRegionMaxPolygons = -1 }.Validate());
        new PlayerbotOptions { CutRegionMaxPolygons = 0 }.Validate();
        new PlayerbotOptions { CutRegionMaxPolygons = 1_000_000 }.Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { CutRegionMaxPolygons = 1_000_001 }.Validate());
    }

    [Fact]
    public void SubOptions_Validators_CoreNavigationAndWorld_ThrowInvalidOperationException()
    {
        static void AssertSection<T>(Action<T> mutate, Action<T> validate) where T : new()
        {
            var opts = new T();
            mutate(opts);
            Assert.Throws<InvalidOperationException>(() => validate(opts));
        }

        AssertSection<PlayerbotContentionOptions>(o => o.HelpChance = -0.1f, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.HelpChance = 1.1f, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.HelpChance = float.NaN, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.ClaimPenaltyYards = -1f, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.ClaimPenaltyYards = 201f, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.SomeoneElsesMemorySeconds = 0, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.SomeoneElsesMemorySeconds = 601, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.ContentionSuspendMinutes = 0, o => o.Validate());
        AssertSection<PlayerbotContentionOptions>(o => o.ContentionSuspendMinutes = 121, o => o.Validate());

        AssertSection<PlayerbotHomeOptions>(o => o.ReturnMode = (PlayerbotHomeReturnMode)99, o => o.Validate());
        AssertSection<PlayerbotHomeOptions>(o => o.TeleportDistanceYards = 99f, o => o.Validate());
        AssertSection<PlayerbotHomeOptions>(o => o.TeleportDistanceYards = 20001f, o => o.Validate());
        AssertSection<PlayerbotHomeOptions>(o => o.TeleportDistanceYards = float.NaN, o => o.Validate());
        AssertSection<PlayerbotHomeOptions>(o => o.DangerLevelMargin = -1, o => o.Validate());
        AssertSection<PlayerbotHomeOptions>(o => o.DangerLevelMargin = 61, o => o.Validate());

        AssertSection<PlayerbotPartyOptions>(o => o.InvitePolicy = (PlayerbotInvitePolicy)99, o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.LootRoll = (PlayerbotLootRoll)99, o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.MasterTimeoutSeconds = 0, o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.MasterTimeoutSeconds = 3601, o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.Allowlist = null!, o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.Allowlist = ["bad123"], o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.Allowlist = ["TooLongCharacterName"], o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(o => o.Allowlist = ["Thrall", "thrall"], o => o.Validate());
        AssertSection<PlayerbotPartyOptions>(
            o => o.Allowlist = Enumerable.Range(0, 65).Select(i => "Ab" + (char)('a' + (i / 26)) + (char)('a' + (i % 26))).ToArray(),
            o => o.Validate());

        AssertSection<PlayerbotTaxiOptions>(o => o.MinRouteYards = 99f, o => o.Validate());
        AssertSection<PlayerbotTaxiOptions>(o => o.MinRouteYards = 20001f, o => o.Validate());
        AssertSection<PlayerbotTaxiOptions>(o => o.MaxWalkToMasterYards = 9f, o => o.Validate());
        AssertSection<PlayerbotTaxiOptions>(o => o.MaxWalkToMasterYards = 2001f, o => o.Validate());
        AssertSection<PlayerbotTaxiOptions>(o => o.BackoffMinutes = 0, o => o.Validate());
        AssertSection<PlayerbotTaxiOptions>(o => o.BackoffMinutes = 1441, o => o.Validate());

        AssertSection<PlayerbotTravelOptions>(o => o.MageServiceRadiusYards = 9f, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.MageServiceRadiusYards = 1001f, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.MageServiceFeeCopper = 100001u, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.MaxTravelSpendCopper = 10000001u, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.TransitBackoffSeconds = 9, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.TransitBackoffSeconds = 3601, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.TransitBackoffMaxSeconds = 9, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => o.TransitBackoffMaxSeconds = 86401, o => o.Validate());
        AssertSection<PlayerbotTravelOptions>(o => { o.TransitBackoffSeconds = 120; o.TransitBackoffMaxSeconds = 60; }, o => o.Validate());

        AssertSection<PlayerbotBattlegroundOptions>(o => o.QueueChancePerMinute = -0.1f, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.QueueChancePerMinute = 1.1f, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.MaxBotsPerBattleground = -1, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.MaxBotsPerBattleground = 81, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.MaxTravelYards = 99, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.MaxTravelYards = 20001, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.MaxQueueWaitMinutes = 0, o => o.Validate());
        AssertSection<PlayerbotBattlegroundOptions>(o => o.MaxQueueWaitMinutes = 241, o => o.Validate());

        AssertSection<PlayerbotBudgetOptions>(o => o.ClassLevelsAhead = -1, o => o.Validate());
        AssertSection<PlayerbotBudgetOptions>(o => o.ClassLevelsAhead = 11, o => o.Validate());
        AssertSection<PlayerbotBudgetOptions>(o => o.EssentialsAllowanceCopper = 100000001u, o => o.Validate());
        AssertSection<PlayerbotBudgetOptions>(o => o.RefreshSeconds = 0, o => o.Validate());
        AssertSection<PlayerbotBudgetOptions>(o => o.RefreshSeconds = 3601, o => o.Validate());
        AssertSection<PlayerbotBudgetOptions>(o => o.DiscretionaryFloorFraction = -0.1f, o => o.Validate());
        AssertSection<PlayerbotBudgetOptions>(o => o.DiscretionaryFloorFraction = 1.1f, o => o.Validate());

        AssertSection<PlayerbotMountOptions>(o => o.SaveLevelsAhead = -1, o => o.Validate());
        AssertSection<PlayerbotMountOptions>(o => o.SaveLevelsAhead = 21, o => o.Validate());
        AssertSection<PlayerbotMountOptions>(o => o.MountMinTravelYards = 0.5f, o => o.Validate());
        AssertSection<PlayerbotMountOptions>(o => o.MountMinTravelYards = 100001f, o => o.Validate());

        AssertSection<PlayerbotChaosOptions>(o => o.ActiveChaosBots = -1, o => o.Validate());
        AssertSection<PlayerbotChaosOptions>(o => o.ActiveChaosBots = 5001, o => o.Validate());
        AssertSection<PlayerbotChaosOptions>(o => o.ProbesPerThink = 0, o => o.Validate());
        AssertSection<PlayerbotChaosOptions>(o => o.ProbesPerThink = 17, o => o.Validate());
    }

    [Fact]
    public void SubOptions_Validators_ChatAndVoice_ThrowExpectedExceptions()
    {
        static void AssertChat(Action<PlayerbotChatOptions> mutate)
        {
            var opts = new PlayerbotChatOptions();
            mutate(opts);
            Assert.Throws<InvalidOperationException>(() => opts.Validate());
        }

        AssertChat(o => o.Providers = null!);
        AssertChat(o => o.Providers = Enumerable.Range(0, 9).Select(_ => new PlayerbotChatProviderOptions()).ToArray());
        AssertChat(o => o.Providers = [null!]);
        AssertChat(o => o.Providers =
        [
            new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Builtin },
            new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic },
        ]);
        AssertChat(o => o.Channels = (PlayerbotChatChannels)1024);
        AssertChat(o => o.PerPlayerCooldownSeconds = -1);
        AssertChat(o => o.PerPlayerCooldownSeconds = 3601);
        AssertChat(o => o.MaxDailySpendUsd = -1);
        AssertChat(o => o.MaxDailySpendUsd = 10001);
        AssertChat(o => o.MaxDailySpendUsd = double.NaN);
        AssertChat(o => o.MemoryExchanges = -1);
        AssertChat(o => o.MemoryExchanges = 17);
        AssertChat(o => o.TimeoutSeconds = 0);
        AssertChat(o => o.TimeoutSeconds = 61);
        AssertChat(o => o.MaxQueuedRequests = 0);
        AssertChat(o => o.MaxQueuedRequests = 257);
        AssertChat(o => o.Safety = null!);
        AssertChat(o => o.InProcess = null!);
        AssertChat(o => o.Speech = null!);
        AssertChat(o => o.Conversation = null!);
        AssertChat(o => o.Voice = null!);

        Assert.NotNull(new PlayerbotChatProviderOptions { Kind = (PlayerbotChatProviderKind)99 }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions { MaxRepliesPerHour = 0 }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, MaxTokens = 10 }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.OpenAICompatible }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.OpenAICompatible,
            BaseUrl = "https://api.example.com/v1",
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.OpenAICompatible,
            BaseUrl = "http://api.example.com/v1",
            Model = "gpt",
            ApiKeyEnvironmentVariable = "TEST_KEY",
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.OpenAICompatible,
            BaseUrl = "https://api.example.com/v1",
            Model = "gpt",
            TokenLimitParameter = "bad_param",
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.Anthropic,
            InputUsdPerMillionTokens = -1,
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.Anthropic,
            OutputUsdPerMillionTokens = 1001,
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.Anthropic,
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" },
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.Anthropic,
            Moderation = (PlayerbotChatModerationKind)99,
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.InProcess,
            BaseUrl = "http://localhost:8080",
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.InProcess,
            ApiKeyEnvironmentVariable = "SOME_KEY",
        }.Problem());
        Assert.NotNull(new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.InProcess,
            Moderation = PlayerbotChatModerationKind.Endpoint,
        }.Problem());

        Assert.Throws<ArgumentNullException>(() => new PlayerbotChatInProcessOptions().Validate(null!));
        AssertChat(o => o.InProcess.ModelPath = null!);
        AssertChat(o => o.InProcess.ModelPath = "bad\0path");
        AssertChat(o => o.InProcess.Threads = 0);
        AssertChat(o => o.InProcess.MaxConcurrent = 0);
        AssertChat(o => o.InProcess.MaxQueued = -1);
        AssertChat(o => o.InProcess.BudgetMs = 100);
        AssertChat(o => o.InProcess.MaxNewTokens = 4);
        AssertChat(o => o.InProcess.ContextTokens = 128);
        AssertChat(o => o.InProcess.Temperature = -0.1);
        AssertChat(o => o.InProcess.TopK = 0);
        AssertChat(o => o.InProcess.TopP = 0.0);
        AssertChat(o =>
        {
            o.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.InProcess }];
            o.InProcess.ModelPath = "";
        });
        AssertChat(o =>
        {
            o.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.InProcess }];
            o.InProcess.ModelPath = "model.gguf";
            o.TimeoutSeconds = 1;
            o.InProcess.BudgetMs = 2000;
        });

        AssertChat(o => o.Safety.Categories = (PlayerbotChatSafetyCategories)512);
        AssertChat(o => o.Safety.TermsFile = null!);
        AssertChat(o => o.Safety.TermsFile = "bad\0file");
        AssertChat(o => o.Safety.OnFlagged = (PlayerbotChatFlaggedAction)99);
        AssertChat(o => o.Safety.OnOutputFlagged = (PlayerbotChatOutputAction)99);
        AssertChat(o => o.Safety.StrikesBeforeCutoff = 0);
        AssertChat(o => o.Safety.StrikeWindowMinutes = 0);
        AssertChat(o => o.Safety.CutoffMinutes = 0);
        AssertChat(o => o.Safety.AutoMuteMinutes = 0);
        AssertChat(o => o.Safety.FlagLogSize = -1);
        AssertChat(o => o.Safety.DisclosureText = "");
        AssertChat(o => o.Safety.DisclosureText = "has|pipe");

        AssertChat(o => o.Conversation.ConversationsPerHour = -1);
        AssertChat(o => o.Conversation.MaxActive = 0);
        AssertChat(o => o.Conversation.MaxLines = 1);
        AssertChat(o => o.Conversation.PairCooldownMinutes = 0);
        AssertChat(o => o.Conversation.BotCooldownMinutes = 0);
        AssertChat(o => o.Conversation.LineGapSeconds = 1);
        AssertChat(o => o.Conversation.LineJitterSeconds = -1);
        AssertChat(o => o.Conversation.LineDeadlineSeconds = 5);
        AssertChat(o => o.Conversation.CandidatesPerPass = 0);
        AssertChat(o => o.Conversation.NearYards = 4);
        AssertChat(o => o.Conversation.WorldReactionsPerMinute = -1);
        AssertChat(o => o.Conversation.ReactionsPerLine = 0);
        AssertChat(o => o.Conversation.ReactionPlayerCooldownMinutes = 0);
        AssertChat(o => o.Conversation.MaxHeardQueue = 4);

        AssertChat(o => o.Speech.WorldChannels = (PlayerbotWorldChannels)999);
        AssertChat(o => o.Speech.BotGapSeconds = -1);
        AssertChat(o => o.Speech.BotLinesPerMinute = 0);
        AssertChat(o => o.Speech.TargetGapSeconds = -1);
        AssertChat(o => o.Speech.ChannelLinesPerMinute = -1);
        AssertChat(o => o.Speech.ChannelCrowdBots = 0);
        AssertChat(o => o.Speech.LocalLinesPerMinute = -1);
        AssertChat(o => o.Speech.GroupLinesPerMinute = -1);
        AssertChat(o => o.Speech.GuildLinesPerMinute = -1);
        AssertChat(o => o.Speech.DedupeSeconds = -1);
        AssertChat(o => o.Speech.ChannelChangesPerPass = 0);
        AssertChat(o => o.Speech.BotToBotMaxTurns = -1);
        AssertChat(o => o.Speech.MaxQueuedLines = 4);

        AssertChat(o => o.Voice.BotsPerPass = 0);
        AssertChat(o => o.Voice.LinesPerPass = 0);
        AssertChat(o => o.Voice.LookingForGroupRepeatSeconds = 10);
        AssertChat(o => o.Voice.TradeRepeatSeconds = 10);
        AssertChat(o => o.Voice.PriceMultiplier = 0);
        AssertChat(o => o.Voice.MinSellCopper = -1);
        AssertChat(o => o.Voice.GeneralSharePercent = -1);
        AssertChat(o => o.Voice.BattlegroundCallSeconds = 1);
        AssertChat(o => o.Voice.QuestHelpRepeatSeconds = 10);
        AssertChat(o => o.Voice.PostLifeSeconds = 10);
    }

    [Fact]
    public void SubOptions_Validators_EconomyGroupsPolicyPopulationSocialQa_ThrowExpectedExceptions()
    {
        static void AssertInvalid<T>(Action<T> mutate, Action<T> validate) where T : new()
        {
            var opts = new T();
            mutate(opts);
            Assert.Throws<InvalidOperationException>(() => validate(opts));
        }

        AssertInvalid<PlayerbotEconomyOptions>(o => o.ListChancePerVisit = -1, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.ListChancePerVisit = 101, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.MaxListingsPerBot = -1, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.MaxListingsPerBot = 100, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.BuyUpgradeBudgetFraction = -0.1f, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.BuyUpgradeBudgetFraction = 1.1f, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.BuyUpgradeBudgetFraction = float.NaN, o => o.Validate());
        AssertInvalid<PlayerbotEconomyOptions>(o => o.VisitIntervalMinutes = 0, o => o.Validate());

        AssertInvalid<PlayerbotGroupOptions>(o => o.MaxGroups = -1, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.MaxGroups = 65, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.LevelRange = -1, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.LevelRange = 61, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.MinTank = -1, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.MinHealer = 6, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.FormationTimeoutSeconds = 10, o => o.Validate());
        AssertInvalid<PlayerbotGroupOptions>(o => o.DungeonRuns = null!, o => o.Validate());

        AssertInvalid<PlayerbotRunOptions>(o => o.StartChancePerMinute = -0.1, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.StartChancePerMinute = 1.1, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.MaxConcurrentRuns = -1, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.MaxWipes = 0, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.MaxRunMinutes = 5, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.TravelTimeoutMinutes = 1, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.BandBelowBoss = -1, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.BandAboveBoss = 25, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.MaxTravelYards = 100f, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.LevelBandOverrides = null!, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.LevelBandOverrides = new Dictionary<string, string> { ["bad"] = "10-20" }, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.LevelBandOverrides = new Dictionary<string, string> { ["36"] = "bad" }, o => o.Validate());
        AssertInvalid<PlayerbotRunOptions>(o => o.Raids = null!, o => o.Validate());

        AssertInvalid<PlayerbotRaidRunOptions>(o => o.MaxConcurrentRaids = -1, o => o.Validate());
        AssertInvalid<PlayerbotRaidRunOptions>(o => o.MaxWipes = 0, o => o.Validate());
        AssertInvalid<PlayerbotRaidRunOptions>(o => o.MaxRunMinutes = 5, o => o.Validate());
        AssertInvalid<PlayerbotRaidRunOptions>(o => o.TanksPerTen = 0, o => o.Validate());
        AssertInvalid<PlayerbotRaidRunOptions>(o => o.HealerPercent = 0, o => o.Validate());

        AssertInvalid<PlayerbotOpportunityOptions>(o => o.ScanRadiusYards = 4f, o => o.Validate());
        AssertInvalid<PlayerbotOpportunityOptions>(o => o.RiskAppetite = 0.1f, o => o.Validate());
        AssertInvalid<PlayerbotOpportunityOptions>(o => o.MinValue = -0.1f, o => o.Validate());
        AssertInvalid<PlayerbotOpportunityOptions>(o => o.MaxGuards = -1, o => o.Validate());
        AssertInvalid<PlayerbotOpportunityOptions>(o => o.MaxDeathProbability = 0f, o => o.Validate());
        AssertInvalid<PlayerbotOpportunityOptions>(o => o.PersonalityVariance = -0.1f, o => o.Validate());

        AssertInvalid<PlayerbotFollowOptions>(o => o.CatchUpMode = (PlayerbotCatchUpMode)99, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.TeleportDistanceYards = 5f, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.MaxPathRatio = 0.5f, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.MinDetourYards = -1f, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.DangerLevelMargin = -1, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.LeaderCombatRangeYards = -1f, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.NoProgressSeconds = 0, o => o.Validate());
        AssertInvalid<PlayerbotFollowOptions>(o => o.MaxFallHealthPercent = -1, o => o.Validate());

        AssertInvalid<PlayerbotPolicyOptions>(o => o.ModelPath = null!, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.ModelPath = "bad\0path", o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.IntervalMs = 100, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.StaleAfterMs = 100, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(
            o => { o.Enabled = true; o.IntervalMs = 5000; o.StaleAfterMs = 3000; },
            o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.TickBudgetMs = 0.01, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.ExplorationRate = -0.1, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.AbPolicyShare = 1.5, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(o => o.Telemetry = null!, o => o.Validate());
        AssertInvalid<PlayerbotPolicyOptions>(
            o => { o.Enabled = false; o.ExplorationRate = 0.05; },
            o => o.Validate());
        AssertInvalid<PlayerbotPolicyTelemetryOptions>(o => o.Directory = "", o => o.Validate());
        AssertInvalid<PlayerbotPolicyTelemetryOptions>(o => o.OutcomeMinutes = 0, o => o.Validate());
        AssertInvalid<PlayerbotPolicyTelemetryOptions>(o => o.MaxFileBytes = 100, o => o.Validate());
        AssertInvalid<PlayerbotPolicyTelemetryOptions>(o => o.MaxFiles = 0, o => o.Validate());

        AssertInvalid<PlayerbotPopulationOptions>(o => o.TargetOnline = -1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.IntervalSeconds = 0, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.TickP95BudgetMs = 1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.ResumeBelowPct = 1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.WindowTicks = 1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.ConfirmWindows = 0, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.MinRunSeconds = -1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.ActionLogSeconds = -1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.MaxCreatePerMinute = -1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.MaxStartsPerInterval = 0, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.MaxParksPerInterval = 0, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Factions = (PopulationFactions)99, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.AllianceSharePct = -1, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Classes = null!, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Classes = [], o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Classes = [99u], o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Bands = null!, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Bands = [], o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(
            o => o.Bands = [new PopulationBand { MinLevel = 20, MaxLevel = 10, Weight = 1 }],
            o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.ExcludedZones = null!, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.Relocation = (PopulationRelocation)99, o => o.Validate());
        AssertInvalid<PlayerbotPopulationOptions>(o => o.RushBrakeP95Ms = 1, o => o.Validate());

        AssertInvalid<PlayerbotRiskOptions>(o => o.Tolerance = 0.1f, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.Tolerance = 5.0f, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.Tolerance = float.NaN, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.RetreatHealthPct = 1f, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.NearlyWonHealthPct = -1f, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.RecoverHealthPct = 5f, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.DangerMemorySeconds = -1, o => o.Validate());
        AssertInvalid<PlayerbotRiskOptions>(o => o.DeathLoopMemorySeconds = -1, o => o.Validate());

        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.HealthPct = 1f, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.HealthPct = 96f, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.HealthPct = float.NaN, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.StepYards = 3f, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.StepYards = 21f, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.MaxStepsPerFight = 0, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite.MaxStepsPerFight = 33, o => o.Validate());
        AssertInvalid<PlayerbotCombatOptions>(o => o.Kite = null!, o => o.Validate());

        AssertInvalid<PlayerbotSocialOptions>(o => o.Guilds = null!, o => o.Validate());
        AssertInvalid<PlayerbotSocialOptions>(o => o.GuildChat = null!, o => o.Validate());
        AssertInvalid<PlayerbotSocialOptions>(o => o.Emotes = null!, o => o.Validate());
        AssertInvalid<PlayerbotSocialGuildOptions>(o => o.MaxBotGuilds = -1, o => o.Validate());
        AssertInvalid<PlayerbotSocialGuildOptions>(o => o.TargetSize = 1, o => o.Validate());
        AssertInvalid<PlayerbotSocialGuildOptions>(o => o.InvitePolicy = (PlayerbotInvitePolicy)99, o => o.Validate());
        AssertInvalid<PlayerbotSocialGuildOptions>(o => o.Creation = (PlayerbotGuildCreation)99, o => o.Validate());
        AssertInvalid<PlayerbotSocialGuildChatOptions>(o => o.MessagesPerHour = -1, o => o.Validate());

        var emptyCfg = new ConfigurationBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => PlayerbotQaOptions.ApplyConfiguration(null!, emptyCfg));
        Assert.Throws<ArgumentNullException>(() => PlayerbotQaOptions.ApplyConfiguration(new PlayerbotQaOptions(), null!));
        Assert.Empty(new PlayerbotQaOptions().Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { Directory = "" }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { ExportIntervalSeconds = 1 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { MaxFindings = 1 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { StaleHours = -1 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { AutoPruneHours = -1 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { TickHangWarmupMinutes = -1 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { MaxSamples = 0 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { SignalQueueCapacity = 10 }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions { GitHub = null! }.Validate());
        Assert.NotEmpty(new PlayerbotQaOptions
        {
            GitHub = new PlayerbotQaGitHubOptions { Enabled = true, Repository = "invalid-repo-format" },
        }.Validate());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotChatCommands_And_ControlParsers_SeededFuzz(int seed)
    {
        var rng = new Random(seed);
        string[] samples =
        [
            "",
            "   ",
            "status",
            "bot status",
            "!follow",
            "/stay",
            "guard",
            "free",
            "grind",
            "quest",
            "attack",
            "passive",
            "defend",
            "flee",
            "stats",
            "questlog",
            "who",
            "spells",
            "talents",
            "bags",
            "gear",
            "equip 19019",
            "unequip 16",
            "use 6948",
            "sell 1234",
            "sell junk",
            "buy 159 5",
            "repair",
            "train",
            "accept all",
            "turnin 42",
            "abandon 42",
            "cast 133",
            "buff",
            "drink",
            "mount",
            "dismount",
            "home",
            "set home",
            "leave",
            "role tank",
            "role heal",
            "role dps",
            "role auto",
            "loot need",
            "loot greed",
            "loot pass",
            "rtm skull",
            "focus 3",
            "cc moon",
            "pull",
            "come",
            "hold",
            "disperse",
            "help",
            "where are you?",
            "what level are you?",
        ];

        foreach (var text in samples)
        {
            if (PlayerbotChatCommands.TryParse(text, out var cmd))
            {
                _ = PlayerbotChatCommands.IsGmInspectCommand(cmd);
            }
            _ = PlayerbotChatCommands.IsHelpQuestion(text);
            _ = PlayerbotChatCommands.IsDebugQuestion(text);
            var line = new PlayerbotChatLine(ChatType.Whisper, Language.Common, new ObjectGuid(10ul), text);
            _ = PlayerbotChatCommands.Classify(line, new ObjectGuid(20ul), new ObjectGuid(10ul), false);
        }

        Assert.NotEmpty(PlayerbotChatCommands.HelpFor(false));
        Assert.NotEmpty(PlayerbotChatCommands.HelpFor(true));

        var buffer = new byte[128];
        for (var i = 0; i < 64; i++)
        {
            var len = rng.Next(0, buffer.Length + 1);
            var payload = buffer[..len];
            rng.NextBytes(payload);

            _ = PlayerbotChatCommands.TryRead(payload, out _);
            _ = PlayerbotChatCommands.TryReadTextEmote(payload, out _);

            var opcode = (WorldOpcode)rng.Next(0, 0x400);
            _ = BotControlEvents.IsNoise(opcode);
            _ = BotControlEvents.Describe(opcode, payload, (ulong)rng.NextInt64());

            var randomStr = Encoding.UTF8.GetString(payload);
            _ = PlayerbotChatCommands.TryParse(randomStr, out _);
            _ = BotControlDispatch.RestAfter(randomStr, rng.Next(0, 4));
        }

        Assert.Equal("follow", BotControlDispatch.RestAfter("bot   follow", 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotChatPrompts_Safety_Speech_Voice_SeededFuzz(int seed)
    {
        var rng = new Random(seed);

        _ = PlayerbotChatPrompts.ReadAction("ACTION: follow\nREPLY: On my way!");
        _ = PlayerbotChatPrompts.Action("follow");
        _ = PlayerbotChatPrompts.Intent("follow");
        _ = PlayerbotChatPrompts.ReadAnswer("REPLY: Yes!\nCOMMAND: follow");
        _ = PlayerbotChatPrompts.ReadClassification("SAFE");
        _ = PlayerbotChatPrompts.Classifier("Hello there!");
        Assert.True(PlayerbotChatPrompts.Addresses("Hey Arthas how are you?", "Arthas"));
        Assert.True(PlayerbotChatPrompts.IsOpenQuestion("Anyone for Deadmines?"));

        Assert.Throws<ArgumentNullException>(() => PlayerbotVoiceFacts.DungeonName(1, null, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotVoiceFacts.MissingRoles(null!, (1, 1)));
        Assert.Throws<ArgumentNullException>(() => PlayerbotVoiceFacts.Offer(null!, 1, false, false, 0));
        Assert.Throws<ArgumentNullException>(() => PlayerbotVoiceFacts.Tradeable(null!));

        var limiter = new PlayerbotSpeechLimiter();
        var speechOpts = new PlayerbotSpeechOptions();

        for (var i = 0; i < 64; i++)
        {
            var raw = $"Test message {i} email@example.com 555-123-4567 |cffff0000red|r";
            var screen = PlayerbotChatSafetyFilter.Default.Screen(raw, PlayerbotChatSafetyCategories.All);
            _ = PlayerbotChatSafetyFilter.Normalize(raw);
            Assert.True(PlayerbotChatSafetyFilter.Default.HasPersonalData(raw, out _));
            var stripped = PlayerbotChatSafetyFilter.Default.StripPersonalData(raw);
            Assert.DoesNotContain("email@example.com", stripped, StringComparison.Ordinal);

            var sanitized = PlayerbotChatPrompts.Sanitize(raw);
            var cleaned = PlayerbotChatPrompts.Clean(raw, rng.Next(5, 80));
            Assert.NotNull(sanitized);
            Assert.NotNull(cleaned);
            _ = screen.Flagged;

            var scope = SpeechScope.Channel("zone:12", speechOpts.ChannelLinesPerMinute, rng.Next(1, 20), rng.Next(0, 10));
            _ = scope.Cap;
            var speaker = new ObjectGuid((ulong)(i + 1));
            var listener = new ObjectGuid((ulong)(i + 100));
            _ = limiter.NextBotTurn(speaker, listener, speechOpts, i * 1000L);

            _ = PlayerbotVoiceFacts.Price(rng.Next(0, 1000000));
            _ = PlayerbotVoiceFacts.AskingPrice((uint)rng.Next(0, 50000), (uint)rng.Next(0, 5), rng.Next(1, 5));
            _ = PlayerbotVoiceFacts.RoleWords(rng.Next(0, 3), rng.Next(0, 3));
            _ = PlayerbotVoiceFacts.CarrierWhere(
                new Vector3((float)rng.NextDouble() * 2000f, (float)rng.NextDouble() * 2000f, 0f),
                Vector3.Zero,
                new Vector3(1000f, 1000f, 0f));
            _ = PlayerbotVoiceFacts.AbCall((byte)rng.Next(0, 5), (byte)rng.Next(0, 5), Team.Alliance, i % 2 == 0);
            _ = PlayerbotVoiceFacts.AvCall(rng.Next(0, 3), rng.Next(0, 3), rng.Next(0, 3), rng.Next(0, 3), i % 2 == 0, i % 2);
            _ = PlayerbotVoiceFacts.AsksInvite("inv pls for group");
            _ = PlayerbotVoiceFacts.AsksAbout("where are you heading?", "where");
            _ = PlayerbotVoiceFacts.IsDing("ding 20!");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotHazards_DangerMemory_Route_Navigation_SeededFuzz(int seed)
    {
        var rng = new Random(seed);

        Assert.Throws<ArgumentException>(() => new PlayerbotRoute(
            [Vector3.Zero, Vector3.One],
            10f,
            [true, false]));

        var validRoute = new PlayerbotRoute([Vector3.Zero, new Vector3(10f, 0f, 0f)], 10f, [true]);
        Assert.True(validRoute.LegsNavigated(1, 1));
        var chained = validRoute.Then(Vector3.Zero, new PlayerbotRoute([new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f)], 10f, [false]));
        Assert.False(chained.Navigated);

        Assert.Throws<ArgumentNullException>(() => new PlayerbotNavigationExecutor((Func<IPathfinder>)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerbotNavigationExecutor(() => null!, 0));
        using var cts = new CancellationTokenSource();
        using var job = new PlayerbotNavigationJob(cts, _ => { });
        Assert.Throws<ArgumentNullException>(() => job.Fail(null!));

        using var navExec = new PlayerbotNavigationExecutor(() => new StraightLinePathfinder(), 4);
        Assert.Throws<ArgumentNullException>(() => navExec.TrySubmit(null!, out _));
        Assert.Throws<ArgumentException>(() => navExec.TrySubmit(
            new PlayerbotNavigationRequest(0u, Vector3.Zero, Vector3.One, null!),
            out _));

        var hazards = new PlayerbotHazards();
        var dangerMem = new PlayerbotDangerMemory();
        Span<PlayerbotGap> gapBuffer = stackalloc PlayerbotGap[8];

        for (var i = 0; i < 48; i++)
        {
            var nowMs = (uint)(1000 + i * 500);
            var center = new Vector3(
                (float)(rng.NextDouble() * 200.0 - 100.0),
                (float)(rng.NextDouble() * 200.0 - 100.0),
                0f);
            var radius = (float)(rng.NextDouble() * 25.0 + 5.0);
            var creatureGuid = new ObjectGuid((ulong)(i + 1));

            hazards.Add(0u, center, radius, nowMs, 300, "test", creatureGuid);
            hazards.BeginStrongScan();
            hazards.SetStrong(0u, center, radius, nowMs, 60_000u, creatureGuid);
            hazards.EndStrongScan();
            _ = hazards.RememberDeathLoop(0u, center, nowMs, 300, [100u]);
            _ = hazards.CoversDeathLoop(0u, center, nowMs);

            var start = new Vector3(-120f, (float)(rng.NextDouble() * 40.0 - 20.0), 0f);
            var goal = new Vector3(120f, (float)(rng.NextDouble() * 40.0 - 20.0), 0f);
            IReadOnlyList<Vector3> routePts = [start, center, goal];
            _ = hazards.FirstOnRoute(0u, routePts, nowMs);
            _ = hazards.TryFindGap(0u, start, nowMs, 0ul, out _, out _);
            _ = hazards.FindGaps(0u, start, nowMs, 0ul, gapBuffer);
            var exposure = hazards.RouteExposure(0u, routePts, nowMs);
            Assert.True(float.IsFinite(exposure) && exposure >= 0f);

            dangerMem.Remember(0u, center, [(creatureGuid, 100u)], nowMs, 600);
            _ = dangerMem.IsRemembered(creatureGuid, nowMs);
            _ = dangerMem.RememberedBefore(creatureGuid, nowMs - 100u, nowMs);
            _ = dangerMem.LostAlone(100u, nowMs);
            Assert.True(dangerMem.Hits(0u, center, 100u, nowMs) >= 0);
        }

        Assert.NotEmpty(hazards.Active(0u, 25_000u));
        dangerMem.Clear();
        hazards.ClearStrong();
        hazards.Clear();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotFleetPlacement_And_Selection_GuardsAndSeededFuzz(int seed)
    {
        Assert.Throws<ArgumentNullException>(() => PlayerbotFleetPlacement.Ring(Vector3.Zero, 0f, 5, null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => PlayerbotFleetPlacement.Ring(Vector3.Zero, 0f, 5, (_, _, z) => z, null!));

        var dummyReq = new FleetRequest(FleetSelectorKind.All, "all", Guid.Empty, null, false, null, "all", "");
        Assert.Throws<ArgumentNullException>(() => PlayerbotFleetSelection.Match(null!, []));
        Assert.Throws<ArgumentNullException>(() => PlayerbotFleetSelection.Match(dummyReq, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotFleetSelection.Order(null!));

        var emptyRing = PlayerbotFleetPlacement.Ring(Vector3.Zero, 0f, 0, (_, _, z) => z, _ => true);
        Assert.Empty(emptyRing);

        var rng = new Random(seed);
        for (var i = 0; i < 32; i++)
        {
            var count = rng.Next(1, 25);
            var anchor = new Vector3(10f, 20f, 5f);
            var slots = PlayerbotFleetPlacement.Ring(
                anchor,
                (float)(rng.NextDouble() * Math.PI * 2.0),
                count,
                (x, y, z) => z + 0.5f,
                pt => Vector3.Distance(anchor, pt) < 50f);
            Assert.Equal(count, slots.Count);
        }

        string[] tokens = ["all", "near", "party", "Thr*", "Thrall", "all 5 --force", ""];
        foreach (var token in tokens)
        {
            _ = PlayerbotFleetSelection.TryParse(token, FleetVerb.Summon, out _);
            _ = PlayerbotFleetSelection.IsMulti(token);
            _ = PlayerbotFleetSelection.NeedsForce(10, 5, false, 3);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotBattlegrounds_GuardsAndSeededFuzz(int seed)
    {
        Assert.Throws<ArgumentNullException>(() => PlayerbotWarsongTactics.Plan(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotArathiBasinTactics.Plan(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotAlteracValleyTactics.Decide(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotBattlegroundRoles.Assign(BattlegroundType.WarsongGulch, null!));

        var rng = new Random(seed);
        for (var i = 0; i < 32; i++)
        {
            var team = i % 2 == 0 ? Team.Alliance : Team.Horde;
            var botGuid = new ObjectGuid((ulong)(i + 1));
            var selfPos = new Vector3((float)rng.NextDouble() * 1500f, (float)rng.NextDouble() * 1500f, 10f);

            var wsgPic = new PlayerbotWsgPicture
            {
                Team = team,
                Bots = [new PlayerbotWsgMember(botGuid, selfPos, true, i % 3 == 0)],
                Enemies = [new PlayerbotWsgEnemy(new ObjectGuid((ulong)(i + 100)), selfPos + new Vector3(20f, 0f, 0f))],
                OwnFlag = (WsgFlagState)(i % 4),
                EnemyFlag = (WsgFlagState)((i + 1) % 4),
                OwnRoom = Vector3.Zero,
                EnemyStand = new Vector3(500f, 500f, 0f),
                Midfield = new Vector3(250f, 250f, 0f),
            };
            var wsgOrders = PlayerbotWarsongTactics.Plan(wsgPic);
            Assert.NotNull(wsgOrders);

            var abPic = new PlayerbotAbPicture
            {
                Team = team,
                Nodes = [new PlayerbotAbNode(0, new Vector3(100f, 100f, 0f), (byte)(i % 5))],
                Bots = [new PlayerbotAbMember(botGuid, selfPos, true, (float)rng.NextDouble(), i % 2 == 0)],
                Enemies = [],
                Home = Vector3.Zero,
            };
            var abOrders = PlayerbotArathiBasinTactics.Plan(abPic);
            Assert.NotNull(abOrders);

            var avDec = new AvDecision
            {
                TeamIndex = i % 2,
                Role = (PlayerbotBattlegroundRole)(i % 3),
                Lane = PlayerbotAlteracValleyTactics.LaneOf(botGuid),
                Position = selfPos,
                Nodes = Enumerable.Range(0, i % 2 == 0 ? 15 : 1).Select(n => new AvNode((i + n) % 3, (i + n) % 3, (i + n) % 3, n % 2 == 0, true, new Vector3(200f + n * 10f, 200f, 0f))).ToArray(),
                EnemyGeneral = new Vector3(800f, 800f, 0f),
            };
            var avObj = PlayerbotAlteracValleyTactics.Decide(avDec);
            _ = avObj.Kind;

            var shares = PlayerbotBattlegroundRoles.Shares(BattlegroundType.WarsongGulch, rng.Next(0, 40));
            Assert.True(shares.Attackers + shares.Escorts + shares.Defenders >= 0);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void PlayerbotEconomy_Budget_Opportunity_Risk_GuardsAndSeededFuzz(int seed)
    {
        var econOpts = new PlayerbotEconomyOptions();
        var itemTpl = new ItemTemplate { Entry = 25, SellPrice = 100, BuyPrice = 400, Stackable = 20 };

        Assert.Throws<ArgumentNullException>(() => PlayerbotAuctionPricing.ReferencePerUnit(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyWire.DecodeAuctionList(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyWire.DecodeCommandResult(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyWire.DecodeMailList(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyWire.DecodeMailResult(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyWire.DecodeHasUnreadMail(null!));

        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeAuctionList([1, 2]));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeAuctionList(
            [0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0, 0, 0, 0]));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeCommandResult([1, 2, 3]));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeMailList([0xFF]));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeMailResult([1, 2, 3]));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeHasUnreadMail([1, 2]));

        var oppOpts = new PlayerbotOpportunityOptions();
        var riskOpts = new PlayerbotRiskOptions();
        Assert.Throws<ArgumentNullException>(() => PlayerbotRiskModel.Assess(null!, riskOpts));
        Assert.Throws<ArgumentNullException>(() => PlayerbotRiskModel.Assess(new PlayerbotEngagementFacts(), null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotRiskModel.NeededGroupSize(null!, riskOpts, 5));
        Assert.Throws<ArgumentNullException>(() => PlayerbotRiskModel.BeyondStrength(null!, riskOpts));
        Assert.Throws<ArgumentNullException>(() => PlayerbotRiskModel.RetreatDamage(null!, 10f));
        Assert.Throws<ArgumentNullException>(() => PlayerbotRiskModel.RetreatSucceeds(null!, 100u, 10f));

        var rng = new Random(seed);
        var oppLedger = new PlayerbotOpportunityLedger();
        Span<PlayerbotBudgetLine> budgetLines = stackalloc PlayerbotBudgetLine[3];

        for (var i = 0; i < 48; i++)
        {
            _ = PlayerbotAuctionPricing.ReferencePerUnit(itemTpl);
            _ = PlayerbotAuctionPricing.VendorFloorPerUnit(itemTpl.SellPrice, 0.05f);
            _ = PlayerbotAuctionPricing.StartBid((uint)rng.Next(10, 50000));
            _ = PlayerbotEconomyPlanner.VisitBudget((uint)rng.Next(0, 100000), econOpts.BuyUpgradeBudgetFraction);
            _ = PlayerbotEconomyPlanner.Remaining(1000u, (uint)rng.Next(0, 2000));
            _ = PlayerbotEconomyPlanner.TryParseAuctionSubject("Outbid: Linen Cloth", out _, out _);

            budgetLines[0] = new PlayerbotBudgetLine(PlayerbotSpendKind.Repair, (uint)rng.Next(0, 5000));
            budgetLines[1] = new PlayerbotBudgetLine(PlayerbotSpendKind.ClassTraining, (uint)rng.Next(0, 5000));
            budgetLines[2] = new PlayerbotBudgetLine(PlayerbotSpendKind.Mount, (uint)rng.Next(0, 5000));
            var reserved = PlayerbotBudgetMath.ReservedAbove(budgetLines, (PlayerbotSpendKind)(i % 5));
            var spendable = PlayerbotBudgetMath.Spendable(
                budgetLines,
                (PlayerbotSpendKind)(i % 5),
                (uint)rng.Next(0, 50000),
                1000u,
                0.1f);
            Assert.True(reserved <= 15000u);
            Assert.True(spendable <= 50000u);

            var enemy = new RiskEnemy(
                (uint)(i + 1),
                (byte)rng.Next(1, 63),
                (uint)(i % 4),
                (uint)rng.Next(50, 8000),
                (float)(rng.NextDouble() * 40.0 + 1.0),
                (RiskJoin)(i % 3));
            var facts = new PlayerbotEngagementFacts
            {
                BotLevel = (byte)rng.Next(1, 60),
                BotHealth = (uint)rng.Next(50, 5000),
                BotMaxHealth = 5000u,
                BotManaPct = (float)rng.NextDouble(),
                ManaDependence = (float)rng.NextDouble(),
                BotDps = (float)(rng.NextDouble() * 60.0 + 5.0),
                ReadyEscapes = rng.Next(0, 3),
                Enemies = [enemy],
                QuestObjective = i % 2 == 0,
                LootValue = (float)(rng.NextDouble() * 5.0),
                GroupSize = rng.Next(1, 5),
            };
            var assessment = PlayerbotRiskModel.Assess(facts, riskOpts);
            Assert.True(float.IsFinite(assessment.Risk));
            _ = PlayerbotRiskModel.NeededGroupSize(facts, riskOpts, 5);
            _ = PlayerbotRiskModel.BeyondStrength(facts, riskOpts);

            var oppFacts = new OpportunityFacts
            {
                Engagement = facts,
                LootUnits = (float)(rng.NextDouble() * 4.0),
                TravelSeconds = (float)(rng.NextDouble() * 20.0),
                Personality = PlayerbotOpportunityModel.Personality((ulong)(i + 1), 0.2f),
            };
            var oppVerdict = PlayerbotOpportunityModel.Decide(oppFacts, oppOpts, riskOpts);
            Assert.True(float.IsFinite(oppVerdict.Net));

            _ = oppLedger.Defer((ulong)(i + 1), (uint)(i * 1000));
            _ = oppLedger.IsDeferred((ulong)(i + 1), (uint)(i * 1000 + 100));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Population_PlannersAndControllers_SeededFuzz(int seed)
    {
        var popOpts = new PlayerbotPopulationOptions();
        Assert.Throws<ArgumentNullException>(() => PopulationBudgetController.EffectiveTarget(null!, 10));
        Assert.Throws<ArgumentNullException>(() => PopulationBudgetController.Decide(10, 20.0, 5, 5, 10, null!, 1));

        var rng = new Random(seed);
        var budgetWindows = new PopulationBudgetWindows();
        var rush = new PopulationRush();

        for (var i = 0; i < 48; i++)
        {
            var maxBots = rng.Next(10, 500);
            var effTarget = PopulationBudgetController.EffectiveTarget(popOpts, maxBots);
            Assert.True(effTarget >= 0);

            var p95 = rng.NextDouble() * 60.0;
            var windows = budgetWindows.Observe(100, (i + 1) * 100L, p95, popOpts);
            var decision = PopulationBudgetController.Decide(100, p95, rng.Next(0, 300), rng.Next(0, 200), effTarget, popOpts, windows);
            Assert.True(decision.Count >= 0);

            var race = (byte)(1 + (i % 8));
            var area = PopulationStartAreas.AreaOf(race);
            Assert.NotNull(area);
            var place = PopulationStartAreas.PlaceOf(area!, StartHomeStage.Start);
            Assert.Equal(StartHomeStage.Start, PopulationStartAreas.StageOfPlace(place));
            _ = PopulationStartAreas.StageFor(rng.Next(1, 60), popOpts);

            rush.Pacer.Add(1000u, popOpts.MaxCreatePerMinute);
            _ = rush.Pacer.Take(5);
            _ = PopulationTownProgression.Rows;
        }
    }

    [Fact]
    public void PlayerbotPolicy_Qa_Scenarios_GuildNames_GuardsAndExceptions()
    {
        Assert.Throws<FileNotFoundException>(() => PlayerbotModelPolicy.Load("Z:/nonexistent_dir_12345/missing_policy.json"));

        static PlayerbotModelPolicy ParseJson(string json)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return PlayerbotModelPolicy.Parse(ms);
        }

        Assert.ThrowsAny<Exception>(() => ParseJson("not-json"));
        Assert.Throws<InvalidDataException>(() => ParseJson("[]"));
        Assert.Throws<InvalidDataException>(() => ParseJson("""{"format":"wrong","version":1}"""));
        Assert.Throws<InvalidDataException>(() => ParseJson("""{"format":"arcanecore.playerbot-policy","version":99}"""));

        var validPolicyJson =
            "{" +
            "\"format\":\"arcanecore.playerbot-policy\"," +
            "\"version\":1," +
            "\"features\":[" + string.Join(",", PlayerbotPolicySchema.FeatureNames.Select(n => $"\"{n}\"")) + "]," +
            "\"goals\":[" + string.Join(",", PlayerbotPolicySchema.GoalNames.Select(n => $"\"{n}\"")) + "]," +
            "\"layers\":[{\"inputs\":" + PlayerbotPolicySchema.FeatureCount + ",\"outputs\":" + PlayerbotPolicySchema.GoalCount + ",\"activation\":\"linear\"," +
            "\"weights\":[" + string.Join(",", Enumerable.Repeat("0.1", PlayerbotPolicySchema.FeatureCount * PlayerbotPolicySchema.GoalCount)) + "]," +
            "\"bias\":[" + string.Join(",", Enumerable.Repeat("0.0", PlayerbotPolicySchema.GoalCount)) + "]}]" +
            "}";
        var model = ParseJson(validPolicyJson);
        Span<float> features = stackalloc float[PlayerbotPolicySchema.FeatureCount];
        Span<float> scores = stackalloc float[PlayerbotPolicySchema.GoalCount];
        model.Score(features, 1, scores);
        Assert.True(float.IsFinite(scores[0]));

        Assert.Throws<ArgumentNullException>(() => new PlayerbotPolicyEngine(null!));
        Assert.Throws<ArgumentNullException>(() => new PlayerbotPolicyTelemetry(null!, "dir"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerbotPolicyTelemetryWriter("dir", 100, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerbotPolicyTelemetryWriter("dir", 2048, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerbotQaSignalBus(0));
        var badSignal = new QaSignal
        {
            Kind = (QaSignalKind)999,
            Message = "bad",
            Detail = "detail",
            AtUtc = DateTime.UtcNow,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerbotQaFingerprint.Key(badSignal));

        var redactor = new PlayerbotQaRedactor("secret-token-123");
        Assert.Throws<ArgumentNullException>(() => redactor.SetNames(null!));
        redactor.SetNames(["Thrall", "Jaina"]);
        var redacted = redactor.Redact("Player Thrall whispered Jaina with secret-token-123");
        Assert.DoesNotContain("Thrall", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-123", redacted, StringComparison.Ordinal);

        var qaEx = new PlayerbotQaPublishException("publish failed", true, new InvalidOperationException("inner"));
        Assert.Equal("publish failed", qaEx.Message);
        Assert.True(qaEx.Transient);
        Assert.NotNull(qaEx.InnerException);

        var timeProvider = new ScenarioTimeProvider(DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => timeProvider.Advance(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ScenarioAssertionException>(() => ScenarioContext.Expect(false, "expected true"));
        Assert.Throws<ScenarioAssertionException>(() => ScenarioContext.ExpectEqual(1, 2, "mismatch"));

        var guildRules = new PlayerbotGuildNameRules(
            new GuildOptions(),
            null,
            null,
            _ => false,
            _ => false,
            null,
            null,
            PlayerbotChatSafetyFilter.Default);
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.Generate(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.Screen(null!, "Silver Hand"));
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.Screen(guildRules, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.TryGenerate(null!, new Random(1)));
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.TryGenerate(guildRules, null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void ScenarioDecoders_QaSignalParser_GuildNames_SeededFuzz(int seed)
    {
        var rng = new Random(seed);
        var guildRules = new PlayerbotGuildNameRules(
            new GuildOptions(),
            null,
            null,
            _ => false,
            _ => false,
            null,
            null,
            PlayerbotChatSafetyFilter.Default);

        Assert.Throws<FormatException>(() => ScenarioDecoders.MailListCount([]));
        Assert.Throws<FormatException>(() => ScenarioDecoders.DuelComplete([]));
        Assert.Throws<FormatException>(() => ScenarioDecoders.GroupList([0]));
        Assert.Throws<FormatException>(() => ScenarioDecoders.TradeStatus([0]));
        Assert.Throws<FormatException>(() => ScenarioDecoders.MailResult([0]));
        Assert.Throws<FormatException>(() => ScenarioDecoders.LootResponse([0]));
        Assert.Throws<FormatException>(() => ScenarioClassDecoders.SpellDamage([0]));
        Assert.Throws<FormatException>(() => ScenarioClassDecoders.PeriodicAura([0]));
        Assert.ThrowsAny<Exception>(() => ScenarioControlPackets.ClientControl([]));
        Assert.ThrowsAny<Exception>(() => ScenarioControlPackets.PetBar([0]));
        Assert.ThrowsAny<Exception>(() => ScenarioControlPackets.Move([0]));
        Assert.ThrowsAny<Exception>(() => ScenarioCreatureDecoders.MonsterChat([0]));
        Assert.ThrowsAny<Exception>(() => ScenarioCreatureDecoders.AttackStart([0]));
        Assert.Throws<FormatException>(() => ScenarioMeetingStones.SetQueue([0]));
        Assert.Throws<FormatException>(() => ScenarioMeetingStones.MemberAdded([0]));
        Assert.Throws<FormatException>(() => ScenarioMeetingStones.JoinFailed([]));
        Assert.Throws<FormatException>(() => ScenarioProcDecoders.DamageShield([0]));
        Assert.Throws<FormatException>(() => ScenarioProcDecoders.ProcResist([0]));
        Assert.Throws<FormatException>(() => ScenarioTalents.WipeConfirm([0]));
        Assert.ThrowsAny<Exception>(() => ScenarioTalents.GossipMessage([0]));
        Assert.Throws<FormatException>(() => ScenarioAuctionWire.CommandResult([0]));
        Assert.Throws<FormatException>(() => ScenarioAuctionWire.BidderNotification([0]));
        Assert.Throws<FormatException>(() => ScenarioAuctionWire.List([0]));

        for (var i = 0; i < 48; i++)
        {
            var gname = PlayerbotGuildNames.Generate(rng);
            Assert.NotEmpty(gname);
            _ = PlayerbotGuildNames.Screen(guildRules, gname);
            _ = PlayerbotGuildNames.TryGenerate(guildRules, rng);

            _ = PlayerbotQaSignalParser.TryParse(
                $"Playerbot 'Thrall' stalled for {i + 15}s at (map=0, x=-8913.2, y=554.6, z=93.8)",
                DateTime.UtcNow,
                Guid.NewGuid(),
                out _);
        }
    }

    private sealed class StraightLinePathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) =>
            PathResult.StraightLine(start, end, PathType.Normal);
    }
}

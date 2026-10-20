using System.Collections.Frozen;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Game.Characters;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.ServerMail;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Chat;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Bots.Progression;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Characters.Life;
using ArcaneCore.World.Characters.Rename;
using ArcaneCore.World.Chat;
using ArcaneCore.World.ClientData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Gm.DebugDraw;
using ArcaneCore.World.Gm.DebugTools;
using ArcaneCore.World.Gm.Events;
using ArcaneCore.World.Gm.FirstLogin;
using ArcaneCore.World.Gm.Teleport;
using ArcaneCore.World.Gm.Worldport;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Items;
using ArcaneCore.World.Names;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Rates;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.ServerMail;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Social;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tickets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// Exhaustive exception/guard tests and deterministic seeded fuzz tests across the 22 owned World subsystems:
/// Economy, Items, Crafting, Social, Chat, Gm, Commands, Characters, Honor, Battlegrounds, Npc, Progression,
/// Skills, Talents, Reputation, ServerMail, Tickets, Names, Rates, Persistence, ClientData, and Features.
/// </summary>
public sealed class WorldEconomySocialGmPersistenceFuzzAndExceptionTests
{
    /// <summary>Deterministic seeds used across all fuzz theories.</summary>
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    public class NoOpDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

#pragma warning disable SYSLIB0050
    private static T Dummy<T>() where T : class
        => typeof(T).IsInterface
            ? DispatchProxy.Create<T, NoOpDispatchProxy>()
            : (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050

    private static ServiceProvider CreateMinimalServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static readonly ServiceProvider s_minimalServices = CreateMinimalServices();

    private static WorldRuntime CreateMinimalWorld()
    {
        var saves = new CharacterSaveQueue(s_minimalServices.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    private static CharacterRecord SampleCharacterRecord(int id = 1) => new()
    {
        Id = id,
        Name = "Arthas",
    };

    private static CharacterState SampleCharacterState(int id = 1) => new(
        Id: id,
        MapId: 0,
        ZoneId: 12,
        X: -8949.95f,
        Y: -132.493f,
        Z: 83.5312f,
        Orientation: 0f,
        Level: 1,
        PlayedTime: 100);

    // =========================================================================
    // 1. ECONOMY, ITEMS & CRAFTING EXCEPTION / GUARD TESTS
    // =========================================================================

    /// <summary>Verifies null and state guards on AuctionBotConfigChecks, AuctionBotFeature, EconomyFeature, and EconomySettlements.</summary>
    [Fact]
    public async Task Economy_Guards_ThrowExpectedExceptions()
    {
        var checks = new AuctionBotConfigChecks();
        Assert.Throws<ArgumentNullException>(() => checks.Check(null!));

        using ServiceProvider sp = CreateMinimalServices();
        var botFeature = new AuctionBotFeature(sp, NullLogger<AuctionBotFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => botFeature.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => botFeature.SetItemData(null!, null));
        Assert.Throws<ArgumentNullException>(() => botFeature.ItemData(null!));

        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var economy = new EconomyFeature(sp, scopes, NullLogger<EconomyFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => economy.Attach(null!));

        using WorldRuntime world = CreateMinimalWorld();
        economy.Attach(world);
        Assert.Throws<InvalidOperationException>(() => economy.Attach(world));

        Assert.Throws<ArgumentNullException>(() =>
            economy.StartBotListing(1, 1, null!, 10, 20, 100, Guid.NewGuid(), _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            economy.StartBotListing(1, 1, new ItemInstanceData { Guid = 1, Entry = 1 }, 10, 20, 100, Guid.NewGuid(), null!));
        Assert.Throws<ArgumentNullException>(() =>
            economy.StartBotBuyout(1, 10, Guid.NewGuid(), null!));
        Assert.Throws<ArgumentNullException>(() =>
            economy.StartBotBid(1, 10, Guid.NewGuid(), null!));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            economy.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));

        Assert.Throws<ArgumentNullException>(() => economy.SendServerMail(null!));
        Assert.Throws<ArgumentNullException>(() => economy.SendGmMail(null!));

        var settlements = new EconomySettlements(scopes, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() =>
            settlements.TryStart(null!, [], (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() =>
            settlements.TryStart([], null!, (_, _) => { }));
    }

    /// <summary>Verifies null guards across Items and Crafting features.</summary>
    [Fact]
    public async Task ItemsAndCrafting_Guards_ThrowArgumentNullException()
    {
        using ServiceProvider sp = CreateMinimalServices();

        Assert.Throws<ArgumentNullException>(() => new ItemEquipSpellFeature(null!, NullLogger<ItemEquipSpellFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ItemEquipSpellFeature(sp, null!));
        var equipFeature = new ItemEquipSpellFeature(sp, NullLogger<ItemEquipSpellFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => equipFeature.Attach(null!));

        Assert.Throws<ArgumentNullException>(() => new ItemRandomPropertyFeature(null!, NullLogger<ItemRandomPropertyFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ItemRandomPropertyFeature(sp, null!));

        var itemsFeature = new ItemsFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ItemsFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => itemsFeature.ReplaceTemplates(null!));

        Assert.Throws<ArgumentNullException>(() => new PageTextFeature(null!, NullLogger<PageTextFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new PageTextFeature(sp, null!));
        var pageText = new PageTextFeature(sp, NullLogger<PageTextFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => pageText.Replace(null!));
        Assert.Throws<ArgumentNullException>(() => pageText.Answer(null!, 1));

        var useItem = new UseItemFeature(sp);
        Assert.Throws<ArgumentNullException>(() => useItem.Attach(null!));

        var crafting = new CraftingFeature(sp);
        Assert.Throws<ArgumentNullException>(() => crafting.Attach(null!));

        var enchanting = new EnchantingFeature(sp, NullLogger<EnchantingFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => enchanting.Attach(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            enchanting.OnPlayerLoadingAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));
    }

    // =========================================================================
    // 2. SOCIAL & CHAT EXCEPTION / GUARD TESTS
    // =========================================================================

    /// <summary>Verifies null, state, and persistence failure guards on SocialFeature, SocialCharacterDeleteHook, SocialGroupPersistenceFeature, and SocialWriteQueue.</summary>
    [Fact]
    public async Task Social_Guards_ThrowExpectedExceptions()
    {
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        var directory = new CharacterDirectory();
        var social = new SocialFeature(directory, scopes, NullLoggerFactory.Instance, services: sp);
        Assert.Throws<InvalidOperationException>(() => _ = social.Context);
        Assert.Throws<ArgumentNullException>(() => social.ExecuteWhenReady(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => social.ExecuteWhenReady(Dummy<Player>(), null!));
        Assert.Throws<InvalidOperationException>(() => social.ExecuteWhenReady(Dummy<Player>(), _ => { }));

        var deleteHook = new SocialCharacterDeleteHook(social, directory);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            deleteHook.CanDeleteCharacterAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            deleteHook.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));

        var groupPersist = new SocialGroupPersistenceFeature(sp, scopes, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => groupPersist.Attach(null!));

        var queue = new SocialWriteQueue(scopes, NullLogger<SocialWriteQueue>.Instance, new SocialWriteQueueOptions { RetryDelayMs = 1 });
        Assert.Throws<ArgumentNullException>(() => queue.SaveGuild(null!));
        Assert.Throws<ArgumentNullException>(() => queue.SavePetition(null!));
        Assert.Throws<ArgumentNullException>(() => queue.CompletePetition(null!, 1));

        using ServiceProvider failingSp = CreateMinimalServices(s => s.AddScoped<ISocialStore>(_ => new FailingSocialStore()));
        var failingQueue = new SocialWriteQueue(failingSp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SocialWriteQueue>.Instance, new SocialWriteQueueOptions { RetryDelayMs = 1 });
        failingQueue.Start();
        failingQueue.SetSocial(77, 88, SocialFlags.Friend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingQueue.FlushCharacterAsync(77));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingQueue.StopAsync());
    }

    /// <summary>Verifies null and configuration guards on ChatFeature, ChatWordFilter, and ChatWordFilterFeature.</summary>
    [Fact]
    public void Chat_Guards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new ChatWordFilter(null!));
        var filter = new ChatWordFilter([]);
        Assert.Throws<ArgumentNullException>(() => filter.Apply(null!));

        using ServiceProvider sp = CreateMinimalServices();
        var wordFilterFeature = new ChatWordFilterFeature(sp);
        Assert.Throws<ArgumentNullException>(() => wordFilterFeature.Publish(null!));

        IConfigurationRoot badConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["World:Chat:EmotesTextDbcPath"] = "EmotesText.dbc",
            })
            .Build();
        var chatFeature = new ChatFeature(badConfig);
        using WorldRuntime world = CreateMinimalWorld();
        Assert.Throws<InvalidOperationException>(() => chatFeature.Attach(world));
    }

    // =========================================================================
    // 3. COMMANDS & GM EXCEPTION / GUARD TESTS
    // =========================================================================

    /// <summary>Verifies null, duplicate, and console invoker guards on CommandTable, ChatCommands, CommandTableSource, CommandListing, and CommandContext.</summary>
    [Fact]
    public void Commands_Guards_ThrowExpectedExceptions()
    {
        var cmd1 = new ChatCommand("help", AccountSecurity.Player, "help", (_, _) => true);
        var cmd2 = new ChatCommand("HELP", AccountSecurity.Player, "help2", (_, _) => true);
        var source = new CommandTableSource(new CommandTable([cmd1], new GmOptions()));
        CommandAddResult dupResult = source.TryAdd([cmd2]);
        Assert.False(dupResult.Applied);
        Assert.NotNull(dupResult.Error);

        var table = new CommandTable([cmd1]);
        WorldSession dummySession = Dummy<WorldSession>();
        Assert.Throws<ArgumentNullException>(() => CommandContext.ForConsole(null!, table, _ => { }));
        Assert.Throws<ArgumentNullException>(() => CommandContext.ForConsole(dummySession, null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => CommandContext.ForConsole(dummySession, table, null!));

        CommandContext consoleCtx = CommandContext.ForConsole(dummySession, table, _ => { });
        Assert.Throws<ConsoleInvokerException>(() => _ = consoleCtx.Player);
        Assert.Throws<ConsoleInvokerException>(() => _ = consoleCtx.SelectedPlayerOrSelf());

        Assert.Throws<ArgumentNullException>(() => CommandContext.ForRemote(null!, table, _ => { }, AccountSecurity.Administrator, "admin", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => CommandContext.ForRemote(dummySession, null!, _ => { }, AccountSecurity.Administrator, "admin", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => CommandContext.ForRemote(dummySession, table, null!, AccountSecurity.Administrator, "admin", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => CommandContext.ForRemote(dummySession, table, _ => { }, AccountSecurity.Administrator, null!, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => CommandContext.ForRemote(dummySession, table, _ => { }, AccountSecurity.Administrator, "", CancellationToken.None));
        Assert.Throws<ArgumentException>(() => CommandContext.ForRemote(dummySession, table, _ => { }, AccountSecurity.Administrator, "   ", CancellationToken.None));
    }

    /// <summary>Verifies null, configuration, and lifecycle guards across GM subsystems (Audit, Boost, DebugDraw, Events, FirstLogin, Teleport, Worldport).</summary>
    [Fact]
    public async Task Gm_Guards_ThrowExpectedExceptions()
    {
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using WorldRuntime world = CreateMinimalWorld();

        var audit = new GmAuditFeature(scopes, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => audit.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => audit.MutedUntilUnixSeconds(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => audit.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));
        Assert.Throws<ArgumentNullException>(() => GmTicketHandlers.BuildTicketStatus(null!, 0, null, 0));

        // Boost planners
        Assert.Throws<ArgumentNullException>(() => BoostActionBarPlanner.Plan(null!, _ => null, Dummy<SpellRankChains>(), 0, 0, []));
        Assert.Throws<ArgumentNullException>(() => BoostActionBarPlanner.Plan(new HashSet<uint>(), null!, Dummy<SpellRankChains>(), 0, 0, []));
        Assert.Throws<ArgumentNullException>(() => BoostActionBarPlanner.Plan(new HashSet<uint>(), _ => null, null!, 0, 0, []));

        ItemTemplate sampleItem = Dummy<ItemTemplate>();
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.IsPoolItem(null!, 60, FrozenSet<uint>.Empty));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.IsPoolItem(sampleItem, 60, null!));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.GivesAnything(null!));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.SuffixScore(null!, Dummy<PlayerbotStatWeights>()));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.SuffixScore(new BoostSuffix(1, "of the Bear", []), null!));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.ChooseSuffix(null!, Dummy<PlayerbotStatWeights>()));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.ChooseSuffix([], null!));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.IsCosmeticQuestReward(null!, FrozenSet<uint>.Empty));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.IsCosmeticQuestReward(sampleItem, null!));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.Plan(null!, Dummy<PlayerbotStatWeights>(), Class.Warrior, false, _ => true));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.Plan([], null!, Class.Warrior, false, _ => true));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.Plan([], Dummy<PlayerbotStatWeights>(), Class.Warrior, false, null!));

        Assert.Throws<ArgumentNullException>(() => BoostEventBinding.From(null!, Dummy<CreatureContent>()));
        Assert.Throws<ArgumentNullException>(() => BoostEventBinding.From(Dummy<GameEventRows>(), null!));
        Assert.Throws<ArgumentNullException>(() => BoostItemSources.Build(null!, QuestStore.Empty, Dummy<LootContent>()));
        Assert.Throws<ArgumentNullException>(() => BoostItemSources.Build(NpcStore.Empty, null!, Dummy<LootContent>()));
        Assert.Throws<ArgumentNullException>(() => BoostItemSources.Build(NpcStore.Empty, QuestStore.Empty, null!));
        Assert.Throws<ArgumentNullException>(() => BoostItemSources.QuestOnly(null!, QuestStore.Empty, Dummy<LootContent>()));
        Assert.Throws<ArgumentNullException>(() => BoostItemSources.QuestOnly(NpcStore.Empty, null!, Dummy<LootContent>()));
        Assert.Throws<ArgumentNullException>(() => BoostItemSources.QuestOnly(NpcStore.Empty, QuestStore.Empty, null!));

        var questFeature = new QuestNpcFeature(sp, scopes, NullLogger<QuestNpcFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => BoostQuestPruner.DropGrayQuests(null!, questFeature.Services, 60));
        Assert.Throws<ArgumentNullException>(() => BoostQuestPruner.DropGrayQuests(Dummy<Player>(), null!, 60));

        Assert.Throws<ArgumentNullException>(() => new BoostSuffixes(null!, _ => null, _ => null));
        Assert.Throws<ArgumentNullException>(() => new BoostSuffixes(ItemRandomPropertyCatalog.Empty, null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => new BoostSuffixes(ItemRandomPropertyCatalog.Empty, _ => null, null!));
        var suffixes = new BoostSuffixes(ItemRandomPropertyCatalog.Empty, _ => null, _ => null);
        Assert.Throws<ArgumentNullException>(() => suffixes.For(null!));

        // Misc GM features
        Assert.Throws<ArgumentNullException>(() => new GmInvisibilityFeature().Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new MapCostTracker().Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new UnstuckFeature(sp).Attach(null!));

        var debugDraw = new DebugDrawFeature(sp);
        Assert.Throws<ArgumentNullException>(() => debugDraw.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => debugDraw.Draw(null!, "topic", []));
        Assert.Throws<ArgumentNullException>(() => debugDraw.Draw(Dummy<Player>(), "topic", null!));
        Assert.Throws<ArgumentNullException>(() => debugDraw.Clear(null!));
        Assert.Throws<ArgumentNullException>(() => DebugMarkerModels.Resolve(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => DebugMarkerModels.Resolve(new DebugDrawOptions(), null!));

        // LiveFx
        Assert.Throws<ArgumentNullException>(() => LiveFxLookup.Run(null!, "sound", "1"));
        Assert.Throws<ArgumentNullException>(() => new LiveFxDataFeature(NullLogger<LiveFxDataFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentException>(() => LiveFxDataFeature.Load("", NullLogger.Instance));
        Assert.Throws<ArgumentException>(() => LiveFxDataFeature.Load("   ", NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => LiveFxDataFeature.Load("some_dir", null!));
        string missingDir = Path.Combine(Path.GetTempPath(), "arcanecore_nonexistent_fx_" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => LiveFxDataFeature.Load(missingDir, NullLogger.Instance));

        // GmFirstLoginTools
        Assert.Throws<ArgumentNullException>(() => GmFirstLoginToolCatalog.Select(null!, AccountSecurity.GameMaster, SpellStore.Empty));
        Assert.Throws<ArgumentNullException>(() => GmFirstLoginToolCatalog.Select(new GmFirstLoginToolsOptions(), AccountSecurity.GameMaster, null!));
        Assert.Throws<InvalidOperationException>(() => GmFirstLoginToolCatalog.Select(new GmFirstLoginToolsOptions { Enabled = true }, AccountSecurity.GameMaster, SpellStore.Empty));

        Assert.Throws<ArgumentNullException>(() => new GmFirstLoginToolsFeature(null!, NullLogger<GmFirstLoginToolsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new GmFirstLoginToolsFeature(sp.GetRequiredService<IConfiguration>(), null!));
        var toolsFeature = new GmFirstLoginToolsFeature(sp.GetRequiredService<IConfiguration>(), NullLogger<GmFirstLoginToolsFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => toolsFeature.Attach(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => toolsFeature.OnSpellbookLoadedAsync(null!, SampleCharacterRecord(), Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => toolsFeature.OnSpellbookLoadedAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => toolsFeature.OnSpellbookLoadedAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));

        Assert.Throws<ArgumentNullException>(() => GmFirstLoginToolsOptions.Bind(null!));
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, MinimumSecurity = AccountSecurity.Player }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, SpellIds = Enumerable.Range(1, 65).Select(i => (uint)i).ToArray() }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [0] }.Validate());
        uint firstToolId = GmFirstLoginToolCatalog.All[0].Id;
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [firstToolId, firstToolId] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [999999] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, ExcludedSpellIds = [999999] }.Validate());

        // WorldportSpike
        Assert.Throws<ArgumentNullException>(() => new WorldportSpike(null!, () => null!));
        Assert.Throws<ArgumentNullException>(() => new WorldportSpike(world, null!));
        var spike = new WorldportSpike(world, () => null!);
        Assert.Throws<ArgumentNullException>(() => spike.StartFar(null!, 0, 0, 0, 0, "gm", out _));
        Assert.Throws<ArgumentNullException>(() => spike.StartReAdd(null!, "gm", out _));
        spike.Dispose();
        Assert.Throws<ObjectDisposedException>(() => spike.StartFar(Dummy<Player>(), 0, 0, 0, 0, "gm", out _));
        Assert.Throws<ObjectDisposedException>(() => spike.StartReAdd(Dummy<Player>(), "gm", out _));

        Assert.Throws<ArgumentNullException>(() => WorldportSpikeCommands.Create(null!, null));
        var spikeModule = new WorldportSpikeServiceModule();
        Assert.Throws<ArgumentNullException>(() => spikeModule.Register(null!, sp.GetRequiredService<IConfiguration>()));
        Assert.Throws<ArgumentNullException>(() => spikeModule.Register(new ServiceCollection(), null!));
    }

    // =========================================================================
    // 4. CHARACTERS, HONOR & BATTLEGROUNDS EXCEPTION / GUARD TESTS
    // =========================================================================

    /// <summary>Verifies null and configuration guards on CharacterCreation, CharacterDeleteHooks, CharacterLifeFeature, CharacterRename, and StartActionsFeature.</summary>
    [Fact]
    public async Task Characters_Guards_ThrowExpectedExceptions()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterDeletion.TryDeleteAsync(null!, 1));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterDeletion.ReconcilePendingAsync(null!));

        using ServiceProvider sp = CreateMinimalServices();
        Assert.Throws<ArgumentNullException>(() => new CharacterCreationFeature(null!, NullLogger<CharacterCreationFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new CharacterCreationFeature(sp, null!));
        var creation = new CharacterCreationFeature(sp, NullLogger<CharacterCreationFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => creation.Attach(null!));

        IConfigurationRoot halfDbcConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CharacterCreation:CharSectionsDbcPath"] = "CharSections.dbc",
            })
            .Build();
        var halfServices = new ServiceCollection();
        halfServices.AddLogging();
        halfServices.AddSingleton<IConfiguration>(halfDbcConfig);
        using ServiceProvider halfSp = halfServices.BuildServiceProvider();
        var badCreation = new CharacterCreationFeature(halfSp, NullLogger<CharacterCreationFeature>.Instance);
        using WorldRuntime world = CreateMinimalWorld();
        Assert.Throws<InvalidOperationException>(() => badCreation.Attach(world));

        CharacterCreationRequest req = default;
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CharacterCreationRules.EvaluateAsync(req, AccountSecurity.Player, null!, 10, Dummy<ICharacterCreationFacts>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CharacterCreationRules.EvaluateAsync(req, AccountSecurity.Player, new CharacterCreationOptions(), 10, null!));

        Assert.Throws<ArgumentNullException>(() => new StartActionsFeature(null!, NullLogger<StartActionsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new StartActionsFeature(sp, null!));
        var startActions = new StartActionsFeature(sp, NullLogger<StartActionsFeature>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => startActions.OnCharacterCreatedAsync(null!, SampleCharacterRecord()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => startActions.OnCharacterCreatedAsync(Dummy<WorldSession>(), null!));
        Assert.Throws<ArgumentNullException>(() => StartActionsFeature.Filter(null!, null, null));

        var life = new CharacterLifeFeature();
        Assert.Throws<ArgumentNullException>(() => life.Attach(null!));
        life.Attach(world);
        Assert.Throws<InvalidOperationException>(() => life.Attach(world));
        await Assert.ThrowsAsync<ArgumentNullException>(() => life.OnPlayerLoadedAsync(null!, SampleCharacterRecord(), Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => life.OnPlayerLoadedAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => life.OnPlayerLoadedAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));
        life.Dispose();

        var renameReq = new CharacterRenameRequest(1, [65, 98]);
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterRename.DecideAsync(null!, new CharacterCreationOptions(), NameCatalog.Empty, AccountSecurity.Player, 1, renameReq));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterRename.DecideAsync(Dummy<ICharacterRenameStore>(), null!, NameCatalog.Empty, AccountSecurity.Player, 1, renameReq));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterRename.DecideAsync(Dummy<ICharacterRenameStore>(), new CharacterCreationOptions(), null!, AccountSecurity.Player, 1, renameReq));
        Assert.Throws<ArgumentNullException>(() => CharacterNameCatalog.Check(null!, "Name", AccountSecurity.Player));
        Assert.Throws<ArgumentNullException>(() => CharacterNameCatalog.Check(NameCatalog.Empty, null!, AccountSecurity.Player));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterRename.FlagsOfAccountAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => CharacterRename.CharEnumFlagsOfAccountAsync(null!));
    }

    /// <summary>Verifies null, attachment, and persistence failure guards on Honor and Battleground subsystems.</summary>
    [Fact]
    public async Task HonorAndBattlegrounds_Guards_ThrowExpectedExceptions()
    {
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using WorldRuntime world = CreateMinimalWorld();

        var honor = new HonorFeature(sp, scopes, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => honor.Attach(null!));
        honor.Attach(world);
        Assert.Throws<InvalidOperationException>(() => honor.Attach(world));
        await Assert.ThrowsAsync<ArgumentNullException>(() => honor.OnCharacterCreatedAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => honor.OnPlayerLoadingAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => honor.OnPlayerLoadingAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));

        var honorDelete = new HonorCharacterDeleteHook(honor);
        await Assert.ThrowsAsync<ArgumentNullException>(() => honorDelete.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));

        var maintenance = new HonorMaintenanceFeature(sp, scopes, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => maintenance.Attach(null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.RunAsync(false));

        var honorQueue = new HonorWriteQueue(scopes, NullLogger<HonorWriteQueue>.Instance);
        Assert.Throws<ArgumentNullException>(() => honorQueue.AppendCp(1, null!));
        Assert.Throws<ArgumentNullException>(() => honorQueue.SaveState(1, null!));

        using ServiceProvider fenceHonorSp = CreateMinimalServices(s => s.AddScoped<IHonorStore>(_ => new FailingHonorStore()));
        var fenceHonorQueue = new HonorWriteQueue(fenceHonorSp.GetRequiredService<IServiceScopeFactory>(), NullLogger<HonorWriteQueue>.Instance);
        fenceHonorQueue.Start();
        fenceHonorQueue.SaveState(11, CharacterHonorState.Empty);
        InvalidOperationException honorEx = await Assert.ThrowsAsync<InvalidOperationException>(() => fenceHonorQueue.FlushCharacterAsync(11));
        Assert.IsType<CharacterOwnershipRejectedException>(honorEx.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fenceHonorQueue.StopAsync());

        Assert.Throws<ArgumentNullException>(() => InspectHandlers.InspectableTarget(null!, default));

        var bg = new BattlegroundFeature(sp, scopes, NullLoggerFactory.Instance);
        Assert.Throws<InvalidOperationException>(() => _ = bg.Manager);
        Assert.Throws<InvalidOperationException>(() => _ = bg.World);
        Assert.Throws<ArgumentNullException>(() => bg.Attach(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => bg.OnPlayerLoadingAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => bg.OnPlayerLoadingAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));

        Assert.Throws<ArgumentNullException>(() => BattlegroundStrings.Format(null!, "src"));
        Assert.Throws<ArgumentNullException>(() => MatchRuntime.RoutesBannerUse(null!));
    }

    // =========================================================================
    // 5. NPC, PROGRESSION, SKILLS, TALENTS & REPUTATION EXCEPTION / GUARD TESTS
    // =========================================================================

    /// <summary>Verifies null, packet-length, revision, ownership, and disposal guards across Npc subsystems.</summary>
    [Fact]
    public async Task Npc_Guards_ThrowExpectedExceptions()
    {
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using WorldRuntime world = CreateMinimalWorld();

        var cond = new ConditionFeature(sp, scopes, NullLogger<ConditionFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => cond.IsSatisfied(1, null!, null));
        Assert.Throws<ArgumentNullException>(() => cond.Attach(null!));

        var npcServices = new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => npcServices.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => npcServices.Extend(null!, NpcStore.Empty));
        Assert.Throws<ArgumentNullException>(() => npcServices.Extend(new QuestNpcDependencies(), null!));

        var questFeature = new QuestNpcFeature(sp, scopes, NullLogger<QuestNpcFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => questFeature.Attach(null!));
        questFeature.Attach(world);
        Assert.Throws<InvalidOperationException>(() => questFeature.Attach(world));
        Assert.Throws<ArgumentNullException>(() => questFeature.InstallSpellEffects(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => questFeature.OnCharacterDeletingAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => questFeature.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));

        var adapter = new QuestObjectiveAdapter(questFeature.Services);
        Assert.Throws<ArgumentNullException>(() => adapter.Attach((Map)null!));
        Assert.Throws<ArgumentNullException>(() => adapter.Detach(null!));
        Assert.Throws<ArgumentNullException>(() => adapter.Attach((SpellSystem)null!));

        // QuestNpcPersistence guards
        var qp = new QuestNpcPersistence(scopes, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => qp.LoadCharacter(1, null!, 0));
        Assert.Throws<ArgumentNullException>(() => qp.SaveQuests(1, null!));
        Assert.Throws<ArgumentNullException>(() => qp.SaveTaxiMask(1, null!));
        Assert.Throws<ArgumentNullException>(() => qp.AdoptRewarded(null!));
        var nonRewardedStatus = new CharacterQuestStatus(1, 100, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Throws<ArgumentException>(() => qp.AdoptRewarded(nonRewardedStatus));
        Assert.Throws<InvalidOperationException>(() => qp.LoadCharacter(1, CharacterQuestData.Empty, expectedRevision: 99));

        var token1 = new CharacterOwnershipToken("world-a", 1, Guid.NewGuid());
        var token2 = new CharacterOwnershipToken("world-b", 2, Guid.NewGuid());
        qp.LoadCharacter(1, CharacterQuestData.Empty, expectedRevision: 0, ownership: token1);
        Assert.Throws<CharacterOwnershipRejectedException>(() =>
            qp.SaveTaxiMask(1, [1u, 2u], ownership: token2));
        Assert.Throws<InvalidOperationException>(() => qp.CaptureLoadRevision(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => qp.DisposeAsync().AsTask());

        var qpDisposed = new QuestNpcPersistence(scopes, NullLogger.Instance);
        await qpDisposed.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => qpDisposed.Start());
        Assert.Throws<ObjectDisposedException>(() => qpDisposed.CaptureLoadRevision(1));

        // TaxiFlightWriteQueue guards
        var taxiQueue = new TaxiFlightWriteQueue(scopes, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => taxiQueue.Save(1, null!, null));

        using ServiceProvider fenceTaxiSp = CreateMinimalServices(s => s.AddScoped<ICharacterTaxiFlightStore>(_ => new FailingTaxiStore()));
        var fenceTaxiQueue = new TaxiFlightWriteQueue(fenceTaxiSp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        fenceTaxiQueue.Start();
        fenceTaxiQueue.Save(6, new TaxiFlightRoute([1, 2], [1], [100]), abandon: null);
        InvalidOperationException taxiEx = await Assert.ThrowsAsync<InvalidOperationException>(() => fenceTaxiQueue.FlushCharacterAsync(6));
        Assert.IsType<CharacterOwnershipRejectedException>(taxiEx.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fenceTaxiQueue.StopAsync());
    }

    /// <summary>Verifies null, attachment, and persistence failure guards on Progression, Skills, Talents, and Reputation subsystems.</summary>
    [Fact]
    public async Task ProgressionSkillsTalentsReputation_Guards_ThrowExpectedExceptions()
    {
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using WorldRuntime world = CreateMinimalWorld();

        // Progression
        Assert.Throws<ArgumentNullException>(() => new ProgressionFeature(null!, NullLogger<ProgressionFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ProgressionFeature(sp, null!));
        var prog = new ProgressionFeature(sp, NullLogger<ProgressionFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => prog.Attach(null!));
        prog.Attach(world);
        Assert.Throws<InvalidOperationException>(() => prog.Attach(world));
        await Assert.ThrowsAsync<ArgumentNullException>(() => prog.OnPlayerLoadingAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));

        var rewardEffects = new QuestRewardEffects(Dummy<SpellSystem>(), NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => rewardEffects.TryPrepareRewardSpell(null!, default, 1, out _));
        Assert.Throws<ArgumentNullException>(() => rewardEffects.AnnounceLearnedSpells(null!, QuestRewardSpellGrant.None));
        Assert.Throws<ArgumentNullException>(() => rewardEffects.AnnounceLearnedSpells(Dummy<Player>(), null!));
        Assert.Throws<ArgumentNullException>(() => rewardEffects.PublishRewardSpell(null!, Dummy<Quest>(), default, QuestRewardSpellGrant.None));
        Assert.Throws<ArgumentNullException>(() => rewardEffects.PublishRewardSpell(Dummy<Player>(), null!, default, QuestRewardSpellGrant.None));
        Assert.Throws<ArgumentNullException>(() => rewardEffects.PublishRewardSpell(Dummy<Player>(), Dummy<Quest>(), default, null!));

        Assert.Throws<ArgumentNullException>(() => new RestFeature(null!, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>(() => new RestFeature(sp, null!));
        using ServiceProvider restSp = CreateMinimalServices(s => s.AddSingleton(prog));
        var rest = new RestFeature(restSp, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => rest.Attach(null!));
        rest.Attach(world);
        Assert.Throws<InvalidOperationException>(() => rest.Attach(world));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rest.OnCharacterCreatedAsync(null!, SampleCharacterRecord()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rest.OnCharacterCreatedAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rest.OnPlayerLoadedAsync(null!, SampleCharacterRecord(), Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rest.OnPlayerLoadedAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rest.OnPlayerLoadedAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));
        await rest.StopAsync();

        Assert.Throws<ArgumentNullException>(() => RewardGroups.Resolver(null!));
        var taverns = new TavernTriggers();
        Assert.Throws<ArgumentNullException>(() => taverns.Replace(null!));

        // Skills
        Assert.Throws<ArgumentNullException>(() => new SkillSaveCoordinator(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new SkillSaveCoordinator(scopes, null!));
        await using var skillCoord = new SkillSaveCoordinator(scopes, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => skillCoord.Enqueue(1, null!));
        skillCoord.Enqueue(1, new CharacterSkillSnapshot([], []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => skillCoord.FlushCharacterAsync(1, TimeSpan.FromMilliseconds(50)));

        Assert.Throws<ArgumentNullException>(() => new SkillsFeature(null!, scopes, NullLogger<SkillsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SkillsFeature(sp, null!, NullLogger<SkillsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SkillsFeature(sp, scopes, null!));
        var skills = new SkillsFeature(sp, scopes, NullLogger<SkillsFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => skills.Attach(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => skills.OnSpellbookLoadedAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => skills.OnSpellbookLoadedAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));
        Assert.Throws<ArgumentNullException>(() => skills.TryUnlearnSkill(null!, 1));
        await Assert.ThrowsAsync<ArgumentNullException>(() => skills.OnCharacterDeletingAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => skills.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));

        IConfigurationRoot partialSkillsConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Skills:Mode"] = "Retail",
                ["Skills:SkillLineDbcPath"] = "SkillLine.dbc",
            })
            .Build();
        var badSkillsServices = new ServiceCollection();
        badSkillsServices.AddLogging();
        badSkillsServices.AddSingleton<IConfiguration>(partialSkillsConfig);
        using ServiceProvider badSkillsSp = badSkillsServices.BuildServiceProvider();
        var badSkills = new SkillsFeature(badSkillsSp, badSkillsSp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SkillsFeature>.Instance);
        Assert.Throws<InvalidOperationException>(() => badSkills.Attach(world));

        // Talents
        Assert.Throws<ArgumentNullException>(() => new TalentPersistence(scopes, null!));
        Assert.Throws<ArgumentNullException>(() => new TalentFeature(null!, scopes, NullLogger<TalentFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new TalentFeature(sp, scopes, null!));
        var talents = new TalentFeature(sp, scopes, NullLogger<TalentFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => talents.Attach(null!));
        talents.Attach(world);
        Assert.Throws<InvalidOperationException>(() => talents.Attach(world));
        Assert.Throws<ArgumentNullException>(() => talents.ResetTalentsNow(null!));
        Assert.Throws<ArgumentNullException>(() => talents.MarkOnlineForLoginReset(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => talents.WithResetFlagsAsync<int>(null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => talents.WithResetFlagsAsync(_ => Task.FromResult(1)));

        var talentNoScopes = new TalentFeature(sp, null!, NullLogger<TalentFeature>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => talentNoScopes.WithResetFlagsAsync(_ => Task.FromResult(1)));

        var talentDeleteHook = new TalentCharacterDeleteHook(talents);
        await Assert.ThrowsAsync<ArgumentNullException>(() => talentDeleteHook.OnCharacterDeletingAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => talentDeleteHook.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));
        Assert.Throws<ArgumentNullException>(() => TalentResetFlags.Resolve(null!));

        // Reputation
        var rep = new ReputationFeature(sp, scopes, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => rep.Attach(null!));
        rep.Attach(world);
        Assert.Throws<InvalidOperationException>(() => rep.Attach(world));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rep.OnCharacterCreatedAsync(Dummy<WorldSession>(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rep.OnPlayerLoadingAsync(Dummy<WorldSession>(), null!, Dummy<Player>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rep.OnPlayerLoadingAsync(Dummy<WorldSession>(), SampleCharacterRecord(), null!));
        await rep.DisposeAsync();

        var repDelete = new ReputationCharacterDeleteHook(rep);
        await Assert.ThrowsAsync<ArgumentNullException>(() => repDelete.OnCharacterDeletedAsync(Dummy<WorldSession>(), null!));
        var repCombat = new ReputationCombatFeature(sp, NullLogger<ReputationCombatFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => repCombat.Attach(null!));
        var repSpell = new ReputationSpellFeature(sp);
        Assert.Throws<ArgumentNullException>(() => repSpell.Attach(null!));

        var repQueue = new ReputationWriteQueue(scopes, NullLogger<ReputationWriteQueue>.Instance);
        Assert.Throws<ArgumentNullException>(() => repQueue.SaveFactions(1, null!));
    }

    // =========================================================================
    // 6. SERVERMAIL, NAMES, RATES, PERSISTENCE, CLIENTDATA & FEATURES GUARDS
    // =========================================================================

    /// <summary>Verifies null, range, and state guards across ServerMail, Names, Rates, Persistence, ClientData, and Features.</summary>
    [Fact]
    public async Task ServerMailNamesRatesPersistenceClientDataFeatures_Guards_ThrowExpectedExceptions()
    {
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using WorldRuntime world = CreateMinimalWorld();

        // ServerMail
        var autoBroadcast = new AutoBroadcastFeature(sp, NullLogger<AutoBroadcastFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => autoBroadcast.Attach(null!));
        var serverMail = new ServerMailFeature(sp, scopes, NullLogger<ServerMailFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => serverMail.Attach(null!));

        var emptyMailContent = new ServerMailContent([], [], []);
        Assert.Throws<ArgumentNullException>(() => ServerMailRules.Load(null!, Dummy<IServerMailValidation>(), _ => { }));
        Assert.Throws<ArgumentNullException>(() => ServerMailRules.Load(emptyMailContent, null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => ServerMailRules.Load(emptyMailContent, Dummy<IServerMailValidation>(), null!));
        Assert.Throws<ArgumentNullException>(() => ServerMailRules.Check(new ServerMailCondition(ServerMailConditionType.Level, 10, 0), null!));

        // Names
        var nameFeature = new NameCatalogFeature(sp);
        Assert.Throws<ArgumentNullException>(() => nameFeature.ReplaceReservedExact(null!));
        Assert.Throws<ArgumentNullException>(() => nameFeature.ReplaceFilterPatterns(null!));
        Assert.Throws<ArgumentNullException>(() => nameFeature.RestoreCatalog(null!));

        IConfigurationRoot halfNamesConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Names:NamesProfanityDbcPath"] = "NamesProfanity.dbc",
            })
            .Build();
        var halfNamesServices = new ServiceCollection();
        halfNamesServices.AddLogging();
        halfNamesServices.AddSingleton<IConfiguration>(halfNamesConfig);
        using ServiceProvider halfNamesSp = halfNamesServices.BuildServiceProvider();
        var badNameFeature = new NameCatalogFeature(halfNamesSp);
        Assert.Throws<InvalidOperationException>(() => badNameFeature.Attach(world));

        // Rates
        Assert.Throws<ArgumentNullException>(() => ServerRates.For(null!));
        Assert.Throws<ArgumentNullException>(() => ServerRates.Find(null!));
        ServerRates rates = ServerRates.For(world);
        var scope = new RateScope(sp, world, NullLogger.Instance);
        RateDefinition firstRate = ServerRates.All[0];
        Assert.Throws<ArgumentNullException>(() => rates.Describe(scope, null!));
        Assert.Throws<ArgumentNullException>(() => rates.TrySet(scope, null!, 1f, "gm", null, out _));
        Assert.Throws<ArgumentNullException>(() => rates.TrySet(scope, firstRate, 1f, null!, null, out _));
        Assert.Throws<ArgumentNullException>(() => rates.Reset(scope, null!, "gm"));
        Assert.Throws<ArgumentNullException>(() => rates.Reset(scope, firstRate, null!));

        // Persistence: CharacterPersistenceBarrier, CharacterSaveQueue, KeyedStoreWriteQueue, RetainedRetryTimer
        Assert.Throws<ArgumentNullException>(() => new CharacterPersistenceBarrier(null!));
        var barrier = new CharacterPersistenceBarrier(sp);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => barrier.TryFlushCharacterAsync(1, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => barrier.TryFlushCharacterAsync(1, TimeSpan.FromSeconds(-1)));

        Assert.Throws<ArgumentNullException>(() => new CharacterSaveQueue(null!, NullLogger<CharacterSaveQueue>.Instance));
        Assert.Throws<ArgumentNullException>(() => new CharacterSaveQueue(scopes, null!));
        var charSaveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        Assert.Throws<ArgumentNullException>(() => charSaveQueue.Enqueue(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => charSaveQueue.SaveForSettlementAsync(null!));

        charSaveQueue.QuarantineCharacter(42);
        await Assert.ThrowsAsync<InvalidOperationException>(() => charSaveQueue.SaveForSettlementAsync(SampleCharacterState(42)));

        charSaveQueue.FailTerminal(new CharacterOwnershipRejectedException("world-x", 9));
        Assert.Throws<InvalidOperationException>(() => charSaveQueue.HoldCharacter(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => charSaveQueue.FlushCharacterAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => charSaveQueue.StopAsync());

        var stoppedCharQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        await stoppedCharQueue.StopAsync();
        Assert.Throws<ObjectDisposedException>(() => stoppedCharQueue.Start());
        Assert.Throws<ObjectDisposedException>(() => stoppedCharQueue.HoldCharacter(1));

        using ServiceProvider failingKeyedSp = CreateMinimalServices(s => s.AddScoped<ISocialStore>(_ => new FailingSocialStore()));
        var keyedQueue = new KeyedStoreWriteQueue<ISocialStore>(failingKeyedSp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, "test-keyed")
        {
            RetryDelay = TimeSpan.FromMilliseconds(1),
        };
        Assert.Throws<ArgumentNullException>(() => keyedQueue.Save(null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentException>(() => keyedQueue.Save("", _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => keyedQueue.Save("k1", null!));
        keyedQueue.Start();
        keyedQueue.Save("k1", _ => throw new InvalidOperationException("simulated store write failure"));
        await keyedQueue.FlushAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => keyedQueue.RetryRetainedAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => keyedQueue.StopAsync());

        var badTimer = new RetainedRetryTimer(TimeSpan.Zero, () => false, () => true);
        Assert.Throws<ArgumentOutOfRangeException>(() => badTimer.Start());

        // ClientData
        Assert.Throws<ArgumentNullException>(() => ClientDataStartup.Apply(null!));
        var cfgMgr = new ConfigurationManager();
        ClientDataReport report = ClientDataStartup.Apply(cfgMgr);
        Assert.Throws<ArgumentNullException>(() => ClientDataStartup.Log(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => ClientDataStartup.Log(report, null!));

        // Features
        Assert.Throws<ArgumentNullException>(() => FeatureCommands.Format(null!));
        var featureHost = new FeatureHost();
        Assert.Throws<ArgumentNullException>(() => featureHost.AttachAll(null!, []));
        Assert.Throws<ArgumentNullException>(() => featureHost.AttachAll(world, null!));
        featureHost.AttachAll(world, []);
        Assert.Throws<InvalidOperationException>(() => featureHost.AttachAll(world, []));

        var waiverRegistry = new TeleportWaiverRegistry();
        Assert.Throws<ArgumentNullException>(() => waiverRegistry.Register(null!));
        Assert.Throws<ArgumentException>(() => waiverRegistry.Register(waiverRegistry));
        var dummyWaiver = new StubAreaTriggerWaiver();
        using (waiverRegistry.Register(dummyWaiver))
        {
            Assert.Throws<InvalidOperationException>(() => waiverRegistry.Register(dummyWaiver));
        }

        Assert.Throws<ArgumentNullException>(() => WorldCodeAssemblies.RegisterBuiltIn(null!));
        var collectibleAsm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CollectibleTestAsm"), AssemblyBuilderAccess.RunAndCollect);
        Assert.Throws<ArgumentException>(() => WorldCodeAssemblies.RegisterBuiltIn(collectibleAsm));
        Assert.Throws<ArgumentException>(() => WorldCodeAssemblies.LoadBuiltInPack(""));
        Assert.Throws<ArgumentException>(() => WorldCodeAssemblies.LoadBuiltInPack("   "));
        Assert.Throws<FileNotFoundException>(() => WorldCodeAssemblies.LoadBuiltInPack("NonExistentPack_" + Guid.NewGuid().ToString("N")));

        Assert.Throws<ArgumentNullException>(() => WorldFeatures.DiscoverFeatureTypes(null!));

        var hooks = new WorldLifecycleHooks();
        Assert.Throws<ArgumentException>(() => hooks.Register(WorldLifecycleStage.BeforeWorldStop, "", _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => hooks.Register(WorldLifecycleStage.BeforeWorldStop, "h1", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => hooks.Register((WorldLifecycleStage)999, "h1", _ => Task.CompletedTask));
        using (hooks.Register(WorldLifecycleStage.BeforeWorldStop, "dup-hook", _ => Task.CompletedTask))
        {
            Assert.Throws<InvalidOperationException>(() => hooks.Register(WorldLifecycleStage.BeforeWorldStop, "dup-hook", _ => Task.CompletedTask));
        }

        using (hooks.Register(WorldLifecycleStage.BeforeWorldStop, "failing-hook", _ => Task.FromException(new InvalidOperationException("boom"))))
        {
            await Assert.ThrowsAsync<AggregateException>(() => hooks.RunAsync(WorldLifecycleStage.BeforeWorldStop));
        }
    }

    // =========================================================================
    // 7. DETERMINISTIC SEEDED FUZZ TESTS
    // =========================================================================

    /// <summary>
    /// Fuzzes GM command argument tokenization, command table lookup/dispatch, duration parsers,
    /// and player name extraction under random/malformed strings.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_GmCommands_TokenizationDispatchAndParsers_NeverThrowUnexpectedly(int seed)
    {
        var rng = new Random(seed);
        var leafCmd = new ChatCommand("lookup", AccountSecurity.GameMaster, "lookup help", (_, rawArgs) =>
        {
            var cursor = new CommandArgs(rawArgs);
            while (!cursor.IsEmpty)
            {
                if (cursor.ExtractArg() is null)
                {
                    break;
                }
            }
            return true;
        });
        var groupCmd = new ChatCommand("gm", AccountSecurity.GameMaster, "gm group", Children: [leafCmd]);
        var table = new CommandTable([leafCmd, groupCmd]);
        WorldSession dummySession = Dummy<WorldSession>();
        var replies = new List<string>();
        CommandContext ctx = CommandContext.ForConsole(dummySession, table, replies.Add);

        char[] alphabet = "abcxyz0123456789 .!@#$%^&*()-_=+[]{}|;:'\",.<>?/\\~`\t\r\n\0dhmsDHMS".ToCharArray();

        for (int i = 0; i < 300; i++)
        {
            int len = rng.Next(0, 128);
            var sb = new StringBuilder(len);
            for (int j = 0; j < len; j++)
            {
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }
            string input = sb.ToString();

            // CommandArgs cursor operations
            var args = new CommandArgs(input);
            _ = args.ExtractLiteral("lookup");
            _ = args.ExtractUInt32(out _);
            _ = args.ExtractInt32(out _);
            _ = args.ExtractFloat(out _);
            _ = args.ExtractOnOff(out _);
            _ = args.ExtractQuotedOrLiteral();
            _ = args.ExtractLink();
            while (!args.IsEmpty)
            {
                if (args.ExtractArg() is null)
                {
                    break;
                }
            }

            // CommandTable & CommandListing
            table.Execute(ctx, input);
            _ = CommandListing.Filtered(table, AccountSecurity.Administrator, input, 1);
            _ = CommandListing.ParseArguments(input);
            _ = CommandListing.Pack([input]);

            // Duration & mute duration parsers
            uint parsedSecs = GmDuration.TimeStringToSecs(input);
            string formattedSecs = GmDuration.SecsToTimeString(parsedSecs, shortText: (i & 1) == 0, hoursOnly: (i & 2) == 0);
            Assert.NotNull(formattedSecs);
            _ = GmMuteDuration.TryParse(input, out _);
        }
    }

    /// <summary>
    /// Fuzzes character name normalization, UTF-8 byte validation, character creation name rules,
    /// and NameCatalog matching with random control characters, non-ASCII, mixed case, and apostrophes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_CharacterNames_ValidationAndCatalog_MaintainInvariants(int seed)
    {
        var rng = new Random(seed);
        var catalog = new NameCatalog(
            profane: [new Regex("badword", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)],
            reserved: [new Regex("^gm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)],
            sources: [],
            reservedExact: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thrall", "Sylvanas" });

        byte[] byteBuf = new byte[64];
        for (int i = 0; i < 400; i++)
        {
            int len = rng.Next(0, 32);
            Span<byte> slice = byteBuf.AsSpan(0, len);
            rng.NextBytes(slice);

            // Occasionally inject near-valid ASCII letters with random casing
            if (i % 2 == 0 && len > 0)
            {
                for (int j = 0; j < len; j++)
                {
                    int pick = rng.Next(10);
                    slice[j] = pick switch
                    {
                        < 4 => (byte)('a' + rng.Next(26)),
                        < 8 => (byte)('A' + rng.Next(26)),
                        8 => (byte)'\'',
                        _ => (byte)rng.Next(0, 128),
                    };
                }
            }

            CharResult? utf8Res = CharacterNames.ValidateUtf8(slice, out string? normalizedFromUtf8);
            string? norm2 = CharacterNames.NormalizeUtf8(slice);

            if (utf8Res is null)
            {
                Assert.NotNull(normalizedFromUtf8);
                Assert.Equal(normalizedFromUtf8, norm2);
                Assert.InRange(normalizedFromUtf8!.Length, CharacterNames.MinLength, CharacterNames.MaxLength);
            }

            string decoded = Encoding.UTF8.GetString(slice);
            string normStr = CharacterNames.Normalize(decoded);
            _ = CharacterNameRules.Check(normStr, NameRuleSettings.Default);
            _ = catalog.Check(normStr);
        }
    }

    /// <summary>
    /// Fuzzes chat message sanitization, link payload validation, profanity word filtering,
    /// time formatting, and GM ticket text cleaning across random byte/char sequences.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_ChatAndTicketSanitization_NeverThrowAndPreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        var wordFilter = new ChatWordFilter(
        [
            new ChatWordFilterRule(1, "murloc", ChatWordFilterScope.Chat, ChatWordFilterAction.Censor),
            new ChatWordFilterRule(2, "forbidden", ChatWordFilterScope.Chat, ChatWordFilterAction.Block),
        ]);

        string[] snippets =
        [
            "|cffff0000|Hitem:19019:0:0:0|h[Thunderfury]|h|r",
            "|Hquest:123:60|h[A Quest]|h",
            "|Hspell:456|h[Spell]|h",
            "|c", "|r", "|H", "|h", "||",
            "\t", "\a", "\n", "\0", "\r\n",
            "murloc", "forbidden", "hello world",
        ];

        for (int i = 0; i < 300; i++)
        {
            int partCount = rng.Next(0, 10);
            var sb = new StringBuilder();
            for (int p = 0; p < partCount; p++)
            {
                if (rng.Next(2) == 0)
                {
                    sb.Append(snippets[rng.Next(snippets.Length)]);
                }
                else
                {
                    sb.Append((char)rng.Next(0, 0x3000));
                }
            }
            string msg = sb.ToString();

            string stripped = ChatSanitizer.StripInvisibleChars(msg);
            Assert.DoesNotContain('\t', stripped);
            Assert.DoesNotContain('\a', stripped);
            Assert.DoesNotContain('\n', stripped);

            bool validSev2 = ChatSanitizer.IsValidChatMessage(stripped, severity: 2);
            bool validSev1 = ChatSanitizer.IsValidChatMessage(stripped, severity: 1);
            if (validSev2)
            {
                Assert.True(validSev1);
            }

            _ = ChatSanitizer.IsValidChatMessage(
                stripped,
                severity: 3,
                linkExists: (type, id) => (type == "item" && id == 19019) || (type == "quest" && id == 123) || (type == "spell" && id == 456));

            _ = wordFilter.Apply(stripped);
            _ = GmTicketHandlers.CleanText(msg);

            long secs = rng.NextInt64(0, 10_000_000);
            string timeStr = ChatText.SecsToTimeString(secs);
            Assert.NotNull(timeStr);
        }
    }

    /// <summary>
    /// Fuzzes packet length guards across NpcServiceHandlers, QuestNpcInteractionHandlers,
    /// QuestNpcQueryHandlers, QuestShareHandlers, TalentHandlers, TicketPackets, and BattlegroundStrings.Format.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_WorldPacketHandlersAndFormatters_RejectMalformedPayloadsCleanly(int seed)
    {
        var rng = new Random(seed);
        var table = new OpcodeTable();
        new NpcServiceHandlers().Register(table);
        new QuestNpcInteractionHandlers().Register(table);
        new QuestNpcQueryHandlers().Register(table);
        new QuestShareHandlers().Register(table);
        new TalentHandlers().Register(table);

        WorldOpcode[] worldOpcodes =
        [
            WorldOpcode.CmsgGossipHello,
            WorldOpcode.CmsgGossipSelectOption,
            WorldOpcode.CmsgListInventory,
            WorldOpcode.CmsgSellItem,
            WorldOpcode.CmsgBuybackItem,
            WorldOpcode.CmsgTrainerList,
            WorldOpcode.CmsgTrainerBuySpell,
            WorldOpcode.CmsgBinderActivate,
            WorldOpcode.CmsgBankerActivate,
            WorldOpcode.CmsgBuyBankSlot,
            WorldOpcode.CmsgTaxinodeStatusQuery,
            WorldOpcode.CmsgTaxiqueryavailablenodes,
            WorldOpcode.CmsgActivatetaxi,
            WorldOpcode.CmsgActivatetaxiexpress,
            WorldOpcode.CmsgQuestgiverStatusQuery,
            WorldOpcode.CmsgQuestgiverHello,
            WorldOpcode.CmsgQuestgiverQueryQuest,
            WorldOpcode.CmsgQuestgiverAcceptQuest,
            WorldOpcode.CmsgQuestgiverCompleteQuest,
            WorldOpcode.CmsgQuestgiverRequestReward,
            WorldOpcode.CmsgQuestgiverChooseReward,
            WorldOpcode.CmsgQuestlogRemoveQuest,
            WorldOpcode.CmsgPushquesttoparty,
            WorldOpcode.MsgQuestPushResult,
            WorldOpcode.CmsgLearnTalent,
            WorldOpcode.MsgTalentWipeConfirm,
        ];

        for (int i = 0; i < 200; i++)
        {
            WorldOpcode op = worldOpcodes[rng.Next(worldOpcodes.Length)];
            Assert.True(table.TryGet(op, out OpcodeHandler? handler));
            Assert.NotNull(handler.World);

            // Pick a length that does not match any valid fixed length (valid lengths are 1, 4, 8, 9, 12, 14, 16, 17, 22)
            int[] invalidLengths = [0, 2, 3, 5, 6, 7, 10, 11, 13, 15, 18, 19, 25];
            int badLen = invalidLengths[rng.Next(invalidLengths.Length)];
            byte[] payload = new byte[badLen];
            rng.NextBytes(payload);

            // For CmsgGossipSelectOption with length >= 12, ensure it's unterminated so it throws ArgumentOutOfRangeException
            if (op == WorldOpcode.CmsgGossipSelectOption && badLen >= 12)
            {
                for (int b = 12; b < badLen; b++)
                {
                    if (payload[b] == 0) payload[b] = 0x41;
                }
            }
            // For CmsgActivateTaxiExpress with length >= 16, set node count to 9999 so it throws ArgumentOutOfRangeException
            if (op == WorldOpcode.CmsgActivatetaxiexpress && badLen >= 16)
            {
                payload[12] = 0xFF;
                payload[13] = 0xFF;
            }

            Exception? ex = Record.Exception(() =>
                handler.World!(Dummy<WorldSession>(), Dummy<Player>(), payload));
            Assert.NotNull(ex);
            Assert.True(ex is ArgumentOutOfRangeException or ArgumentNullException);
        }

        // Session handlers: CmsgQuestQuery (expects 4) & CmsgNpcTextQuery (expects 12)
        Assert.True(table.TryGet(WorldOpcode.CmsgQuestQuery, out OpcodeHandler? questQuery));
        Assert.True(table.TryGet(WorldOpcode.CmsgNpcTextQuery, out OpcodeHandler? npcTextQuery));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => questQuery.Session!(Dummy<WorldSession>(), [1, 2, 3]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => npcTextQuery.Session!(Dummy<WorldSession>(), [1, 2, 3, 4, 5]));

        // BattlegroundStrings.Format fuzzing with arbitrary %s / %u / $n / %z tokens
        for (int i = 0; i < 100; i++)
        {
            string tpl = $"$n captured %s (%u) %% %x ${rng.Next(100)}";
            string formatted = BattlegroundStrings.Format(tpl, i % 2 == 0 ? "PlayerOne" : null, "GoldMine", 42u);
            Assert.NotNull(formatted);
        }

        byte[] ticketFrame = TicketPackets.BuildSystemStatus((uint)rng.Next());
        Assert.Equal(4, ticketFrame.Length);
    }

    /// <summary>
    /// Fuzzes persistence write queues (TalentPersistence, RestWriteQueue, ReputationWriteQueue)
    /// under randomized sequence of saves, deletes, forgets, and flushes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_PersistenceWriteQueues_RandomizedMutationsAndFlushes_ConvergeCleanly(int seed)
    {
        var rng = new Random(seed);
        using ServiceProvider sp = CreateMinimalServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        var talentPersistence = new TalentPersistence(scopes, NullLogger.Instance);
        var restQueue = new RestWriteQueue(scopes, NullLogger<RestWriteQueue>.Instance);
        var repQueue = new ReputationWriteQueue(scopes, NullLogger<ReputationWriteQueue>.Instance);
        restQueue.Start();
        repQueue.Start();

        for (int i = 0; i < 100; i++)
        {
            int charId = rng.Next(1, 8);
            int op = rng.Next(6);
            switch (op)
            {
                case 0:
                    talentPersistence.SaveRespec(charId, new RespecState((uint)rng.Next(0, 10), rng.NextInt64(0, 100_000)));
                    break;
                case 1:
                    talentPersistence.SetDisabled(charId, (uint)rng.Next(1, 5), disabled: (i & 1) == 0);
                    break;
                case 2:
                    talentPersistence.DeleteCharacter(charId);
                    break;
                case 3:
                    restQueue.Save(charId, new CharacterRestState(rng.Next(0, 5000), rng.NextInt64(0, 100_000), (i & 1) == 0));
                    break;
                case 4:
                    restQueue.Forget(charId);
                    break;
                case 5:
                    repQueue.SaveFactions(charId, [new CharacterReputationRow(charId, (uint)rng.Next(1, 20), rng.Next(-1000, 1000), 1u)]);
                    break;
            }
        }

        for (int c = 1; c < 8; c++)
        {
            await talentPersistence.FlushCharacterAsync(c);
            await restQueue.FlushCharacterAsync(c);
            await repQueue.FlushCharacterAsync(c);
        }

        await talentPersistence.DisposeAsync();
        await restQueue.StopAsync();
        await repQueue.StopAsync();
    }

    // =========================================================================
    // HELPER STUBS FOR FAILURE INJECTION
    // =========================================================================

    private sealed class StubAreaTriggerWaiver : IAreaTriggerWaiver
    {
        public AreaTriggerWaivers Waives(Player player, AreaTriggerTeleport teleport) => AreaTriggerWaivers.None;
    }

    private sealed class FailingSocialStore : ISocialStore
    {
        private static Exception Fail() => new InvalidOperationException("simulated social db failure");

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => throw Fail();
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => throw Fail();
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => throw Fail();
    }

    private sealed class FailingHonorStore : IHonorStore
    {
        private static Exception Fail() => new CharacterOwnershipRejectedException("world-fence", 1);

        public Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
        public Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default) => throw Fail();
        public Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default) => throw Fail();
        public Task ResetAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default) => throw Fail();
        public Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default) => throw Fail();
        public Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default) => throw Fail();
    }

    private sealed class FailingTaxiStore : ICharacterTaxiFlightStore
    {
        private static Exception Fail() => new CharacterOwnershipRejectedException("world-fence", 1);

        public Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
        public Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default) => throw Fail();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => throw Fail();
    }
}

using System.Numerics;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Combat.Duel;
using ArcaneCore.World.Spells.Casters;
using ArcaneCore.World.Spells.Mods;
using ArcaneCore.World.Spells.Modules;
using ArcaneCore.World.Spells.Procs;
using ArcaneCore.World.Spells.Totems;
using ArcaneCore.World.Spells.Utility;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Handlers;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Collision;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Death;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Graveyards;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Locomotion;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Ranged;
using ArcaneCore.World.Scripting;
using ArcaneCore.World.Scripting.Modules.DuelReset;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Stats;
using ArcaneCore.World.Stealth;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Transports;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// Exception-path and deterministic seeded fuzz tests across the 18 owned <c>ArcaneCore.World</c>
/// gameplay slices: <c>Spells</c>, <c>Combat</c>, <c>Creatures</c>, <c>Pets</c>, <c>Locomotion</c>,
/// <c>Transports</c>, <c>Collision</c>, <c>Instances</c>, <c>GameObjects</c>, <c>Scripting</c>,
/// <c>Death</c>, <c>Ranged</c>, <c>Stealth</c>, <c>Stats</c>, <c>Graveyards</c>, <c>Teleport</c>,
/// <c>OutdoorPvP</c>, and <c>WorldState</c>.
/// </summary>
public sealed class WorldCombatSpellsMovementFuzzAndExceptionTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>Deterministic fuzz seeds required by the test harness.</summary>
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    /// <summary>
    /// Verifies that spell world features validate null arguments and throw <c>InvalidOperationException</c>
    /// when a required combo-point service is absent during attachment.
    /// </summary>
    [Fact]
    public async Task Spells_Features_AttachNullAndMissingComboDependency_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);

        SpellFeature spellFeature = sp.GetRequiredService<SpellFeature>();
        var petsFeature = new PetsFeature(sp, NullLogger<PetsFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => new CastCheckFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new CasterFeature(spellFeature, sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new DruidScriptFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new FormMountCheckFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new LiquidInterruptFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new PassiveFormGateFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellProcFeature(sp, NullLogger<SpellProcFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new RogueScriptFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellRulesAuraEngineFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellRulesCcFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellRulesCoreFeature(sp, NullLogger<SpellRulesCoreFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new TotemFeature(sp, NullLogger<TotemFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new PetTargetFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellScriptFeature(sp, NullLogger<SpellScriptFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new WarlockDemonFeature(spellFeature, petsFeature).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => WorldCorpseSpellTargetResolver.Instance.ResolveCorpse(null!, default));

        // DruidScriptFeature and RogueScriptFeature require ComboFeature.Service to be attached first.
        Assert.Throws<InvalidOperationException>(() => new DruidScriptFeature(sp).Attach(world));
        Assert.Throws<InvalidOperationException>(() => new RogueScriptFeature(sp).Attach(world));
    }

    /// <summary>
    /// Verifies that <c>FileClassMaskSource</c> rejects null/whitespace paths and malformed, out-of-range,
    /// or duplicate class-mask rows.
    /// </summary>
    [Fact]
    public void FileClassMaskSource_InvalidPathsAndLines_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => FileClassMaskSource.Load(null!));
        Assert.Throws<ArgumentException>(() => FileClassMaskSource.Load(string.Empty));
        Assert.Throws<ArgumentException>(() => FileClassMaskSource.Load("   "));
        Assert.Throws<ArgumentNullException>(() => FileClassMaskSource.Parse(null!, "test"));

        Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["100 0"], "too-few-columns"));
        Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["not_a_uint 0 0x1"], "bad-spell-id"));
        Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["100 3 0x1"], "effect-index-out-of-range"));
        Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["100 0 not_hex"], "bad-mask"));
        Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["100 0 0x1", "100 0 0x2"], "duplicate-entry"));
    }

    /// <summary>
    /// Verifies that <c>SpellStoreFactory</c> rejects null content and null lookup dictionaries.
    /// </summary>
    [Fact]
    public void SpellStoreFactory_NullArguments_ThrowArgumentNullException()
    {
        var row = new SpellTemplateRow { Id = 1 };
        var castTimes = new Dictionary<uint, SpellCastTimeRow>();
        var durations = new Dictionary<uint, SpellDurationRow>();
        var ranges = new Dictionary<uint, SpellRangeRow>();
        var radii = new Dictionary<uint, SpellRadiusRow>();

        Assert.Throws<ArgumentNullException>(() => SpellStoreFactory.Build(null!));
        Assert.Throws<ArgumentNullException>(() => SpellStoreFactory.ToSpellInfo(null!, castTimes, durations, ranges, radii));
        Assert.Throws<ArgumentNullException>(() => SpellStoreFactory.ToSpellInfo(row, null!, durations, ranges, radii));
        Assert.Throws<ArgumentNullException>(() => SpellStoreFactory.ToSpellInfo(row, castTimes, null!, ranges, radii));
        Assert.Throws<ArgumentNullException>(() => SpellStoreFactory.ToSpellInfo(row, castTimes, durations, null!, radii));
        Assert.Throws<ArgumentNullException>(() => SpellStoreFactory.ToSpellInfo(row, castTimes, durations, ranges, null!));
    }

    /// <summary>
    /// Verifies that <c>SpellFeature</c>, <c>SpellModFeature</c>, <c>SpellCharacterDeleteHook</c>,
    /// and <c>WorldSpellGroups</c> enforce their null guards.
    /// </summary>
    [Fact]
    public async Task SpellFeature_And_SpellModFeature_And_WorldSpellGroups_NullGuards_ThrowArgumentNullException()
    {
        await using ServiceProvider sp = CreateServices();
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentNullException>(() => new SpellFeature(null!, NullLogger<SpellFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new SpellFeature(scopes, null!));

        SpellFeature spellFeature = sp.GetRequiredService<SpellFeature>();
        Assert.Throws<ArgumentNullException>(() => spellFeature.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => spellFeature.BuildInitialSpells(null!));

        Assert.Throws<ArgumentNullException>(() => SpellModFeature.BindOptions(null, null!));
        Assert.Throws<ArgumentNullException>(() => new SpellModFeature(sp, NullLogger<SpellModFeature>.Instance).Attach(null!));

        var deleteHook = new SpellCharacterDeleteHook(spellFeature);
        await Assert.ThrowsAsync<ArgumentNullException>(() => deleteHook.OnCharacterDeletingAsync(null!, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => deleteHook.OnCharacterDeletedAsync(null!, null!));

        var groups = new WorldSpellGroups(static () => null);
        Assert.Throws<ArgumentNullException>(() => groups.GetGroupMembers(null!, false));
    }

    /// <summary>
    /// Verifies that <c>StanceFeature</c> enforces null guards and fails closed when
    /// <c>RequireShapeshiftFormDbc</c> is enabled without a valid DBC path.
    /// </summary>
    [Fact]
    public async Task StanceFeature_MissingRequiredShapeshiftDbc_ThrowsInvalidOperationException()
    {
        await using ServiceProvider sp = CreateServices(
            ("Combat:RequireShapeshiftFormDbc", "true"),
            ("Combat:ShapeshiftFormDbcPath", ""));
        using WorldRuntime world = CreateWorld(sp);

        var stance = new StanceFeature(sp, NullLogger<StanceFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => stance.AddFormChangeListener(null!));
        Assert.Throws<ArgumentNullException>(() => stance.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => stance.Attach(world));
    }

    /// <summary>
    /// Verifies that <c>SpellbookCache</c> enforces null guards, rejects flushes after disposal,
    /// and faults when the character ownership fence or backing store fails.
    /// </summary>
    [Fact]
    public async Task SpellbookCache_NullGuards_DisposedFlush_And_OwnershipFence_ThrowExpectedExceptions()
    {
        var store = new FaultableSpellStore();
        await using ServiceProvider sp = CreateServices(configure: s => s.AddSingleton<ICharacterSpellStore>(store));
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentNullException>(() => new SpellbookCache(scopes, null!));

        var cache = new SpellbookCache(scopes, NullLogger<SpellbookCache>.Instance);
        Player player = CreatePlayer(1);

        Assert.Throws<ArgumentNullException>(() => cache.Load(null!));
        Assert.Throws<ArgumentNullException>(() => cache.LoadCharacter(1, null!));
        Assert.Throws<ArgumentNullException>(() => SpellbookCache.CharacterId(null!));
        Assert.Throws<ArgumentNullException>(() => cache.AdoptCommitted(1, null!));
        Assert.Throws<ArgumentNullException>(() => cache.EnsureDefaults(player, null!));

        cache.Start();
        cache.LoadCharacter(1, []);
        store.RejectOwnership = true;
        Assert.True(cache.LearnSpell(player, 133));
        await cache.FlushAsync().WaitAsync(WaitBudget);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.FlushCharacterAsync(1).WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.DisposeAsync().AsTask().WaitAsync(WaitBudget));

        // A freshly disposed cache refuses FlushCharacterAsync.
        await using ServiceProvider cleanSp = CreateServices();
        var cleanCache = new SpellbookCache(cleanSp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellbookCache>.Instance);
        cleanCache.Start();
        await cleanCache.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanCache.FlushCharacterAsync(1));
    }

    /// <summary>
    /// Verifies that <c>SpellStatePersistence</c> enforces null guards and faults when a retained save
    /// or ownership rejection blocks character state loading or durability flushing.
    /// </summary>
    [Fact]
    public async Task SpellStatePersistence_NullGuards_And_OwnershipFence_ThrowExpectedExceptions()
    {
        var store = new FaultableSpellStateStore();
        await using ServiceProvider sp = CreateServices(configure: s => s.AddSingleton<ICharacterSpellStateStore>(store));
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentNullException>(() => new SpellStatePersistence(scopes, null!));

        var persistence = new SpellStatePersistence(scopes, NullLogger<SpellStatePersistence>.Instance);
        Assert.Throws<ArgumentNullException>(() => persistence.Save(1, null!));
        Assert.Throws<ArgumentNullException>(() => SpellStatePersistence.ToRows(null!));
        Assert.Throws<ArgumentNullException>(() => SpellStatePersistence.FromRows(null!));

        store.FailSaves = true;
        persistence.Save(7, new SpellStateSnapshot([], []));
        await persistence.FlushAsync().WaitAsync(WaitBudget);
        Assert.True(persistence.HasUnsaved(7));

        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.FlushCharacterDurableAsync(7).WaitAsync(WaitBudget));

        // Recover and verify FlushCharacterDurableAsync clears the unsaved snapshot.
        store.FailSaves = false;
        await persistence.FlushCharacterDurableAsync(7).WaitAsync(WaitBudget);
        Assert.False(persistence.HasUnsaved(7));

        // Ownership rejection permanently fences the persistence queue.
        var fencedStore = new FaultableSpellStateStore { RejectOwnership = true };
        await using ServiceProvider fencedSp = CreateServices(configure: s => s.AddSingleton<ICharacterSpellStateStore>(fencedStore));
        var fenced = new SpellStatePersistence(fencedSp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellStatePersistence>.Instance);
        fenced.Save(8, new SpellStateSnapshot([], []));
        await fenced.FlushAsync().WaitAsync(WaitBudget);
        Assert.NotNull(fenced.TerminalFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fenced.LoadAsync(8, fencedStore).WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fenced.FlushCharacterDurableAsync(8).WaitAsync(WaitBudget));
    }

    /// <summary>
    /// Verifies that combat features enforce null guards, reject duplicate attachment where prohibited,
    /// and fail closed when <c>ReactiveFeature</c> is invoked without an attached <c>ComboFeature</c>.
    /// </summary>
    [Fact]
    public async Task Combat_Features_NullGuards_DuplicateAttach_And_MissingComboService_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);

        Assert.Throws<ArgumentNullException>(() => CombatEnvironments.GetOrCreate(null!, world, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => CombatEnvironments.GetOrCreate(sp, null!, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => new ComboFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new MeleeSpellFeature(sp, NullLogger<MeleeSpellFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new PowerFeature(sp, NullLogger<PowerFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellThreatFeature(sp, NullLogger<SpellThreatFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new ThreatFeature(sp).Attach(null!));

        Assert.Throws<ArgumentNullException>(() => new DuelFeature(null!, NullLogger<DuelFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DuelFeature(sp, null!));
        var duel = new DuelFeature(sp, NullLogger<DuelFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => duel.Attach(null!));
        duel.Attach(world);
        Assert.Throws<InvalidOperationException>(() => duel.Attach(world));

        var reactive = new ReactiveFeature(sp);
        Assert.Throws<ArgumentNullException>(() => reactive.Attach(null!));
        reactive.Attach(world);
        Player attacker = CreatePlayer(1);
        Player victim = CreatePlayer(2);
        Assert.Throws<InvalidOperationException>(() => reactive.Reactives!.OnAttackAvoided(attacker, victim, AttackAvoidance.Dodge));
    }

    /// <summary>
    /// Verifies that death, ranged, and stealth features enforce null guards and reject duplicate attachment.
    /// </summary>
    [Fact]
    public async Task Death_Ranged_Stealth_Features_NullGuards_And_DuplicateAttach_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);

        Assert.Throws<ArgumentNullException>(() => new DeathFeature(null!, NullLogger<DeathFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new DeathFeature(sp, null!));
        var death = new DeathFeature(sp, NullLogger<DeathFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => death.Attach(null!));
        death.Attach(world);
        Assert.Throws<InvalidOperationException>(() => death.Attach(world));

        Assert.Throws<ArgumentNullException>(() => new GhostFormFeature(sp, NullLogger<GhostFormFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new GhostVisibilityFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new ResurrectionFeature(sp).Attach(null!));

        using var selfRes = new SelfResurrectionFeature();
        Assert.Throws<ArgumentNullException>(() => selfRes.Attach(null!));
        selfRes.Attach(world);
        Assert.Throws<InvalidOperationException>(() => selfRes.Attach(world));

        Assert.Throws<ArgumentNullException>(() => new RangedFeature(null!, NullLogger<RangedFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RangedFeature(sp, null!));
        Assert.Throws<ArgumentNullException>(() => new RangedFeature(sp, NullLogger<RangedFeature>.Instance).Attach(null!));

        Assert.Throws<ArgumentNullException>(() => new StealthFeature(sp.GetRequiredService<SpellFeature>(), sp).Attach(null!));
    }

    /// <summary>
    /// Verifies that <c>FormStatFeature</c> and <c>StatsFeature</c> enforce null guards, reject duplicate
    /// attachment, and fail closed when <c>Stats:RequireImportedData</c> is enabled without complete rows.
    /// </summary>
    [Fact]
    public async Task Stats_Features_NullGuards_DuplicateAttach_And_IncompleteImportedData_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices(("Stats:RequireImportedData", "true"));
        using WorldRuntime world = CreateWorld(sp);
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentNullException>(() => new FormStatFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new StatsFeature(null!, scopes, NullLogger<StatsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new StatsFeature(sp, null!, NullLogger<StatsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new StatsFeature(sp, scopes, null!));

        var stats = new StatsFeature(sp, scopes, NullLogger<StatsFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => stats.Attach(null!));
        stats.Attach(world);
        Assert.Throws<InvalidOperationException>(() => stats.Attach(world));

        // With RequireImportedData=true and no IPlayerStatsDataStore registered, EnsureLoadedAsync throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() => stats.EnsureLoadedAsync());

        // With an incomplete store registered, EnsureLoadedAsync also throws InvalidOperationException.
        await using ServiceProvider incompleteSp = CreateServices(
            configure: s => s.AddSingleton<IPlayerStatsContentStore>(new EmptyPlayerStatsStore()),
            ("Stats:RequireImportedData", "true"));
        var incompleteStats = new StatsFeature(
            incompleteSp,
            incompleteSp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StatsFeature>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => incompleteStats.EnsureLoadedAsync());
    }

    /// <summary>
    /// Verifies that creature features validate null arguments, reject unattached map lookups,
    /// and refuse half-configured display model DBC paths.
    /// </summary>
    [Fact]
    public async Task Creatures_Features_NullGuards_UnattachedSystem_And_PartialDisplayDbc_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);
        Player player = CreatePlayer(1);
        var quests = new QuestNpcServices(
            new QuestStore(QuestContent.Empty),
            NpcStore.Empty,
            new QuestNpcDependencies(),
            new QuestNpcOptions(),
            new NullQuestNpcSink(),
            () => 0,
            NullLogger.Instance);

        var engineTrigger = new AreaTriggerEngineScriptsFeature(sp);
        Assert.Throws<ArgumentNullException>(() => engineTrigger.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => engineTrigger.OnAreaTrigger(null!, 1));
        Assert.Throws<ArgumentNullException>(() => AreaTriggerEngineScriptsFeature.Run(null!, player, 1));
        Assert.Throws<ArgumentNullException>(() => AreaTriggerEngineScriptsFeature.Run(quests, null!, 1));

        var scriptsTrigger = new AreaTriggerScriptsFeature(sp);
        Assert.Throws<ArgumentNullException>(() => scriptsTrigger.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => scriptsTrigger.OnAreaTrigger(null!, 1));
        Assert.Throws<ArgumentNullException>(() => AreaTriggerScriptsFeature.Run(null!, player, 1));
        Assert.Throws<ArgumentNullException>(() => AreaTriggerScriptsFeature.Run(quests, null!, 1));

        Assert.Throws<ArgumentNullException>(() => CreatureAiServicesBinder.Build(null!, new CreatureOptions()));
        Assert.Throws<ArgumentNullException>(() => CreatureAiServicesBinder.Build(sp, null!));

        Assert.Throws<ArgumentNullException>(() => new SmartAreaTriggerFeature(sp).Attach(null!));
        var eventAiQuests = new EventAiQuestEvents(sp);
        var creature = new Creature(
            990001,
            new CreatureTemplate { Entry = 1, Name = "Wolf" },
            null,
            CreatureContent.Empty,
            new Random(1));
        Assert.Throws<ArgumentNullException>(() => eventAiQuests.EventHappened(null!, 1, creature, false));
        Assert.Throws<ArgumentNullException>(() => eventAiQuests.EventHappened(player, 1, null!, false));
        Assert.Throws<ArgumentNullException>(() => eventAiQuests.KillCredit(null!, 1, creature));
        Assert.Throws<ArgumentNullException>(() => eventAiQuests.KillCredit(player, 1, null!));

        var creatureWorld = new CreatureWorldFeature(sp, NullLogger<CreatureWorldFeature>.Instance);
        Assert.ThrowsAny<Exception>(() => creatureWorld.Install(null!));
        Assert.Throws<ArgumentNullException>(() => creatureWorld.Install(CreatureContent.Empty, null!));
        Assert.Throws<InvalidOperationException>(() => creatureWorld.GetOrCreateSystem(0u));
        Assert.Throws<InvalidOperationException>(() => creatureWorld.GetOrCreateSystem(world.GetMap(0)));

        // Setting only one of CreatureDisplayInfoDbcPath / CreatureModelDataDbcPath throws InvalidOperationException.
        await using ServiceProvider partialDbcSp = CreateServices(("Creatures:CreatureDisplayInfoDbcPath", "only-one.dbc"));
        var displayFeature = new CreatureDisplayModelMetadataFeature(partialDbcSp, NullLogger<CreatureDisplayModelMetadataFeature>.Instance);
        Assert.Throws<InvalidOperationException>(() => displayFeature.Attach(world));
    }

    /// <summary>
    /// Verifies that <c>CreatureRespawnQueue</c> rejects null initial records and throws
    /// <c>InvalidOperationException</c> on shutdown when retained writes cannot drain.
    /// </summary>
    [Fact]
    public async Task CreatureRespawnQueue_NullInitialAndUndrainedRetainedShutdown_ThrowExpectedExceptions()
    {
        var store = new FaultableCreatureRespawnStore { AlwaysFail = true };
        await using ServiceProvider sp = CreateServices(configure: s => s.AddSingleton<ICreatureRespawnStore>(store));
        var queue = new CreatureRespawnQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CreatureRespawnQueue>.Instance);

        Assert.Throws<ArgumentNullException>(() => queue.LoadInitial(null!));

        queue.Start();
        queue.Save(0, 0, 101, 1_700_000_500);
        await queue.FlushAsync().WaitAsync(WaitBudget);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(WaitBudget));
    }

    /// <summary>
    /// Verifies that pet world features enforce null guards on construction and attachment.
    /// </summary>
    [Fact]
    public async Task Pets_Features_NullGuards_ThrowArgumentNullException()
    {
        await using ServiceProvider sp = CreateServices();

        Assert.Throws<ArgumentNullException>(() => new PetStableFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PetStableFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new PetsFeature(null!, NullLogger<PetsFeature>.Instance));
        Assert.Throws<ArgumentNullException>(() => new PetsFeature(sp, null!));
        Assert.Throws<ArgumentNullException>(() => new PetsFeature(sp, NullLogger<PetsFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new TalentPetFeature(sp, NullLogger<TalentPetFeature>.Instance).Attach(null!));
    }

    /// <summary>
    /// Verifies that locomotion, transport, and collision features enforce null guards, reject duplicate
    /// attachment, and throw <c>InvalidOperationException</c> when accessed before attachment.
    /// </summary>
    [Fact]
    public async Task Locomotion_Transports_Collision_Features_NullGuards_And_UnattachedAccess_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);

        Assert.Throws<ArgumentNullException>(() => new EnvironmentalMitigationFeature(sp.GetRequiredService<SpellFeature>()).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => MovementRateCommands.Describe(null!));

        var speedRate = new SpeedRateFeature(NullLogger<SpeedRateFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => speedRate.Attach(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => speedRate.OnPlayerLoadedAsync(null!, new CharacterRecord { Name = "Test" }, null!));

        var transport = new TransportFeature(sp, NullLogger<TransportFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => transport.Attach(null!));
        transport.Attach(world);
        Assert.Throws<InvalidOperationException>(() => transport.Attach(world));

        var collision = new CollisionFeature(sp, NullLogger<CollisionFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => collision.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => _ = collision.Collision);
    }

    /// <summary>
    /// Verifies that underlying locomotion, speed, and spline helpers validate enum ranges, null arguments,
    /// and minimum control-point counts.
    /// </summary>
    [Fact]
    public void Locomotion_And_Spline_EngineGuards_ThrowExpectedExceptions()
    {
        const MoveType InvalidMoveType = (MoveType)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.ForceOpcode(InvalidMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.AckOpcode(InvalidMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.ObserverOpcode(InvalidMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPackets.SplineOpcode(InvalidMoveType));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerSpeedRates.Retail.For(InvalidMoveType));

        Player player = CreatePlayer(1);
        var options = new LocomotionOptions();
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Apply(null!, PlayerSpeedRates.Retail, options));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Apply(player, PlayerSpeedRates.Retail, null!));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Refresh(null!, options));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.Refresh(player, null!));
        Assert.Throws<ArgumentNullException>(() => SpeedRates.ApplyToAll(null!, options));

        Assert.Throws<ArgumentException>(() => TransportSpline.CatmullRom([Vector3.Zero]));
        Assert.Throws<ArgumentNullException>(() => TransportSpline.Raw(null!));
        Assert.Throws<ArgumentException>(() => TransportSpline.Raw([Vector3.Zero, Vector3.One, Vector3.UnitX]));

        TransportSpline spline = TransportSpline.CatmullRom([Vector3.Zero, new Vector3(10, 0, 0), new Vector3(20, 5, 0)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Evaluate(0, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Evaluate(spline.Last, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Derivative(0, 0.5f));

        Assert.Throws<ArgumentNullException>(() => CreatureMovePackets.BuildPath(default, Vector3.Zero, 1, SplineFacing.None, true, 100, null!));
        Assert.Throws<ArgumentException>(() => CreatureMovePackets.BuildPath(default, Vector3.Zero, 1, SplineFacing.None, true, 100, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatureMovePackets.BuildPath(
            default, Vector3.Zero, 1, new SplineFacing((MonsterMoveType)99, 0, default, default), true, 100, [Vector3.One]));
    }

    /// <summary>
    /// Verifies that instance features and <c>InstanceWriteQueue</c> enforce null guards, unattached access
    /// checks, retained-failure barriers, and ownership rejection fences.
    /// </summary>
    [Fact]
    public async Task Instances_Features_And_WriteQueue_NullGuards_RetainedFailures_And_OwnershipFence_ThrowExpectedExceptions()
    {
        var store = new FaultableInstanceStore { AlwaysFail = true };
        await using ServiceProvider sp = CreateServices(configure: s => s.AddSingleton<IInstanceStore>(store));

        Assert.Throws<ArgumentNullException>(() => new DungeonEventFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new DungeonScriptFeature(sp).Attach(null!));

        var worldMapScripts = new WorldMapScriptsFeature();
        Assert.Throws<ArgumentNullException>(() => worldMapScripts.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => WorldMapScriptsFeature.Install(null!));

        await using var instanceFeature = new InstanceFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => instanceFeature.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => _ = instanceFeature.Instances);
        Assert.Throws<InvalidOperationException>(() => instanceFeature.InstanceRemoved += _ => { });
        await Assert.ThrowsAsync<ArgumentNullException>(() => instanceFeature.OnCharacterDeletedAsync(null!, null!));

        var queue = new InstanceWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        Assert.Throws<ArgumentNullException>(() => queue.InstanceSaved(null!));
        Assert.Throws<ArgumentNullException>(() => queue.InstanceDataSaved(null!));

        queue.Start();
        queue.PlayerBound(15, 200, permanent: true);
        await queue.FlushAsync().WaitAsync(WaitBudget);
        Assert.True(queue.HasRetainedFailure(15));

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(15).WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(WaitBudget));

        // Ownership rejection permanently fences InstanceWriteQueue.
        var fencedStore = new FaultableInstanceStore { RejectOwnership = true };
        await using ServiceProvider fencedSp = CreateServices(configure: s => s.AddSingleton<IInstanceStore>(fencedStore));
        var fencedQueue = new InstanceWriteQueue(fencedSp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        fencedQueue.Start();
        fencedQueue.PlayerBound(16, 201, permanent: true);
        await fencedQueue.FlushAsync().WaitAsync(WaitBudget);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fencedQueue.FlushCharacterAsync(16).WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fencedQueue.StopAsync().WaitAsync(WaitBudget));
    }

    /// <summary>
    /// Verifies that game object and scripting features validate null arguments, reject unattached map
    /// lookups, and enforce empty-payload checks in <c>MeetingStoneHandlers</c>.
    /// </summary>
    [Fact]
    public async Task GameObjects_And_Scripting_Features_NullGuards_UnattachedMap_And_MeetingStonePayload_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        var goLoot = new GameObjectLootFeature(sp, NullLogger<GameObjectLootFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => goLoot.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => goLoot.ReplaceContent(null!));
        Assert.Throws<ArgumentNullException>(() => goLoot.ReplaceLootContent(null!));
        Assert.Throws<InvalidOperationException>(() => goLoot.GetOrCreateSystem(0u));
        Assert.Throws<InvalidOperationException>(() => goLoot.GetOrCreateSystem(world.GetMap(0)));

        Assert.Throws<ArgumentNullException>(() => new GameObjectQuestGiverFeature(sp, NullLogger<GameObjectQuestGiverFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new GameObjectTypesFeature(sp, NullLogger<GameObjectTypesFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpecialLootFeature(sp, NullLogger<SpecialLootFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellFocusFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new WorldGameObjectScriptsFeature(sp).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new MeetingStoneFeature(sp, NullLogger<MeetingStoneFeature>.Instance).Attach(null!));

        var settlements = new LootSettlements(scopes, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => settlements.CanPersist(null!));
        Assert.Throws<ArgumentNullException>(() => settlements.CreateActor(null!, new InventorySnapshot([]), new InventorySnapshot([])));
        Assert.Throws<ArgumentNullException>(() => settlements.TryStart(null!));

        // CMSG_MEETINGSTONE_INFO requires an empty payload (non-empty body throws ArgumentOutOfRangeException).
        var meetingStoneTable = new OpcodeTable();
        new MeetingStoneHandlers().Register(meetingStoneTable);
        Assert.True(meetingStoneTable.TryGet(WorldOpcode.CmsgMeetingstoneInfo, out OpcodeHandler meetingStoneInfo));
        Assert.Throws<ArgumentOutOfRangeException>(() => meetingStoneInfo.World!(null!, CreatePlayer(1), [0x01]));

        var duelReset = new DuelResetModule();
        Assert.Throws<ArgumentNullException>(() => duelReset.Register(null!));

        var scriptHooks = new ScriptHooksFeature(sp);
        Assert.Throws<ArgumentNullException>(() => scriptHooks.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => scriptHooks.ReloadConfig(null!));
    }

    /// <summary>
    /// Verifies that graveyard, teleport, and outdoor PvP features validate null arguments and throw
    /// <c>InvalidOperationException</c> when their services or world properties are accessed before attachment.
    /// </summary>
    [Fact]
    public async Task Graveyards_Teleport_OutdoorPvP_Features_NullGuards_And_UnattachedAccess_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();

        Assert.Throws<ArgumentNullException>(() => new GraveyardFeature(scopes, sp, NullLogger<GraveyardFeature>.Instance).Attach(null!));

        var repop = new GraveyardRepopService(world, static () => null);
        Assert.Throws<ArgumentNullException>(() => repop.AddOverride(null!));
        Assert.Throws<ArgumentNullException>(() => repop.AddLinkSource(null!));
        Assert.Throws<ArgumentNullException>(() => repop.RepopAtGraveyard(null!));
        Assert.Throws<ArgumentNullException>(() => repop.RelocateLeavingPlayer(null!));
        Assert.Throws<ArgumentNullException>(() => repop.TeleportToCorpseGraveyard(null!, null));

        var teleport = new TeleportFeature(sp, scopes, NullLogger<TeleportFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => teleport.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => _ = teleport.Teleports);
        Assert.Throws<InvalidOperationException>(() => _ = teleport.Maps);

        var outdoorPvP = new OutdoorPvPFeature(sp, NullLogger<OutdoorPvPFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => outdoorPvP.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => _ = outdoorPvP.World);
    }

    /// <summary>
    /// Verifies that world-state features enforce null guards, reject calls before attachment,
    /// and fail closed when <c>GameEventReloadable</c> or <c>GameWeatherReloadable</c> is built without a store.
    /// </summary>
    [Fact]
    public async Task WorldState_Features_NullGuards_UnattachedAccess_And_MissingStoreReload_ThrowExpectedExceptions()
    {
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        Player player = CreatePlayer(1);

        var exploration = new ExplorationFeature(sp, NullLogger<ExplorationFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => exploration.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => exploration.ReplaceBaseXp([]));

        var persistence = new ExploredZonesPersistence(scopes, NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => persistence.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => persistence.Changed(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => persistence.OnCharacterCreatedAsync(null!, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => persistence.OnPlayerLoadingAsync(null!, null!, player));
        await Assert.ThrowsAsync<ArgumentNullException>(() => persistence.OnPlayerLoadingAsync(null!, new CharacterRecord { Name = "Test" }, null!));

        var deleteHook = new ExploredZonesDeleteHook(persistence);
        await Assert.ThrowsAsync<ArgumentNullException>(() => deleteHook.OnCharacterDeletedAsync(null!, null!));

        var gameEvent = new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => new GameEventCreatureDataFeature(sp, NullLogger<GameEventCreatureDataFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new GameEventQuestFeature(sp, NullLogger<GameEventQuestFeature>.Instance).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new PvpAreaFeature().Attach(null!));
        Assert.Throws<ArgumentNullException>(() => new WarEffortFeature(scopes, gameEvent, NullLogger<WarEffortFeature>.Instance).Attach(null!));

        var spawnFeature = new GameEventSpawnFeature(sp, NullLogger<GameEventSpawnFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => spawnFeature.Attach(null!));
        var gate = new EmptySpawnGate();
        Assert.Throws<ArgumentNullException>(() => GameEventSpawnFeature.CountOrphans(null!, CreatureContent.Empty, GameObjectContent.Empty));
        Assert.Throws<ArgumentNullException>(() => GameEventSpawnFeature.CountOrphans(gate, null!, GameObjectContent.Empty));
        Assert.Throws<ArgumentNullException>(() => GameEventSpawnFeature.CountOrphans(gate, CreatureContent.Empty, null!));

        var emptyActiveIds = new HashSet<ushort>();
        Assert.Throws<ArgumentNullException>(() => gameEvent.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => gameEvent.UseContent(null!, emptyActiveIds));
        Assert.Throws<ArgumentNullException>(() => gameEvent.UseContent(GameEventContent.Empty, null!));
        Assert.Throws<ArgumentNullException>(() => gameEvent.AddListener(null!));
        Assert.Throws<ArgumentNullException>(() => gameEvent.Preview(null!));
        Assert.Throws<InvalidOperationException>(() => gameEvent.UseContent(GameEventContent.Empty, emptyActiveIds));
        Assert.Throws<InvalidOperationException>(() => gameEvent.Preview(GameEventContent.Empty));

        var eventReloadable = new GameEventReloadable(sp);
        await Assert.ThrowsAsync<InvalidOperationException>(() => eventReloadable.BuildAsync(CancellationToken.None));

        var weather = new WeatherFeature(sp, NullLogger<WeatherFeature>.Instance);
        Assert.Throws<ArgumentNullException>(() => weather.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => weather.ReplaceChances([]));
        Assert.Throws<InvalidOperationException>(() => weather.MergeChances([]));

        var weatherReloadable = new GameWeatherReloadable(sp);
        await Assert.ThrowsAsync<InvalidOperationException>(() => weatherReloadable.BuildAsync(CancellationToken.None));

        var zoneArea = new ZoneAreaFeature(sp);
        Assert.Throws<ArgumentNullException>(() => zoneArea.Attach(null!));
        Assert.Throws<InvalidOperationException>(() => zoneArea.OnZoneChanged(player, 0, 12, 87, null));
    }

    /// <summary>
    /// Verifies that <c>ExploredZonesWriteQueue</c> faults <c>FlushCharacterAsync</c> and <c>StopAsync</c>
    /// when a character's explored-zones write is retained or fenced by an ownership rejection.
    /// </summary>
    [Fact]
    public async Task ExploredZonesWriteQueue_RetainedFailure_And_OwnershipFence_ThrowInvalidOperationException()
    {
        var store = new FaultableExploredZonesStore { AlwaysFail = true };
        await using ServiceProvider sp = CreateServices(configure: s => s.AddSingleton<IExploredZonesStore>(store));
        var queue = new ExploredZonesWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetryDelay = TimeSpan.Zero,
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();

        var words = new uint[64];
        words[0] = 0xAA55u;
        queue.Save(21, words);
        await queue.FlushAsync().WaitAsync(WaitBudget);
        Assert.True(queue.HasRetainedFailure(21));

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(21).WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(WaitBudget));

        var fencedStore = new FaultableExploredZonesStore { RejectOwnership = true };
        await using ServiceProvider fencedSp = CreateServices(configure: s => s.AddSingleton<IExploredZonesStore>(fencedStore));
        var fencedQueue = new ExploredZonesWriteQueue(fencedSp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetryDelay = TimeSpan.Zero,
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        fencedQueue.Start();
        fencedQueue.Save(22, words);
        await fencedQueue.FlushAsync().WaitAsync(WaitBudget);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fencedQueue.FlushCharacterAsync(22).WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fencedQueue.StopAsync().WaitAsync(WaitBudget));
    }

    /// <summary>
    /// Deterministically fuzzes spell cast target, range, line-of-sight, cooldown, mana cost,
    /// direct damage, healing, and periodic aura tick execution across randomized states.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_SpellCastValidation_RangeLosCooldownManaAndPeriodicAuras_StayConsistent(int seed)
    {
        var rng = new Random(seed);
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);

        var spellFeature = sp.GetRequiredService<SpellFeature>();
        new CastCheckFeature(sp).Attach(world);
        new CasterFeature(spellFeature, sp).Attach(world);
        new SpellRulesCoreFeature(sp, NullLogger<SpellRulesCoreFeature>.Instance).Attach(world);
        new SpellRulesAuraEngineFeature(sp).Attach(world);
        new SpellRulesCcFeature(sp).Attach(world);
        spellFeature.Attach(world);

        const uint NukeSpell = 900_001;
        const uint HealSpell = 900_002;
        const uint DotSpell = 900_003;

        SpellSystem spells = spellFeature.System;
        spells.Store = new SpellStore(
        [
            new SpellInfo
            {
                Id = NukeSpell,
                Name = "Fuzz Nuke",
                School = SpellSchool.Fire,
                PowerType = (int)PowerType.Mana,
                ManaCost = 40,
                RecoveryTime = 1500,
                RangeIndex = 4,
                Range = new SpellRange(0f, 30f),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.SchoolDamage,
                        BasePoints = 25,
                        BaseDice = 1,
                        DieSides = 10,
                        TargetA = SpellImplicitTarget.UnitEnemy,
                    },
                ],
            },
            new SpellInfo
            {
                Id = HealSpell,
                Name = "Fuzz Heal",
                School = SpellSchool.Holy,
                PowerType = (int)PowerType.Mana,
                ManaCost = 30,
                RecoveryTime = 1000,
                RangeIndex = 4,
                Range = new SpellRange(0f, 40f),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.Heal,
                        BasePoints = 35,
                        BaseDice = 1,
                        DieSides = 10,
                        TargetA = SpellImplicitTarget.UnitCaster,
                    },
                ],
            },
            new SpellInfo
            {
                Id = DotSpell,
                Name = "Fuzz Periodic Dot",
                School = SpellSchool.Shadow,
                PowerType = (int)PowerType.Mana,
                ManaCost = 20,
                Duration = new SpellDuration(6000, 0, 6000),
                RangeIndex = 4,
                Range = new SpellRange(0f, 30f),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura,
                        AuraType = AuraType.PeriodicDamage,
                        Amplitude = 2000,
                        BasePoints = 12,
                        BaseDice = 1,
                        DieSides = 1,
                        TargetA = SpellImplicitTarget.UnitEnemy,
                    },
                ],
            },
        ], [], []);

        Map map = world.GetMap(0);
        Player caster = CreatePlayer(1, powerType: PowerType.Mana, maxHealth: 600, maxPower: 500);
        Player enemy = CreatePlayer(2, race: 2, powerType: PowerType.Rage, maxHealth: 600, maxPower: 1000);
        map.AddPlayer(caster);
        map.AddPlayer(enemy);

        spellFeature.Spellbook.LoadCharacter(1, [NukeSpell, HealSpell, DotSpell]);
        spellFeature.Spellbook.LoadCharacter(2, []);

        for (int step = 0; step < 140; step++)
        {
            float dist = (float)(rng.NextDouble() * 55.0);
            float angle = (float)(rng.NextDouble() * MathF.Tau);
            caster.Relocate(0f, 0f, 10f, angle, (uint)(step * 100));
            enemy.Relocate(dist * MathF.Cos(angle), dist * MathF.Sin(angle), 10f, 0f, (uint)(step * 100));

            if (!caster.IsAlive)
            {
                caster.Combat.DeathState = DeathState.Alive;
                caster.Health = caster.MaxHealth;
            }

            if (!enemy.IsAlive)
            {
                enemy.Combat.DeathState = DeathState.Alive;
                enemy.Health = enemy.MaxHealth;
            }

            if (rng.Next(6) == 0)
            {
                uint maxMana = caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
                uint restored = (uint)rng.Next(0, (int)maxMana + 1);
                caster.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana, restored);
            }

            uint choice = rng.Next(3) switch
            {
                0 => NukeSpell,
                1 => HealSpell,
                _ => DotSpell,
            };

            SpellCastTargets targets = choice == HealSpell
                ? SpellCastTargets.ForSelf()
                : new SpellCastTargets { Mask = SpellCastTargetFlags.Unit, Unit = enemy.Guid };

            bool triggered = rng.Next(5) == 0;
            uint manaBefore = caster.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana);
            SpellInfo info = spells.Store.Get(choice)!;

            SpellCastResult result = spells.CastSpell(caster, choice, targets, triggered: triggered);

            if (!triggered && dist > info.Range.Max + caster.BoundingRadius + enemy.BoundingRadius + 8.0f && choice != HealSpell)
            {
                Assert.NotEqual(SpellCastResult.CastOk, result);
            }

            if (!triggered && manaBefore < info.ManaCost && result == SpellCastResult.CastOk)
            {
                Assert.Fail($"Untriggered spell {choice} succeeded with mana {manaBefore} < cost {info.ManaCost}");
            }

            uint tickMs = (uint)rng.Next(50, 800);
            world.RunTick(tickMs);

            uint manaAfter = caster.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana);
            Assert.InRange(manaAfter, 0u, caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana));
            Assert.InRange(caster.Health, 0u, caster.MaxHealth);
            Assert.InRange(enemy.Health, 0u, enemy.MaxHealth);
        }
    }

    /// <summary>
    /// Deterministically fuzzes combat damage, school immunity, school absorb shields, mana shields,
    /// split-damage auras, environmental/fall damage, and threat list ordering across randomized combat steps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_CombatDamage_SplitDamage_EnvironmentalDamage_And_ThreatOrdering_MaintainInvariants(int seed)
    {
        var rng = new Random(seed);
        await using ServiceProvider sp = CreateServices();
        using WorldRuntime world = CreateWorld(sp);

        var spellFeature = sp.GetRequiredService<SpellFeature>();
        new SpellRulesCoreFeature(sp, NullLogger<SpellRulesCoreFeature>.Instance).Attach(world);
        new SpellRulesAuraEngineFeature(sp).Attach(world);
        new EnvironmentalMitigationFeature(spellFeature).Attach(world);
        new ThreatFeature(sp).Attach(world);
        new SpellThreatFeature(sp, NullLogger<SpellThreatFeature>.Instance).Attach(world);
        spellFeature.Attach(world);

        const uint ShieldSpell = 910_001;
        const uint SplitAuraSpell = 910_002;
        const uint FireImmuneSpell = 910_003;

        uint allSchools = 0x7Fu;
        uint fireMask = SpellSchoolMasks.Of(SpellSchool.Fire);
        SpellSystem spells = spellFeature.System;
        spells.Store = new SpellStore(
        [
            new SpellInfo
            {
                Id = ShieldSpell,
                Name = "Fuzz School Shield",
                Duration = new SpellDuration(30_000, 0, 30_000),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura,
                        AuraType = AuraType.SchoolAbsorb,
                        BasePoints = 79,
                        BaseDice = 1,
                        DieSides = 1,
                        MiscValue = (int)allSchools,
                        TargetA = SpellImplicitTarget.UnitCaster,
                    },
                ],
            },
            new SpellInfo
            {
                Id = SplitAuraSpell,
                Name = "Fuzz Split Damage",
                Duration = new SpellDuration(30_000, 0, 30_000),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura,
                        AuraType = AuraType.SplitDamagePct,
                        BasePoints = 29,
                        BaseDice = 1,
                        DieSides = 1,
                        MiscValue = (int)allSchools,
                        TargetA = SpellImplicitTarget.Unit,
                    },
                ],
            },
            new SpellInfo
            {
                Id = FireImmuneSpell,
                Name = "Fuzz Fire Immunity",
                Duration = new SpellDuration(10_000, 0, 10_000),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura,
                        AuraType = AuraType.SchoolImmunity,
                        BasePoints = 0,
                        BaseDice = 1,
                        DieSides = 1,
                        MiscValue = (int)fireMask,
                        TargetA = SpellImplicitTarget.UnitCaster,
                    },
                ],
            },
        ], [], []);

        Map map = world.GetMap(0);
        Player victim = CreatePlayer(1, maxHealth: 2000);
        Player splitter = CreatePlayer(2, maxHealth: 2000);
        Player attackerA = CreatePlayer(3, race: 2, maxHealth: 2000);
        Player attackerB = CreatePlayer(4, race: 2, maxHealth: 2000);
        map.AddPlayer(victim);
        map.AddPlayer(splitter);
        map.AddPlayer(attackerA);
        map.AddPlayer(attackerB);

        var bossTemplate = new CreatureTemplate
        {
            Entry = 5001,
            Name = "Fuzz Boss",
            MinLevel = 60,
            MaxLevel = 60,
            MinLevelHealth = 5000,
            MaxLevelHealth = 5000,
            Faction = 14,
            DisplayIds = [903],
        };
        var boss = new Creature(1, bossTemplate, null, new CreatureContent([bossTemplate], [], [], [], []), new Random(seed));

        EnvironmentalDamageType[] envTypes =
        [
            EnvironmentalDamageType.Exhausted,
            EnvironmentalDamageType.Drowning,
            EnvironmentalDamageType.Fall,
            EnvironmentalDamageType.Lava,
            EnvironmentalDamageType.Slime,
            EnvironmentalDamageType.Fire,
        ];

        for (int step = 0; step < 140; step++)
        {
            foreach (Player p in new[] { victim, splitter, attackerA, attackerB })
            {
                if (!p.IsAlive || p.Health < 200)
                {
                    p.Combat.DeathState = DeathState.Alive;
                    p.Health = p.MaxHealth;
                }
            }

            switch (rng.Next(6))
            {
                case 0:
                    // Cast absorb shield on victim.
                    spells.CastSpell(victim, ShieldSpell, SpellCastTargets.ForSelf(), triggered: true);
                    break;

                case 1:
                    // Cast split-damage aura from splitter onto victim.
                    spells.CastSpell(
                        splitter,
                        SplitAuraSpell,
                        new SpellCastTargets { Mask = SpellCastTargetFlags.Unit, Unit = victim.Guid },
                        triggered: true);
                    break;

                case 2:
                    // Toggle fire immunity on victim.
                    if (rng.Next(2) == 0)
                    {
                        spells.CastSpell(victim, FireImmuneSpell, SpellCastTargets.ForSelf(), triggered: true);
                    }
                    else
                    {
                        spells.RemoveAuras(victim, FireImmuneSpell);
                    }
                    break;

                case 3:
                {
                    // Absorb + split damage test.
                    uint rawDamage = (uint)rng.Next(1, 250);
                    uint splitterBefore = splitter.Health;
                    uint absorbed = spells.AbsorbDamage(attackerA, victim, allSchools, rawDamage, spell: null);
                    Assert.InRange(absorbed, 0u, rawDamage);
                    Assert.InRange(splitter.Health, 0u, splitterBefore);
                    uint dealt = map.Combat.DealDamage(attackerA, victim, rawDamage - absorbed);
                    Assert.Equal(rawDamage - absorbed, dealt);
                    break;
                }

                case 4:
                {
                    // Environmental & fall damage test.
                    EnvironmentalDamageType envType = envTypes[rng.Next(envTypes.Length)];
                    uint rawEnv = (uint)rng.Next(10, 180);
                    bool hasFireImmunity = spells.GetAuras(victim).Any(h => h.Spell.Id == FireImmuneSpell && !h.IsRemoved);
                    uint dealtEnv = EnvironmentalDamage.Apply(world, victim, envType, rawEnv);
                    if (hasFireImmunity && envType is EnvironmentalDamageType.Lava or EnvironmentalDamageType.Fire)
                    {
                        Assert.Equal(0u, dealtEnv);
                    }
                    else
                    {
                        Assert.InRange(dealtEnv, 0u, rawEnv);
                    }

                    float zDiff = (float)(rng.NextDouble() * 60.0);
                    int safeFall = rng.Next(-5, 25);
                    uint fallDmg = FallDamageCalculator.Damage(zDiff, safeFall, victim.MaxHealth);
                    if (zDiff - safeFall < FallDamageCalculator.MinFallDistance)
                    {
                        Assert.Equal(0u, fallDmg);
                    }
                    break;
                }

                case 5:
                {
                    // Threat list additions, percent modifiers, and descending order verification.
                    Player target = rng.Next(3) switch
                    {
                        0 => victim,
                        1 => attackerA,
                        _ => attackerB,
                    };
                    float delta = (float)((rng.NextDouble() * 120.0) - 35.0);
                    boss.Combat.Threat.AddThreat(target, delta);

                    IReadOnlyList<ThreatEntry> entries = boss.Combat.Threat.Entries;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        Assert.True(entries[i].Threat >= 0f);
                        if (i > 0)
                        {
                            Assert.True(entries[i - 1].Threat >= entries[i].Threat);
                        }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Deterministically fuzzes <c>MovementInfo</c> serialization, pending movement-change ack ledgers
    /// (speed, root, water-walk/hover/feather-fall, knockback, turn rate), <c>TransportSpline</c>,
    /// and <c>CreatureSplineSmoothing</c> under valid and adversarial inputs.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_Locomotion_MovementPacketVerification_AckLedgers_And_SplineAdvances_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        Player player = CreatePlayer(1);

        for (int step = 0; step < 140; step++)
        {
            var flags = MovementFlags.None;
            if (rng.Next(2) == 0) flags |= MovementFlags.Forward;
            if (rng.Next(3) == 0) flags |= MovementFlags.OnTransport;
            if (rng.Next(3) == 0) flags |= MovementFlags.Jumping;
            if (rng.Next(4) == 0) flags |= MovementFlags.Swimming;
            if (rng.Next(5) == 0) flags |= MovementFlags.SplineElevation;

            var info = new MovementInfo
            {
                Flags = flags,
                Time = (uint)rng.Next(1, 1_000_000),
                X = (float)((rng.NextDouble() * 2000.0) - 1000.0),
                Y = (float)((rng.NextDouble() * 2000.0) - 1000.0),
                Z = (float)((rng.NextDouble() * 200.0) - 50.0),
                Orientation = (float)(rng.NextDouble() * MathF.Tau),
                TransportGuid = (flags & MovementFlags.OnTransport) != 0 ? 0x1FC0_0000_0000_0010UL : 0UL,
                TransportX = (flags & MovementFlags.OnTransport) != 0 ? (float)(rng.NextDouble() * 20.0) : 0f,
                TransportY = (flags & MovementFlags.OnTransport) != 0 ? (float)(rng.NextDouble() * 20.0) : 0f,
                TransportZ = (flags & MovementFlags.OnTransport) != 0 ? (float)(rng.NextDouble() * 5.0) : 0f,
                TransportOrientation = (flags & MovementFlags.OnTransport) != 0 ? 1.25f : 0f,
                Pitch = (flags & MovementFlags.Swimming) != 0 ? 0.25f : 0f,
                FallTime = (uint)rng.Next(0, 5000),
                JumpZSpeed = (flags & MovementFlags.Jumping) != 0 ? -7.95f : 0f,
                JumpSinAngle = (flags & MovementFlags.Jumping) != 0 ? 0f : 0f,
                JumpCosAngle = (flags & MovementFlags.Jumping) != 0 ? 1f : 0f,
                JumpXySpeed = (flags & MovementFlags.Jumping) != 0 ? 7.0f : 0f,
                SplineElevation = (flags & MovementFlags.SplineElevation) != 0 ? 1.5f : 0f,
            };

            // Round-trip MovementInfo through PacketWriter / PacketReader.
            var writer = new PacketWriter(96);
            info.Write(writer);
            var reader = new PacketReader(writer.AsSpan());
            MovementInfo decoded = MovementInfo.Read(ref reader);
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(info.Flags, decoded.Flags);
            Assert.Equal(info.X, decoded.X);
            Assert.Equal(info.Y, decoded.Y);
            Assert.Equal(info.Z, decoded.Z);

            // Exercise PendingMovementChanges ledger against valid, spoofed, and replayed acks.
            uint counter = player.NextMovementCounter();
            float expectedSpeed = 7.0f * (float)(0.5 + rng.NextDouble());
            player.Locomotion.Pending.Push(counter, MovementChangeType.SpeedRun, apply: true, newValue: expectedSpeed);
            Assert.True(player.Locomotion.Pending.HasPending);

            // Wrong counter or wrong speed fails and leaves the entry pending.
            Assert.False(player.Locomotion.Pending.TryAcknowledgeSpeed(counter + 1, MovementChangeType.SpeedRun, expectedSpeed));
            Assert.False(player.Locomotion.Pending.TryAcknowledgeSpeed(counter, MovementChangeType.SpeedRun, expectedSpeed + 1.0f));
            Assert.True(player.Locomotion.Pending.HasPending);

            // Matching counter and speed consumes the entry once; replay is rejected.
            Assert.True(player.Locomotion.Pending.TryAcknowledgeSpeed(counter, MovementChangeType.SpeedRun, expectedSpeed));
            Assert.False(player.Locomotion.Pending.HasPending);
            Assert.False(player.Locomotion.Pending.TryAcknowledgeSpeed(counter, MovementChangeType.SpeedRun, expectedSpeed));

            // Flag change ledger check.
            uint flagCounter = player.NextMovementCounter();
            bool applyFlag = rng.Next(2) == 0;
            player.Locomotion.Pending.Push(flagCounter, MovementChangeType.WaterWalk, applyFlag);
            Assert.False(player.Locomotion.Pending.TryAcknowledge(flagCounter, MovementChangeType.WaterWalk, !applyFlag));
            Assert.True(player.Locomotion.Pending.TryAcknowledge(flagCounter, MovementChangeType.WaterWalk, applyFlag));
            Assert.False(player.Locomotion.Pending.TryAcknowledge(flagCounter, MovementChangeType.WaterWalk, applyFlag));

            // Knockback ledger check.
            uint kbCounter = player.NextMovementCounter();
            var kbInfo = new KnockbackInfo(1f, 0f, 8.5f, -6.0f);
            player.Locomotion.Pending.Push(kbCounter, MovementChangeType.KnockBack, apply: true, knockback: kbInfo);
            var wrongKbBlock = new MovementInfo { JumpCosAngle = 1f, JumpSinAngle = 0f, JumpXySpeed = 8.5f, JumpZSpeed = -9.0f };
            var matchingKbBlock = new MovementInfo { JumpCosAngle = 1f, JumpSinAngle = 0f, JumpXySpeed = 8.5f, JumpZSpeed = -6.0f };
            Assert.False(player.Locomotion.Pending.TryAcknowledgeKnockBack(kbCounter, in wrongKbBlock));
            Assert.True(player.Locomotion.Pending.TryAcknowledgeKnockBack(kbCounter, in matchingKbBlock));

            // TransportSpline evaluation & CreatureMovePackets packing.
            int controlCount = rng.Next(2, 7);
            var controls = new Vector3[controlCount];
            for (int i = 0; i < controlCount; i++)
            {
                controls[i] = new Vector3(i * 10f, (float)(rng.NextDouble() * 15.0), (float)(rng.NextDouble() * 5.0));
            }

            TransportSpline catmull = TransportSpline.CatmullRom(controls);
            float u = (float)rng.NextDouble();
            int seg = rng.Next(catmull.First, catmull.Last);
            Vector3 evaluated = catmull.Evaluate(seg, u);
            Vector3 deriv = catmull.Derivative(seg, u);
            Assert.True(float.IsFinite(evaluated.X) && float.IsFinite(evaluated.Y) && float.IsFinite(evaluated.Z));
            Assert.True(float.IsFinite(deriv.X) && float.IsFinite(deriv.Y) && float.IsFinite(deriv.Z));

            byte[] movePacket = CreatureMovePackets.BuildPath(
                ObjectGuid.WithEntry(HighGuid.Unit, 100, (uint)(step + 1)),
                controls[0],
                (uint)(step + 1),
                SplineFacing.ToAngle(1.0f),
                run: rng.Next(2) == 0,
                durationMs: 1200,
                controls[1..]);
            Assert.NotEmpty(movePacket);
        }

        // Verify CreatureSplineSmoothing.SmoothCorners preserves destination and stays bounded.
        var clearMesh = new AlwaysClearMeshQuery();
        var corners = new List<Vector3>
        {
            new(10f, 0f, 0f),
            new(10f, 10f, 0f),
            new(20f, 10f, 0f),
        };
        List<Vector3> smoothed = CreatureSplineSmoothing.SmoothCorners(
            0,
            Vector3.Zero,
            corners,
            clearMesh,
            static (_, _, _, z) => z);
        Assert.Equal(corners[^1], smoothed[^1]);
        Assert.InRange(smoothed.Count, corners.Count, CreatureSplineSmoothing.MaxSplinePoints);
    }

    /// <summary>
    /// Deterministically fuzzes <c>InstanceWriteQueue</c> persistence sequences, <c>GraveyardRepopService</c>
    /// graveyard selection/relocation, and <c>TeleportService</c> near/far teleports and stall checks.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_Instances_GraveyardResolution_And_TeleportStateMachine_ConvergeDeterministically(int seed)
    {
        var rng = new Random(seed);
        var instanceStore = new FaultableInstanceStore();
        var mapStore = new InMemoryFuzzMapStore();
        var graveyardStore = new InMemoryFuzzGraveyardStore();

        await using ServiceProvider sp = CreateServices(configure: s =>
        {
            s.AddSingleton<IInstanceStore>(instanceStore);
            s.AddSingleton<IMapDataStore>(mapStore);
            s.AddSingleton<IGraveyardDataStore>(graveyardStore);
        });
        using WorldRuntime world = CreateWorld(sp);

        var teleportFeature = sp.GetRequiredService<TeleportFeature>();
        var graveyardFeature = sp.GetRequiredService<GraveyardFeature>();
        teleportFeature.Attach(world);
        graveyardFeature.Attach(world);

        var queue = new InstanceWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();

        Map map0 = world.GetMap(0);
        Player player = CreatePlayer(1, zoneId: 12);
        map0.AddPlayer(player);

        for (int step = 0; step < 100; step++)
        {
            switch (rng.Next(5))
            {
                case 0:
                {
                    // InstanceWriteQueue random bind / unbind / save / delete.
                    uint instanceId = (uint)rng.Next(101, 115);
                    uint charId = (uint)rng.Next(1, 6);
                    queue.EnqueueInstanceSaveForTest(instanceId, 36, 1_700_000_000 + step, rng.Next(4) == 0 ? $"state-{step}" : null);
                    queue.PlayerBound(charId, instanceId, permanent: rng.Next(2) == 0);
                    if (rng.Next(3) == 0)
                    {
                        queue.PlayerUnbound(charId, instanceId);
                    }
                    break;
                }

                case 1:
                {
                    // Same-map near teleport + client ack.
                    float destX = -8900f + rng.Next(-50, 50);
                    float destY = -120f + rng.Next(-50, 50);
                    float destZ = 82f;
                    bool scheduled = teleportFeature.Teleports.TeleportTo(player, 0, destX, destY, destZ, 1.0f);
                    Assert.True(scheduled);
                    Assert.Equal(TeleportStage.Near, teleportFeature.Teleports.StageOf(player));
                    Assert.True(teleportFeature.Teleports.HandleTeleportAck(player, player.Guid.Value));
                    Assert.Null(teleportFeature.Teleports.StageOf(player));
                    Assert.Equal(destX, player.X);
                    Assert.Equal(destY, player.Y);
                    break;
                }

                case 2:
                {
                    // Graveyard repop while dead (or on transport).
                    player.ZoneId = 12;
                    player.Health = 0;
                    player.Combat.DeathState = DeathState.Corpse;
                    if (rng.Next(3) == 0)
                    {
                        player.AddMovementFlags(MovementFlags.OnTransport);
                    }
                    else
                    {
                        player.RemoveMovementFlags(MovementFlags.OnTransport);
                    }

                    bool repopped = graveyardFeature.Service!.RepopAtGraveyard(player);
                    Assert.True(repopped);
                    Assert.NotNull(player.LastReleasedGraveyard);
                    if (teleportFeature.Teleports.StageOf(player) == TeleportStage.Near)
                    {
                        teleportFeature.Teleports.HandleTeleportAck(player, player.Guid.Value);
                    }

                    player.Combat.DeathState = DeathState.Alive;
                    player.Health = player.MaxHealth;
                    player.RemoveMovementFlags(MovementFlags.OnTransport);
                    break;
                }

                case 3:
                {
                    // RelocateLeavingPlayer resolves the closest linked graveyard into LogoutLocation.
                    player.ZoneId = 12;
                    Assert.True(graveyardFeature.Service!.RelocateLeavingPlayer(player));
                    Assert.NotNull(player.LogoutLocation);
                    Assert.Equal(0u, player.LogoutLocation!.Value.MapId);
                    break;
                }

                case 4:
                {
                    // Stall check on pending near teleport does not drop the teleport entry.
                    teleportFeature.Teleports.TeleportTo(player, 0, -8850f, -110f, 85f, 0.5f);
                    teleportFeature.Teleports.CheckStalled(world.NowMs + 60_000);
                    Assert.Equal(TeleportStage.Near, teleportFeature.Teleports.StageOf(player));
                    Assert.True(teleportFeature.Teleports.HandleTeleportAck(player, player.Guid.Value));
                    break;
                }
            }
        }

        await queue.FlushAsync().WaitAsync(WaitBudget);
        Assert.Empty(queue.RetainedCharacters);
        await queue.StopAsync().WaitAsync(WaitBudget);
    }

    /// <summary>
    /// Deterministically fuzzes <c>EasternPlaguelandsZone</c> tower capture sliders and <c>SilithusZone</c>
    /// silithyst turn-ins, flag drops, dust bag spawns, and <c>MaxResources</c> resets.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_OutdoorPvP_EasternPlaguelandsTowers_SilithusSilithyst_And_WorldStateUpdates_PreserveInvariants(int seed)
    {
        var rng = new Random(seed);
        var host = new RecordingOutdoorPvPHost();

        var ep = new EasternPlaguelandsZone(host);
        ep.Setup();

        const uint CustomMaxResources = 45;
        var silithus = new SilithusZone(host, maxResources: CustomMaxResources)
        {
            AreaTriggerPosition = static trigger => trigger switch
            {
                SilithusCatalog.AreaTriggerAlliance => (-7144f, 1402f, 4.5f),
                SilithusCatalog.AreaTriggerHorde => (-7596f, 756f, -16.8f),
                _ => null,
            },
        };
        silithus.Setup();

        var alliancePlayers = Enumerable.Range(1, 5)
            .Select(i => new OutdoorPvPPlayer(ObjectGuid.Player((uint)i), Team.Alliance))
            .ToArray();
        var hordePlayers = Enumerable.Range(10, 5)
            .Select(i => new OutdoorPvPPlayer(ObjectGuid.Player((uint)i), Team.Horde))
            .ToArray();

        foreach (OutdoorPvPPlayer p in alliancePlayers.Concat(hordePlayers))
        {
            ep.OnPlayerEnter(p);
            silithus.OnPlayerEnter(p);
        }

        EasternPlaguelandsTower[] towers = [.. ep.Towers];
        Assert.Equal(4, towers.Length);

        for (int step = 0; step < 150; step++)
        {
            // Randomize active Alliance / Horde presence around each Eastern Plaguelands tower.
            foreach (EasternPlaguelandsTower tower in towers)
            {
                int aCount = rng.Next(0, 5);
                int hCount = rng.Next(0, 5);
                host.SetNear(tower.Point, alliancePlayers[..aCount].Concat(hordePlayers[..hCount]));
            }

            ep.Tick((uint)rng.Next(500, 35_000));

            Assert.InRange(ep.AllianceTowers, 0u, 4u);
            Assert.InRange(ep.HordeTowers, 0u, 4u);
            Assert.True(ep.AllianceTowers + ep.HordeTowers <= 4u);

            foreach (EasternPlaguelandsTower tower in towers)
            {
                Assert.InRange(tower.Value, -tower.MaxValue, tower.MaxValue);
                Assert.InRange(tower.ValuePct, 0u, 100u);
            }

            EpTowerState crownState = ep.Tower(EpTower.CrownGuard).TowerState;
            if (crownState is EpTowerState.Alliance or EpTowerState.AllianceProgressing)
            {
                Assert.Equal(Team.Alliance, ep.GraveyardTeam);
            }
            else if (crownState is EpTowerState.Horde or EpTowerState.HordeProgressing)
            {
                Assert.Equal(Team.Horde, ep.GraveyardTeam);
            }
            else
            {
                Assert.Null(ep.GraveyardTeam);
            }

            // Fuzz Silithus silithyst turn-ins and flag drops.
            OutdoorPvPPlayer carrier = rng.Next(2) == 0
                ? alliancePlayers[rng.Next(alliancePlayers.Length)]
                : hordePlayers[rng.Next(hordePlayers.Length)];

            if (rng.Next(2) == 0)
            {
                host.GrantAura(carrier.Guid, SilithusCatalog.SpellSilithystFlag);
            }

            if (rng.Next(3) == 0)
            {
                // Drop flag either inside or outside the keep radius.
                host.PlayerDistance = (float)(rng.NextDouble() * 12.0);
                uint spellId = rng.Next(4) == 0 ? 999u : SilithusCatalog.SpellSilithystFlag;
                bool handledDrop = silithus.HandleDropFlag(carrier, spellId);
                if (spellId != SilithusCatalog.SpellSilithystFlag || host.PlayerDistance <= SilithusCatalog.TurnInKeepRadius)
                {
                    Assert.False(handledDrop);
                }
                else
                {
                    Assert.True(handledDrop);
                }
            }
            else
            {
                uint triggerId = rng.Next(3) switch
                {
                    0 => SilithusCatalog.AreaTriggerAlliance,
                    1 => SilithusCatalog.AreaTriggerHorde,
                    _ => 9999u,
                };
                uint beforeA = silithus.GatheredAlliance;
                uint beforeH = silithus.GatheredHorde;
                bool hadFlag = host.HasAura(carrier.Guid, SilithusCatalog.SpellSilithystFlag);

                bool consumed = silithus.HandleAreaTrigger(carrier, triggerId);
                if (!hadFlag || (triggerId != SilithusCatalog.AreaTriggerAlliance && triggerId != SilithusCatalog.AreaTriggerHorde))
                {
                    Assert.False(consumed);
                    Assert.Equal(beforeA, silithus.GatheredAlliance);
                    Assert.Equal(beforeH, silithus.GatheredHorde);
                }
                else
                {
                    Assert.True(consumed);
                    Assert.InRange(silithus.GatheredAlliance, 0u, CustomMaxResources - 1);
                    Assert.InRange(silithus.GatheredHorde, 0u, CustomMaxResources - 1);
                    Assert.Equal((int)(silithus.GatheredAlliance / SilithusCatalog.SilithystPerDustBag), silithus.DustBagsAlliance);
                    Assert.Equal((int)(silithus.GatheredHorde / SilithusCatalog.SilithystPerDustBag), silithus.DustBagsHorde);
                }
            }
        }

        var initialStates = new List<WorldStatePair>();
        ep.FillInitialWorldStates(initialStates);
        silithus.FillInitialWorldStates(initialStates);
        Assert.NotEmpty(initialStates);
    }

    private static ServiceProvider CreateServices(params (string Key, string? Value)[] settings)
        => CreateServices(null, settings);

    private static ServiceProvider CreateServices(
        Action<IServiceCollection>? configure = null,
        params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)))
            .Build();
        services.AddSingleton(config);
        services.AddSingleton<SpellFeature>();
        services.AddSingleton<TeleportFeature>();
        services.AddSingleton<GraveyardFeature>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static WorldRuntime CreateWorld(IServiceProvider services)
    {
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    private static Player CreatePlayer(
        int id,
        byte race = 1,
        byte @class = 1,
        PowerType powerType = PowerType.Mana,
        uint maxHealth = 1000,
        uint maxPower = 1000,
        uint zoneId = 12)
    {
        var record = new CharacterRecord
        {
            Id = id,
            AccountId = id,
            Name = $"FuzzPlayer{id}",
            Race = race,
            Class = @class,
            Gender = 0,
            Level = 60,
            MapId = 0,
            ZoneId = zoneId,
            X = -8949.95f,
            Y = -132.493f,
            Z = 83.5312f,
            HomeMapId = 0,
            HomeZoneId = zoneId,
            HomeX = -8949.95f,
            HomeY = -132.493f,
            HomeZ = 83.5312f,
        };
        var appearance = new PlayerAppearance(49, 1, powerType, 60, 0, 60, maxHealth, maxPower, maxPower);
        return new Player(record, appearance, new NullPlayerSession());
    }

    private sealed class FaultableSpellStore : ICharacterSpellStore
    {
        public volatile bool RejectOwnership;

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([]);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
        {
            if (RejectOwnership)
            {
                throw new CharacterOwnershipRejectedException("characters", 1);
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FaultableSpellStateStore : ICharacterSpellStateStore
    {
        public volatile bool FailSaves;
        public volatile bool RejectOwnership;

        public Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterSpellState([], []));

        public Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default)
        {
            if (RejectOwnership)
            {
                throw new CharacterOwnershipRejectedException("characters", 1);
            }

            if (FailSaves)
            {
                throw new InvalidOperationException("simulated spell-state storage outage");
            }

            return Task.CompletedTask;
        }

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class EmptyPlayerStatsStore : IPlayerStatsContentStore
    {
        public Task<PlayerStatsContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PlayerStatsContent([], [], [], [], []));
    }

    private sealed class FaultableCreatureRespawnStore : ICreatureRespawnStore
    {
        public volatile bool AlwaysFail;

        public Task<IReadOnlyList<CreatureRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CreatureRespawnRecord>>([]);

        public Task SaveAsync(
            IReadOnlyCollection<CreatureRespawnRecord> upserts,
            IReadOnlyCollection<CreatureRespawnKey> deletes,
            CancellationToken cancellationToken = default)
        {
            if (AlwaysFail)
            {
                throw new InvalidOperationException("simulated creature respawn store outage");
            }

            return Task.CompletedTask;
        }

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
            => AlwaysFail ? Task.FromException(new InvalidOperationException("outage")) : Task.CompletedTask;
    }

    private sealed class FaultableInstanceStore : IInstanceStore
    {
        public volatile bool AlwaysFail;
        public volatile bool RejectOwnership;

        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(InstanceStoreSnapshot.Empty);

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default)
            => Check();

        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default)
            => Check();

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
            => Check();

        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default)
            => Check();

        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default)
            => Check();

        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default)
            => Check();

        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default)
            => Check();

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => Check();

        private Task Check()
        {
            if (RejectOwnership)
            {
                throw new CharacterOwnershipRejectedException("characters", 1);
            }

            if (AlwaysFail)
            {
                throw new InvalidOperationException("simulated instance store failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FaultableExploredZonesStore : IExploredZonesStore
    {
        public volatile bool AlwaysFail;
        public volatile bool RejectOwnership;

        public Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<uint[]?>(null);

        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default)
        {
            if (RejectOwnership)
            {
                throw new CharacterOwnershipRejectedException("characters", 1);
            }

            if (AlwaysFail)
            {
                throw new IOException("simulated explored-zones write failure");
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AlwaysClearMeshQuery : IMeshPointQuery
    {
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return true;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(Available: true, Hit: false, Fraction: 1f, Point: end);
    }

    private sealed class InMemoryFuzzMapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent(
                [
                    new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                    new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                ],
                [new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 2, 0)],
                [],
                [],
                []));
    }

    private sealed class InMemoryFuzzGraveyardStore : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GraveyardContent(
                [
                    new WorldSafeLoc(1, 0, -8800f, -100f, 90f, 1.5f, "Goldshire"),
                    new WorldSafeLoc(2, 0, -9100f, -200f, 85f, 0.5f, "Northshire"),
                ],
                [
                    new GraveyardLink(1, 12, 0),
                    new GraveyardLink(2, 12, 0),
                ]));
    }

    private sealed class RecordingOutdoorPvPHost : IOutdoorPvPHost
    {
        private readonly Dictionary<(float X, float Y), List<OutdoorPvPPlayer>> _near = [];
        private readonly HashSet<(ObjectGuid Player, uint Spell)> _auras = [];
        private uint _nextObjectCounter = 1;
        private uint _nextCreatureCounter = 1;

        public float PlayerDistance { get; set; } = 10f;

        public void SetNear(OutdoorPvPSpawn point, IEnumerable<OutdoorPvPPlayer> players)
            => _near[(point.X, point.Y)] = [.. players];

        public void GrantAura(ObjectGuid player, uint spellId)
            => _auras.Add((player, spellId));

        public void SendWorldState(ObjectGuid player, uint state, uint value)
        {
        }

        public void CastOnSelf(ObjectGuid player, uint spellId)
            => _auras.Add((player, spellId));

        public void RemoveAura(ObjectGuid player, uint spellId)
            => _auras.Remove((player, spellId));

        public bool HasAura(ObjectGuid player, uint spellId)
            => _auras.Contains((player, spellId));

        public IEnumerable<OutdoorPvPPlayer> ActivePlayersNear(uint mapId, float x, float y, float z, float radius)
            => _near.GetValueOrDefault((x, y)) ?? [];

        public CapturePointTemplate? CapturePoint(uint entry)
            => EasternPlaguelandsCatalog.FallbackTemplate;

        public ObjectGuid? SummonObject(OutdoorPvPSpawn spawn, bool spawnedByDefault = false)
            => ObjectGuid.WithEntry(HighGuid.GameObject, spawn.Entry, _nextObjectCounter++);

        public void RemoveObject(ObjectGuid guid)
        {
        }

        public void SetBannerArt(ObjectGuid guid, uint artKit, uint animation)
        {
        }

        public void PlayObjectSound(ObjectGuid guid, uint soundId)
        {
        }

        public ObjectGuid? SummonCreature(OutdoorPvPSpawn spawn, uint faction = 0, uint aura = 0)
            => ObjectGuid.WithEntry(HighGuid.Unit, spawn.Entry, _nextCreatureCounter++);

        public void RemoveCreature(ObjectGuid guid)
        {
        }

        public void JoinCreatureGroup(ObjectGuid member, ObjectGuid leader)
        {
        }

        public bool StartSpecialPath(ObjectGuid creature, uint pathId)
            => true;

        public void CreatureCastOnSelf(ObjectGuid creature, uint spellId)
        {
        }

        public void SendDefenseMessage(uint mapId, uint zoneId, uint broadcastTextId)
        {
        }

        public void SendZoneText(uint zoneId, string text)
        {
        }

        public void NearestCreatureSays(ObjectGuid player, uint entry, uint broadcastTextId)
        {
        }

        public void KilledMonsterCredit(ObjectGuid player, uint creatureEntry)
        {
        }

        public float? DistanceTo(ObjectGuid player, uint mapId, float x, float y, float z)
            => PlayerDistance;
    }

    private sealed class NullQuestNpcSink : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows)
        {
        }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask)
        {
        }

        public void CharacterChanged(Player player)
        {
        }
    }

    private sealed class EmptySpawnGate : ISpawnGate
    {
        public bool AllowsCreature(uint spawnGuid) => true;

        public bool AllowsGameObject(uint spawnGuid) => true;

        public IEnumerable<uint> GatedCreatures => [];

        public IEnumerable<uint> GatedGameObjects => [];
    }
}

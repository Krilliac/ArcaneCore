using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotQuestFilterAndFarReturnTests
{
    private const float StartX = -8949.95f;
    private const float StartY = -132.49f;
    private const float StartZ = 83.53f;

    [Fact]
    public async Task UnsupportedQuestObjectives_AreNotPursuedOrAccepted_WhileSupportedKillQuestRemainsEligible()
    {
        var fixture = new UnsupportedMechanicsFixture();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
            services.AddScoped<ILootDataStore>(_ => fixture);
            services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 8),
                new FactionTemplateRecord(UnsupportedMechanicsFixture.FriendlyFaction, 0, 0, OwnMask: 2, FriendlyMask: 3, HostileMask: 0),
                new FactionTemplateRecord(UnsupportedMechanicsFixture.HostileFaction, 0, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 3),
            ]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.WaitForWorldAsync(
                () => session.Player!.Map?.FindObject(UnsupportedMechanicsFixture.GiverGuid) is Creature
                    && host.WorldServices.GetRequiredService<ArcaneCore.World.GameObjects.GameObjectLootFeature>().LootContent.RowCount > 0,
                "quest giver spawned and loot loaded");
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                QuestNpcServices services = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;
                CreatureContent content = host.WorldServices.GetRequiredService<CreatureWorldFeature>().Content!;

                // #379 Lazy Peons without a source item, #414 Doctor Theolen Krastinov
                // (Dungeon Type 81, instance-only target 11261, GameObject targets), spell-cast objectives without a source item, and
                // ExplorationOrEvent quests are rejected by CanExecuteObjectives.
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.LazyPeonsQuest)!));
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.DungeonQuest)!));
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.GameObjectQuest)!));
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.SpellCastQuest)!));
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.ExplorationQuest)!));
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.OffMapCreatureQuest)!));

                // Control: a normal hostile kill quest on the bot's map is executable.
                Assert.True(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.SupportedKillQuest)!));

                // When LootContent is loaded, an item objective with zero creature droppers (a GameObject-harvest quest such as
                // 365 Fields of Grief or 3902 Scavenging Deathknell) is rejected unless the player already holds the items,
                // whereas an item objective dropped by a creature remains executable.
                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.GameObjectItemQuest)!));
                Assert.True(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(UnsupportedMechanicsFixture.CreatureDroppedItemQuest)!));

                var options = new PlayerbotOptions { Enabled = true };
                var destinations = new PlayerbotWorldDestinations(session, options);
                var goals = new PlayerbotQuestGoals(session, options);

                // When only the unsupported quests are rewardable, neither travel nor the visible giver selects them.
                services.Options.RewardMode = QuestRewardMode.AllowlistOnly;
                services.Options.OrdinaryRewardQuestIds =
                [
                    UnsupportedMechanicsFixture.LazyPeonsQuest,
                    UnsupportedMechanicsFixture.DungeonQuest,
                    UnsupportedMechanicsFixture.GameObjectQuest,
                    UnsupportedMechanicsFixture.SpellCastQuest,
                    UnsupportedMechanicsFixture.ExplorationQuest,
                    UnsupportedMechanicsFixture.OffMapCreatureQuest,
                    UnsupportedMechanicsFixture.GameObjectItemQuest,
                ];
                Assert.False(destinations.HasQuestCandidate(player));
                player.VisibleObjects.Add(UnsupportedMechanicsFixture.GiverGuid);
                Assert.False(goals.HasCandidate(player));

                // Adding the supported kill quest makes the giver eligible for that quest only.
                services.Options.OrdinaryRewardQuestIds =
                [
                    .. services.Options.OrdinaryRewardQuestIds,
                    UnsupportedMechanicsFixture.SupportedKillQuest,
                ];
                Assert.True(goals.HasCandidate(player));
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>
    /// Quest 2681 "The Stones That Bind Us": its kill objectives (Blasted Lands servants 7668-7671, ReqSpellCast 0) are creatures whose EventAI
    /// holds them at 1 health from spawn (event 11, action 42) until Shackle Shatter hits them. A plain attack never kills them, so the bot
    /// neither takes the quest nor hunts those entries. Controls: a servant whose death prevention is also lifted by an ordinary event (a health
    /// threshold), and a plain hostile creature, stay killable.
    /// </summary>
    [Fact]
    public async Task KillObjectivesOfCreaturesHeldInvincibleOnSpawn_AreNotAcceptedOrPursued()
    {
        var fixture = new InvincibleServantFixture();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
            services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 8),
                new FactionTemplateRecord(UnsupportedMechanicsFixture.FriendlyFaction, 0, 0, OwnMask: 2, FriendlyMask: 3, HostileMask: 0),
                new FactionTemplateRecord(UnsupportedMechanicsFixture.HostileFaction, 0, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 3),
            ]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.WaitForWorldAsync(
                () => session.Player!.Map?.FindObject(InvincibleServantFixture.GiverGuid) is Creature,
                "quest giver spawned");
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                QuestNpcServices services = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;
                CreatureContent content = host.WorldServices.GetRequiredService<CreatureWorldFeature>().Content!;

                foreach (uint servant in InvincibleServantFixture.Servants)
                {
                    Assert.True(content.Ai.IsInvincibleUntilSpellHit(servant), $"servant {servant} is not read as held invincible");
                    Assert.False(PlayerbotQuestGoals.IsKillObjective(session, player, services, content, servant), $"servant {servant}");
                    Assert.True(content.GetSpawns(player.MapId, servant).Count > 0, "the servant has no reachable spawn: the test proves nothing");
                }

                Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(InvincibleServantFixture.StonesQuest)!));

                // Controls: the checks still pass what a plain attack kills.
                Assert.False(content.Ai.IsInvincibleUntilSpellHit(InvincibleServantFixture.LiftedByHealthEntry));
                Assert.False(content.Ai.IsInvincibleUntilSpellHit(UnsupportedMechanicsFixture.HostileMobEntry));
                Assert.True(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(InvincibleServantFixture.LiftedQuest)!));
                Assert.True(PlayerbotQuestGoals.CanExecuteObjectives(
                    session, player, services, content, services.Quests.Get(InvincibleServantFixture.PlainQuest)!));

                var options = new PlayerbotOptions { Enabled = true };
                var goals = new PlayerbotQuestGoals(session, options);
                services.Options.RewardMode = QuestRewardMode.AllowlistOnly;
                services.Options.OrdinaryRewardQuestIds = [InvincibleServantFixture.StonesQuest];
                player.VisibleObjects.Add(InvincibleServantFixture.GiverGuid);
                Assert.False(goals.HasCandidate(player), "the bot would take quest 2681");

                services.Options.OrdinaryRewardQuestIds = [InvincibleServantFixture.StonesQuest, InvincibleServantFixture.PlainQuest];
                Assert.True(goals.HasCandidate(player), "the control quest is not offered: the test proves nothing");
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ItemUseCreatureQuest_WithSourceItem_IsAcceptedAndExecutedViaCmsgUseItem()
    {
        var fixture = new UnsupportedMechanicsFixture();
        var itemContent = new ItemTestContent();
        itemContent.Templates.Templates.Add(
            new ItemTemplate
            {
                Entry = UnsupportedMechanicsFixture.ForemansBlackjackItem,
                Class = 12,
                Name = "Foreman's Blackjack",
                DisplayId = 1,
                Quality = 1,
                Stackable = 1,
                Spells = [new ItemSpell(LazyPeonAI.SpellAwakenPeon, ItemSpellTriggers.OnUse, 0, 0, -1, 0, -1)],
            }.Normalized());
        SpellContent baseSpells = SpellTestServices.Content();
        SpellContent spellsWithBlackjack = baseSpells with
        {
            Spells =
            [
                .. baseSpells.Spells,
                new SpellTemplateRow
                {
                    Id = LazyPeonAI.SpellAwakenPeon,
                    SpellName = "Awaken Peon",
                    RangeIndex = 4,
                    Effect1 = 3,
                    EffectBaseDice1 = 1,
                    EffectDieSides1 = 1,
                    EffectImplicitTargetA1 = 25,
                },
            ],
        };

        WorldTestHost host;
        using (itemContent.Use())
        {
            host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IQuestContentStore>(fixture);
                services.AddSingleton<ICreatureDataStore>(fixture);
                services.AddScoped<ILootDataStore>(_ => fixture);
                services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(spellsWithBlackjack));
                services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
                services.AddSingleton(new FactionTemplateCatalog(
                [
                    new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 8),
                    new FactionTemplateRecord(UnsupportedMechanicsFixture.FriendlyFaction, 0, 0, OwnMask: 2, FriendlyMask: 3, HostileMask: 0),
                    new FactionTemplateRecord(UnsupportedMechanicsFixture.HostileFaction, 0, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 3),
                ]));
            });
        }

        await using (host)
        {
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(
                    () => session.Player!.Map?.FindObject(UnsupportedMechanicsFixture.PeonGuid) is Creature
                        && host.WorldServices.GetRequiredService<ArcaneCore.World.GameObjects.GameObjectLootFeature>().LootContent.RowCount > 0,
                    "lazy peon spawned and loot loaded");
                await host.World.InvokeAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    Player player = session.Player!;
                    QuestNpcServices services = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;
                    CreatureContent content = host.WorldServices.GetRequiredService<CreatureWorldFeature>().Content!;
                    PlayerNpcState state = services.StateOf(player)!;
                    Quest quest = services.Quests.Get(UnsupportedMechanicsFixture.ItemUseLazyPeonsQuest)!;

                    // Before acceptance, SrcItemId != 0 makes the friendly creature objective executable.
                    Assert.True(PlayerbotQuestGoals.CanExecuteObjectives(session, player, services, content, quest));

                    // Once Incomplete in the player's log, CanExecuteObjectives requires the source item to still be in inventory.
                    QuestStatusData killRow = state.Quests.GetOrAdd(UnsupportedMechanicsFixture.SupportedKillQuest);
                    killRow.Status = QuestStatus.Complete;
                    killRow.Rewarded = true;
                    QuestStatusData row = state.Quests.GetOrAdd(UnsupportedMechanicsFixture.ItemUseLazyPeonsQuest);
                    state.Quests.SetSlot(state.Quests.FindSlot(0), UnsupportedMechanicsFixture.ItemUseLazyPeonsQuest);
                    row.Status = QuestStatus.Incomplete;
                    Assert.False(PlayerbotQuestGoals.CanExecuteObjectives(session, player, services, content, quest));

                    Assert.Equal(
                        ArcaneCore.Game.Items.InventoryResult.Ok,
                        player.Inventory.AddItem(UnsupportedMechanicsFixture.ForemansBlackjackItem, 1, out _));
                    Assert.True(PlayerbotQuestGoals.CanExecuteObjectives(session, player, services, content, quest));

                    var options = new PlayerbotOptions { Enabled = true };
                    var goals = new PlayerbotQuestGoals(session, options);

                    Creature peon = (Creature)player.Map!.FindObject(UnsupportedMechanicsFixture.PeonGuid)!;
                    LazyPeonAI ai = Assert.IsType<LazyPeonAI>(peon.AI);
                    Assert.True(ai.IsAsleep);
                    Assert.False(goals.IsUnavailableItemTarget(peon));

                    // Move peon within interaction reach and make it visible; Update refreshes the objective and uses the blackjack via CmsgUseItem.
                    peon.Relocate(player.X + 2f, player.Y, player.Z, peon.Orientation, host.World.NowMs);
                    player.VisibleObjects.Add(peon.Guid);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    Assert.True(goals.Update(player, 500));
                    Assert.Equal(UnsupportedMechanicsFixture.FriendlyPeonEntry, goals.PreferredCreatureEntry);
                    Assert.Equal(UnsupportedMechanicsFixture.ForemansBlackjackItem, goals.PreferredItemUseId);

                    Assert.False(ai.IsAsleep);
                    Assert.Equal(1u, row.CreatureOrGOCount[0]);
                    Assert.True(goals.IsUnavailableItemTarget(peon));
                    return true;
                });
            }
            finally
            {
                session.Kick();
                await session.ManagedClosed;
            }
        }
    }

    [Fact]
    public async Task FarSameContinentReturnWithoutTaxi_IsHeldInLogWithoutWalking_UntilNearOrTaxiRouteIsKnown()
    {
        var fixture = new FarReturnFixture();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
            services.AddSingleton<INpcContentStore>(fixture);
            services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(UnsupportedMechanicsFixture.FriendlyFaction, 0, 0, 8, 0, 0),
            ]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.WaitForWorldAsync(
                () => host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(session.Player!) is { Loaded: true },
                "npc state loaded");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                QuestNpcServices services = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;
                CreatureContent content = host.WorldServices.GetRequiredService<CreatureWorldFeature>().Content!;
                PlayerNpcState state = services.StateOf(player)!;

                // Mark Quest 428 (Lost Deathstalkers, Silverpine ender 1950 at 10,967 yards from Redridge) complete in the log.
                QuestStatusData row = state.Quests.GetOrAdd(FarReturnFixture.LostDeathstalkersQuest);
                state.Quests.SetSlot(state.Quests.FindSlot(0), FarReturnFixture.LostDeathstalkersQuest);
                row.Status = QuestStatus.Complete;

                var options = new PlayerbotOptions { Enabled = true };
                var goals = new PlayerbotQuestGoals(session, options);

                // 1. Unhomed bot in Redridge (10,967 yards from Silverpine ender) with no known taxi nodes:
                //    Quest 428 is kept in the log (not abandoned), but CompletedReturnQuest holds it (returns 0)
                //    so the bot does not attempt a 10,967-yard walk across the continent.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.AbandonDistantCompletedQuest(player));
                Assert.Equal(QuestStatus.Complete, row.Status);
                Assert.Equal(0u, goals.CompletedReturnQuest(player));

                // 2. A graduated Redridge home does not treat Silverpine's Quest 428 as a Redridge leave-home quest,
                //    because its starter (1952 High Executor Hadrec) spawns in Silverpine, not inside Redridge.
                var redridgeHome = new PlayerbotHomeArea(0, new Vector3(StartX, StartY, StartZ), 900f, "zone:redridge")
                {
                    AllowsLeaveQuests = true,
                };
                Assert.False(redridgeHome.QuestLeavesHome(services.Quests, content, services.Quests.Get(FarReturnFixture.LostDeathstalkersQuest)!));
                Assert.True(redridgeHome.QuestLeavesHome(services.Quests, content, services.Quests.Get(FarReturnFixture.MessengerToStormwindQuest)!));
                // The turn-in is a leave-home one only within LeaveTurnInYards of the centre (2,561 yards here): a bound of 2,000 refuses it.
                var tightHome = new PlayerbotHomeArea(0, new Vector3(StartX, StartY, StartZ), 900f, "zone:redridge") { AllowsLeaveQuests = true, LeaveTurnInYards = 2000f };
                Assert.False(tightHome.QuestLeavesHome(services.Quests, content, services.Quests.Get(FarReturnFixture.MessengerToStormwindQuest)!));

                // 3. A graduated Redridge home with Quest 120 (Messenger to Stormwind, ender 2,561 yards away > MaxDeliveryYards 1200):
                //    Kept in the log by AbandonDistantCompletedQuest, and held by CompletedReturnQuest until the bot knows the flight path.
                state.Quests.SetSlot(state.Quests.FindSlot(FarReturnFixture.LostDeathstalkersQuest), 0);
                row.Status = QuestStatus.None;

                QuestStatusData messenger = state.Quests.GetOrAdd(FarReturnFixture.MessengerToStormwindQuest);
                state.Quests.SetSlot(state.Quests.FindSlot(0), FarReturnFixture.MessengerToStormwindQuest);
                messenger.Status = QuestStatus.Complete;

                goals.Home = redridgeHome;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.AbandonDistantCompletedQuest(player));
                Assert.Equal(QuestStatus.Complete, messenger.Status);
                Assert.Equal(0u, goals.CompletedReturnQuest(player));

                // 4. Once the bot knows both taxi nodes (Lakeshire -> Stormwind) and can pay the fare,
                //    CompletedReturnQuest selects Quest 120 so the bot flies the return trip.
                state.TaxiMask[0] |= (1u << 0) | (1u << 1);
                player.Money = 1_000;
                Assert.Equal(FarReturnFixture.MessengerToStormwindQuest, goals.CompletedReturnQuest(player));
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => StartZ;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class OpenPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    private sealed class UnsupportedMechanicsFixture : IQuestContentStore, ICreatureDataStore, ILootDataStore
    {
        internal const uint GiverEntry = 11378;
        internal const uint FriendlyPeonEntry = 10556;
        internal const uint InstanceBossEntry = 11261;
        internal const uint HostileMobEntry = 3101;
        internal const uint FriendlyFaction = 900021;
        internal const uint HostileFaction = 900022;
        internal const uint ForemansBlackjackItem = 16114;
        internal const uint TirisfalPumpkinItem = 2846;
        internal const uint FamiliarClawItem = 940090;
        internal static readonly ObjectGuid GiverGuid = ObjectGuid.WithEntry(HighGuid.Unit, GiverEntry, 940001);
        internal static readonly ObjectGuid PeonGuid = ObjectGuid.WithEntry(HighGuid.Unit, FriendlyPeonEntry, 940002);

        internal const uint LazyPeonsQuest = 5441;
        internal const uint DungeonQuest = 5382;
        internal const uint GameObjectQuest = 940010;
        internal const uint SpellCastQuest = 940011;
        internal const uint ExplorationQuest = 940012;
        internal const uint OffMapCreatureQuest = 940013;
        internal const uint SupportedKillQuest = 940014;
        internal const uint ItemUseLazyPeonsQuest = 940015;
        internal const uint GameObjectItemQuest = 365;
        internal const uint CreatureDroppedItemQuest = 940016;

        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestTemplate[] templates =
            [
                new()
                {
                    Entry = LazyPeonsQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Lazy Peons (No Source Item)",
                    ReqCreatureOrGOId1 = (int)FriendlyPeonEntry, ReqCreatureOrGOCount1 = 5,
                },
                new()
                {
                    Entry = ItemUseLazyPeonsQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Lazy Peons",
                    SrcItemId = ForemansBlackjackItem, SrcItemCount = 1,
                    ReqCreatureOrGOId1 = (int)FriendlyPeonEntry, ReqCreatureOrGOCount1 = 5,
                },
                new()
                {
                    Entry = DungeonQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Type = 81, Title = "Doctor Theolen Krastinov, the Butcher",
                    ReqCreatureOrGOId1 = (int)InstanceBossEntry, ReqCreatureOrGOCount1 = 1,
                    ReqCreatureOrGOId2 = -176544, ReqCreatureOrGOCount2 = 1,
                },
                new()
                {
                    Entry = GameObjectQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Burn the Tent",
                    ReqCreatureOrGOId1 = -176545, ReqCreatureOrGOCount1 = 1,
                },
                new()
                {
                    Entry = SpellCastQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Awaken the Peon",
                    ReqCreatureOrGOId1 = (int)HostileMobEntry, ReqCreatureOrGOCount1 = 1, ReqSpellCast1 = 19938,
                },
                new()
                {
                    Entry = ExplorationQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Scout the Cave",
                    SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent,
                },
                new()
                {
                    Entry = OffMapCreatureQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Slay the Instance Target",
                    ReqCreatureOrGOId1 = (int)InstanceBossEntry, ReqCreatureOrGOCount1 = 1,
                },
                new()
                {
                    Entry = SupportedKillQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Vile Familiars",
                    ReqCreatureOrGOId1 = (int)HostileMobEntry, ReqCreatureOrGOCount1 = 1,
                },
                new()
                {
                    Entry = GameObjectItemQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Fields of Grief",
                    ReqItemId1 = TirisfalPumpkinItem, ReqItemCount1 = 10,
                },
                new()
                {
                    Entry = CreatureDroppedItemQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Familiar Claws",
                    ReqItemId1 = FamiliarClawItem, ReqItemCount1 = 4,
                },
            ];
            uint[] allQuests =
            [
                LazyPeonsQuest, ItemUseLazyPeonsQuest, DungeonQuest, GameObjectQuest, SpellCastQuest,
                ExplorationQuest, OffMapCreatureQuest, SupportedKillQuest, GameObjectItemQuest,
            ];
            CreatureQuestRelation[] relations = [.. allQuests.Select(q => new CreatureQuestRelation { Id = GiverEntry, Quest = q })];
            return Task.FromResult(new QuestContent(templates, relations, relations));
        }

        Task<LootContent> ILootDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LootContent(
            [
                (LootTableKind.GameObject, new LootStoreRow(375, TirisfalPumpkinItem, -100f, 0, 1, 1)),
                (LootTableKind.Creature, new LootStoreRow(HostileMobEntry, FamiliarClawItem, -100f, 0, 1, 1)),
            ],
            [new CreatureLootInfo(HostileMobEntry, HostileMobEntry, 0, 0, 0)]));

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                new CreatureTemplate { Entry = GiverEntry, Name = "Foreman Thazz'ril", Faction = FriendlyFaction, NpcFlags = 2, DisplayIds = [49] },
                new CreatureTemplate { Entry = FriendlyPeonEntry, Name = "Lazy Peon", Faction = FriendlyFaction, DisplayIds = [49] },
                new CreatureTemplate { Entry = InstanceBossEntry, Name = "Doctor Theolen Krastinov", Faction = HostileFaction, DisplayIds = [49] },
                new CreatureTemplate { Entry = HostileMobEntry, Name = "Vile Familiar", Faction = HostileFaction, DisplayIds = [49] },
            ],
            [
                new CreatureSpawn { Guid = 940001, Entry = GiverEntry, MapId = 0, X = StartX + 10f, Y = StartY, Z = StartZ },
                new CreatureSpawn { Guid = 940002, Entry = FriendlyPeonEntry, MapId = 0, X = StartX + 30f, Y = StartY, Z = StartZ },
                new CreatureSpawn { Guid = 940003, Entry = InstanceBossEntry, MapId = 289, X = 100f, Y = 100f, Z = 100f },
                new CreatureSpawn { Guid = 940004, Entry = HostileMobEntry, MapId = 0, X = StartX + 50f, Y = StartY, Z = StartZ },
            ], [], [], []));
    }

    private sealed class InvincibleServantFixture : IQuestContentStore, ICreatureDataStore
    {
        internal const uint GiverEntry = UnsupportedMechanicsFixture.GiverEntry;
        internal const uint StonesQuest = 2681;
        internal const uint LiftedQuest = 940020;
        internal const uint PlainQuest = 940021;
        internal const uint LiftedByHealthEntry = 940030;
        internal static readonly uint[] Servants = [7668, 7669, 7670, 7671];
        internal static readonly ObjectGuid GiverGuid = ObjectGuid.WithEntry(HighGuid.Unit, GiverEntry, 942001);

        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestTemplate[] templates =
            [
                new()
                {
                    Entry = StonesQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "The Stones That Bind Us",
                    ReqCreatureOrGOId1 = 7668, ReqCreatureOrGOCount1 = 1, ReqCreatureOrGOId2 = 7669, ReqCreatureOrGOCount2 = 1,
                    ReqCreatureOrGOId3 = 7670, ReqCreatureOrGOCount3 = 1, ReqCreatureOrGOId4 = 7671, ReqCreatureOrGOCount4 = 1,
                },
                new()
                {
                    Entry = LiftedQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Held Until Hurt",
                    ReqCreatureOrGOId1 = (int)LiftedByHealthEntry, ReqCreatureOrGOCount1 = 1,
                },
                new()
                {
                    Entry = PlainQuest, Method = 2, MinLevel = 1, QuestLevel = 4, Title = "Vile Familiars",
                    ReqCreatureOrGOId1 = (int)UnsupportedMechanicsFixture.HostileMobEntry, ReqCreatureOrGOCount1 = 1,
                },
            ];
            CreatureQuestRelation[] relations =
                [.. new[] { StonesQuest, LiftedQuest, PlainQuest }.Select(q => new CreatureQuestRelation { Id = GiverEntry, Quest = q })];
            return Task.FromResult(new QuestContent(templates, relations, relations));
        }

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
        {
            // creature_ai_scripts rows 766801 and 766803: invincible at 1 HP on spawn (event 11, action 42 Param1 1), lifted only by a spell hit (event 8).
            List<CreatureAiEvent> events = [];
            foreach (uint servant in Servants)
            {
                events.Add(new CreatureAiEvent { Id = servant * 100 + 1, CreatureId = servant, EventType = 11, Action1 = new CreatureAiAction(42, 1, 0, 0) });
                events.Add(new CreatureAiEvent
                {
                    Id = servant * 100 + 3, CreatureId = servant, EventType = 8, Param1 = 10805, Action1 = new CreatureAiAction(42, 0, 0, 0),
                });
            }

            // Control: held on spawn, but an ordinary health-threshold event (2) lifts the prevention, which a plain attack reaches.
            events.Add(new CreatureAiEvent { Id = 940031, CreatureId = LiftedByHealthEntry, EventType = 11, Action1 = new CreatureAiAction(42, 1, 0, 0) });
            events.Add(new CreatureAiEvent
            {
                Id = 940032, CreatureId = LiftedByHealthEntry, EventType = 2, Param1 = 50, Action1 = new CreatureAiAction(42, 0, 0, 0),
            });

            List<CreatureTemplate> templates =
            [
                new() { Entry = GiverEntry, Name = "Foreman Thazz'ril", Faction = UnsupportedMechanicsFixture.FriendlyFaction, NpcFlags = 2, DisplayIds = [49] },
                new() { Entry = LiftedByHealthEntry, Name = "Held Until Hurt", Faction = UnsupportedMechanicsFixture.HostileFaction, DisplayIds = [49] },
                new() { Entry = UnsupportedMechanicsFixture.HostileMobEntry, Name = "Vile Familiar", Faction = UnsupportedMechanicsFixture.HostileFaction, DisplayIds = [49] },
            ];
            List<CreatureSpawn> spawns =
            [
                new() { Guid = 942001, Entry = GiverEntry, MapId = 0, X = StartX + 10f, Y = StartY, Z = StartZ },
                new() { Guid = 942002, Entry = LiftedByHealthEntry, MapId = 0, X = StartX + 40f, Y = StartY, Z = StartZ },
                new() { Guid = 942003, Entry = UnsupportedMechanicsFixture.HostileMobEntry, MapId = 0, X = StartX + 50f, Y = StartY, Z = StartZ },
            ];
            uint guid = 942010;
            foreach (uint servant in Servants)
            {
                templates.Add(new CreatureTemplate
                {
                    Entry = servant, Name = "Servant " + servant, Faction = UnsupportedMechanicsFixture.HostileFaction, DisplayIds = [49],
                    AIName = "EventAI",
                });
                spawns.Add(new CreatureSpawn { Guid = guid++, Entry = servant, MapId = 0, X = StartX + 60f, Y = StartY + (guid - 942010), Z = StartZ });
            }

            return Task.FromResult(new CreatureContent(templates, spawns, [], [], [], new CreatureAiContent(events, [])));
        }
    }

    private sealed class FarReturnFixture : IQuestContentStore, ICreatureDataStore, INpcContentStore
    {
        internal const uint LostDeathstalkersQuest = 428;
        internal const uint MessengerToStormwindQuest = 120;

        internal const uint HadrecEntry = 1952;
        internal const uint RaneYorickEntry = 1950;
        internal const uint SolomonEntry = 344;
        internal const uint MarcusEntry = 466;
        internal const uint FlightMasterEntry = 352;

        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
            [
                new QuestTemplate { Entry = LostDeathstalkersQuest, Method = 2, MinLevel = 1, QuestLevel = 12, Title = "Lost Deathstalkers" },
                new QuestTemplate { Entry = MessengerToStormwindQuest, Method = 2, MinLevel = 1, QuestLevel = 14, Title = "Messenger to Stormwind" },
            ],
            [
                new CreatureQuestRelation { Id = HadrecEntry, Quest = LostDeathstalkersQuest },
                new CreatureQuestRelation { Id = SolomonEntry, Quest = MessengerToStormwindQuest },
            ],
            [
                new CreatureQuestRelation { Id = RaneYorickEntry, Quest = LostDeathstalkersQuest },
                new CreatureQuestRelation { Id = MarcusEntry, Quest = MessengerToStormwindQuest },
            ]));

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                new CreatureTemplate { Entry = HadrecEntry, Name = "High Executor Hadrec", NpcFlags = 2, DisplayIds = [49] },
                new CreatureTemplate { Entry = RaneYorickEntry, Name = "Rane Yorick", NpcFlags = 2, DisplayIds = [49] },
                new CreatureTemplate { Entry = SolomonEntry, Name = "Magistrate Solomon", NpcFlags = 2, DisplayIds = [49] },
                new CreatureTemplate { Entry = MarcusEntry, Name = "General Marcus Jonathan", NpcFlags = 2, DisplayIds = [49] },
                new CreatureTemplate { Entry = FlightMasterEntry, Name = "Ariena Stormfeather", Faction = UnsupportedMechanicsFixture.FriendlyFaction, NpcFlags = (uint)NpcFlags.FlightMaster, DisplayIds = [49] },
            ],
            [
                // Silverpine starter and ender (876 yards apart, ~10,967 yards from StartX/StartY)
                new CreatureSpawn { Guid = 941001, Entry = HadrecEntry, MapId = 0, X = 522.4f, Y = 1626.2f, Z = 59.5f },
                new CreatureSpawn { Guid = 941002, Entry = RaneYorickEntry, MapId = 0, X = 1290.7f, Y = 1204.7f, Z = 51.5f },
                // Lakeshire starter + flight master inside Redridge home, and Stormwind ender 2,500 yards away
                new CreatureSpawn { Guid = 941003, Entry = SolomonEntry, MapId = 0, X = StartX + 20f, Y = StartY, Z = StartZ },
                new CreatureSpawn { Guid = 941004, Entry = FlightMasterEntry, MapId = 0, X = StartX + 10f, Y = StartY, Z = StartZ },
                new CreatureSpawn { Guid = 941005, Entry = MarcusEntry, MapId = 0, X = StartX + 2500f, Y = StartY, Z = StartZ },
            ], [], [], []));

        Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
        {
            TaxiNodes =
            [
                new TaxiNode { Id = 1, MapId = 0, X = StartX + 10f, Y = StartY, Z = StartZ, Name = "Lakeshire", MountHorde = 541, MountAlliance = 541 },
                new TaxiNode { Id = 2, MapId = 0, X = StartX + 2490f, Y = StartY, Z = StartZ, Name = "Stormwind", MountHorde = 541, MountAlliance = 541 },
            ],
            TaxiPaths =
            [
                new TaxiPath { Id = 1, FromNode = 1, ToNode = 2, Price = 100 },
            ],
        });
    }
}

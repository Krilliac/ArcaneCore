using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using System.Reflection;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

public sealed class QuestInteractionWorldTests
{
    /// <summary>
    /// Makes <paramref name="creature"/> invisible to <paramref name="player"/> the way the game does, with a visibility rule, so the state survives every later
    /// visibility pass. Removing the guid from <see cref="Player.VisibleObjects"/> by hand is undone by the next pass the map runs for that player or
    /// creature (any movement), which raced the socket requests that follow and let them see the creature again.
    /// </summary>
    private static void HideFrom(Player player, Creature creature)
    {
        player.Map!.AddVisibilityRule(new HideObject(creature.Guid));
        player.Map.RefreshVisibility(player);
        Assert.DoesNotContain(creature.Guid, player.VisibleObjects);
    }

    private sealed class HideObject(ObjectGuid guid) : IVisibilityRule
    {
        public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect) => target.Guid != guid;
    }

    [Fact]
    public void GreetingHandlersRunInWorldAndDoNotRegisterForeignNpcServices()
    {
        var table = new OpcodeTable();
        new QuestNpcInteractionHandlers().Register(table);
        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgGossipHello, WorldOpcode.CmsgQuestgiverHello })
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler handler));
            Assert.NotNull(handler.World);
            Assert.Null(handler.Session);
            Assert.Equal(SessionStates.InWorld, handler.AllowedStates);
        }

        foreach (WorldOpcode opcode in new[] { WorldOpcode.CmsgGossipSelectOption, WorldOpcode.CmsgListInventory,
            WorldOpcode.CmsgTrainerList, WorldOpcode.CmsgBuyItem })
        {
            Assert.False(table.TryGet(opcode, out _));
        }
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgGossipHello)]
    [InlineData(WorldOpcode.CmsgQuestgiverHello)]
    public async Task SocketGreetingOpensDetailsRequestAndOfferFromCurrentServerProgress(WorldOpcode hello)
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("GREETING", "Greeting");
        await VisibleAsync(host, "Greeting");
        await client.SendAsync(hello, GuidBody());
        byte[] details = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails);
        Assert.Equal(QuestInteractionFixture.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(details));
        Assert.Equal(QuestInteractionFixture.QuestId, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(8)));
        Assert.Equal(0u, await host.PlayerStateAsync("Greeting", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0, fixture.Characters.SaveCalls);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        QuestNpcFeature feature = await FeatureAsync(host, "Greeting");
        int id = await host.PlayerStateAsync("Greeting", p => checked((int)p.Guid.Low));
        await feature.Persistence.FlushCharacterAsync(id);
        int writesBeforeHello = fixture.Characters.SaveCalls;
        await client.SendAsync(hello, GuidBody());
        byte[] request = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverRequestItems);
        var reader = new PacketReader(request);
        Assert.Equal(QuestInteractionFixture.Guid.Value, reader.ReadUInt64());
        Assert.Equal(QuestInteractionFixture.QuestId, reader.ReadUInt32());
        reader.ReadCString();
        Assert.Equal("Finish the synthetic task.", reader.ReadCString());
        Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(id, QuestInteractionFixture.QuestId).Status);
        Assert.Equal(0u, fixture.Characters.Stored(id, QuestInteractionFixture.QuestId).MobCount1);
        Assert.Equal(writesBeforeHello, fixture.Characters.SaveCalls);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Greeting")!;
            feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
        });
        await feature.Persistence.FlushCharacterAsync(id);
        writesBeforeHello = fixture.Characters.SaveCalls;
        await client.SendAsync(hello, GuidBody());
        byte[] offer = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverOfferReward);
        Assert.Equal(QuestInteractionFixture.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(offer));
        Assert.Equal(QuestInteractionFixture.QuestId, BinaryPrimitives.ReadUInt32LittleEndian(offer.AsSpan(8)));
        CharacterQuestStatus completed = fixture.Characters.Stored(id, QuestInteractionFixture.QuestId);
        Assert.Equal((byte)QuestStatus.Complete, completed.Status);
        Assert.Equal(1u, completed.MobCount1);
        Assert.False(completed.Rewarded);
        Assert.Equal(writesBeforeHello, fixture.Characters.SaveCalls);
    }

    [Theory]
    [InlineData("visibility")]
    [InlineData("hostile")]
    [InlineData("unknown")]
    [InlineData("distance")]
    [InlineData("deadnpc")]
    [InlineData("deadplayer")]
    [InlineData("wrongguid")]
    [InlineData("foreignmap")]
    [InlineData("charmed")]
    public async Task SocketInvalidGreetingsDoNotOpenMenusOrChangeQuestState(string guard)
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("BADGREETING", "Badgreeting");
        await VisibleAsync(host, "Badgreeting");
        await client.CollectAsync();
        ObjectGuid requested = QuestInteractionFixture.Guid;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Badgreeting")!;
            var map = player.Map!;
            var creature = (Creature)map.FindObject(QuestInteractionFixture.Guid)!;
            switch (guard)
            {
                case "visibility": HideFrom(player, creature); break;
                case "hostile": creature.FactionTemplate = 900012; break;
                case "unknown": creature.FactionTemplate = 999999; break;
                case "distance": creature.Relocate(player.X + 20, player.Y, player.Z, 0, host.World.NowMs); break;
                case "deadnpc": creature.Health = 0; break;
                case "deadplayer": player.Health = 0; break;
                case "wrongguid": requested = player.Guid; break;
                case "foreignmap": map.RemoveObject(creature); host.World.GetMap(1).AddObject(creature); break;
                case "charmed": creature.SetUInt64(UpdateFields.UnitFieldCharmedby, player.Guid.Value); break;
            }
        });
        await client.SendAsync(WorldOpcode.CmsgGossipHello, GuidBody(requested));
        await client.SendAsync(WorldOpcode.CmsgQuestgiverHello, GuidBody(requested));
        // The unrelated accept response acknowledges that both earlier greetings ran.
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(QuestInteractionFixture.UnrelatedId));
        while (true)
        {
            var (opcode, _) = await client.ReadAsync();
            Assert.NotEqual(WorldOpcode.SmsgGossipMessage, opcode);
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverQuestList, opcode);
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverQuestDetails, opcode);
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverRequestItems, opcode);
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverOfferReward, opcode);
            if (opcode == WorldOpcode.SmsgGossipComplete) break;
        }

        Assert.Equal(0u, await host.PlayerStateAsync("Badgreeting", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0, fixture.Characters.SaveCalls);
    }

    [Fact]
    public async Task SocketQuestFlow_DetailsAcceptPersistAbandonAndRelog()
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        byte[] key = await host.AddAccountAsync("INTERACTION");
        int characterId;
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync("INTERACTION", key);
            await client.CreateCharacterAsync("Interaction");
            var account = (await host.Accounts.FindByUsernameAsync("INTERACTION"))!;
            characterId = (await host.Characters.GetByAccountAsync(account.Id)).Single().Id;
            await client.LoginAsync((ulong)characterId);
            await VisibleAsync(host, "Interaction");
            await client.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, GuidBody());
            byte[] status = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverStatus);
            Assert.Equal(12, status.Length);
            Assert.Equal(QuestInteractionFixture.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(status));
            Assert.Equal((uint)DialogStatus.Available, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)));
            await client.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody());
            byte[] details = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails);
            Assert.Equal(QuestInteractionFixture.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(details));
            Assert.Equal(QuestInteractionFixture.QuestId, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(8)));

            await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
            await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
            QuestNpcFeature feature = await FeatureAsync(host, "Interaction");
            await feature.Persistence.FlushCharacterAsync(characterId);
            CharacterQuestStatus accepted = fixture.Characters.Stored(characterId, QuestInteractionFixture.QuestId);
            Assert.Equal((byte)QuestStatus.Incomplete, accepted.Status);
            Assert.True(accepted.Timer > fixture.Clock.GetUtcNow().ToUnixTimeSeconds());
            Assert.Equal(QuestInteractionFixture.QuestId,
                await host.PlayerStateAsync("Interaction", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
            int saves = fixture.Characters.SaveCalls;
            await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
            await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
            await feature.Persistence.FlushCharacterAsync(characterId);
            Assert.Equal(saves, fixture.Characters.SaveCalls);

            await client.SendAsync(WorldOpcode.CmsgQuestlogRemoveQuest, [0]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Interaction")!.GetUInt32(UpdateFields.PlayerQuestLog11) == 0,
                "abandoned quest leaves the log");
            await feature.Persistence.FlushCharacterAsync(characterId);
            CharacterQuestStatus abandoned = fixture.Characters.Stored(characterId, QuestInteractionFixture.QuestId);
            Assert.Equal((byte)QuestStatus.None, abandoned.Status);
            Assert.Equal(0, abandoned.Timer);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "quest character disconnects");
        await using WorldTestClient relog = await host.ConnectAsync();
        await relog.AuthenticateAsync("INTERACTION", key);
        await relog.LoginAsync((ulong)characterId);
        Assert.Equal(0u, await host.PlayerStateAsync("Interaction", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        await VisibleAsync(host, "Interaction");
        await relog.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
        await relog.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        await (await FeatureAsync(host, "Interaction")).Persistence.FlushCharacterAsync(characterId);
        Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(characterId, QuestInteractionFixture.QuestId).Status);
    }

    [Theory]
    [InlineData("visibility")]
    [InlineData("hostile")]
    [InlineData("unknown")]
    [InlineData("distance")]
    [InlineData("relation")]
    [InlineData("dead")]
    [InlineData("flag")]
    public async Task SocketInvalidInteractions_CannotAcceptOrReadDetails(string guard)
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("GUARDED", "Guarded");
        await VisibleAsync(host, "Guarded");
        uint requested = guard == "relation" ? QuestInteractionFixture.UnrelatedId : QuestInteractionFixture.QuestId;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Guarded")!;
            var creature = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            switch (guard)
            {
                case "visibility": HideFrom(player, creature); break;
                case "hostile": creature.FactionTemplate = 900012; break;
                case "unknown": creature.FactionTemplate = 999999; break;
                case "distance": creature.Relocate(player.X + 20, player.Y, player.Z, 0, host.World.NowMs); break;
                case "dead": creature.Health = 0; break;
                case "flag": creature.NpcFlags = 0; break;
            }
        });
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody(requested));
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(requested));
        // The map processes both requests in socket order. The accept response proves
        // the preceding details request ran; inspect every intervening packet for a leak.
        while (true)
        {
            var (opcode, _) = await client.ReadAsync();
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverQuestDetails, opcode);
            if (opcode == WorldOpcode.SmsgGossipComplete) break;
        }
        Assert.Equal(0u, await host.PlayerStateAsync("Guarded", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0, fixture.Characters.SaveCalls);
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgGossipHello, 7)]
    [InlineData(WorldOpcode.CmsgGossipHello, 9)]
    [InlineData(WorldOpcode.CmsgQuestgiverHello, 7)]
    [InlineData(WorldOpcode.CmsgQuestgiverHello, 9)]
    [InlineData(WorldOpcode.CmsgQuestgiverStatusQuery, 7)]
    [InlineData(WorldOpcode.CmsgQuestgiverStatusQuery, 9)]
    [InlineData(WorldOpcode.CmsgQuestgiverQueryQuest, 11)]
    [InlineData(WorldOpcode.CmsgQuestgiverAcceptQuest, 16)]
    [InlineData(WorldOpcode.CmsgQuestlogRemoveQuest, 0)]
    [InlineData(WorldOpcode.CmsgQuestlogRemoveQuest, 2)]
    public async Task MalformedInteractionPayload_Disconnects(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = Start(new QuestInteractionFixture());
        await using WorldTestClient client = await host.EnterWorldAsync("BADINTERACTION", "Badinteract");
        await client.CollectAsync();
        await client.SendAsync(opcode, new byte[length]);
        Assert.True(await client.IsClosedByServerAsync());
    }

    /// <summary>
    /// The quest is accepted and completed first (its two linen cloth are in the bags), so <c>RewardBase</c> passes and the open trade is the only
    /// difference between the two cases: with no trade the turn-in settles (the positive control), with the delivery stack in a trade slot it is
    /// refused and the stack keeps its two items.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChooseReward_DeliveryItemOfferedInOpenTrade_RefusesTurnInOnlyWhileTheTradeIsOpen(bool tradeOpen)
    {
        const uint cloth = 2589;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = cloth, Class = 7, Name = "Linen Cloth", DisplayId = 1, Stackable = 20, Quality = 1 });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, cloth, 2));

        var fixture = new QuestInteractionFixture
        {
            RepeatableItem = cloth,
            RepeatableItemCount = 2,
        };

        WorldTestHost host;
        using (items.Use())
        {
            host = Start(fixture);
        }

        await using (host)
        await using (WorldTestClient crafter = await host.EnterWorldAsync("CRAFTER", "Crafter"))
        await using (WorldTestClient partner = await host.EnterWorldAsync("PARTNER", "Partner"))
        {
            await VisibleAsync(host, "Crafter");
            await VisibleAsync(host, "Partner");
            await crafter.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
            await crafter.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
            QuestNpcFeature feature = await FeatureAsync(host, "Crafter");

            (QuestStatus status, bool settlementStarted, uint countAfter) = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Crafter")!;
                Player other = host.World.FindOnlinePlayer("Partner")!;
                QuestStatus status = feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status;
                Item stack = Assert.Single(player.Inventory.AllItems, i => i.Entry == cloth);
                Assert.Equal(2u, stack.Count);

                if (tradeOpen)
                {
                    OfferInOpenTrade(host, player, other, stack);
                }

                // Drives the real wiring: QuestNpcFeature.ChooseReward extracts TradeOf -> SideOf -> TradedItems.
                feature.ChooseReward((WorldSession)player.Session, player, QuestInteractionFixture.Guid, QuestInteractionFixture.QuestId, 0);

                return (status, player.IsQuestSettlementPending, player.Inventory.GetItemCount(cloth));
            });

            Assert.Equal(QuestStatus.Complete, status);
            Assert.Equal(!tradeOpen, settlementStarted);
            Assert.Equal(2u, countAfter);
            if (!tradeOpen)
            {
                int id = await host.PlayerStateAsync("Crafter", p => checked((int)p.Guid.Low));

                // Let the started settlement finish so the host does not shut down under it; only that it started is asserted here.
                await feature.WaitForSettlementAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>
    /// CMSG_QUESTGIVER_COMPLETE_QUEST previews the turn-in: the "completable" flag of the request-items window must follow the same offered-item
    /// rule as the settlement (<c>QuestNpcInteractionHandlers.Complete</c> passes the open trade's items), or the client is told it may hand in a stack
    /// the turn-in will then refuse.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteQuest_DeliveryItemOfferedInOpenTrade_IsNotCompletableOnlyWhileTheTradeIsOpen(bool tradeOpen)
    {
        const uint cloth = 2589;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = cloth, Class = 7, Name = "Linen Cloth", DisplayId = 1, Stackable = 20, Quality = 1 });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, cloth, 2));

        var fixture = new QuestInteractionFixture
        {
            RepeatableItem = cloth,
            RepeatableItemCount = 2,
            DeliverItems = true,
        };

        WorldTestHost host;
        using (items.Use())
        {
            host = Start(fixture);
        }

        await using (host)
        await using (WorldTestClient crafter = await host.EnterWorldAsync("CRAFTER", "Crafter"))
        await using (WorldTestClient partner = await host.EnterWorldAsync("PARTNER", "Partner"))
        {
            await VisibleAsync(host, "Crafter");
            await VisibleAsync(host, "Partner");
            await crafter.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
            await crafter.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
            if (tradeOpen)
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = host.World.FindOnlinePlayer("Crafter")!;
                    OfferInOpenTrade(host, player, host.World.FindOnlinePlayer("Partner")!, Assert.Single(player.Inventory.AllItems, i => i.Entry == cloth));
                });
            }

            await crafter.SendAsync(WorldOpcode.CmsgQuestgiverCompleteQuest, QuestBody());
            byte[] window = await crafter.ReadUntilAsync(WorldOpcode.SmsgQuestgiverRequestItems);
            Assert.Equal(!tradeOpen, QuestPackets.IsRequestItemsCompletable(window));
        }
    }

    /// <summary>Opens a trade between the two players with <paramref name="stack"/> in the first of the offering side's six slots.</summary>
    private static void OfferInOpenTrade(WorldTestHost host, Player player, Player other, Item stack)
    {
        EconomyFeature economy = host.WorldServices.GetRequiredService<EconomyFeature>();
        var trades = (IDictionary)typeof(EconomyFeature).GetField("_trades", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(economy)!;
        var trade = new TradeSession(player, other) { Opened = true };
        trades[player] = trade;
        trades[other] = trade;
        trade.SideOf(player)[0] = stack.Guid;
    }

    private static WorldTestHost Start(QuestInteractionFixture fixture)
    {
        QuestInteractionTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestInteractionTestServices.Current.Value = null; }
    }

    private static Task VisibleAsync(WorldTestHost host, string name) => host.WaitForWorldAsync(
        () => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver becomes visible");

    private static Task<QuestNpcFeature> FeatureAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name,
        p => ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>());

    private static byte[] GuidBody(ObjectGuid? guid = null)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64((guid ?? QuestInteractionFixture.Guid).Value);
        return writer.ToArray();
    }

    private static byte[] QuestBody(uint id = QuestInteractionFixture.QuestId)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        writer.WriteUInt32(id);
        return writer.ToArray();
    }

    private sealed class HideQuestTargetRule(ObjectGuid viewer, ObjectGuid target) : IVisibilityRule
    {
        public bool CanSee(Player candidate, WorldObject visible, bool alreadyVisible, bool detect)
            => candidate.Guid != viewer || visible.Guid != target;
    }
}

internal sealed class QuestInteractionTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<QuestInteractionFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddSingleton(new FactionTemplateCatalog([
            new(1, 1, 0, 1, 0, 0), new(900011, 0, 0, 8, 0, 0), new(900012, 0, 0, 8, 0, 1)]));
    }
}

internal sealed class QuestInteractionFixture : IQuestContentStore, ICreatureDataStore
{
    public const uint QuestId = 900001;
    public const uint UnrelatedId = 900002;
    public const uint Entry = 900010;
    public const uint RemoteEnder = 900011;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 900020);
    public MemoryQuestStore Characters { get; } = new();
    public ManualQuestClock Clock { get; } = new();

    /// <summary>The item the quest hands out on accept (SrcItemId), none by default.</summary>
    public uint SourceItem { get; init; }

    /// <summary>An item-only repeatable starter, when set (the normal fixture remains a kill quest).</summary>
    public uint RepeatableItem { get; init; }
    public uint RepeatableItemCount { get; init; }

    /// <summary>Marks the item-only starter as a delivery quest (QUEST_SPECIAL_FLAG_DELIVER), so the turn-in checks the items before staging.</summary>
    public bool DeliverItems { get; init; }
    public bool EnderOnOtherMap { get; init; }
    public bool DistantEnder { get; init; }

    /// <summary>The remote ender's spawns, when set (overrides <see cref="EnderOnOtherMap"/> and <see cref="DistantEnder"/>).</summary>
    public CreatureSpawn[]? EnderSpawns { get; init; }

    /// <summary>A quest with no objectives at all (complete the moment it is taken), for turn-ins that can never be satisfied.</summary>
    public bool NoObjectives { get; init; }

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
        [new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, LimitTime = 30, SrcItemId = SourceItem, SrcItemCount = (byte)(SourceItem == 0 ? 0 : 1),
            Title = "Synthetic quest", Details = "Accept a bounded journal task.", RequestItemsText = "Finish the synthetic task.",
            SpecialFlags = (byte)((RepeatableItem == 0 ? 0 : 1) | (DeliverItems ? 8 : 0)), ReqItemId1 = RepeatableItem, ReqItemCount1 = RepeatableItemCount,
            ReqCreatureOrGOId1 = RepeatableItem == 0 && !NoObjectives ? 90 : 0, ReqCreatureOrGOCount1 = RepeatableItem == 0 && !NoObjectives ? 1u : 0u },
        new QuestTemplate { Entry = UnrelatedId, Method = 2, Title = "Unrelated quest" }],
        [new CreatureQuestRelation { Id = Entry, Quest = QuestId }],
        [new CreatureQuestRelation { Id = EnderOnOtherMap || DistantEnder || EnderSpawns is not null ? RemoteEnder : Entry, Quest = QuestId }]));

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [new CreatureTemplate { Entry = Entry, Name = "Synthetic questgiver", Faction = 900011, NpcFlags = 2, DisplayIds = [49] },
         new CreatureTemplate { Entry = RemoteEnder, Name = "Remote ender", Faction = 900011, NpcFlags = 2, DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 900020, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f },
         .. (EnderSpawns is { } enders ? enders : EnderOnOtherMap || DistantEnder
             ? new[] { new CreatureSpawn { Guid = 900021, Entry = RemoteEnder, MapId = EnderOnOtherMap ? 1u : 0u,
                 X = DistantEnder ? -6948.95f : -8948.95f, Y = -132.49f, Z = 83.53f } }
             : Array.Empty<CreatureSpawn>())], [], [], []));
}

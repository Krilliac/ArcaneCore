using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Quest 4402 "Galgar's Cactus Apple Surprise": the Cactus Apple (gameobject 171938) is a type 3 chest with flags 4 (INTERACT_COND), lock 43
/// (one Lock.dbc case: skill, lock type 13 "open - kneeling", no required skill), loot 11500 holding the quest item 11583 at -100 and
/// data3 1 (consumable). With the quest in the log a click must open the loot window with the apple, taking it must raise the quest
/// counter once, and the emptied chest must go away.
/// </summary>
public sealed class QuestChestUseWorldTests
{
    private const uint QuestId = 4402;
    private const uint Apple = 11583;
    private const uint ChestEntry = 171938;
    private const uint ChestSpawn = 77002;
    private const uint LootId = 11500;
    private const uint LockId = 43;

    private static ulong ChestGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;

    private static WorldTestHost Start(QuestJournalFixture quests, out GameObjectTestContext context, Characters.Creation.LogCapture? logs = null)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = LockId;
        data[1] = LootId;
        data[3] = 1;
        var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 3451, Name = "Cactus Apple", Flags = 4, Data = data };
        var spawn = new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        // Lock.dbc 43 as imported: case 2 is a skill case of lock type 13, required skill 0.
        var lockEntry = new LockEntry(LockId, [0, 2, 0, 0, 0, 0, 0, 0], [0, 13, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);
        var goContent = new GameObjectContent([chest], [spawn], [lockEntry], [], []);
        var lootContent = new LootContent([(LootTableKind.GameObject, new LootStoreRow(LootId, Apple, -100f, 0, 1, 1))], []);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Apple, Class = 12, Name = "Cactus Apple", DisplayId = 4003, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        QuestNpcTestServices.Current.Value = quests;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: services =>
                {
                    Configure(services);
                    logs?.Register(services);
                });
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
            QuestNpcTestServices.Current.Value = null;
        }
    }

    /// <summary>Spell 6478 "Opening" as imported: OPEN_LOCK (33), lock type 13, target mask GAMEOBJECT|LOCKED (0x4000), range 12.</summary>
    private const uint OpeningKneeling = 6478;

    private static void Configure(IServiceCollection services)
    {
        GatheringWorldTests.Configure(services);
        services.AddSingleton<ISpellContentStore>(new Skills.InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = OpeningKneeling, SpellName = "Opening", RangeIndex = 12, Targets = 0x4000, CastingTimeIndex = 6, Attributes = 65936,
                    Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = 13, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            [new SpellCastTimeRow { Id = 6, CastTime = 5000, MinCastTime = 5000 }], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = OpeningKneeling }],
            [])));
    }

    private static QuestJournalFixture Quests() => new()
    {
        Templates = [new QuestTemplate { Entry = QuestId, Method = 2, QuestLevel = 3, MinLevel = 1, Title = "Galgar's Cactus Apple Surprise", ReqItemId1 = Apple, ReqItemCount1 = 10 }],
    };

    private static async Task<WorldTestClient> EnterWithQuestAsync(WorldTestHost host, QuestJournalFixture quests)
    {
        WorldTestClient client = await host.ConnectAsync();
        byte[] key = await host.AddAccountAsync("APPLER");
        await client.AuthenticateAsync("APPLER", key);
        await client.CreateCharacterAsync("Appler");
        var account = (await host.Accounts.FindByUsernameAsync("APPLER"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        quests.Characters.Seed(new CharacterQuestStatus(character.Id, QuestId, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        await client.LoginAsync((ulong)character.Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Appler") is { } p
            && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true }, "the quest journal to load");
        return client;
    }

    [Fact]
    public async Task ClickingTheCactusApple_WithTheQuest_OpensTheLootWithTheApple_AndTheChestIsUsedUp()
    {
        QuestJournalFixture quests = Quests();
        await using WorldTestHost host = Start(quests, out GameObjectTestContext context);
        await using WorldTestClient client = await EnterWithQuestAsync(host, quests);
        ulong chest = ChestGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, chest);

        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(chest));
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(chest, loot.ReadUInt64());
        loot.ReadByte();                              // loot type
        Assert.Equal(0u, loot.ReadUInt32());          // no gold
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());       // slot
        Assert.Equal(Apple, loot.ReadUInt32());

        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(chest));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);

        (uint inBags, uint counter) = await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Appler")!;
            var services = ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services;
            return (p.Inventory.GetItemCount(Apple), services.StateOf(p)!.Quests.Get(QuestId)!.ItemCount[0]);
        });
        Assert.Equal((1u, 1u), (inBags, counter));
        await host.WaitForWorldAsync(
            () => context.Feature!.FindSystem(0)!.Find(new ObjectGuid(chest)) is { IsSpawned: false },
            "the used-up apple despawns");
    }

    [Fact]
    public async Task CastingOpeningOnTheCactusApple_WithTheQuest_RunsTheCastBar_OpensTheLoot_CountsTheApple_AndUsesTheChestUp()
    {
        // What the 5875 client does for lock 43 (lock type 13, docs: WoW-RE-Research protocol/gameobject-interaction.md): CMSG_CAST_SPELL of the
        // known "Opening" spell at the object, never CMSG_GAMEOBJ_USE. The chest is ready (state 1) and not locked (flag 0x2 clear).
        QuestJournalFixture quests = Quests();
        await using WorldTestHost host = Start(quests, out GameObjectTestContext context);
        await using WorldTestClient client = await EnterWithQuestAsync(host, quests);
        ulong chest = ChestGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, chest);
        (GameObjectState state, uint flags) = await host.OnWorldAsync(() =>
        {
            GameObject go = context.Feature!.FindSystem(0)!.Find(new ObjectGuid(chest))!;
            return (go.State, (uint)go.Flags);
        });
        Assert.Equal(GameObjectState.Ready, state);
        Assert.Equal(0u, flags & 0x2);

        var cast = new PacketWriter(16);
        cast.WriteUInt32(OpeningKneeling);
        new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = new ObjectGuid(chest) }.Write(cast);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);       // the cast bar
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(chest, loot.ReadUInt64());
        loot.ReadByte();
        Assert.Equal(0u, loot.ReadUInt32());
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());
        Assert.Equal(Apple, loot.ReadUInt32());

        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(chest));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        (uint inBags, uint counter) = await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Appler")!;
            var services = ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services;
            return (p.Inventory.GetItemCount(Apple), services.StateOf(p)!.Quests.Get(QuestId)!.ItemCount[0]);
        });
        Assert.Equal((1u, 1u), (inBags, counter));
        await host.WaitForWorldAsync(
            () => context.Feature!.FindSystem(0)!.Find(new ObjectGuid(chest)) is { IsSpawned: false },
            "the used-up apple despawns");
    }

    /// <summary>
    /// An Opening that went through its own range check (5 yd plus 1.25 yd leeway, centre to centre) but lands 5.3 yd from the apple, past the
    /// 5 yd the object takes an open from: the cast completes and nothing opens. The client is told nothing (no loot window, no error), so the
    /// realm log must say which object refused the open and why.
    /// </summary>
    [Fact]
    public async Task AnOpeningRefusedByTheObject_AfterTheCastWentThrough_OpensNothing_AndLogsWhy()
    {
        var logs = new Characters.Creation.LogCapture();
        QuestJournalFixture quests = Quests();
        await using WorldTestHost host = Start(quests, out GameObjectTestContext context, logs);
        await using WorldTestClient client = await EnterWithQuestAsync(host, quests);
        ulong chest = ChestGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, chest);
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Appler")!;
            GameObject go = context.Feature!.FindSystem(0)!.Find(new ObjectGuid(chest))!;
            p.Relocate(go.X + 5.3f, go.Y, go.Z, p.Orientation, host.World.NowMs);
        });

        var cast = new PacketWriter(16);
        cast.WriteUInt32(OpeningKneeling);
        new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = new ObjectGuid(chest) }.Write(cast);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        List<(WorldOpcode Opcode, byte[] Payload)> after = await client.CollectFromAsync(WorldOpcode.SmsgSpellGo);
        Assert.DoesNotContain(after, p => p.Opcode == WorldOpcode.SmsgLootResponse);

        string line = Assert.Single(logs.Lines, l => l.Contains("refused the open", StringComparison.Ordinal));
        Assert.Contains("Spell 6478 (Opening) by Appler", line, StringComparison.Ordinal);
        Assert.Contains("'Cactus Apple' (type Chest, lock 43, lock type 13", line, StringComparison.Ordinal);
        Assert.EndsWith(": TooFar", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCactusApple_IsActivatedAndSparkles_ForAPlayerWhoseQuestNeedsTheApple()
    {
        QuestJournalFixture quests = Quests();
        await using WorldTestHost host = Start(quests, out GameObjectTestContext context);
        await using WorldTestClient client = await EnterWithQuestAsync(host, quests);
        ulong chest = ChestGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, chest);

        GameObjectDynFlags flags = await host.OnWorldAsync(() =>
        {
            GameObjectMapSystem system = context.Feature!.FindSystem(0)!;
            return system.QuestFlagsFor(system.Find(new ObjectGuid(chest))!, host.World.FindOnlinePlayer("Appler")!);
        });
        Assert.Equal(GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle, flags);
    }

    /// <summary>
    /// The chest's GAMEOBJECT_DYN_FLAGS as this client received them: from its first create block when <paramref name="wantCreate"/>, else
    /// from the first values block that sets the field, and from either kind when it is null. Null when no such block arrives in time.
    /// </summary>
    private static async Task<uint?> ReceivedDynFlagsAsync(WorldTestClient client, ulong chest, bool? wantCreate)
    {
        while (true)
        {
            Task<(WorldOpcode Op, byte[] Payload)> read = client.ReadAsync();
            if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(6))) != read)
            {
                return null;
            }

            (WorldOpcode op, byte[] payload) = await read;
            byte[]? body = op switch
            {
                WorldOpcode.SmsgUpdateObject => payload,
                WorldOpcode.SmsgCompressedUpdateObject => WorldTestClient.Inflate(payload),
                _ => null,
            };
            if (body is null)
            {
                continue;
            }

            foreach (UpdateBlock block in UpdateBlockReader.Parse(body))
            {
                bool isCreate = block.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2;
                if (block.Guid != chest || (wantCreate is { } only && isCreate != only))
                {
                    continue;
                }

                if (isCreate && wantCreate == true)
                {
                    return block.Values.GetValueOrDefault(UpdateFields.GameobjectDynFlags);
                }

                if (block.Values.TryGetValue(UpdateFields.GameobjectDynFlags, out uint value))
                {
                    return value;
                }
            }
        }
    }

    private const uint ActivateAndSparkle = (uint)(GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle);

    [Fact]
    public async Task ACactusAppleSeenBeforeTheQuest_GetsAnActivateUpdate_WhenTheQuestIsAccepted()
    {
        QuestJournalFixture quests = Quests();
        await using WorldTestHost host = Start(quests, out _);
        await using WorldTestClient client = await host.ConnectAsync();
        byte[] key = await host.AddAccountAsync("LATEQ");
        await client.AuthenticateAsync("LATEQ", key);
        await client.CreateCharacterAsync("Lateq");
        await client.LoginAsync((ulong)(await host.Characters.GetByAccountAsync((await host.Accounts.FindByUsernameAsync("LATEQ"))!.Id)).Single().Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lateq") is { } p
            && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true }, "the quest journal to load");
        ulong chest = ChestGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, chest);

        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Lateq")!;
            var services = ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services;
            services.GmAddQuest(p, services.Quests.Get(QuestId)!);
        });

        Assert.Equal(ActivateAndSparkle, await ReceivedDynFlagsAsync(client, chest, wantCreate: false));
    }

    [Fact]
    public async Task ACactusAppleForAPlayerWhoAlreadyHasTheQuest_ReachesTheClientWithActivate()
    {
        QuestJournalFixture quests = Quests();
        await using WorldTestHost host = Start(quests, out _);
        await using WorldTestClient client = await EnterWithQuestAsync(host, quests);
        Assert.Equal(ActivateAndSparkle, await ReceivedDynFlagsAsync(client, ChestGuid(), wantCreate: null));
    }
}

using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Skills;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The game object type behaviours over the loopback daemon: a key used on a locked chest (CMSG_USE_ITEM, the key's open-lock spell, its
/// charge), a spell-caster object casting at its user through the world's spell system, the meeting stone opcodes, and accepting a summon.
/// </summary>
public sealed class GameObjectTypesWorldTests
{
    // Human start is (-8949.95, -132.49, 83.53): every object below is within two yards of it.
    private const float X = -8948f;
    private const float Y = -132.5f;
    private const float Z = 83.5f;

    private const uint ChestEntry = 2850;
    private const uint ChestSpawn = 78001;
    private const uint ChestLoot = 2850;
    private const uint ChestLock = 30;
    private const uint DullIronKey = 3467;
    private const uint OpeningSpell = 3366;
    private const uint LinenCloth = 2589;
    private const uint WellEntry = 2851;
    private const uint WellSpawn = 78002;
    private const uint WellSpell = 9300;
    private const uint StoneEntry = 178828;
    private const uint StoneSpawn = 78003;
    private const uint StoneArea = 719;

    private static ObjectGuid Guid(uint entry, uint spawn) => ObjectGuid.WithEntry(HighGuid.GameObject, entry, spawn);

    [Fact]
    public async Task KeyUsedOnALockedChest_OpensIt_AndAnExpendableKeyIsSpent()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GOKEY", "Gokey");
        Player player = await host.PlayerAsync("Gokey");
        Item key = await host.OnWorldAsync(() =>
        {
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(DullIronKey, 1, out Item? added));
            return added!;
        });

        var use = new PacketWriter(16);
        use.WriteByte(key.BagSlot);
        use.WriteByte(key.Slot);
        use.WriteByte(0);
        new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = Guid(ChestEntry, ChestSpawn) }.Write(use);
        await client.SendAsync(WorldOpcode.CmsgUseItem, use.ToArray());

        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(Guid(ChestEntry, ChestSpawn).Value, loot.ReadUInt64());
        await host.WaitForWorldAsync(() => player.Inventory.GetItemCount(DullIronKey) == 0, "the key is spent");
    }

    [Fact]
    public async Task ClickingASpellCaster_CastsItsSpellAtTheUser_ThroughTheWorldSpellSystem()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GOWELL", "Gowell");
        Player player = await host.PlayerAsync("Gowell");
        await host.OnWorldAsync(() => player.Health = 10);

        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(Guid(WellEntry, WellSpawn).Value));
        await host.WaitForWorldAsync(() => player.Health > 10, "the well's heal lands");
    }

    [Fact]
    public async Task MeetingStone_JoinInfoLeave_OverTheWire()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GOSTONE", "Gostone");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgMeetingstoneJoin, BitConverter.GetBytes(Guid(StoneEntry, StoneSpawn).Value));
        Assert.Equal(MeetingStonePackets.SetQueue(StoneArea, MeetingStoneStatus.JoinedQueue), await client.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));

        await client.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);
        Assert.Equal(MeetingStonePackets.SetQueue(StoneArea, MeetingStoneStatus.JoinedQueue), await client.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));

        await client.SendAsync(WorldOpcode.CmsgMeetingstoneLeave, []);
        Assert.Equal(MeetingStonePackets.SetQueue(0, MeetingStoneStatus.LeaveQueue), await client.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));

        await client.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);
        Assert.Equal(MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None), await client.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));
    }

    [Fact]
    public async Task MeetingStone_JoinFromTooFarOrAnotherObject_IsIgnored()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GOFAR", "Gofar");
        await client.CollectAsync();

        // Not a meeting stone, then the stone from 30 yards away: no answer, no queue.
        await client.SendAsync(WorldOpcode.CmsgMeetingstoneJoin, BitConverter.GetBytes(Guid(ChestEntry, ChestSpawn).Value));
        await host.PlaceAsync("Gofar", X + 30, Y, Z);
        await client.SendAsync(WorldOpcode.CmsgMeetingstoneJoin, BitConverter.GetBytes(Guid(StoneEntry, StoneSpawn).Value));
        await client.SendAsync(WorldOpcode.CmsgMeetingstoneInfo, []);
        Assert.Equal(MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None), await client.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));
        Player player = await host.PlayerAsync("Gofar");
        Assert.False(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<MeetingStoneFeature>().Queue!.IsPlayerQueued(player.Guid)));
    }

    [Fact]
    public async Task MeetingStone_APartyMemberWhoDoesNotLead_IsRefused()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient leader = await host.EnterWorldAsync("GOLEAD", "Golead");
        await using WorldTestClient member = await host.EnterWorldAsync("GOMEMB", "Gomemb");
        byte[] invite = [.. System.Text.Encoding.UTF8.GetBytes("Gomemb"), 0];
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, invite);
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupList);
        await member.CollectAsync();

        await member.SendAsync(WorldOpcode.CmsgMeetingstoneJoin, BitConverter.GetBytes(Guid(StoneEntry, StoneSpawn).Value));
        byte[] failed = await member.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneJoinfailed);
        Assert.Equal(new[] { (byte)MeetingStoneFailure.PartyLeader }, failed);

        // The leader queues the whole party; the member hears it too.
        await leader.SendAsync(WorldOpcode.CmsgMeetingstoneJoin, BitConverter.GetBytes(Guid(StoneEntry, StoneSpawn).Value));
        Assert.Equal(MeetingStonePackets.SetQueue(StoneArea, MeetingStoneStatus.JoinedQueue), await member.ReadUntilAsync(WorldOpcode.SmsgMeetingstoneSetqueue));
    }

    [Fact]
    public async Task AcceptingASummon_TeleportsToTheSummonPoint()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GOSUMM", "Gosumm");
        Player player = await host.PlayerAsync("Gosumm");
        uint now = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<World.Spells.SpellFeature>().System.NowMs);
        await host.OnWorldAsync(() => player.PendingSummon = new PendingSummon(ObjectGuid.Player(424242), 0, X + 20, Y + 5, Z, now + Player.SummonAcceptMs));

        await client.SendAsync(WorldOpcode.CmsgSummonResponse, BitConverter.GetBytes(ObjectGuid.Player(424242).Value));

        // A same-map summon is a near teleport: the client acknowledges MSG_MOVE_TELEPORT_ACK, then the player is there.
        var ack = new PacketReader(await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck));
        ulong guid = ack.ReadPackedGuid();
        uint counter = ack.ReadUInt32();
        var reply = new PacketWriter(16);
        reply.WriteUInt64(guid);
        reply.WriteUInt32(counter);
        reply.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
        await host.WaitForWorldAsync(() => Math.Abs(player.X - (X + 20)) < 0.5f && Math.Abs(player.Y - (Y + 5)) < 0.5f, "the summon teleport");
        Assert.Null(await host.OnWorldAsync(() => player.PendingSummon));
    }

    /// <summary>The skills feature needs its catalog to install the open-lock effect (GatheringSpells); the key needs no skill.</summary>
    private static IServiceCollection Configure(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog([], [], [], []));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        return services;
    }

    private static WorldTestHost Start()
    {
        uint[] chestData = new uint[GameObjectTemplate.DataCount];
        chestData[0] = ChestLock;
        chestData[1] = ChestLoot;
        var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Locked Chest", Data = chestData };
        uint[] wellData = new uint[GameObjectTemplate.DataCount];
        wellData[0] = WellSpell;
        var well = new GameObjectTemplate { Entry = WellEntry, Type = (uint)GameObjectType.SpellCaster, DisplayId = 6671, Name = "Test Well", Data = wellData };
        uint[] stoneData = new uint[GameObjectTemplate.DataCount];
        (stoneData[0], stoneData[1], stoneData[2]) = (24, 32, StoneArea);
        var stone = new GameObjectTemplate { Entry = StoneEntry, Type = (uint)GameObjectType.MeetingStone, DisplayId = 5494, Name = "Meeting Stone", Data = stoneData };
        uint[] types = new uint[LockEntry.Cases];
        uint[] indexes = new uint[LockEntry.Cases];
        (types[0], indexes[0]) = (1, DullIronKey);
        var goContent = new GameObjectContent([chest, well, stone],
        [
            new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = X, Y = Y, Z = Z },
            new GameObjectSpawn { Guid = WellSpawn, Entry = WellEntry, MapId = 0, X = X, Y = Y + 1, Z = Z },
            new GameObjectSpawn { Guid = StoneSpawn, Entry = StoneEntry, MapId = 0, X = X, Y = Y - 1, Z = Z },
        ], [new LockEntry(ChestLock, types, indexes, new uint[LockEntry.Cases])], [], []);
        var lootContent = new LootContent([(LootTableKind.GameObject, new LootStoreRow(ChestLoot, LinenCloth, 100f, 0, 1, 1))], []);
        var context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = LinenCloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Quality = 1, Stackable = 20 });
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = DullIronKey, Class = 13, Name = "Dull Iron Key", DisplayId = 6714, Spells = [new ItemSpell(OpeningSpell, 0, -1, 0, -1, 0, -1)],
        });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: services => Configure(services).AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(new SpellContent(
                    [
                        // Opening (the key's spell): OPEN_LOCK at the object target (Spell.dbc 3366: effect 33, target 23).
                        new SpellTemplateRow
                        {
                            Id = OpeningSpell, SpellName = "Opening", RangeIndex = 12, Targets = 0x800,
                            Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = 1, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                        },
                        // A heal on the caster: the object casts it at its user (the user stands in, TARGET_UNIT_CASTER is the user).
                        new SpellTemplateRow
                        {
                            Id = WellSpell, SpellName = "Test Renew", RangeIndex = 1,
                            Effect1 = 10, EffectImplicitTargetA1 = 1, EffectBasePoints1 = 49, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                        },
                    ],
                    [], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [], [], []))));
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }
}

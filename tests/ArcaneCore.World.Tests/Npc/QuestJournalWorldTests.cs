using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Npc.QuestJournalFixture;

namespace ArcaneCore.World.Tests.Npc;

public sealed class QuestJournalWorldTests
{
    [Fact]
    public async Task Login_RestoresJournalBeforeSelfCreate_AndServesContentQueries()
    {
        var fixture = new QuestJournalFixture();
        await using var host = Start(fixture);
        await using WorldTestClient client = await host.ConnectAsync();
        (_, CharacterRecord character) = await CreateAsync(host, client, "JOURNAL", "Journal");
        long deadline = fixture.Clock.GetUtcNow().ToUnixTimeSeconds() + 30;
        fixture.Characters.Seed(fixture.Progress(character.Id, Ordinary), fixture.Progress(character.Id, Timed, deadline, 0));
        byte[] self = await client.LoginAsync((ulong)character.Id);
        Dictionary<int, uint> fields = ReadSelfFields(self);
        Assert.Equal(Ordinary, fields[UpdateFields.PlayerQuestLog11]);
        Assert.Equal(2u, fields[UpdateFields.PlayerQuestLog11 + 1] & 0x3F);
        Assert.Equal(Timed, fields[UpdateFields.PlayerQuestLog11 + 3]);
        Assert.Equal((uint)deadline, fields[UpdateFields.PlayerQuestLog11 + 5]);

        await client.SendAsync(WorldOpcode.CmsgQuestQuery, UInt32(Ordinary));
        byte[] response = await client.ReadUntilAsync(WorldOpcode.SmsgQuestQueryResponse);
        var query = new PacketReader(response);
        Assert.Equal(Ordinary, query.ReadUInt32());
        Assert.Equal(2u, query.ReadUInt32());
        Assert.Equal(7, query.ReadInt32());
        Assert.Contains("Saved journal", Encoding.UTF8.GetString(response));
        Assert.Contains("Remember the task.", Encoding.UTF8.GetString(response));

        await client.SendAsync(WorldOpcode.CmsgNpcTextQuery, TextQuery(TextId));
        ReadNpcText(await client.ReadUntilAsync(WorldOpcode.SmsgNpcTextUpdate), TextId, "Hello, journal keeper.", 1);
    }

    [Fact]
    public async Task UnknownQueries_FollowReferenceMissingContentBehavior()
    {
        await using var host = Start(new QuestJournalFixture());
        await using WorldTestClient client = await host.EnterWorldAsync("NOQUEST", "Noquest");
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgQuestQuery, UInt32(999999));
        Assert.DoesNotContain(await client.CollectAsync(TimeSpan.FromMilliseconds(75)),
            p => p.Opcode == WorldOpcode.SmsgQuestQueryResponse);
        await client.SendAsync(WorldOpcode.CmsgNpcTextQuery, TextQuery(999999));
        ReadNpcText(await client.ReadUntilAsync(WorldOpcode.SmsgNpcTextUpdate), 999999, "Greetings $N", 0);
    }

    [Fact]
    public async Task ContentQueries_RequireACharacterInWorld()
    {
        await using var host = Start(new QuestJournalFixture());
        await using WorldTestClient client = await host.ConnectAsync();
        byte[] key = await host.AddAccountAsync("QUERYSTATE");
        await client.AuthenticateAsync("QUERYSTATE", key);
        await client.SendAsync(WorldOpcode.CmsgQuestQuery, UInt32(Ordinary));
        await client.SendAsync(WorldOpcode.CmsgNpcTextQuery, TextQuery(TextId));
        await client.AssertSilentAsync(TimeSpan.FromMilliseconds(75));
        await client.CreateCharacterAsync("Querystate");
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgQuestQuery, 3)]
    [InlineData(WorldOpcode.CmsgQuestQuery, 5)]
    [InlineData(WorldOpcode.CmsgNpcTextQuery, 11)]
    [InlineData(WorldOpcode.CmsgNpcTextQuery, 13)]
    public async Task MalformedContentRequests_CloseTheSession(WorldOpcode opcode, int length)
    {
        await using var host = Start(new QuestJournalFixture());
        await using WorldTestClient client = await host.EnterWorldAsync("BADQUERY", "Badquery");
        await client.CollectAsync();
        await client.SendAsync(opcode, new byte[length]);
        Assert.True(await client.IsClosedByServerAsync());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task TimedQuest_ExpiresOnce_AndPersistsAcrossRelog(int secondsUntilDeadline)
    {
        var fixture = new QuestJournalFixture();
        await using var host = Start(fixture);
        byte[] key;
        CharacterRecord character;
        QuestNpcFeature feature;
        await using (WorldTestClient initial = await host.ConnectAsync())
        {
            (key, character) = await CreateAsync(host, initial, "EXPIRE", "Expire");
            long deadline = secondsUntilDeadline == 0 ? 0 : fixture.Clock.GetUtcNow().ToUnixTimeSeconds() + secondsUntilDeadline;
            fixture.Characters.Seed(fixture.Progress(character.Id, Ordinary), fixture.Progress(character.Id, Timed, deadline, 0));
            await initial.LoginAsync((ulong)character.Id);
            feature = Feature(await host.PlayerAsync("Expire"));
            if (secondsUntilDeadline > 0)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(secondsUntilDeadline + 1));
            }

            Assert.Equal(Timed, BinaryPrimitives.ReadUInt32LittleEndian(
                await initial.ReadUntilAsync(WorldOpcode.SmsgQuestupdateFailedtimer)));
            // The socket can deliver FailQuest's packet before CheckTimers queues its save.
            // This world-thread read runs after the expiry tick finishes, before the save barrier.
            Assert.Equal(QuestStatus.Failed, await host.PlayerStateAsync("Expire", p => feature.Services.StateOf(p)!.Quests.GetStatus(Timed)));
            await feature.Persistence.FlushCharacterAsync(character.Id);
            Assert.Equal((byte)QuestStatus.Failed, fixture.Characters.Stored(character.Id, Timed).Status);
            Assert.Equal(0, fixture.Characters.Stored(character.Id, Timed).Timer);
            Assert.Equal(2u, fixture.Characters.Stored(character.Id, Ordinary).MobCount1);
            await initial.CollectAsync();
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(await initial.CollectAsync(TimeSpan.FromMilliseconds(75)),
                p => p.Opcode == WorldOpcode.SmsgQuestupdateFailedtimer);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "quest session logs out");
        await using WorldTestClient relog = await host.ConnectAsync();
        await relog.AuthenticateAsync("EXPIRE", key);
        Dictionary<int, uint> fields = ReadSelfFields(await relog.LoginAsync((ulong)character.Id));
        Assert.Equal(Timed, fields[UpdateFields.PlayerQuestLog11 + 3]);
        Assert.Equal(QuestConstants.SlotStateFail,
            (byte)(fields[UpdateFields.PlayerQuestLog11 + 4] >> 24));
        Assert.Equal(2u, fields[UpdateFields.PlayerQuestLog11 + 1] & 0x3F);
        Assert.DoesNotContain(await relog.CollectAsync(TimeSpan.FromMilliseconds(75)),
            p => p.Opcode == WorldOpcode.SmsgQuestupdateFailedtimer);
    }

    [Fact]
    public async Task JournalReadFailure_RefusesLogin_ThenAllowsRetry()
    {
        var fixture = new QuestJournalFixture();
        await using var host = Start(fixture);
        await using WorldTestClient client = await host.ConnectAsync();
        (_, CharacterRecord character) = await CreateAsync(host, client, "QUESTREAD", "Questread");
        fixture.Characters.Seed(fixture.Progress(character.Id, Ordinary));
        fixture.Characters.FailRead = true;
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, Login(character.Id));
        Assert.Equal((byte)CharResult.CharLoginFailed, (await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed))[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);
        fixture.Characters.FailRead = false;
        Assert.Equal(Ordinary, ReadSelfFields(await client.LoginAsync((ulong)character.Id))[UpdateFields.PlayerQuestLog11]);
    }

    [Fact]
    public async Task FailedExpirySave_RefusesRelog_ThenRecoversAuthoritativeFailure()
    {
        var fixture = new QuestJournalFixture();
        await using var host = Start(fixture);
        byte[] key;
        CharacterRecord character;
        QuestNpcFeature feature;
        await using (WorldTestClient initial = await host.ConnectAsync())
        {
            (key, character) = await CreateAsync(host, initial, "QUESTWRITE", "Questwrite");
            fixture.Characters.Seed(fixture.Progress(character.Id, Timed, 0, 0));
            fixture.Characters.FailWrite = true;
            await initial.LoginAsync((ulong)character.Id);
            await initial.ReadUntilAsync(WorldOpcode.SmsgQuestupdateFailedtimer);
            feature = Feature(await host.PlayerAsync("Questwrite"));
            await Assert.ThrowsAsync<IOException>(() => feature.Persistence.FlushCharacterAsync(character.Id));
            Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(character.Id, Timed).Status);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "failed quest save session leaves");
        await using WorldTestClient retry = await host.ConnectAsync();
        await retry.AuthenticateAsync("QUESTWRITE", key);
        await retry.SendAsync(WorldOpcode.CmsgPlayerLogin, Login(character.Id));
        Assert.Equal((byte)CharResult.CharLoginFailed, (await retry.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed))[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);
        fixture.Characters.FailWrite = false;
        await retry.LoginAsync((ulong)character.Id);
        Assert.Equal((byte)QuestStatus.Failed, fixture.Characters.Stored(character.Id, Timed).Status);
        Assert.Equal(QuestStatus.Failed, await host.PlayerStateAsync("Questwrite", p => feature.Services.StateOf(p)!.Quests.GetStatus(Timed)));
    }

    [Fact]
    public async Task FarTransfer_RetainsJournal_AndNewMapChecksItsTimer()
    {
        var fixture = new QuestJournalFixture();
        await using var host = Start(fixture);
        await using WorldTestClient client = await host.ConnectAsync();
        (_, CharacterRecord character) = await CreateAsync(host, client, "QUESTPORT", "Questport");
        fixture.Characters.Seed(fixture.Progress(character.Id, Timed, fixture.Clock.GetUtcNow().ToUnixTimeSeconds() + 30, 0));
        await client.LoginAsync((ulong)character.Id);
        Player player = await host.PlayerAsync("Questport");
        QuestNpcFeature feature = Feature(player);
        var state = await host.OnWorldAsync(() => feature.Services.StateOf(player));
        await host.OnWorldAsync(() =>
        {
            var teleports = ((WorldSession)player.Session).Services.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(player, 1, -441.8f, -2596f, 96f, 0));
        });
        await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Null(await host.PlayerStateAsync("Questport", p => p.Map));
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await host.OnWorldAsync(() => Assert.Same(state, feature.Services.StateOf(player)));
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        Assert.Equal(Timed, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgQuestupdateFailedtimer)));
        await feature.Persistence.FlushCharacterAsync(character.Id);
        Assert.Equal(1u, await host.PlayerStateAsync("Questport", p => p.Map!.MapId));
        await host.OnWorldAsync(() => Assert.Same(state, feature.Services.StateOf(player)));
        Assert.Equal((byte)QuestStatus.Failed, fixture.Characters.Stored(character.Id, Timed).Status);
    }

    private static WorldTestHost Start(QuestJournalFixture fixture)
    {
        QuestNpcTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestNpcTestServices.Current.Value = null; }
    }

    private static async Task<(byte[] Key, CharacterRecord Character)> CreateAsync(
        WorldTestHost host, WorldTestClient client, string accountName, string name)
    {
        byte[] key = await host.AddAccountAsync(accountName);
        await client.AuthenticateAsync(accountName, key);
        await client.CreateCharacterAsync(name);
        var account = (await host.Accounts.FindByUsernameAsync(accountName))!;
        return (key, (await host.Characters.GetByAccountAsync(account.Id)).Single());
    }

    private static QuestNpcFeature Feature(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<QuestNpcFeature>();

    private static byte[] UInt32(uint value)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(value);
        return writer.ToArray();
    }

    private static byte[] Login(int id)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64((ulong)id);
        return writer.ToArray();
    }

    private static byte[] TextQuery(uint id)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(id);
        writer.WriteUInt64(0); // vmangos uses the content id; guid is not required for the reply.
        return writer.ToArray();
    }

    private static void ReadNpcText(byte[] body, uint id, string firstText, float probability)
    {
        var reader = new PacketReader(body);
        Assert.Equal(id, reader.ReadUInt32());
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i == 0 ? probability : 0, reader.ReadSingle());
            string text = i == 0 ? firstText : "Greetings $N";
            Assert.Equal(text, reader.ReadCString());
            Assert.Equal(text, reader.ReadCString());
            for (int field = 0; field < 7; field++)
            {
                Assert.Equal(0u, reader.ReadUInt32());
            }
        }

        Assert.Equal(0, reader.Remaining);
    }

    private static Dictionary<int, uint> ReadSelfFields(byte[] body)
    {
        // Build 5875 self-create: same movement/mask format as UpdateBlockWriter and
        // gtker wow_messages smsg_update_object.wowm. This fixture has no item objects.
        var reader = new PacketReader(body);
        Assert.Equal(1u, reader.ReadUInt32());
        reader.ReadByte();
        byte type = reader.ReadByte();
        Assert.True(type is 2 or 3);
        reader.ReadPackedGuid();
        Assert.Equal((byte)4, reader.ReadByte()); // TYPEID_PLAYER
        var flags = (ObjectUpdateFlags)reader.ReadByte();
        Assert.True((flags & ObjectUpdateFlags.Living) != 0);
        MovementInfo.Read(ref reader);
        reader.Skip(6 * 4);
        if ((flags & ObjectUpdateFlags.HighGuid) != 0) reader.Skip(4);
        if ((flags & ObjectUpdateFlags.All) != 0) reader.Skip(4);
        if ((flags & ObjectUpdateFlags.MeleeAttacking) != 0) reader.ReadPackedGuid();
        if ((flags & ObjectUpdateFlags.Transport) != 0) reader.Skip(4);
        int count = reader.ReadByte();
        uint[] masks = new uint[count];
        for (int i = 0; i < count; i++) masks[i] = reader.ReadUInt32();
        var values = new Dictionary<int, uint>();
        for (int field = 0; field < count * 32; field++)
        {
            if ((masks[field >> 5] & (1u << (field & 31))) != 0)
                values[field] = reader.ReadUInt32();
        }

        Assert.Equal(0, reader.Remaining);
        return values;
    }
}

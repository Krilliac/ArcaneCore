using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// M6 end to end: the full vmangos login sequence and its account/character state (MOTD, bind
/// point, tutorials, account data, action bar), character name rules, and name/time queries.
/// </summary>
public sealed class M6LoginAndAccountTests
{
    // InMemoryWorldDataStore's start position (human and orc warriors).
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;
    private const float StartZ = 83.5312f;

    [Fact]
    public async Task Login_SendsTheVmangosSequence_WithFreshAccountState()
    {
        await using var host = WorldTestHost.Start(configure: o => o.Motd = "Line one@Line two");
        await using WorldTestClient client = await host.EnterWorldAsync("FRESH", "Fresh");

        // LoginAsync already asserted the order; check the contents.
        Assert.Equal(new byte[128], client.LoginPacket(WorldOpcode.SmsgAccountDataMd5)); // no account data yet
        Assert.Equal(new byte[] { 0 }, client.LoginPacket(WorldOpcode.SmsgFriendList));
        Assert.Equal(new byte[] { 0 }, client.LoginPacket(WorldOpcode.SmsgIgnoreList));

        ChatMessage[] motd = client.LastLoginPackets.Where(p => p.Opcode == WorldOpcode.SmsgMessagechat)
            .Select(p => ChatMessage.Parse(p.Payload)).ToArray();
        Assert.Equal(new[] { "Line one", "Line two" }, motd.Select(m => m.Text));
        Assert.All(motd, m => Assert.Equal((ChatType.System, Language.Universal, 0ul), (m.Type, m.Language, m.Sender)));

        Assert.Equal(new byte[4], client.LoginPacket(WorldOpcode.SmsgSetRestStart));

        var bind = new PacketReader(client.LoginPacket(WorldOpcode.SmsgBindpointupdate));
        Assert.Equal((StartX, StartY, StartZ), (bind.ReadSingle(), bind.ReadSingle(), bind.ReadSingle()));
        Assert.Equal((0u, 12u), (bind.ReadUInt32(), bind.ReadUInt32()));
        Assert.Equal(0, bind.Remaining);

        Assert.Equal(new byte[32], client.LoginPacket(WorldOpcode.SmsgTutorialFlags)); // every tutorial still to show
        Assert.Equal(new byte[Player.ActionButtonCount * 4], client.LoginPacket(WorldOpcode.SmsgActionButtons));

        byte[] factions = client.LoginPacket(WorldOpcode.SmsgInitializeFactions);
        Assert.Equal(4 + (64 * 5), factions.Length);
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(factions));

        var states = new PacketReader(client.LoginPacket(WorldOpcode.SmsgInitWorldStates));
        Assert.Equal((0u, 12u, (ushort)0), (states.ReadUInt32(), states.ReadUInt32(), states.ReadUInt16()));
        Assert.Equal(0, states.Remaining);
    }

    [Fact]
    public async Task CharacterCreate_NormalizesNames_AndAppliesTheVmangosRules()
    {
        await using var host = WorldTestHost.Start(configure: o => o.CharactersPerRealm = 3);
        byte[] key = await host.AddAccountAsync("NAMER");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("NAMER", key);

        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("tHRALL"));
        Assert.Equal((byte)CharResult.CharCreateNameInUse, await client.TryCreateCharacterAsync("THRALL"));
        Assert.Equal((byte)CharResult.CharNameNoName, await client.TryCreateCharacterAsync(""));
        Assert.Equal((byte)CharResult.CharNameNoName, await client.TryCreateCharacterAsync("Abcdefghijklmnop")); // 16 > 15
        Assert.Equal((byte)CharResult.CharNameTooLong, await client.TryCreateCharacterAsync("Abcdefghijklm")); // 13
        Assert.Equal((byte)CharResult.CharNameTooShort, await client.TryCreateCharacterAsync("A"));
        Assert.Equal((byte)CharResult.CharNameMixedLanguages, await client.TryCreateCharacterAsync("Abc1"));
        Assert.Equal((byte)CharResult.CharNameMixedLanguages, await client.TryCreateCharacterAsync("Abcд")); // Latin + Cyrillic
        Assert.Equal((byte)CharResult.CharCreateFailed, await client.TryCreateCharacterAsync("Gnomer", race: 7)); // not offered by the test data
        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("ñandú"));
        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("ЖЕНЯ"));
        Assert.Equal((byte)CharResult.CharCreateServerLimit, await client.TryCreateCharacterAsync("Fourth"));

        Account account = (await host.Accounts.FindByUsernameAsync("NAMER"))!;
        IReadOnlyList<CharacterRecord> characters = await host.Characters.GetByAccountAsync(account.Id);
        Assert.Equal(new[] { "Thrall", "Ñandú", "Женя" }, characters.Select(c => c.Name));

        // A new character is bound at its start position, and the name cache knows it.
        CharacterRecord thrall = characters[0];
        Assert.Equal((0u, 12u, StartX, StartY, StartZ), (thrall.HomeMapId, thrall.HomeZoneId, thrall.HomeX, thrall.HomeY, thrall.HomeZ));
        Assert.Equal("Thrall", host.Directory.Find(thrall.Id)!.Name);
    }

    [Fact]
    public async Task AccountData_IsStoredReturnedAndHashed_AcrossSessions()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("KEEPER");
        byte[] macros = Encoding.ASCII.GetBytes("MACRO 1 \"Hello\" Ability_Warrior_Charge\n/say hi\nEND\n");
        byte[] layout = [0x00, 0xFF, 0xFE, 0x10, 0x80]; // arbitrary bytes, not text

        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync("KEEPER", key);
            await client.CreateCharacterAsync("Keeper");
            await client.LoginAsync(1);

            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, UpdateAccountData(4, macros, adler: true));
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, UpdateAccountData(6, layout, adler: false)); // no Adler-32 trailer
            byte[] wrongSize = UpdateAccountData(5, macros, adler: true);
            BinaryPrimitives.WriteUInt32LittleEndian(wrongSize.AsSpan(4), (uint)macros.Length + 1);
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, wrongSize); // rejected

            await AssertAccountDataAsync(client, 4, macros);
            await AssertAccountDataAsync(client, 6, layout);
            await AssertAccountDataAsync(client, 5, []);
            await AssertAccountDataAsync(client, 0, []);

            // Size 0 erases a type.
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, [6, 0, 0, 0, 0, 0, 0, 0]);
            await AssertAccountDataAsync(client, 6, []);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session to leave");

        await using (WorldTestClient again = await host.ConnectAsync())
        {
            await again.AuthenticateAsync("KEEPER", key);
            await again.LoginAsync(1);

            byte[] hashes = again.LoginPacket(WorldOpcode.SmsgAccountDataMd5);
            for (int type = 0; type < AccountSettings.DataTypeCount; type++)
            {
                byte[] expected = type == 4 ? MD5.HashData(macros) : new byte[16];
                Assert.Equal(expected, hashes.AsSpan(type * 16, 16).ToArray());
            }

            await AssertAccountDataAsync(again, 4, macros);
        }
    }

    [Fact]
    public async Task AccountData_IsAccepted_AtTheCharacterScreen()
    {
        // The client uploads changed settings after it leaves the world.
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("SCREEN");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("SCREEN", key);

        byte[] bindings = Encoding.ASCII.GetBytes("bind W MOVEFORWARD\n");
        await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, UpdateAccountData(2, bindings, adler: true));
        await AssertAccountDataAsync(client, 2, bindings);
    }

    [Fact]
    public async Task Tutorials_ArePersistedPerAccount()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("LEARNER");

        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync("LEARNER", key);
            await client.CreateCharacterAsync("Learner");
            await client.LoginAsync(1);
            await client.SendAsync(WorldOpcode.CmsgTutorialFlag, U32(0));
            await client.SendAsync(WorldOpcode.CmsgTutorialFlag, U32(33));   // word 1, bit 1
            await client.SendAsync(WorldOpcode.CmsgTutorialFlag, U32(255));  // word 7, bit 31
            await client.SendAsync(WorldOpcode.CmsgTutorialFlag, U32(256));  // out of range: ignored
            await SyncSessionAsync(client);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session to leave");
        Assert.Equal(new uint[] { 1, 2, 0, 0, 0, 0, 0, 0x80000000 }, await RelogTutorialsAsync(host, key, clearOrReset: null));
        Assert.Equal(Enumerable.Repeat(0xFFFFFFFFu, 8), await RelogTutorialsAsync(host, key, WorldOpcode.CmsgTutorialClear));
        Assert.Equal(new uint[8], await RelogTutorialsAsync(host, key, WorldOpcode.CmsgTutorialReset));
    }

    [Fact]
    public async Task ActionBar_IsSavedAndRestored()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("BARS");

        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync("BARS", key);
            await client.CreateCharacterAsync("Bars");
            await client.LoginAsync(1);
            await client.SendAsync(WorldOpcode.CmsgSetActionButton, ActionButton(0, 6603));                 // spell
            await client.SendAsync(WorldOpcode.CmsgSetActionButton, ActionButton(1, 0x80000000 | 2516));    // item
            await client.SendAsync(WorldOpcode.CmsgSetActionButton, ActionButton(2, 0x40000000 | 7));       // macro
            await client.SendAsync(WorldOpcode.CmsgSetActionButton, ActionButton(3, 0x01000000 | 5));       // unknown type: ignored
            await client.SendAsync(WorldOpcode.CmsgSetActionButton, ActionButton(120, 6603));               // no such slot: ignored
            await client.SendAsync(WorldOpcode.CmsgSetActionButton, ActionButton(2, 0));                    // cleared again
            await client.SendAsync(WorldOpcode.CmsgSetActionbarToggles, [0x0F]);
            await SyncWorldAsync(client);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session to leave");
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the save");

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("BARS", key);
        await again.LoginAsync(1);
        byte[] buttons = again.LoginPacket(WorldOpcode.SmsgActionButtons);
        Assert.Equal(6603u, BinaryPrimitives.ReadUInt32LittleEndian(buttons.AsSpan(0)));
        Assert.Equal(0x80000000u | 2516, BinaryPrimitives.ReadUInt32LittleEndian(buttons.AsSpan(4)));
        Assert.Equal(new byte[(Player.ActionButtonCount - 2) * 4], buttons.AsSpan(8).ToArray());
        Assert.Equal(0x0F, await host.PlayerStateAsync("Bars", p => p.ActionBarToggles));
    }

    [Fact]
    public async Task NameQuery_AnswersOnlineAndOfflineCharacters_AndIgnoresUnknownOnes()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient asker = await host.EnterWorldAsync("ASKER", "Asker");

        // An orc that exists but has never logged in.
        byte[] key = await host.AddAccountAsync("OFFLINE");
        await using (WorldTestClient other = await host.ConnectAsync())
        {
            await other.AuthenticateAsync("OFFLINE", key);
            await other.CreateCharacterAsync("Grunt", race: 2, gender: 1);
        }

        Assert.Equal((1ul, "Asker", 1u, 0u, 1u), await NameQueryAsync(asker, 1));
        Assert.Equal((2ul, "Grunt", 2u, 1u, 1u), await NameQueryAsync(asker, 2));

        await asker.SendAsync(WorldOpcode.CmsgNameQuery, U64(999));
        await asker.SendAsync(WorldOpcode.CmsgQueryTime, []);
        (WorldOpcode next, byte[] time) = await asker.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgQueryTimeResponse, next); // nothing for the unknown GUID
        Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(time), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
    }

    [Fact]
    public async Task PlayedTime_GmTicket_MailAndRaidPolls_AreAnswered()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("POLLER", "Poller");

        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        var played = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgPlayedTime));
        uint total = played.ReadUInt32();
        uint atLevel = played.ReadUInt32();
        Assert.InRange(total, 0u, 10u); // seconds since this first login
        Assert.InRange(atLevel, 0u, total);

        await client.SendAsync(WorldOpcode.CmsgGmticketGetticket, []);
        Assert.Equal(WorldOpcode.SmsgQueryTimeResponse, (await client.ReadAsync()).Opcode);
        (WorldOpcode op, byte[] ticket) = await client.ReadAsync();
        Assert.Equal((WorldOpcode.SmsgGmticketGetticket, 0x0Au), (op, BinaryPrimitives.ReadUInt32LittleEndian(ticket)));

        await client.SendAsync(WorldOpcode.MsgQueryNextMailTime, []);
        Assert.Equal(-86400.0f, BinaryPrimitives.ReadSingleLittleEndian(await client.ReadUntilAsync(WorldOpcode.MsgQueryNextMailTime)));

        await client.SendAsync(WorldOpcode.CmsgRequestRaidInfo, []);
        Assert.Equal(new byte[4], await client.ReadUntilAsync(WorldOpcode.SmsgRaidInstanceInfo));
    }

    [Fact]
    public async Task CharacterDirectory_FollowsCreatesAndDeletes()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("CYCLE");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("CYCLE", key);
        await client.CreateCharacterAsync("Ephemeral");
        Assert.Equal("Ephemeral", host.Directory.Find(1)!.Name);

        await client.SendAsync(WorldOpcode.CmsgCharDelete, U64(1));
        Assert.Equal((byte)CharResult.CharDeleteSuccess, (await client.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
        Assert.Null(host.Directory.Find(1));
    }

    [Fact]
    public async Task WorldHost_LoadsTheCharacterDirectory_AtStartup()
    {
        var store = new InMemoryCharacterStore();
        await store.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Preloaded", Race = 2, Gender = 1, Class = 1 });
        var services = new ServiceCollection();
        services.AddSingleton<ICharacterStore>(store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions(), saveQueue, NullLogger<WorldRuntime>.Instance);
        var directory = new CharacterDirectory();
        var worldHost = new WorldHost(world, saveQueue, directory, scopes, NullLogger<WorldHost>.Instance);

        await worldHost.StartAsync(CancellationToken.None);
        await worldHost.StopAsync(CancellationToken.None);
        Assert.Equal(new CharacterIdentity(1, 1, "Preloaded", 2, 1, 1), directory.Find(1));
    }

    // --- helpers -------------------------------------------------------------------

    /// <summary>CMSG_UPDATE_ACCOUNT_DATA body: u32 type, u32 size, zlib stream (optionally without its Adler-32 trailer).</summary>
    private static byte[] UpdateAccountData(uint type, byte[] data, bool adler)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        byte[] stream = compressed.ToArray();
        if (!adler)
        {
            stream = stream[..^4];
        }

        var writer = new PacketWriter(8 + stream.Length);
        writer.WriteUInt32(type);
        writer.WriteUInt32((uint)data.Length);
        writer.WriteBytes(stream);
        return writer.ToArray();
    }

    private static async Task AssertAccountDataAsync(WorldTestClient client, uint type, byte[] expected)
    {
        (uint returnedType, byte[] data) = await RequestAccountDataAsync(client, type);
        Assert.Equal(type, returnedType);
        Assert.Equal(expected, data);
    }

    private static async Task<(uint Type, byte[] Data)> RequestAccountDataAsync(WorldTestClient client, uint type)
    {
        await client.SendAsync(WorldOpcode.CmsgRequestAccountData, U32(type));
        byte[] payload = await client.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        uint returnedType = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
        if (size == 0)
        {
            Assert.Equal(8, payload.Length);
            return (returnedType, []);
        }

        using var zlib = new ZLibStream(new MemoryStream(payload, 8, payload.Length - 8), CompressionMode.Decompress);
        byte[] data = new byte[size];
        zlib.ReadExactly(data);
        return (returnedType, data);
    }

    private static async Task<uint[]> RelogTutorialsAsync(WorldTestHost host, byte[] key, WorldOpcode? clearOrReset)
    {
        uint[] words;
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync("LEARNER", key);
            await client.LoginAsync(1);
            byte[] flags = client.LoginPacket(WorldOpcode.SmsgTutorialFlags);
            words = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(flags.AsSpan(i * 4))).ToArray();
            if (clearOrReset is { } opcode)
            {
                await client.SendAsync(opcode, []);
                await SyncSessionAsync(client);
            }
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");
        if (clearOrReset is null)
        {
            return words;
        }

        // Report what the next login sees after the clear/reset.
        return await RelogTutorialsAsync(host, key, clearOrReset: null);
    }

    private static async Task<(ulong Guid, string Name, uint Race, uint Gender, uint Class)> NameQueryAsync(WorldTestClient client, ulong guid)
    {
        await client.SendAsync(WorldOpcode.CmsgNameQuery, U64(guid));
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgNameQueryResponse));
        ulong returned = reader.ReadUInt64();
        string name = reader.ReadCString();
        Assert.Equal(string.Empty, reader.ReadCString()); // realm name: same realm
        (ulong, string, uint, uint, uint) result = (returned, name, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        return result;
    }

    /// <summary>Session handlers run in order, so a query-time reply proves the earlier ones finished.</summary>
    private static async Task SyncSessionAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgQueryTime, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgQueryTimeResponse);
    }

    /// <summary>World packets run in order, so a played-time reply proves the earlier ones were handled.</summary>
    private static async Task SyncWorldAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
    }

    private static byte[] ActionButton(byte slot, uint packed)
    {
        byte[] payload = new byte[5];
        payload[0] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), packed);
        return payload;
    }

    private static byte[] U32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] U64(ulong value)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }
}

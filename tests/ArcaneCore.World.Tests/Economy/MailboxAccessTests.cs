using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Tests.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>
/// Every mail opcode must prove the player is at a real mailbox (vmangos WorldSession::CheckMailBox):
/// the GUID names a spawned mailbox game object in the player's map within interaction distance.
/// A fabricated GUID, another kind of game object and a mailbox out of range are refused (no state change;
/// CMSG_SEND_MAIL answers MAIL_ERR_INTERNAL_ERROR with mail id 0, the other seven stay silent) for each of the eight
/// opcodes; an in-range mailbox works; Permissive restores the old accept-any-GameObject-GUID behaviour. This host
/// has no economy store, so an allowed CMSG_SEND_MAIL answers the same result as a refused one; the CMSG_SEND_MAIL
/// refusal is told apart from an allowed request in EconomyMailSendRecheckTests, which runs against a real store.
/// </summary>
public sealed class MailboxAccessTests
{
    private const uint MailboxEntry = 1001;
    private const uint MailboxSpawn = 91001;
    private const uint ChestEntry = 1002;
    private const uint ChestSpawn = 91002;

    // Human start is (-8949.95, -132.49, 83.53); both objects are about 2 yd away.
    private const float StartX = -8949.95f;
    private const float StartY = -132.49f;
    private const float StartZ = 83.53f;

    public enum Probe
    {
        Fabricated,
        NotAMailbox,
        OutOfRange,
        InRange,
    }

    /// <summary>Opcode, the reply an allowed request gets on a host without an economy store (null: none).</summary>
    public static TheoryData<WorldOpcode, WorldOpcode?> MailOpcodes => new()
    {
        { WorldOpcode.CmsgGetMailList, WorldOpcode.SmsgMailListResult },
        { WorldOpcode.CmsgSendMail, WorldOpcode.SmsgSendMailResult },
        { WorldOpcode.CmsgMailTakeMoney, WorldOpcode.SmsgSendMailResult },
        { WorldOpcode.CmsgMailTakeItem, WorldOpcode.SmsgSendMailResult },
        { WorldOpcode.CmsgMailMarkAsRead, null },
        { WorldOpcode.CmsgMailReturnToSender, WorldOpcode.SmsgSendMailResult },
        { WorldOpcode.CmsgMailDelete, WorldOpcode.SmsgSendMailResult },
        { WorldOpcode.CmsgMailCreateTextItem, WorldOpcode.SmsgSendMailResult },
    };

    [Theory]
    [MemberData(nameof(MailOpcodes))]
    public async Task Retail_RefusesFabricatedWrongTypeAndOutOfRange_ForEveryMailOpcode(WorldOpcode opcode, WorldOpcode? reply)
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await host.EnterWorldAsync("MBXRETAIL", "Mbxretail");
        await host.PlaceAsync("Mbxretail", StartX, StartY, StartZ);
        await DrainAsync(client);

        foreach (Probe probe in new[] { Probe.Fabricated, Probe.NotAMailbox, Probe.OutOfRange })
        {
            await PlaceForAsync(host, "Mbxretail", probe);
            AssertRefused(opcode, await SendAsync(client, opcode, GuidFor(probe)));
        }

        await PlaceForAsync(host, "Mbxretail", Probe.InRange);
        AssertAllowed(opcode, reply, await SendAsync(client, opcode, GuidFor(Probe.InRange)));
    }

    [Theory]
    [MemberData(nameof(MailOpcodes))]
    public async Task Permissive_RestoresAnyGameObjectGuid_ForEveryMailOpcode(WorldOpcode opcode, WorldOpcode? reply)
    {
        await using WorldTestHost host = StartHost();
        host.WorldServices.GetRequiredService<EconomyFeature>().Options.MailboxAccess = MailboxAccessMode.Permissive;
        await using WorldTestClient client = await host.EnterWorldAsync("MBXPERM", "Mbxperm");
        await host.PlaceAsync("Mbxperm", StartX, StartY, StartZ);
        await DrainAsync(client);

        foreach (Probe probe in new[] { Probe.Fabricated, Probe.NotAMailbox, Probe.OutOfRange, Probe.InRange })
        {
            await PlaceForAsync(host, "Mbxperm", probe);
            AssertAllowed(opcode, reply, await SendAsync(client, opcode, GuidFor(probe)));
        }

        // Permissive still requires a game object GUID.
        await host.PlaceAsync("Mbxperm", StartX, StartY, StartZ);
        AssertRefused(opcode, await SendAsync(client, opcode, ObjectGuid.Player(1).Value));
    }

    [Fact]
    public void Retail_IsTheDefault()
        => Assert.Equal(MailboxAccessMode.Retail, new EconomyOptions().MailboxAccess);

    public static TheoryData<WorldOpcode> AllMailOpcodes => [.. MailOpcodes.Select(row => (WorldOpcode)row[0])];

    [Theory]
    [MemberData(nameof(AllMailOpcodes))]
    public async Task EveryMailOpcode_ConsultsTheMailboxCheck_WithTheAddressedGuid(WorldOpcode opcode)
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await host.EnterWorldAsync("MBXSPY", "Mbxspy");
        await DrainAsync(client);
        var spy = new SpyAccess();
        host.WorldServices.GetRequiredService<EconomyFeature>().MailboxAccessOverride = spy;

        ulong guid = GuidFor(Probe.Fabricated);
        AssertRefused(opcode, await SendAsync(client, opcode, guid));
        Assert.Equal([guid], spy.Asked);
    }

    [Fact]
    public async Task GameObjectMailboxAccess_RequiresTypeRangeMapAndLife()
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await host.EnterWorldAsync("MBXUNIT", "Mbxunit");
        await host.PlaceAsync("Mbxunit", StartX, StartY, StartZ);
        var access = new GameObjectMailboxAccess(() => host.WorldServices.GetService<World.GameObjects.GameObjectLootFeature>());
        ObjectGuid mailbox = new(GuidFor(Probe.InRange));
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Mbxunit")!;
            Assert.True(access.CanUseMailbox(player, mailbox));
            Assert.False(access.CanUseMailbox(player, new ObjectGuid(GuidFor(Probe.NotAMailbox))));
            Assert.False(access.CanUseMailbox(player, new ObjectGuid(GuidFor(Probe.Fabricated))));
            Assert.False(access.CanUseMailbox(player, default));
            Assert.False(new GameObjectMailboxAccess(() => null).CanUseMailbox(player, mailbox));
            player.Relocate(StartX + 30, StartY, StartZ, player.Orientation, host.World.NowMs);
            Assert.False(access.CanUseMailbox(player, mailbox));
            player.Relocate(StartX, StartY, StartZ, player.Orientation, host.World.NowMs);
            Assert.True(access.CanUseMailbox(player, mailbox));
            return true;
        });
    }

    private static ulong GuidFor(Probe probe) => probe switch
    {
        Probe.Fabricated => ObjectGuid.WithEntry(HighGuid.GameObject, 424242, 424242).Value,
        Probe.NotAMailbox => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value,
        _ => ObjectGuid.WithEntry(HighGuid.GameObject, MailboxEntry, MailboxSpawn).Value,
    };

    private static Task PlaceForAsync(WorldTestHost host, string name, Probe probe)
        => host.PlaceAsync(name, probe == Probe.OutOfRange ? StartX + 30 : StartX, StartY, StartZ);

    /// <summary>A refused request changes nothing. CMSG_SEND_MAIL answers MAIL_ERR_INTERNAL_ERROR, as vmangos HandleSendMail does (MailHandler.cpp:131-134);
    /// the other mail opcodes stay silent.</summary>
    private static void AssertRefused(WorldOpcode opcode, List<(WorldOpcode Opcode, byte[] Payload)> replies)
    {
        if (opcode == WorldOpcode.CmsgSendMail)
        {
            (WorldOpcode got, byte[] payload) = Assert.Single(replies);
            Assert.Equal(WorldOpcode.SmsgSendMailResult, got);
            (uint mailId, MailAction action, MailResult result) = DecodeSendMailResult(payload);
            Assert.Equal(0u, mailId);
            Assert.Equal(MailAction.Send, action);
            Assert.Equal(MailResult.InternalError, result);
        }
        else
        {
            Assert.Empty(replies);
        }
    }

    /// <summary>
    /// An allowed request answers the expected opcode (or stays silent). This host has no economy store, so the economy is disabled and an allowed
    /// request is itself answered MAIL_ERR_INTERNAL_ERROR: the result cannot tell it from a refusal here. EveryMailOpcode_ConsultsTheMailboxCheck_WithTheAddressedGuid
    /// proves each opcode asks the check, and EconomyMailSendRecheckTests proves the refusal against a real store; this asserts the decoded action of a send answer.
    /// </summary>
    private static void AssertAllowed(WorldOpcode opcode, WorldOpcode? reply, List<(WorldOpcode Opcode, byte[] Payload)> got)
    {
        if (reply is not { } expected)
        {
            Assert.Empty(got);
            return;
        }

        (WorldOpcode op, byte[] payload) = Assert.Single(got);
        Assert.Equal(expected, op);
        if (opcode == WorldOpcode.CmsgSendMail)
        {
            Assert.Equal(MailAction.Send, DecodeSendMailResult(payload).Action);
        }
    }

    private static (uint MailId, MailAction Action, MailResult Result) DecodeSendMailResult(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), (MailAction)reader.ReadUInt32(), (MailResult)reader.ReadUInt32());
    }

    /// <summary>Send one mail opcode, then the sentinel; return the mail replies (opcode and payload) that arrived before it.</summary>
    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> SendAsync(WorldTestClient client, WorldOpcode opcode, ulong mailbox)
    {
        await client.SendAsync(opcode, Payload(opcode, mailbox));
        await client.SendAsync(WorldOpcode.MsgQueryNextMailTime, []);
        var replies = new List<(WorldOpcode Opcode, byte[] Payload)>();
        while (true)
        {
            (WorldOpcode got, byte[] payload) = await client.ReadAsync();
            if (got == WorldOpcode.MsgQueryNextMailTime)
            {
                return replies;
            }

            if (got is WorldOpcode.SmsgSendMailResult or WorldOpcode.SmsgMailListResult)
            {
                replies.Add((got, payload));
            }
        }
    }

    /// <summary>Everything the world thread queued so far is processed once the sentinel comes back.</summary>
    private static async Task DrainAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.MsgQueryNextMailTime, []);
        await client.ReadUntilAsync(WorldOpcode.MsgQueryNextMailTime);
    }

    private static byte[] Payload(WorldOpcode opcode, ulong mailbox)
    {
        var w = new PacketWriter(64);
        w.WriteUInt64(mailbox);
        switch (opcode)
        {
            case WorldOpcode.CmsgGetMailList:
                break;
            case WorldOpcode.CmsgSendMail:
                w.WriteCString("Nobody");
                w.WriteCString("subject");
                w.WriteCString("body");
                w.WriteUInt64(0);
                w.WriteUInt64(0);   // item
                w.WriteUInt32(0);   // money
                w.WriteUInt32(0);   // cod
                w.WriteUInt64(0);
                break;
            default:
                w.WriteUInt32(1);   // mail id
                break;
        }

        return w.ToArray();
    }

    private static WorldTestHost StartHost()
    {
        var mailbox = new GameObjectTemplate
        {
            Entry = MailboxEntry, Type = (uint)GameObjectType.Mailbox, DisplayId = 3, Name = "Mailbox",
            Data = new uint[GameObjectTemplate.DataCount],
        };
        var chest = new GameObjectTemplate
        {
            Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Chest",
            Data = new uint[GameObjectTemplate.DataCount],
        };
        var spawns = new[]
        {
            new GameObjectSpawn { Guid = MailboxSpawn, Entry = MailboxEntry, MapId = 0, X = StartX + 2f, Y = StartY, Z = StartZ },
            new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = StartX, Y = StartY + 2f, Z = StartZ },
        };
        var context = new GameObjectTestContext(new GameObjectContent([mailbox, chest], spawns, [], [], []), LootContent.Empty);
        GameObjectTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private sealed class SpyAccess : IMailboxAccess
    {
        public List<ulong> Asked { get; } = [];

        public bool CanUseMailbox(Player player, ObjectGuid mailbox)
        {
            lock (Asked)
            {
                Asked.Add(mailbox.Value);
            }

            return false;
        }
    }
}

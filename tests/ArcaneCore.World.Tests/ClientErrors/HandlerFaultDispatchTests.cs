using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.ClientErrors;

/// <summary>
/// A handler that throws, end to end through a real session: the client gets the internal error of the action it took,
/// never the exception text, and the session goes on until a fault storm (docs/areas/client-errors.md).
/// </summary>
public sealed class HandlerFaultDispatchTests
{
    private const string Marker = "SECRET-HANDLER-MARKER";

    private static void ThrowBug() => throw new InvalidOperationException(Marker + " at C:\\secret\\path.cs");

    /// <summary>Serve the host's table with <paramref name="opcodes"/> handled by <paramref name="register"/>'s handlers.</summary>
    private static void Swap(WorldTestHost host, Action<OpcodeTable> register, params WorldOpcode[] opcodes)
    {
        var add = new OpcodeTable();
        register(add);
        host.Opcodes.Replace(host.Opcodes.Copy().TrySwap(opcodes, add, out string? error) ?? throw new InvalidOperationException(error));
    }

    private static byte[] CastPayload(uint spellId)
    {
        byte[] payload = new byte[6]; // u32 spell, u16 target flags (self)
        BinaryPrimitives.WriteUInt32LittleEndian(payload, spellId);
        return payload;
    }

    private static void AssertNoMarker(IEnumerable<(WorldOpcode Opcode, byte[] Payload)> packets)
    {
        foreach ((WorldOpcode opcode, byte[] payload) in packets)
        {
            Assert.DoesNotContain("SECRET", Encoding.Latin1.GetString(payload), StringComparison.Ordinal);
            _ = opcode;
        }
    }

    /// <summary>A world-thread query on the containment allowlist (WorldSession.IsContainableWorldOpcode), registered OnWorld in production.</summary>
    private const WorldOpcode ContainedOpcode = WorldOpcode.CmsgPlayedTime;

    private static readonly byte[] InternalErrorNotification = [.. Encoding.ASCII.GetBytes("Internal error"), 0];

    [Fact]
    public async Task AThrowingAllowlistedQueryHandler_SendsTheInternalError_AndTheSessionGoesOn()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(ContainedOpcode, (_, _, _) => ThrowBug()), ContainedOpcode);
        await using WorldTestClient client = await host.EnterWorldAsync("QUERYBUG", "Querybug");

        await client.SendAsync(ContainedOpcode, new byte[8]);
        byte[] notification = await client.ReadUntilAsync(WorldOpcode.SmsgNotification);
        Assert.Equal(InternalErrorNotification, notification);
        AssertNoMarker([(WorldOpcode.SmsgNotification, notification)]);

        // Contained: the session still answers on the world thread, and the same opcode is handled again.
        await client.SendAsync(ContainedOpcode, new byte[8]);
        Assert.Equal(InternalErrorNotification, await client.ReadUntilAsync(WorldOpcode.SmsgNotification));
        await host.OnWorldAsync(() => { });
    }

    [Fact]
    public async Task AThrowingCastHandler_SendsCastFailedInternalError_ThenDisconnects()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(WorldOpcode.CmsgCastSpell, (_, _, _) => ThrowBug()), WorldOpcode.CmsgCastSpell);
        await using WorldTestClient client = await host.EnterWorldAsync("CASTBUG", "Castbug");

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(18960));
        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

        // u32 spell 18960, u8 status 2 (failed), u8 0x1C SPELL_FAILED_ERROR "Internal error".
        Assert.Equal(new byte[] { 0x10, 0x4A, 0x00, 0x00, 0x02, 0x1C }, result);
        AssertNoMarker([(WorldOpcode.SmsgCastResult, result)]);

        // A spell is state-changing: not on the allowlist, so the session is dropped after the reply.
        Assert.True(await client.IsClosedByServerAsync(), "a non-allowlisted handler fault disconnects");
    }

    [Fact]
    public async Task AThrowingItemMoveHandler_SendsInventoryChangeFailureInternalBagError_ThenDisconnects()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(WorldOpcode.CmsgSwapInvItem, (_, _, _) => ThrowBug()), WorldOpcode.CmsgSwapInvItem);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMBUG", "Itembug");

        await client.SendAsync(WorldOpcode.CmsgSwapInvItem, [23, 24]);
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);

        Assert.Equal(1 + 8 + 8 + 1, failure.Length);
        Assert.Equal(40, failure[0]); // EQUIP_ERR_INT_BAG_ERROR -> ERR_INTERNAL_BAG_ERROR
        AssertNoMarker([(WorldOpcode.SmsgInventoryChangeFailure, failure)]);
        Assert.True(await client.IsClosedByServerAsync(), "a non-allowlisted handler fault disconnects");
    }

    /// <summary>
    /// Economy handlers mutate before they can throw (loot money credits before it clears the bag, a split decrements
    /// before it stores): absorbing the fault would make the exception a repeatable duplication loop. They disconnect.
    /// </summary>
    [Theory]
    [InlineData(WorldOpcode.CmsgLootMoney)]
    [InlineData(WorldOpcode.CmsgSplitItem)]
    public async Task AThrowingEconomyHandler_StillDisconnects(WorldOpcode opcode)
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(opcode, (_, _, _) => ThrowBug()), opcode);
        await using WorldTestClient client = await host.EnterWorldAsync("ECONBUG", "Econbug");

        await client.SendAsync(opcode, new byte[8]);
        Assert.True(await client.IsClosedByServerAsync(), $"{opcode} throwing must disconnect, not be contained");
    }

    [Fact]
    public void TheAllowlist_ExcludesEconomyMovementAndMail_AndIncludesQueries()
    {
        Assert.True(WorldSession.IsContainableWorldOpcode(WorldOpcode.CmsgPlayedTime));
        Assert.True(WorldSession.IsContainableWorldOpcode(WorldOpcode.CmsgMessagechat));
        Assert.True(WorldSession.IsContainableWorldOpcode(WorldOpcode.CmsgWho));
        foreach (WorldOpcode opcode in new[]
        {
            WorldOpcode.CmsgLootMoney, WorldOpcode.CmsgSplitItem, WorldOpcode.CmsgSendMail, WorldOpcode.CmsgMailTakeItem,
            WorldOpcode.CmsgMailTakeMoney, WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgBuyItem, WorldOpcode.CmsgSellItem,
            WorldOpcode.MsgMoveHeartbeat, WorldOpcode.MsgMoveStartForward, WorldOpcode.CmsgEmote, WorldOpcode.CmsgTextEmote,
            WorldOpcode.CmsgStandstatechange, WorldOpcode.CmsgSetSelection, WorldOpcode.CmsgNameQuery,
        })
        {
            Assert.False(WorldSession.IsContainableWorldOpcode(opcode), $"{opcode} must not be contained");
        }
    }

    [Fact]
    public async Task RepeatedFaults_AreRateLimited_AndAStormDisconnects()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(ContainedOpcode, (_, _, _) => ThrowBug()), ContainedOpcode);
        await using WorldTestClient client = await host.EnterWorldAsync("STORMBUG", "Stormbug");

        // Faults 1..4: contained; only the first ClientError.Burst are told to the client. Wait for each told reply
        // (no fixed collection window), which also proves the session is still handling packets.
        for (int i = 0; i < WorldSession.HandlerFaultStorm - 1; i++)
        {
            await client.SendAsync(ContainedOpcode, new byte[8]);
            if (i < Game.ClientErrors.ClientError.Burst)
            {
                byte[] told = await client.ReadUntilAsync(WorldOpcode.SmsgNotification);
                AssertNoMarker([(WorldOpcode.SmsgNotification, told)]);
            }
        }

        // The world thread has handled all four faults (it processes the session queue in order) and is alive.
        await host.OnWorldAsync(() => { });

        // Fault 5 within the window: the session is treated as broken and dropped.
        await client.SendAsync(ContainedOpcode, new byte[8]);
        Assert.True(await client.IsClosedByServerAsync(), "the fifth handler fault within the window disconnects");
        await host.OnWorldAsync(() => { }); // the world thread is alive
    }

    [Fact]
    public async Task AThrowingCharCreateHandler_AnswersCharCreateError_AtTheCharacterScreen()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnSession(WorldOpcode.CmsgCharCreate, SessionStates.CharacterSelect, (_, _) => { ThrowBug(); return Task.CompletedTask; }),
            WorldOpcode.CmsgCharCreate);
        byte[] key = await host.AddAccountAsync("CREATEBUG");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("CREATEBUG", key);

        // 0x2F CHAR_CREATE_ERROR "Error creating character", every time (an answer is never rate-limited).
        Assert.Equal(0x2F, await client.TryCreateCharacterAsync("Createbug"));
        Assert.Equal(0x2F, await client.TryCreateCharacterAsync("Createbug"));
    }
}

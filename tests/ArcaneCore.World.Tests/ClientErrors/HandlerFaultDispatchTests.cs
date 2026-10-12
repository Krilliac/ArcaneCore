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

    [Fact]
    public async Task AThrowingCastHandler_SendsCastFailedInternalError_AndTheSessionGoesOn()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(WorldOpcode.CmsgCastSpell, (_, _, _) => ThrowBug()), WorldOpcode.CmsgCastSpell);
        await using WorldTestClient client = await host.EnterWorldAsync("CASTBUG", "Castbug");

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(18960));
        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

        // u32 spell 18960, u8 status 2 (failed), u8 0x1C SPELL_FAILED_ERROR "Internal error".
        Assert.Equal(new byte[] { 0x10, 0x4A, 0x00, 0x00, 0x02, 0x1C }, result);
        AssertNoMarker([(WorldOpcode.SmsgCastResult, result)]);

        // Contained: the session still answers on the world thread.
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(133));
        Assert.Equal(new byte[] { 0x85, 0x00, 0x00, 0x00, 0x02, 0x1C }, await client.ReadUntilAsync(WorldOpcode.SmsgCastResult));
    }

    [Fact]
    public async Task AThrowingItemMoveHandler_SendsInventoryChangeFailureInternalBagError()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(WorldOpcode.CmsgSwapInvItem, (_, _, _) => ThrowBug()), WorldOpcode.CmsgSwapInvItem);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMBUG", "Itembug");

        await client.SendAsync(WorldOpcode.CmsgSwapInvItem, [23, 24]);
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);

        Assert.Equal(1 + 8 + 8 + 1, failure.Length);
        Assert.Equal(40, failure[0]); // EQUIP_ERR_INT_BAG_ERROR -> ERR_INTERNAL_BAG_ERROR
        AssertNoMarker([(WorldOpcode.SmsgInventoryChangeFailure, failure)]);
    }

    [Fact]
    public async Task RepeatedFaults_AreRateLimited_AndAStormDisconnects()
    {
        await using var host = WorldTestHost.Start();
        Swap(host, t => t.OnWorld(WorldOpcode.CmsgCastSpell, (_, _, _) => ThrowBug()), WorldOpcode.CmsgCastSpell);
        await using WorldTestClient client = await host.EnterWorldAsync("STORMBUG", "Stormbug");

        // Faults 1..4: contained; only the first ClientError.Burst are told to the client.
        for (int i = 0; i < WorldSession.HandlerFaultStorm - 1; i++)
        {
            await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(18960));
        }

        List<(WorldOpcode Opcode, byte[] Payload)> received = await client.CollectFromAsync(WorldOpcode.SmsgCastResult, TimeSpan.FromSeconds(1));
        Assert.Equal(Game.ClientErrors.ClientError.Burst, received.Count(p => p.Opcode == WorldOpcode.SmsgCastResult));
        AssertNoMarker(received);

        // Fault 5 within the window: the session is treated as broken and dropped.
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(18960));
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

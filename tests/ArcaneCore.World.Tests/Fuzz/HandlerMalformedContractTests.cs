using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.PacketHeuristics;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// The malformed-packet contract of handlers that check their payload length by hand: a wrong-length payload must end in the packet reader's
/// controlled outcome, <see cref="ArgumentOutOfRangeException"/> with ParamName "packet" (<see cref="MalformedPacket.Throw"/>), never an
/// ArgumentOutOfRangeException of some other parameter name. One row per distinct throw site the opcode handler fuzz found.
/// </summary>
public sealed class HandlerMalformedContractTests(HandlerMalformedContractTests.Fixture fixture) : IClassFixture<HandlerMalformedContractTests.Fixture>
{
    private static readonly byte[] None = [];

    public static TheoryData<string, WorldOpcode, byte[]> Rows => new()
    {
        { "NpcServiceHandlers.RequireLength (LIST_INVENTORY, empty)", WorldOpcode.CmsgListInventory, None },
        { "NpcServiceHandlers.GossipSelectOption length (:60)", WorldOpcode.CmsgGossipSelectOption, None },
        { "NpcServiceHandlers.GossipSelectOption code (:71)", WorldOpcode.CmsgGossipSelectOption, [.. new byte[12], 0x41] },
        { "NpcServiceHandlers.ActivateTaxiExpress length (:178)", WorldOpcode.CmsgActivatetaxiexpress, None },
        { "NpcServiceHandlers.ActivateTaxiExpress count (:187)", WorldOpcode.CmsgActivatetaxiexpress, new byte[17] },
        { "QuestNpcInteractionHandlers.RequireLength (QUESTGIVER_HELLO)", WorldOpcode.CmsgQuestgiverHello, None },
        { "QuestNpcQueryHandlers.HandleQuestQuery (:30)", WorldOpcode.CmsgQuestQuery, None },
        { "QuestNpcQueryHandlers.HandleNpcTextQuery (:50)", WorldOpcode.CmsgNpcTextQuery, None },
        { "QuestShareHandlers.PushResult (:37)", WorldOpcode.MsgQuestPushResult, None },
        { "QuestShareHandlers.ReadQuest (:48)", WorldOpcode.CmsgPushquesttoparty, None },
        { "TalentHandlers.RequireLength (LEARN_TALENT)", WorldOpcode.CmsgLearnTalent, None },
        { "MeetingStoneHandlers.Info (:77)", WorldOpcode.CmsgMeetingstoneInfo, new byte[1] },
        { "InactiveQueueHandlers.RequireEmptyBody (:39)", WorldOpcode.CmsgBattlefieldStatus, new byte[1] },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task WrongLengthPayload_EndsInTheControlledMalformedOutcome(string site, WorldOpcode opcode, byte[] payload)
    {
        Assert.True(fixture.Opcodes.TryGet(opcode, out OpcodeHandler? handler), site);
        Exception? thrown = await fixture.InvokeAsync(handler, payload);

        Assert.True(
            thrown is ArgumentOutOfRangeException { ParamName: MalformedPacket.ParamName },
            $"{site}: expected ArgumentOutOfRangeException with ParamName '{MalformedPacket.ParamName}', got {thrown?.GetType().Name ?? "no exception"} {(thrown as ArgumentOutOfRangeException)?.ParamName}");
    }

    /// <summary>One world with a character in it, shared by the rows.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private WorldTestHost? _host;
        private WorldTestClient? _client;
        private WorldSession? _session;
        private Player? _player;

        internal OpcodeTable Opcodes => _host!.Opcodes;

        public async Task InitializeAsync()
        {
            _host = WorldTestHost.Start();
            (_client, _session) = await PacketHeuristicsTestKit.EnterClientAsync(_host, "CONTRACT", "Contract");
            _player = _session.Player;
        }

        public async Task DisposeAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            if (_host is not null)
            {
                await _host.DisposeAsync();
            }
        }

        internal async Task<Exception?> InvokeAsync(OpcodeHandler handler, byte[] payload)
        {
            WorldSession session = _session!;
            Player player = _player!;
            if (handler.World is { } world)
            {
                return await _host!.OnWorldAsync(() =>
                {
                    try
                    {
                        world(session, player, payload);
                        return (Exception?)null;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        return ex;
                    }
                });
            }

            try
            {
                await handler.Session!(session, payload).WaitAsync(TimeSpan.FromSeconds(10));
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return ex;
            }
        }
    }
}

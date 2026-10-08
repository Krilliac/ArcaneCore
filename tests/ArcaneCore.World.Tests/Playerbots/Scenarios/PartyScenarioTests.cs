using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The <c>party-master</c> scenario (<see cref="PartyScenario"/>) on the manual clock: a real socket client is the master of a managed
/// bot that runs autonomously (its party AI). The world is the scenario test world plus the Deadmines content and an uncommon item in
/// the wolf's loot (so its corpse starts a group roll); the bots may be on map 36.
/// </summary>
public sealed class PartyScenarioTests
{
    /// <summary>An uncommon (quality 2) item every wolf drops: at the group's default loot threshold, so its corpse starts a roll.</summary>
    private const uint ScenarioFang = 990300;

    [Fact]
    public async Task PartyMaster_TheBotJoinsFollowsFightsRollsHoldsReportsAndEntersTheDungeonWithItsMaster()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Configure);
        await world.Host.World.InvokeAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new GroundEverywhere());
            return true;
        });
        await using SocketMaster master = await SocketMaster.EnterAsync(world, "PARTYMASTER", "Partymaster");

        ScenarioReport report = await world.RunPassingAsync(new PartyScenario(master, WolfEntry));

        string text = report.ToString();
        Assert.Contains("the bot lands in the same instance", text);
        Assert.Contains("the group loot roll does not wait for the bot", text);
        PlayerbotStatus bot = world.Bots.Snapshot().Single(s => s.Name == PartyScenario.BotName);
        Assert.Equal(0u, bot.MapId);
    }

    [Fact]
    public async Task PartyMaster_WithoutARealPlayer_FailsAtItsFirstStep()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Configure);

        ScenarioReport report = await world.RunAsync(new PartyScenario());

        Assert.False(report.Passed);
        Assert.Equal("a real player is the master", report.FailedStep);
        Assert.IsType<PartyScenario>(PlayerbotScenarioCatalog.Find(world.Services, "party-master"));
    }

    private static void Configure(IServiceCollection services)
    {
        DeadminesTestContent.Register(services);
        services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
        {
            Enabled = true, MaxBots = 8, AllowedMaps = [0, 1, DungeonEntryScenario.Deadmines], Scenarios = { Enabled = true },
        }));
        var items = new InMemoryItemTemplateSource();
        items.Templates.Add(new ItemTemplate { Entry = LinenCloth, Name = "Linen Cloth", Class = 7, SubClass = 0, Quality = 1, Stackable = 20, SellPrice = 13 });
        items.Templates.Add(new ItemTemplate { Entry = ScenarioFang, Name = "Scenario Fang", Class = 15, SubClass = 0, Quality = 2, Stackable = 1, SellPrice = 50 });
        services.AddSingleton<IItemTemplateSource>(items);
        var loot = new LootContent(
            [
                (LootTableKind.Creature, new LootStoreRow(WolfEntry, LinenCloth, 100f, 0, 1, 1)),
                (LootTableKind.Creature, new LootStoreRow(WolfEntry, ScenarioFang, 100f, 0, 1, 1)),
            ],
            [new CreatureLootInfo(WolfEntry, WolfEntry, 0, WolfGold, WolfGold)]);
        services.AddScoped<ILootDataStore>(_ => new FixedLoot(loot));
    }

    /// <summary>
    /// Flat open ground at the human start's height (the synthetic maps have no terrain), so the bot can plan its walks around the
    /// start. It only walks there: in Westfall and the dungeon it lands on its master's spot by teleport.
    /// </summary>
    private sealed class GroundEverywhere : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => StartZ;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class FixedLoot(LootContent content) : ILootDataStore
    {
        public Task<LootContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    /// <summary>
    /// The real player: a <see cref="WorldTestClient"/> with a reader that records every packet it receives and, like a game client,
    /// acknowledges the server's teleports (MSG_MOVE_TELEPORT_ACK for a near one, MSG_MOVE_WORLDPORT_ACK after SMSG_NEW_WORLD).
    /// </summary>
    private sealed class SocketMaster : IPartyScenarioMaster, IAsyncDisposable
    {
        private readonly WorldTestClient _client;
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly List<(long Sequence, WorldOpcode Opcode, byte[] Payload)> _received = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reader;
        private long _sequence;

        private SocketMaster(WorldTestClient client, string name)
        {
            _client = client;
            Name = name;
            _reader = Task.Run(ReadLoopAsync);
        }

        public string Name { get; }

        public static async Task<SocketMaster> EnterAsync(ScenarioTestWorld world, string account, string character)
            => new(await world.EnterWorldAsync(account, character, AccountSecurity.Player), character);

        public async Task SendAsync(WorldOpcode opcode, byte[] payload)
        {
            await _send.WaitAsync();
            try
            {
                await _client.SendAsync(opcode, payload);
            }
            finally
            {
                _send.Release();
            }
        }

        public long Mark() => Interlocked.Read(ref _sequence) + 1;

        public IReadOnlyList<(WorldOpcode Opcode, byte[] Payload)> Received(long since)
        {
            lock (_received)
            {
                return [.. _received.Where(p => p.Sequence >= since).Select(p => (p.Opcode, p.Payload))];
            }
        }

        private async Task ReadLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                (WorldOpcode opcode, byte[] payload) packet;
                try
                {
                    packet = await _client.ReadAsync(TimeSpan.FromMinutes(10));
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    return; // the server closed the connection
                }

                lock (_received)
                {
                    _received.Add((Interlocked.Increment(ref _sequence), packet.opcode, packet.payload));
                }

                if (packet.opcode == WorldOpcode.MsgMoveTeleportAck)
                {
                    var reader = new PacketReader(packet.payload);
                    ulong guid = reader.ReadPackedGuid();
                    byte[] ack = new byte[16];
                    BinaryPrimitives.WriteUInt64LittleEndian(ack, guid);
                    await SendAsync(WorldOpcode.MsgMoveTeleportAck, ack);
                }
                else if (packet.opcode == WorldOpcode.SmsgNewWorld)
                {
                    await SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _client.DisposeAsync();
            try
            {
                await _reader;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // closed under the reader
            }

            _stop.Dispose();
            _send.Dispose();
        }
    }
}

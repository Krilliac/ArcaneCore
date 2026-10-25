using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Worldport;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Worldport;

/// <summary>
/// The Tier 3 same-map world-port spike (<c>.worldport</c>, docs/design/hot-reload-tiers.md section 5.5) over real sessions: the far
/// variant sends SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD with the map the player is on and puts it back on that map after
/// MSG_MOVE_WORLDPORT_ACK; the re-add variant sends no transfer packet; every step is recorded; the root exists only with
/// <c>World:HotCode:Enabled</c> and only for an Administrator.
/// </summary>
public sealed class WorldportSpikeTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    /// <summary>What the far path sends after the worldport ack (TeleportTests.Tele_FarTeleport_TransfersToTheNewMap_WithTheLoginPackets).</summary>
    private static readonly WorldOpcode[] LoginSequence =
    [
        WorldOpcode.SmsgSetRestStart, WorldOpcode.SmsgBindpointupdate, WorldOpcode.SmsgTutorialFlags,
        WorldOpcode.SmsgInitialSpells, WorldOpcode.SmsgActionButtons, WorldOpcode.SmsgInitializeFactions,
        WorldOpcode.SmsgLoginSettimespeed, WorldOpcode.SmsgUpdateObject, WorldOpcode.SmsgInitWorldStates,
    ];

    private sealed class ListLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public string[] Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }

        public required WorldTestClient Admin { get; init; }

        public required WorldportSpike Spike { get; init; }

        public required ListLogger Log { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Admin.DisposeAsync();
            await Host.OnWorldAsync(Spike.Dispose);
            await Host.DisposeAsync();
        }
    }

    private static async Task<Rig> StartAsync(string tag, TimeSpan? ackTimeout = null, TimeSpan? observeWindow = null, int fillerCharacters = 0)
    {
        WorldTestHost host = WorldTestHost.Start();
        for (int i = 0; i < fillerCharacters; i++)
        {
            // Characters created first take the low guids; the host disposes its clients.
            await host.EnterWorldAsync("WPF" + tag + i, "Wpf" + tag.ToLowerInvariant() + (char)('a' + i));
        }

        WorldTestClient admin = await host.EnterWorldAsync("WPA" + tag, "Wpa" + tag.ToLowerInvariant(), AccountSecurity.Administrator);
        var log = new ListLogger();
        TeleportFeature feature = host.WorldServices.GetRequiredService<TeleportFeature>();
        var spike = new WorldportSpike(host.World, () => feature, log, ackTimeout, observeWindow ?? TimeSpan.FromMilliseconds(100));
        Assert.True(host.WorldServices.GetRequiredService<CommandTableSource>().TryAdd([WorldportSpikeCommands.Create(spike, audit: null)]).Applied);
        await admin.CollectAsync(Quiet);
        return new Rig { Host = host, Admin = admin, Spike = spike, Log = log };
    }

    private static Task<WorldportTrace> LastRunAsync(Rig rig) => rig.Host.OnWorldAsync(() => rig.Spike.Recent[^1]);

    private static Task<string[]> StepsAsync(Rig rig) => rig.Host.OnWorldAsync(() => rig.Spike.Recent[^1].Steps.Select(s => s.Name).ToArray());

    [Fact]
    public async Task Far_SendsTransferPendingAndNewWorldForTheSameMap_AndTheWorldportAckPutsThePlayerBackOnIt()
    {
        await using Rig rig = await StartAsync("FAR");
        string name = "Wpafar";

        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport far -8900 -130 90 1.5");

        // Everything up to SMSG_NEW_WORLD: the command's reply, SMSG_TRANSFER_PENDING (map 0), SMSG_NEW_WORLD (map 0). No near-teleport
        // ack: the same-map move took the far path.
        var before = new List<(WorldOpcode Opcode, byte[] Payload)>();
        while (before.Count == 0 || before[^1].Opcode != WorldOpcode.SmsgNewWorld)
        {
            before.Add(await rig.Admin.ReadAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.DoesNotContain(before, p => p.Opcode == WorldOpcode.MsgMoveTeleportAck);
        byte[] pending = Assert.Single(before, p => p.Opcode == WorldOpcode.SmsgTransferPending).Payload;
        Assert.Equal(4, pending.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
        Assert.True(before.FindIndex(p => p.Opcode == WorldOpcode.SmsgTransferPending) < before.Count - 1, "TRANSFER_PENDING precedes NEW_WORLD");
        byte[] newWorld = before[^1].Payload;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        Assert.Equal(-8900f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(4)));
        Assert.Equal(-130f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(8)));
        Assert.Equal(90f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(12)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(16)));

        // Loading: the player is in no map until the client acks.
        Assert.Null(await rig.Host.PlayerStateAsync(name, p => p.Map));
        Assert.Equal(["requested", "far-scheduled", "transfer-sent"], await StepsAsync(rig));

        await rig.Admin.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        foreach (WorldOpcode opcode in LoginSequence)
        {
            Assert.Equal(opcode, (await rig.Admin.ReadAsync()).Opcode);
        }

        Assert.Equal((0u, -8900f, -130f, 90f), await rig.Host.PlayerStateAsync(name, p => (p.Map!.MapId, p.X, p.Y, p.Z)));
        await rig.Host.WaitForWorldAsync(() => rig.Spike.Recent[^1].Finished, "the run finishes after the observation window");
        WorldportTrace run = await LastRunAsync(rig);
        Assert.Equal("ok", run.Outcome);
        Assert.Equal(WorldportSpikeMode.FarSameMap, run.Mode);
        Assert.Equal(["requested", "far-scheduled", "transfer-sent", "worldport-ack", "re-added", "observed"], run.Steps.Select(s => s.Name));
        Assert.True(run.Steps.Zip(run.Steps.Skip(1)).All(p => p.First.ElapsedMs <= p.Second.ElapsedMs), "step times never go backwards");
        Assert.Contains("now on map 0", run.Steps.Single(s => s.Name == "re-added").Detail, StringComparison.Ordinal);

        // Every step is in the server log, with the prefix a human or agent greps for.
        string[] lines = rig.Log.Lines;
        foreach (string step in new[] { "transfer-sent", "worldport-ack", "re-added" })
        {
            Assert.Contains(lines, l => l.StartsWith(WorldportSpike.LogPrefix + " #" + run.Id + " far " + name, StringComparison.Ordinal) && l.Contains(" " + step + ":", StringComparison.Ordinal));
        }

        Assert.Contains(lines, l => l.Contains("finished after", StringComparison.Ordinal) && l.EndsWith(": ok", StringComparison.Ordinal));

        // In the world again: commands work, and the status lists the run.
        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport status");
        // One system message per line of the reply.
        string status = (await rig.Admin.ReadChatAsync()).Text + "\n" + string.Join('\n', (await rig.Admin.CollectAsync(Quiet))
            .Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text));
        Assert.Contains("#" + run.Id + " far " + name + " map 0", status, StringComparison.Ordinal);
        Assert.Contains("worldport-ack", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Far_WithoutCoordinates_UsesWhereThePlayerStands()
    {
        await using Rig rig = await StartAsync("HERE");
        (float x, float y, float z) = await rig.Host.PlayerStateAsync("Wpahere", p => (p.X, p.Y, p.Z));

        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport far");
        byte[] newWorld = await rig.Admin.ReadUntilAsync(WorldOpcode.SmsgNewWorld);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        Assert.Equal((x, y, z), (BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(4)), BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(8)), BinaryPrimitives.ReadSingleLittleEndian(newWorld.AsSpan(12))));
        await rig.Admin.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await rig.Host.WaitForWorldAsync(() => rig.Host.World.FindOnlinePlayer("Wpahere")!.Map is { MapId: 0 }, "back on map 0");
    }

    [Fact]
    public async Task ReAdd_SendsNoTransferPackets_ButTheLoginPacketsAndTheSelfCreate_AndKeepsThePlayerOnItsMap()
    {
        await using Rig rig = await StartAsync("READD");
        string name = "Wpareadd";
        (float x, float y) = await rig.Host.PlayerStateAsync(name, p => (p.X, p.Y));

        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport readd");
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await rig.Admin.CollectAsync(TimeSpan.FromMilliseconds(500));

        Assert.DoesNotContain(packets, p => p.Opcode is WorldOpcode.SmsgTransferPending or WorldOpcode.SmsgNewWorld or WorldOpcode.MsgMoveTeleportAck);
        WorldOpcode[] relevant = [.. packets.Select(p => p.Opcode).Where(o => LoginSequence.Contains(o))];
        Assert.Equal(LoginSequence, relevant.Take(LoginSequence.Length));
        Assert.Equal((0u, x, y), await rig.Host.PlayerStateAsync(name, p => (p.Map!.MapId, p.X, p.Y)));

        await rig.Host.WaitForWorldAsync(() => rig.Spike.Recent[^1].Finished, "the run finishes after the observation window");
        WorldportTrace run = await LastRunAsync(rig);
        Assert.Equal("ok", run.Outcome);
        Assert.Equal(WorldportSpikeMode.ReAddInPlace, run.Mode);
        Assert.Equal(["requested", "removed", "re-added", "observed"], run.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Far_WithoutAnAck_ReportsTheTimeout_AndAClientThatLeavesIsRecorded()
    {
        await using Rig rig = await StartAsync("NOACK", ackTimeout: TimeSpan.FromMilliseconds(200));

        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport far");
        await rig.Admin.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await rig.Host.WaitForWorldAsync(() => rig.Spike.Recent[^1].Steps.Any(s => s.Name == "ack-timeout"), "the ack timeout is reported");
        Assert.Contains("stage Far", (await LastRunAsync(rig)).Steps.Single(s => s.Name == "ack-timeout").Detail, StringComparison.Ordinal);
        Assert.Null((await LastRunAsync(rig)).Outcome); // still open: a late ack would complete it

        await rig.Admin.DisposeAsync(); // the client drops
        await rig.Host.WaitForWorldAsync(() => rig.Spike.Recent[^1].Finished, "the drop closes the run");
        WorldportTrace run = await LastRunAsync(rig);
        Assert.Equal("failed: the client left the world before the re-add", run.Outcome);
        Assert.Equal("client-left", run.Steps[^1].Name);
    }

    [Fact]
    public async Task ATeleportAnomalyReportedThroughServerAnomalies_IsRecordedInTheRun_ForAGuidAboveNine()
    {
        await using Rig rig = await StartAsync("ANOM", observeWindow: TimeSpan.FromSeconds(30), fillerCharacters: 9);
        uint low = await rig.Host.PlayerStateAsync("Wpaanom", p => p.Guid.Low);
        Assert.True(low >= 10, "the decimal and hex subject forms differ only from guid 10 up; the premise needs such a guid, got " + low);

        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport far");
        await rig.Admin.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await rig.Host.OnWorldAsync(() => ServerAnomalies.Report(rig.Host.World.FindOnlinePlayer("Wpaanom")!, ServerAnomalies.Teleport, ServerAnomalies.AckTimeout, "test anomaly"));

        await rig.Host.WaitForWorldAsync(() => rig.Spike.Recent[^1].Steps.Any(s => s.Name == "anomaly"), "the anomaly is recorded in the run");
        Assert.Contains("test anomaly", (await LastRunAsync(rig)).Steps.First(s => s.Name == "anomaly").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunInProgress_RefusesASecondOne()
    {
        await using Rig rig = await StartAsync("TWICE", observeWindow: TimeSpan.FromSeconds(30));

        await rig.Admin.SendChatAsync(ChatType.Say, Language.Common, ".worldport far");
        await rig.Admin.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        string? refusal = await rig.Host.OnWorldAsync(() =>
        {
            rig.Spike.StartReAdd(rig.Host.World.FindOnlinePlayer("Wpatwice")!, "test", out string? why);
            return why;
        });

        Assert.Equal("Wpatwice is not in a map.", refusal);
        await rig.Admin.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await rig.Host.WaitForWorldAsync(() => rig.Host.World.FindOnlinePlayer("Wpatwice")!.Map is not null, "back in the map");
        refusal = await rig.Host.OnWorldAsync(() =>
        {
            rig.Spike.StartFar(rig.Host.World.FindOnlinePlayer("Wpatwice")!, 0, 0, 0, 0, "test", out string? why);
            return why;
        });
        Assert.Equal("Wpatwice already has a spike run in progress.", refusal);
    }

    [Fact]
    public async Task TheCommands_AreForAnAdministratorOnly()
    {
        await using Rig rig = await StartAsync("SEC");
        await using WorldTestClient gm = await rig.Host.EnterWorldAsync("WPGM", "Wpgm", AccountSecurity.GameMaster);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".worldport far");

        Assert.Equal("This command is not available to you.", (await gm.ReadChatAsync()).Text);
        Assert.DoesNotContain(await gm.CollectAsync(Quiet), p => p.Opcode is WorldOpcode.SmsgTransferPending or WorldOpcode.SmsgNewWorld);
        Assert.Equal(0u, await rig.Host.PlayerStateAsync("Wpgm", p => p.Map!.MapId));
    }

    [Fact]
    public void TheSpike_IsRegisteredOnlyWithHotCodeEnabled()
    {
        static bool Registered(Dictionary<string, string?> values)
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            IServiceCollection services = new ServiceCollection().AddWorldDaemon(configuration);
            return services.Any(d => d.ServiceType == typeof(WorldportSpike));
        }

        Assert.False(Registered([]));
        Assert.False(Registered(new() { ["World:HotCode:Modules:Enabled"] = "true", ["World:HotCode:Modules:Directory"] = "modules" }));
        Assert.True(Registered(new() { ["World:HotCode:Enabled"] = "true" }));
    }
}

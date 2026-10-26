using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.MapFeed;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Ops.Control;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>Freezes the world clock: game time moves only through <c>AdvanceClockAsync</c>, so frame counts are exact.</summary>
internal sealed class FeedManualClock : IWorldFeature
{
    public void Attach(WorldRuntime world) => world.UseManualClock();
}

/// <summary>What a map feed test wants of its world (everything else is the harness default).</summary>
internal sealed class FeedWorldSetup
{
    /// <summary>Managed playerbots (the playerbot pack attached, the route seam registered). Cannot be combined with <see cref="Terrain"/>.</summary>
    public bool Bots { get; init; }

    /// <summary>A Young Wolf spawn and a chest spawn on map 0, near the human start.</summary>
    public bool Content { get; init; }

    /// <summary>With <see cref="Content"/>: this many more Young Wolf spawns (guids <see cref="FeedWorld.ExtraSpawnBase"/> and up), in a row beside the first.</summary>
    public int ExtraCreatures { get; init; }

    /// <summary>A flat synthetic terrain tile (<see cref="FeedWorld.GroundZ"/>) under the human start.</summary>
    public bool Terrain { get; init; } = true;

    /// <summary>A data root to use as it is (real data, read only); replaces <see cref="Terrain"/>.</summary>
    public string? DataRoot { get; init; }

    /// <summary>Whether the harness starts the feed (a test of the start itself starts it).</summary>
    public bool StartFeed { get; init; } = true;

    /// <summary>The feed's options (Enabled is true unless this says otherwise).</summary>
    public Action<WorldMapFeedOptions>? Options { get; init; }

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan WriteStallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The pipe name when the test wants to hold it first (null: a fresh unique one).</summary>
    public string? PipeName { get; init; }

    /// <summary>With <see cref="Content"/>, also spawn an elevator (a Transport-type game object) at <see cref="FeedWorld.ElevatorSpawn"/>.</summary>
    public bool Elevator { get; init; }

    /// <summary>Leave the pipe name at the production default, <c>arcanecore-mapfeed-{pid}</c>.</summary>
    public bool DefaultPipeName { get; init; }

    /// <summary>The packet tools' options, registered with the host (sessions read them when built); null leaves them unconfigured (off).</summary>
    public Action<WorldPacketToolsOptions>? PacketTools { get; init; }

    /// <summary>The host environment the feed judges the packet tools by.</summary>
    public string Environment { get; init; } = "Development";

    /// <summary>Whether the feed treats the process as the live server (<c>ARCANECORE_LIVE_SERVER</c>); false, never the test process's own environment.</summary>
    public bool LiveServer { get; init; }

    /// <summary>A permit to register as it is (instead of the one <see cref="PacketTools"/> registers: <c>AddSingleton&lt;PacketToolsPermit&gt;()</c> makes DI pick the longest constructor it can satisfy, the three-argument one), such as one built from configuration like the daemon's.</summary>
    internal PacketToolsPermit? Permit { get; init; }

    /// <summary>The action channel's options (lane devui-chan, docs/areas/atlas-actions.md); non-null registers <c>AddMapFeedActions()</c> with them. Null leaves the channel unconfigured (default config: off).</summary>
    public Action<MapFeedActionsOptions>? Actions { get; init; }

    /// <summary>The web ticket service lane auth implements (<see cref="IFeedActorTickets"/>); a test fake here.</summary>
    internal IFeedActorTickets? Tickets { get; init; }

    /// <summary>Register the real web login (<c>AddMapFeedWebLogin()</c>: the ticket store, the <c>login</c>/<c>logout</c>/<c>characters</c> ops and <c>links</c>) instead of a fake <see cref="Tickets"/>. Needs <see cref="Actions"/>.</summary>
    public bool WebLogin { get; init; }

    /// <summary>The clock the channel's caches and rate limits read (registered as the <see cref="TimeProvider"/>); null leaves the system clock.</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>Also start the separate web pipe (<c>WebFeedPipe</c>, section 0.10) with these options; its pipe name is unique.</summary>
    public Action<WebFeedPipeOptions>? WebPipe { get; init; }

    /// <summary>
    /// Apply the web pipe's client-account rules as production does, including the refusal of the daemon's own account. Off by default: the
    /// pipe client in a test is this very process, so most web pipe tests must name the daemon's account as the client.
    /// </summary>
    public bool StrictWebPipeAccount { get; init; }
}

/// <summary>
/// A world daemon in process (<see cref="WorldTestHost"/>: real world thread, in-memory stores) on the manual clock, with the map feed
/// started on a unique named pipe and its log captured. State: owns the host, the feed, the logger factory and the temporary terrain
/// directory, all released by <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class FeedWorld : IAsyncDisposable
{
    /// <summary>The human start (the position a new character logs in at).</summary>
    public const float StartX = -8949.95f;

    public const float StartY = -132.493f;

    public const float StartZ = 83.5312f;

    /// <summary>The height of the synthetic terrain tile.</summary>
    public const float GroundZ = 80f;

    public const uint WolfEntry = 299;

    public const uint WolfSpawn = 4242;

    public const uint ChestEntry = 2843;

    public const uint ChestSpawn = 77001;

    /// <summary>The elevator of <see cref="FeedWorldSetup.Elevator"/>: a type 11 game object, whose guid carries HighGuid.Transport.</summary>
    public const uint ElevatorEntry = 176080;

    public const uint ElevatorSpawn = 88001;

    public static ulong ElevatorGuid => ObjectGuid.WithEntry(HighGuid.Transport, ElevatorEntry, ElevatorSpawn).Value;

    /// <summary>The spawn id of the first extra wolf (<see cref="FeedWorldSetup.ExtraCreatures"/>).</summary>
    public const uint ExtraSpawnBase = 5000;

    private readonly ILoggerFactory _loggers;
    private readonly string? _terrainDirectory;
    private readonly List<FeedClient> _clients = [];
    private bool _feedStopped;

    private FeedWorld(WorldTestHost host, WorldMapFeed feed, WorldMapFeedOptions options, CapturingLoggerProvider logs, ILoggerFactory loggers, string? terrainDirectory)
    {
        Host = host;
        Feed = feed;
        Options = options;
        Logs = logs;
        _loggers = loggers;
        _terrainDirectory = terrainDirectory;
    }

    public WorldTestHost Host { get; }

    public WorldMapFeed Feed { get; }

    public WorldMapFeedOptions Options { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>The separate web pipe (null unless <see cref="FeedWorldSetup.WebPipe"/>).</summary>
    public WebFeedPipe? WebPipe { get; private set; }

    public static ulong ExtraWolfGuid(int index) => ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, ExtraSpawnBase + (uint)index).Value;

    public static ulong WolfGuid => ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn).Value;

    public static ulong ChestGuid => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;

    /// <summary>A GUID the way the feed writes it: 16 upper-case hex digits.</summary>
    public static string Hex(ulong guid) => guid.ToString("X16", CultureInfo.InvariantCulture);

    public static async Task<FeedWorld> StartAsync(FeedWorldSetup? setup = null)
    {
        setup ??= new FeedWorldSetup();
        if (setup.Bots && (setup.Terrain || setup.DataRoot is not null))
        {
            throw new ArgumentException("the bot host has no terrain option; use a world without bots for terrain");
        }

        string? terrainDirectory = setup.Terrain && setup.DataRoot is null ? FeedTerrain.Write(GroundZ) : null;
        string? dataDirectory = setup.DataRoot ?? terrainDirectory;
        var logs = new CapturingLoggerProvider();
        ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));

        var options = new WorldMapFeedOptions { Enabled = true };
        setup.Options?.Invoke(options);

        WorldTestHost host = StartHost(setup, dataDirectory, loggers);
        var feed = new WorldMapFeed(Microsoft.Extensions.Options.Options.Create(options), host.World, host.WorldServices, loggers)
        {
            PipeNameTemplate = setup.DefaultPipeName
                ? WorldMapFeedOptions.PipeNameTemplate
                : setup.PipeName ?? "arcanecore-mapfeed-test-" + Guid.NewGuid().ToString("N"),
            IdleTimeout = setup.IdleTimeout,
            WriteStallTimeout = setup.WriteStallTimeout,
            QueryTimeout = setup.QueryTimeout,
            EnvironmentName = setup.Environment,
            LiveServer = setup.LiveServer,
        };
        var world = new FeedWorld(host, feed, options, logs, loggers, terrainDirectory);
        if (setup.StartFeed)
        {
            await feed.StartAsync(CancellationToken.None);
            if (setup.WebPipe is not null)
            {
                world.WebPipe = host.WorldServices.GetRequiredService<WebFeedPipe>();
                world.WebPipe.PipeNameTemplate = "arcanecore-mapfeed-web-test-" + Guid.NewGuid().ToString("N");
                world.WebPipe.SkipClientAccountRules = !setup.StrictWebPipeAccount;
                await world.WebPipe.StartAsync(CancellationToken.None);
            }
        }

        return world;
    }

    private static WorldTestHost StartHost(FeedWorldSetup setup, string? dataDirectory, ILoggerFactory loggers)
    {
        CreatureTestContext? creatures = null;
        GameObjectTestContext? objects = null;
        if (setup.Content)
        {
            var wolf = new CreatureTemplate
            {
                Entry = WolfEntry, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35, CreatureType = 1, Family = 1,
                MinLevelHealth = 55, MaxLevelHealth = 55,
            };
            var spawn = new CreatureSpawn { Guid = WolfSpawn, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
            var spawns = new List<CreatureSpawn> { spawn };
            for (int i = 0; i < setup.ExtraCreatures; i++)
            {
                spawns.Add(spawn with { Guid = ExtraSpawnBase + (uint)i, Y = -131f + (i * 0.5f) });
            }

            creatures = new CreatureTestContext(new CreatureContent([wolf], spawns, [], [], []));

            var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Battered Chest", Data = new uint[GameObjectTemplate.DataCount] };
            var chestSpawn = new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
            var templates = new List<GameObjectTemplate> { chest };
            var objectSpawns = new List<GameObjectSpawn> { chestSpawn };
            if (setup.Elevator)
            {
                templates.Add(new GameObjectTemplate { Entry = ElevatorEntry, Type = (uint)GameObjectType.Transport, DisplayId = 455, Name = "Elevator", Data = new uint[GameObjectTemplate.DataCount] });
                objectSpawns.Add(new GameObjectSpawn { Guid = ElevatorSpawn, Entry = ElevatorEntry, MapId = 0, X = -8930f, Y = -140f, Z = 90f });
            }

            objects = new GameObjectTestContext(new GameObjectContent(templates, objectSpawns, [], [], []), ArcaneCore.Kernel.WorldData.Loot.LootContent.Empty);
        }

        CreatureTestStore.Current.Value = creatures;
        GameObjectTestStore.Current.Value = objects;
        try
        {
            void Services(IServiceCollection services)
            {
                services.AddSingleton<IWorldFeature, FeedManualClock>();
                if (setup.PacketTools is { } || setup.Permit is not null)
                {
                    services.AddOptions();
                    if (setup.PacketTools is { } tools)
                    {
                        services.Configure(tools);
                    }

                    if (setup.Permit is { } permit)
                    {
                        services.AddSingleton(permit);
                    }
                    else
                    {
                        services.AddSingleton<PacketToolsPermit>();
                    }

                    // The daemon registers its opcode table (WorldServiceCollectionExtensions); the test host builds its own outside DI.
                    services.AddSingleton(_ => WorldServiceCollectionExtensions.BuildOpcodeTable());
                }

                if (setup.Actions is { } actions)
                {
                    // The channel logs through the harness's factory (so the test reads the audit line) and builds its per-request executors
                    // from the same opcode table the daemon registers.
                    services.AddOptions();
                    services.AddSingleton(loggers);
                    if (setup.PacketTools is null && setup.Permit is null)
                    {
                        services.AddSingleton(_ => WorldServiceCollectionExtensions.BuildOpcodeTable());
                    }

                    services.Configure(actions);
                    if (setup.Tickets is { } tickets)
                    {
                        services.AddSingleton(tickets);
                    }

                    // A scoped service the spy commands resolve from the command context's session services: it tells the tests when the
                    // per-request service scope is disposed.
                    services.AddSingleton<ScopeProbeState>();
                    services.AddScoped<ScopeProbe>();

                    if (setup.Time is { } time)
                    {
                        services.AddSingleton(time);
                    }

                    services.AddMapFeedActions();
                    if (setup.WebLogin)
                    {
                        services.AddMapFeedWebLogin();
                    }
                }

                if (setup.WebPipe is { } webPipe)
                {
                    services.AddOptions();
                    services.Configure(webPipe);
                    services.AddWebFeedPipe();
                }
            }

            return setup.Bots
                ? PartyTestHost.Start(configureServices: Services)
                : WorldTestHost.Start(
                    configure: o =>
                    {
                        if (dataDirectory is not null)
                        {
                            o.Maps.DataDirectory = dataDirectory;
                        }
                    },
                    configureServices: Services);
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            GameObjectTestStore.Current.Value = null;
        }
    }

    /// <summary>A character in the world at the human start (it stays for the life of the host).</summary>
    public async Task<WorldTestClient> AddPlayerAsync(string name = "Feedalice")
        => await Host.EnterWorldAsync(name.ToUpperInvariant(), name);

    /// <summary>A managed bot, logged in at the human start. Needs <see cref="FeedWorldSetup.Bots"/>.</summary>
    public Task<Guid> AddBotAsync(string name) => PartyTestHost.StartBotAsync(Host, name);

    /// <summary>Connect a client to the feed's pipe; with <paramref name="hello"/> the handshake is done and its reply checked.</summary>
    public async Task<FeedClient> ConnectAsync(bool hello = true)
    {
        Assert.NotNull(Feed.PipeName);
        FeedClient client = await FeedClient.ConnectAsync(Feed.PipeName!);
        _clients.Add(client);
        if (hello)
        {
            JsonElement reply = await client.HelloAsync();
            Assert.Equal("hello", reply.Op());
        }

        return client;
    }

    /// <summary>Log lines of the action channel (category <c>ArcaneCore.Ops.MapFeed.Actions</c>).</summary>
    public CapturedCategoryLog[] ActionLog => [.. Logs.Entries.Where(e => e.Category == "ArcaneCore.Ops.MapFeed.Actions")];

    /// <summary>
    /// Connect to the shared pipe and bind the connection's role: <c>web</c> sends <c>{"op":"role","role":"web"}</c> right after the hello and
    /// checks the acknowledgement; <c>local</c> sends nothing (a connection whose first extension op is not <c>role</c> is local).
    /// </summary>
    public async Task<FeedClient> ConnectAsync(string role)
    {
        FeedClient client = await ConnectAsync();
        if (role == "web")
        {
            await client.SendAsync("""{"op":"role","id":900,"role":"web"}""");
            JsonElement ack = await client.ReadOpAsync("result");
            Assert.True(ack.GetProperty("ok").GetBoolean(), ack.ToString());
            Assert.Equal("web", ack.GetProperty("role").GetString());
        }

        return client;
    }

    /// <summary>Connect to the separate web pipe (no <c>role</c> op: the pipe fixes the role) and do the hello.</summary>
    public async Task<FeedClient> ConnectWebPipeAsync()
    {
        Assert.NotNull(WebPipe?.PipeName);
        FeedClient client = await FeedClient.ConnectAsync(WebPipe!.PipeName!);
        _clients.Add(client);
        Assert.Equal("hello", (await client.HelloAsync()).Op());
        return client;
    }

    /// <summary>
    /// Advance the world <paramref name="ticks"/> times <paramref name="stepMs"/> of game time, reading the frame of every tick that
    /// captured one before the next tick runs (so the connection is never behind and nothing is dropped).
    /// </summary>
    public async Task<List<JsonElement>> StepFramesAsync(FeedClient client, int ticks, uint stepMs = 50)
    {
        var frames = new List<JsonElement>();
        for (int i = 0; i < ticks; i++)
        {
            long before = Feed.FramesCaptured;
            await Host.World.AdvanceClockAsync(stepMs);
            if (Feed.FramesCaptured > before)
            {
                frames.Add(await client.ReadOpAsync("frame"));
            }
        }

        return frames;
    }

    /// <summary>Run the world on the manual clock for <paramref name="ticks"/> ticks without reading anything.</summary>
    public async Task StepAsync(int ticks, uint stepMs = 50)
    {
        for (int i = 0; i < ticks; i++)
        {
            await Host.World.AdvanceClockAsync(stepMs);
        }
    }

    /// <summary>Install a line-of-sight service and/or a pathfinder on the world's collision (on the world thread).</summary>
    public Task InstallCollisionAsync(ILineOfSight? lineOfSight = null, IPathfinder? pathfinder = null)
        => Host.OnWorldAsync(() => WorldCollision.Of(Host.World).Install(lineOfSight, pathfinder));

    /// <summary>How many handlers the world's <c>Updated</c> event has now, read from its backing field (the idle feed must add none).</summary>
    public int UpdatedHandlers()
    {
        FieldInfo field = typeof(WorldRuntime).GetField("Updated", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorldRuntime.Updated has no backing field: the idle-cost check cannot run");
        return (field.GetValue(Host.World) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>Log lines of the feed (category <see cref="WorldMapFeed.LogCategory"/>).</summary>
    public CapturedCategoryLog[] FeedLog => [.. Logs.Entries.Where(e => e.Category == WorldMapFeed.LogCategory)];

    /// <summary>Stop the feed now, as the host does at shutdown (a test of the stop itself; disposing the world does not stop it twice).</summary>
    public async Task StopFeedAsync()
    {
        if (!_feedStopped)
        {
            _feedStopped = true;
            await Feed.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (FeedClient client in _clients)
        {
            await client.DisposeAsync();
        }

        await StopFeedAsync();
        if (WebPipe is not null)
        {
            await WebPipe.StopAsync(CancellationToken.None);
        }

        await Host.DisposeAsync();
        _loggers.Dispose();
        if (_terrainDirectory is not null)
        {
            try
            {
                Directory.Delete(_terrainDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory in the temp folder; a handle still open on it is no failure of the test.
            }
        }
    }
}

/// <summary>A synthetic terrain data root: a flat <c>.map</c> tile under the human start, built at run time (nothing derived from client data).</summary>
internal static class FeedTerrain
{
    public static string Write(float groundZ)
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanecore-mapfeed-" + Guid.NewGuid().ToString("N"));
        string maps = Path.Combine(root, "maps");
        Directory.CreateDirectory(maps);
        (int tx, int ty) = TerrainTile.TileOf(FeedWorld.StartX, FeedWorld.StartY) ?? throw new InvalidOperationException("the human start is outside the map");
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, tx, ty)), FlatTile(groundZ));
        return root;
    }

    /// <summary>A tile with only a height section of the "no height" kind: the whole tile stands at <paramref name="groundZ"/>.</summary>
    private static byte[] FlatTile(float groundZ)
    {
        byte[] bytes = new byte[56];
        Span<byte> span = bytes;

        // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:4-16 (header: magic, version, then offset/size pairs area, height, liquid, holes)
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:370-379 (the server reads the same ten u32, little-endian)
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], TerrainTile.MapMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], TerrainTile.MapVersionMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 40u); // height section offset (right after the header)
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 16u); // height section size

        // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:28-38 (MHGT header: fourcc, flags, gridHeight, gridMaxHeight; 0x1 = no height)
        // ref: vmangos@0e3ff01 src/game/Maps/GridMap.cpp:38,174 (MAP_HEIGHT_MAGIC = "MHGT", little-endian u32 = 0x5447484D)
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:41,121-130 (HeightMagic = 0x5447484D and the server's read of that header)
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 0x5447484Du); // "MHGT"
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 1u);
        BinaryPrimitives.WriteSingleLittleEndian(span[48..], groundZ);
        BinaryPrimitives.WriteSingleLittleEndian(span[52..], groundZ);
        return bytes;
    }
}

/// <summary>A line-oriented NDJSON client of the feed over a real named pipe. Every read is bounded.</summary>
internal sealed class FeedClient : IAsyncDisposable
{
    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(20);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;

    private FeedClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    public static async Task<FeedClient> ConnectAsync(string pipeName, int timeoutMs = 5000)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        return new FeedClient(pipe);
    }

    /// <summary>Send one line (a newline is added).</summary>
    public Task SendAsync(string json) => SendRawAsync(Encoding.UTF8.GetBytes(json + "\n"));

    public async Task SendRawAsync(byte[] bytes)
    {
        await _pipe.WriteAsync(bytes);
        await _pipe.FlushAsync();
    }

    /// <summary>The next line parsed, or null when the server closed the connection.</summary>
    public async Task<JsonElement?> TryReadAsync()
    {
        using var timeout = new CancellationTokenSource(ReadBound);
        string? line;
        try
        {
            line = await _reader.ReadLineAsync(timeout.Token);
        }
        catch (IOException)
        {
            return null; // the server ended the connection under a pending read
        }

        if (line is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    /// <summary>The next line; fails the test when the connection ended instead.</summary>
    public async Task<JsonElement> ReadAsync()
        => await TryReadAsync() ?? throw new InvalidOperationException("the server closed the connection when a line was expected");

    /// <summary>The next line, which must be <paramref name="op"/>.</summary>
    public async Task<JsonElement> ReadOpAsync(string op)
    {
        JsonElement line = await ReadAsync();
        Assert.True(line.Op() == op, $"expected op '{op}', got: {line}");
        return line;
    }

    /// <summary>Send hello (protocol 1) and return the reply.</summary>
    public async Task<JsonElement> HelloAsync()
    {
        await SendAsync("""{"op":"hello","protocol":1}""");
        return await ReadAsync();
    }

    /// <summary>Send a query and return the next line (the result).</summary>
    public async Task<JsonElement> QueryAsync(long id, string kind, uint map, Vector3 a, Vector3? b = null, uint instance = 0)
    {
        await SendAsync(QueryLine(id, kind, map, a, b, instance));
        return await ReadAsync();
    }

    /// <summary>A query line (<paramref name="b"/> only for the kinds that take two points).</summary>
    public static string QueryLine(long id, string kind, uint map, Vector3 a, Vector3? b = null, uint instance = 0)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"op\":\"query\",\"id\":{id},\"kind\":\"{kind}\",\"map\":{map},\"instance\":{instance},\"a\":{Point(a)}");
        return b is { } to ? line + ",\"b\":" + Point(to) + "}" : line + "}";
    }

    /// <summary>A subscribe line. <paramref name="kinds"/> is the JSON array text (for example <c>["bot"]</c>).</summary>
    public static string Subscribe(uint map = 0, double? hz = null, string? kinds = null, bool? paths = null, uint instance = 0)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{{\"op\":\"subscribe\",\"map\":{map},\"instance\":{instance}"));
        if (hz is { } rate)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $",\"hz\":{rate}"));
        }

        if (kinds is not null)
        {
            text.Append(",\"kinds\":").Append(kinds);
        }

        if (paths is { } withPaths)
        {
            text.Append(",\"paths\":").Append(withPaths ? "true" : "false");
        }

        return text.Append('}').ToString();
    }

    private static string Point(Vector3 p) => string.Create(CultureInfo.InvariantCulture, $"[{p.X},{p.Y},{p.Z}]");

    /// <summary>Read until the server closes the connection; the lines that came before that.</summary>
    public async Task<List<JsonElement>> ReadToEndAsync()
    {
        var lines = new List<JsonElement>();
        while (await TryReadAsync() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _pipe.DisposeAsync();
    }
}

/// <summary>Accessors for the lines the feed writes.</summary>
internal static class FeedJson
{
    public static string Op(this JsonElement line) => line.GetProperty("op").GetString()!;

    public static string Code(this JsonElement line) => line.GetProperty("code").GetString()!;

    public static JsonElement[] Entities(this JsonElement frame) => [.. frame.GetProperty("entities").EnumerateArray()];

    public static JsonElement Counters(this JsonElement frame) => frame.GetProperty("counters");

    /// <summary>The one entity of the frame with this GUID (16 hex digits).</summary>
    public static JsonElement Entity(this JsonElement frame, ulong guid)
        => Assert.Single(frame.Entities(), e => e.GetProperty("g").GetString() == FeedWorld.Hex(guid));

    public static JsonElement[] OfKind(this JsonElement frame, string kind) => [.. frame.Entities().Where(e => e.GetProperty("k").GetString() == kind)];

    public static Vector3 ToVector(this JsonElement point)
        => new(point[0].GetSingle(), point[1].GetSingle(), point[2].GetSingle());

    public static Vector3[] ToVectors(this JsonElement points) => [.. points.EnumerateArray().Select(p => p.ToVector())];

    public static string[] Strings(this JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}

/// <summary>
/// Static models for the query tests: a solid wall and a doodad (a plane each, across x), and a building (a floor, an interior area)
/// along x. Lets the query tests give answers the open default cannot: blocked segments with a first hit, doodads that block only when
/// asked, a model floor above the terrain, an interior.
/// </summary>
internal sealed class ScriptedLineOfSight : ILineOfSight
{
    /// <summary>A solid model: a segment across x = <see cref="WallX"/> is blocked, doodads ignored or not.</summary>
    public const float WallX = -8940f;

    /// <summary>A doodad (M2): a segment across x = <see cref="DoodadX"/> is blocked only when doodads count.</summary>
    public const float DoodadX = -8920f;

    /// <summary>The building spans x from <see cref="BuildingMinX"/> to <see cref="BuildingMaxX"/>.</summary>
    public const float BuildingMinX = -8900f;

    public const float BuildingMaxX = -8880f;

    /// <summary>The building's floor.</summary>
    public const float BuildingFloor = 82f;

    public bool Enabled => true;

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => FirstBlock(from, to, ignoreM2) is null;

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        Vector3? first = FirstBlock(from, to, ignoreM2: false);
        hit = first ?? to;
        return first is not null;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        => InBuilding(x) && z >= BuildingFloor ? BuildingFloor : null;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = InBuilding(x) ? new ModelAreaInfo(ModelAreaInfo.MogpInterior, 1, 1, 1, BuildingFloor) : default;
        return InBuilding(x);
    }

    private static bool InBuilding(float x) => x is >= BuildingMinX and <= BuildingMaxX;

    private static Vector3? FirstBlock(Vector3 from, Vector3 to, bool ignoreM2)
    {
        float best = float.PositiveInfinity;
        float dx = to.X - from.X;
        if (MathF.Abs(dx) >= 1e-6f)
        {
            foreach (float plane in ignoreM2 ? new[] { WallX } : new[] { WallX, DoodadX })
            {
                float t = (plane - from.X) / dx;
                if (t is >= 0f and <= 1f && t < best)
                {
                    best = t;
                }
            }
        }

        return float.IsPositiveInfinity(best) ? null : from + ((to - from) * best);
    }
}

/// <summary>A pathfinder that answers what the test scripts and remembers what it was asked (world thread).</summary>
internal sealed class ScriptedPathfinder(Func<Vector3, Vector3, PathResult> answer) : IPathfinder
{
    public bool Enabled => true;

    public int Calls { get; private set; }

    public PathOptions? LastOptions { get; private set; }

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        Calls++;
        LastOptions = options;
        return answer(start, end);
    }
}

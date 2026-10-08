using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.DebugDraw;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.DebugDraw;

/// <summary>
/// <c>.debug vis</c> end to end over loopback (docs/areas/debug-draw.md): the markers reach the requesting GM's client only, they carry the
/// data the command computed (line of sight, path, waypoints), and they are removed by <c>.debug vis clear</c>, the cap, their lifetime
/// and logout, with a destroy for every create.
/// </summary>
public sealed partial class DebugDrawCommandTests
{
    private const uint WolfEntry = 20102;
    private const uint WolfSpawn = 88201;

    // The human start is (-8949.95, -132.49, 83.53); the wolf stands 10 yards east of it.
    private static readonly Vector3 WolfAt = new(-8940f, -132f, 83.5f);

    private static readonly CreatureWaypoint[] WolfPath =
    [
        new(1, -8935f, -130f, 83.5f, 0f, 0),
        new(2, -8925f, -130f, 83.5f, 0f, 2500) { Run = true },
        new(3, -8925f, -110f, 83.5f, 0f, 0),
    ];

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    private sealed record Run(List<string> Replies, List<(WorldOpcode Opcode, byte[] Payload)> Packets)
    {
        public List<ParsedMarker> Created => [.. Packets.Where(p => p.Opcode == WorldOpcode.SmsgUpdateObject).SelectMany(p => ParseMarkerCreates(p.Payload))];

        public List<ObjectGuid> Destroyed => [.. Packets.Where(p => p.Opcode == WorldOpcode.SmsgDestroyObject).Select(p => new ObjectGuid(BinaryPrimitives.ReadUInt64LittleEndian(p.Payload)))];
    }

    private sealed record ParsedMarker(ObjectGuid Guid, Vector3 Position, uint DisplayId)
    {
        public DebugMarkerKind Kind => (DebugMarkerKind)(Guid.Entry - DebugMarkerStyles.EntryBase);
    }

    private static WorldTestHost Start(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    {
        var wolf = new CreatureTemplate
        {
            Entry = WolfEntry, Name = "Test Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent(
            [wolf],
            [new CreatureSpawn { Guid = WolfSpawn, Entry = WolfEntry, MapId = 0, X = WolfAt.X, Y = WolfAt.Y, Z = WolfAt.Z }],
            WolfPath.Select(p => (WolfSpawn, p)), [], []));
        try
        {
            return WorldTestHost.Start(configureServices: services =>
            {
                if (settings is not null)
                {
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
                }

                configureServices?.Invoke(services);
            });
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, string name, AccountSecurity security)
    {
        WorldTestClient client = await host.EnterWorldAsync(name.ToUpperInvariant(), name, security);
        await client.CollectAsync(Quiet);
        return client;
    }

    private static async Task<Run> SendAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectFromAsync(WorldOpcode.SmsgMessagechat, Quiet);
        List<string> replies = [.. packets
            .Where(p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload[0] == (byte)ChatType.System)
            .Select(p => ChatMessage.Parse(p.Payload).Text)];
        return new Run(replies, packets);
    }

    private static DebugDrawFeature Feature(WorldTestHost host) => host.WorldServices.GetRequiredService<DebugDrawFeature>();

    private static Task<IReadOnlyList<ObjectGuid>> MarkersOfAsync(WorldTestHost host, string gm)
        => host.OnWorldAsync(() => Feature(host).MarkerGuids(host.World.FindOnlinePlayer(gm)!));

    private static Task SelectWolfAsync(WorldTestHost host, string gm)
        => host.OnWorldAsync(() => { host.World.FindOnlinePlayer(gm)!.Selection = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn); });

    /// <summary>Nothing the bystander received names any of <paramref name="markers"/>.</summary>
    private static async Task AssertNeverSawAsync(WorldTestClient bystander, IEnumerable<ObjectGuid> markers)
    {
        List<(WorldOpcode Opcode, byte[] Payload)> seen = await bystander.CollectAsync(Quiet);
        foreach (ObjectGuid guid in markers)
        {
            byte[] packed = guid.ToPacked();
            byte[] full = BitConverter.GetBytes(guid.Value);
            Assert.DoesNotContain(seen, p => p.Payload.AsSpan().IndexOf(packed) >= 0 || p.Payload.AsSpan().IndexOf(full) >= 0);
        }
    }

    [Fact]
    public void Levels_DebugVisNeedsAGameMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "debug", "debug vis", "debug vis los", "debug vis path", "debug vis waypoints", "debug vis cells", "debug vis collision",
                     "debug vis height", "debug vis range", "debug vis spawns", "debug vis kit", "debug vis list", "debug vis clear" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task Los_SendsMarkersToTheGmOnly_AndClearDestroysEachOne()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await using WorldTestClient bystander = await EnterAsync(host, "Watcher", AccountSecurity.Player);
        await gm.CollectAsync(Quiet);
        await SelectWolfAsync(host, "Drawer");

        Run los = await SendAsync(gm, ".debug vis los");

        Assert.Contains(los.Replies, r => r.Contains("line of sight to Test Wolf CLEAR", StringComparison.Ordinal));
        Assert.Contains(los.Replies, r => r.Contains("only you see them", StringComparison.Ordinal));
        List<ParsedMarker> created = los.Created;
        Assert.NotEmpty(created);
        Assert.All(created, m => Assert.Equal(DebugMarkerKind.LosClear, m.Kind));
        // The far end (at the wolf's eye) has a glow companion: the green crystal and the green aura at the same spot.
        var wolfEye = new Vector3(WolfAt.X, WolfAt.Y, WolfAt.Z + MapCollision.DefaultEyeHeight);
        Assert.Contains(created, m => m.DisplayId == 2972 && Vector3.Distance(m.Position, wolfEye) < 0.01f);
        Assert.Contains(created, m => m.DisplayId == 3993 && Vector3.Distance(m.Position, wolfEye) < 0.01f);
        // No marker inside the invoker.
        Assert.All(created, m => Assert.True(Vector2.Distance(new Vector2(m.Position.X, m.Position.Y), new Vector2(-8949.95f, -132.49f)) >= DebugDrawCommands.SkipNearInvoker - 0.01f));

        IReadOnlyList<ObjectGuid> held = await MarkersOfAsync(host, "Drawer");
        Assert.Equal(created.Select(m => m.Guid.Value).Order(), held.Select(g => g.Value).Order());
        await AssertNeverSawAsync(bystander, held);
        Assert.Empty(await host.PlayerStateAsync("Drawer", p => p.VisibleObjects.Where(DebugMarkerStyles.IsMarkerGuid).ToList()));

        Run clear = await SendAsync(gm, ".debug vis clear");

        Assert.Equal(DebugDrawCommands.Cleared(held.Count), Assert.Single(clear.Replies));
        Assert.Equal(held.Select(g => g.Value).Order(), clear.Destroyed.Select(g => g.Value).Order());
        Assert.Empty(await MarkersOfAsync(host, "Drawer"));
        await AssertNeverSawAsync(bystander, held);
        Assert.Equal(DebugDrawCommands.Cleared(0), Assert.Single((await SendAsync(gm, ".debug vis clear")).Replies));
    }

    [Fact]
    public async Task Los_ThroughAWall_MarksTheHitPoint_FromTheCollisionService()
    {
        await using WorldTestHost host = Start();
        await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(lineOfSight: new WallAtX(-8945f)));
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");

        Run los = await SendAsync(gm, ".debug vis los");

        Assert.Contains(los.Replies, r => r.Contains("line of sight to Test Wolf BLOCKED", StringComparison.Ordinal));
        ParsedMarker hit = Assert.Single(los.Created, m => m.Kind == DebugMarkerKind.HitPoint && m.DisplayId == 5746);
        Assert.Equal(-8945.5f, hit.Position.X, 0.05f); // 0.5 yards back toward the GM, as vmangos getObjectHitPos with -0.5
        Assert.All(los.Created.Where(m => m.Kind == DebugMarkerKind.LosBlocked), m => Assert.True(m.Position.X < -8945f));
        Assert.All(los.Created.Where(m => m.Kind == DebugMarkerKind.LosOccluded), m => Assert.True(m.Position.X > -8945.5f));
        Assert.Contains(los.Created, m => m.Kind == DebugMarkerKind.LosOccluded);
    }

    [Fact]
    public async Task Path_DrawsTheCornersThePathfinderReturns()
    {
        await using WorldTestHost host = Start();
        var detour = new Vector3(-8946f, -125f, 83.5f);
        await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(pathfinder: new DetourPathfinder(detour)));
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");

        Run path = await SendAsync(gm, ".debug vis path");

        Assert.Contains(path.Replies, r => r.Contains("path to Test Wolf: Normal", StringComparison.Ordinal) && r.Contains("pathfinder DetourPathfinder", StringComparison.Ordinal));
        List<ParsedMarker> corners = [.. path.Created.Where(m => m.Kind == DebugMarkerKind.PathCorner && m.DisplayId == DebugMarkerStyles.Of(DebugMarkerKind.PathCorner).DisplayId)];
        Assert.Equal(2, corners.Count); // the start corner is the GM's own position and is left out
        Assert.Contains(corners, m => Vector3.Distance(m.Position, detour) < 0.01f);
        Assert.Contains(corners, m => Vector3.Distance(m.Position, WolfAt) < 0.01f);
        Assert.Contains(path.Created, m => m.Kind == DebugMarkerKind.PathFill);
        Assert.DoesNotContain(path.Created, m => m.Kind == DebugMarkerKind.PathCornerBad);
    }

    [Fact]
    public async Task Path_WithoutNavmesh_IsMarkedAsAStraightLine()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");

        Run path = await SendAsync(gm, ".debug vis path");

        Assert.Contains(path.Replies, r => r.Contains("NotUsingPath", StringComparison.Ordinal) && r.Contains("(no navmesh data)", StringComparison.Ordinal));
        Assert.Contains(path.Created, m => m.Kind == DebugMarkerKind.PathCornerBad);
        Assert.DoesNotContain(path.Created, m => m.Kind == DebugMarkerKind.PathCorner);
    }

    [Fact]
    public async Task Waypoints_DrawTheCreatureMovementNodes_AndListThem()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);

        Assert.Equal(DebugDrawCommands.SelectCreature, Assert.Single((await SendAsync(gm, ".debug vis waypoints")).Replies));
        await SelectWolfAsync(host, "Drawer");
        Run run = await SendAsync(gm, ".debug vis waypoints");

        Assert.Contains(run.Replies, r => r.Contains("3 node(s) from creature_movement", StringComparison.Ordinal));
        Assert.Contains(run.Replies, r => r.Contains("waypoint 2 of Test Wolf", StringComparison.Ordinal) && r.Contains("wait 2.5 s, run", StringComparison.Ordinal));
        List<ParsedMarker> nodes = [.. run.Created.Where(m => m.Kind == DebugMarkerKind.Waypoint && m.DisplayId == 2974)];
        Assert.Equal(3, nodes.Count);
        foreach (CreatureWaypoint point in WolfPath)
        {
            Assert.Contains(nodes, m => Vector3.Distance(m.Position, new Vector3(point.X, point.Y, point.Z)) < 0.01f);
        }

        // The loop back from the last node to the first is drawn too.
        Assert.Contains(run.Created, m => m.Kind == DebugMarkerKind.WaypointFill && m.Position.X > -8935f && m.Position.X < -8925f && m.Position.Y > -130f && m.Position.Y < -110f);
    }

    [Fact]
    public async Task RightClickingAMarker_PrintsItsLabel_AndTheQueryNamesItsKind()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");
        Run run = await SendAsync(gm, ".debug vis waypoints");
        ParsedMarker node = run.Created.First(m => m.Kind == DebugMarkerKind.Waypoint && m.DisplayId == 2974 && m.Position.X == -8925f && m.Position.Y == -130f);

        var use = new PacketWriter(8);
        use.WriteUInt64(node.Guid.Value);
        await gm.SendAsync(WorldOpcode.CmsgGameobjUse, use.ToArray());
        ChatMessage label = await gm.ReadChatAsync();
        Assert.StartsWith("[waypoints] waypoint 2 of Test Wolf", label.Text, StringComparison.Ordinal);

        var query = new PacketWriter(12);
        query.WriteUInt32(node.Guid.Entry);
        query.WriteUInt64(node.Guid.Value);
        await gm.SendAsync(WorldOpcode.CmsgGameobjectQuery, query.ToArray());
        var reader = new PacketReader(await gm.ReadUntilAsync(WorldOpcode.SmsgGameobjectQueryResponse));
        Assert.Equal(node.Guid.Entry, reader.ReadUInt32());
        Assert.Equal(10u, reader.ReadUInt32()); // goober
        Assert.Equal(2974u, reader.ReadUInt32());
        Assert.Equal("DebugDraw: waypoint", reader.ReadCString());
    }

    [Fact]
    public async Task TheCap_RemovesTheOldestDrawing_AndDropsWhatDoesNotFit()
    {
        await using WorldTestHost host = Start(new Dictionary<string, string?> { [$"{DebugDrawOptions.SectionName}:MaxMarkersPerGm"] = "30" });
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);

        Run first = await SendAsync(gm, ".debug vis range 20");
        Assert.Equal(30, first.Created.Count);
        Assert.Contains(first.Replies, r => r.Contains($"{DebugDrawCommands.RingPoints - 30} left out (marker limit)", StringComparison.Ordinal));

        Run second = await SendAsync(gm, ".debug vis range 25");
        Assert.Equal(30, second.Created.Count);
        Assert.Equal(first.Created.Select(m => m.Guid.Value).Order(), second.Destroyed.Select(g => g.Value).Order());
        Assert.Contains(second.Replies, r => r.Contains("30 older marker(s) removed", StringComparison.Ordinal));
        Assert.Equal(second.Created.Select(m => m.Guid.Value).Order(), (await MarkersOfAsync(host, "Drawer")).Select(g => g.Value).Order());
    }

    [Fact]
    public async Task Markers_GoAwayOnTheirOwn_AfterTheirLifetime()
    {
        await using WorldTestHost host = Start(new Dictionary<string, string?> { [$"{DebugDrawOptions.SectionName}:LifetimeSeconds"] = "5" });
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);

        Run run = await SendAsync(gm, ".debug vis range 15");
        Assert.Contains(run.Replies, r => r.Contains("they go in 5 s", StringComparison.Ordinal));
        Assert.Equal(DebugDrawCommands.RingPoints, run.Created.Count);
        Assert.Contains((await SendAsync(gm, ".debug vis list")).Replies, r => r.Contains("range: 36 marker(s)", StringComparison.Ordinal));

        List<ObjectGuid> destroyed = [];
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (destroyed.Count < run.Created.Count && DateTime.UtcNow < deadline)
        {
            (WorldOpcode opcode, byte[] payload) = await gm.ReadAsync(TimeSpan.FromSeconds(15));
            if (opcode == WorldOpcode.SmsgDestroyObject)
            {
                destroyed.Add(new ObjectGuid(BinaryPrimitives.ReadUInt64LittleEndian(payload)));
            }
        }

        Assert.Equal(run.Created.Select(m => m.Guid.Value).Order(), destroyed.Select(g => g.Value).Order());
        Assert.Equal(0, await host.OnWorldAsync(() => Feature(host).ActiveGmCount));
    }

    [Fact]
    public async Task Logout_DestroysTheMarkers_AndForgetsThem()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        Run run = await SendAsync(gm, ".debug vis cells 1");
        Assert.Equal(9, run.Created.Count(m => m.Kind is DebugMarkerKind.Cell or DebugMarkerKind.GridCorner));

        await gm.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        List<ObjectGuid> destroyed = [];
        while (true)
        {
            (WorldOpcode opcode, byte[] payload) = await gm.ReadAsync();
            if (opcode == WorldOpcode.SmsgLogoutComplete)
            {
                break;
            }

            if (opcode == WorldOpcode.SmsgDestroyObject)
            {
                destroyed.Add(new ObjectGuid(BinaryPrimitives.ReadUInt64LittleEndian(payload)));
            }
        }

        Assert.Equal(run.Created.Select(m => m.Guid.Value).Order(), destroyed.Where(DebugMarkerStyles.IsMarkerGuid).Select(g => g.Value).Order());
        Assert.Equal(0, await host.OnWorldAsync(() => Feature(host).ActiveGmCount));
    }

    [Fact]
    public async Task Kit_PlaysTheVisualForTheGmOnly()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await using WorldTestClient bystander = await EnterAsync(host, "Watcher", AccountSecurity.Player);
        await gm.CollectAsync(Quiet);

        Run run = await SendAsync(gm, ".debug vis kit 179");

        byte[] visual = Assert.Single(run.Packets, p => p.Opcode == WorldOpcode.SmsgPlaySpellVisual).Payload;
        Assert.Equal(179u, BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(8)));
        Assert.DoesNotContain(await bystander.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgPlaySpellVisual);
    }

    /// <summary>Every marker create block in an SMSG_UPDATE_OBJECT that starts with one (the debug-draw packets carry nothing else).</summary>
    private static IEnumerable<ParsedMarker> ParseMarkerCreates(byte[] body)
    {
        var reader = new PacketReader(body);
        uint blocks = reader.ReadUInt32();
        reader.ReadByte();
        var markers = new List<ParsedMarker>();
        for (uint b = 0; b < blocks; b++)
        {
            if (reader.ReadByte() != (byte)ObjectUpdateType.CreateObject2)
            {
                return markers;
            }

            var guid = new ObjectGuid(reader.ReadPackedGuid());
            if (!DebugMarkerStyles.IsMarkerGuid(guid))
            {
                return markers;
            }

            reader.ReadByte();   // type id
            reader.ReadByte();   // update flags (ALL | HAS_POSITION)
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            reader.ReadSingle(); // orientation
            reader.ReadUInt32(); // UPDATEFLAG_ALL
            int maskBlocks = reader.ReadByte();
            uint[] mask = new uint[maskBlocks];
            for (int i = 0; i < maskBlocks; i++)
            {
                mask[i] = reader.ReadUInt32();
            }

            uint display = 0;
            for (int index = 0; index < maskBlocks * 32; index++)
            {
                if ((mask[index >> 5] & (1u << (index & 31))) != 0)
                {
                    uint value = reader.ReadUInt32();
                    if (index == UpdateFields.GameobjectDisplayid)
                    {
                        display = value;
                    }
                }
            }

            markers.Add(new ParsedMarker(guid, position, display));
        }

        return markers;
    }

    /// <summary>A wall across the line x = <paramref name="wallX"/>: a segment that crosses it is blocked there.</summary>
    private sealed class WallAtX(float wallX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => !Crosses(from, to);

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            if (!Crosses(from, to))
            {
                hit = to;
                return false;
            }

            float t = (wallX - from.X) / (to.X - from.X);
            Vector3 at = Vector3.Lerp(from, to, t);
            hit = at + (Vector3.Normalize(to - from) * modifyDistance);
            return true;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }

        private bool Crosses(Vector3 from, Vector3 to) => (from.X - wallX) * (to.X - wallX) < 0;
    }

    /// <summary>A navmesh stand-in: every path goes through one detour corner.</summary>
    private sealed class DetourPathfinder(Vector3 detour) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, detour, end]);
    }
}

using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The <c>links</c> op (docs/areas/atlas-public.md, "Guest data policy"): for one map, the guids of the entities that are linked to a real
/// (non-bot) player by an owner, charmer, creator, summoner or group relation, so the public Atlas listener can leave them out of what a guest sees.
/// With <c>Guest:ShowPlayers</c> off, hiding a player is not enough: a hunter pet, a guardian, a totem, a trap, a bot following the player or
/// grouped with them all mark where the player stands. The frame carries no owner, creator or group keys, so the world answers here.
/// <para>
/// Rules: a creature is linked when its charmer-or-owner is a real player, or is itself a linked entity (transitively, three steps); a game object
/// when its owner is; a bot when its group holds a real player. The answer also lists the creature entries that were owned by anything on the map
/// (<c>summonEntries</c>) and the game object entries with an owner (<c>ownedObjectEntries</c>), which the listener keeps as its template set for the
/// moment it can no longer ask.
/// </para>
/// <para>
/// Access and cost: a <c>web</c> connection only (<c>role-mismatch</c> otherwise), and only one that holds a subscription (<c>not-subscribed</c>), so the
/// world computes nothing while no web visitor is watching. The scan runs on the world thread at most once per <see cref="MinInterval"/> per map;
/// repeated asks inside the window are answered from the last scan and do not count in <see cref="Computations"/>. Thread-safe.
/// </para>
/// </summary>
internal sealed class MapFeedPublicLinks : IFeedExtension
{
    /// <summary>The fewest time between two scans of one map.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How many owner steps a link may be transitive over (an owner's owner's owner).</summary>
    public const int MaxDepth = 3;

    private const string Kind = "links";

    private sealed record Scan(long AtTimestamp, ulong[] Linked, uint[] SummonEntries, uint[] OwnedObjectEntries);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly Dictionary<(uint Map, uint Instance), Scan> _scans = [];
    private readonly Lock _gate = new();
    private long _computations;

    public MapFeedPublicLinks(IServiceProvider services)
    {
        _services = services;
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    /// <summary>How many world-thread scans have run (tests assert it stays 0 while nobody is subscribed).</summary>
    public long Computations => Interlocked.Read(ref _computations);

    /// <inheritdoc />
    public bool Handles(string op) => op == MapFeedExtensions.LinksOp;

    /// <inheritdoc />
    public void FeedStarted(WorldMapFeed feed)
    {
    }

    /// <inheritdoc />
    public ValueTask ConnectionEndedAsync(MapFeedConnection connection) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async Task<bool> HandleAsync(MapFeedConnection connection, string op, ReadOnlyMemory<byte> line)
    {
        using JsonDocument document = FeedExtensionJson.Parse(line);
        JsonElement root = document.RootElement;
        if (!FeedExtensionJson.TryId(root, out long id))
        {
            await connection.QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.BadArgs, "links needs an integer \"id\"")).ConfigureAwait(false);
            return false;
        }

        if (connection.Role != FeedRole.Web)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, MapFeedRoles.RoleMismatch, "links belongs to a web connection")).ConfigureAwait(false);
            return true;
        }

        if (!TryUInt(root, "map"u8, out uint map) || !TryUInt(root, "instance"u8, out uint instance, optional: true))
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, MapFeedProtocol.BadArgs, "links needs a numeric \"map\" and optionally \"instance\"")).ConfigureAwait(false);
            return true;
        }

        if (!connection.IsSubscribed)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, "not-subscribed", "links is answered only while the connection holds a subscription")).ConfigureAwait(false);
            return true;
        }

        long now = _time.GetTimestamp();
        Scan? scan;
        lock (_gate)
        {
            scan = _scans.GetValueOrDefault((map, instance));
        }

        if (scan is null || TimeSpan.FromTicks((now - scan.AtTimestamp) * TimeSpan.TicksPerSecond / _time.TimestampFrequency) >= MinInterval)
        {
            (Scan? fresh, FeedError? failure) = await connection.InvokeWorldAsync(() => ScanMap(connection.Feed.World, map, instance, now)).ConfigureAwait(false);
            if (fresh is null)
            {
                await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, failure!.Value.Code, failure.Value.Message)).ConfigureAwait(false);
                return true;
            }

            scan = fresh;
            lock (_gate)
            {
                _scans[(map, instance)] = scan;
                if (_scans.Count > 64)
                {
                    foreach ((uint, uint) stale in _scans.Where(p => p.Value.AtTimestamp < now - (5 * _time.TimestampFrequency)).Select(p => p.Key).ToList())
                    {
                        _scans.Remove(stale);
                    }
                }
            }
        }

        Scan answer = scan;
        await connection.QueueAsync(FeedExtensionJson.Result(id, ok: true, Kind, writer =>
        {
            writer.WriteNumber("map"u8, map);
            writer.WriteNumber("instance"u8, instance);
            writer.WriteStartArray("linked"u8);
            foreach (ulong guid in answer.Linked)
            {
                writer.WriteStringValue(guid.ToString("X", CultureInfo.InvariantCulture));
            }

            writer.WriteEndArray();
            writer.WriteStartArray("summonEntries"u8);
            foreach (uint entry in answer.SummonEntries)
            {
                writer.WriteNumberValue(entry);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("ownedObjectEntries"u8);
            foreach (uint entry in answer.OwnedObjectEntries)
            {
                writer.WriteNumberValue(entry);
            }

            writer.WriteEndArray();
        })).ConfigureAwait(false);
        return true;
    }

    /// <summary>The scan itself. World thread only (the caller hands it to <c>InvokeWorldAsync</c>); returns an empty scan when the map is not running.</summary>
    private Scan ScanMap(WorldRuntime world, uint mapId, uint instanceId, long timestamp)
    {
        Map? map = world.FindMap(mapId, instanceId);
        if (map is null || map.IsUnloaded)
        {
            return new Scan(timestamp, [], [], []);
        }

        Interlocked.Increment(ref _computations);
        HashSet<ulong> bots = BotGuids();
        GroupManager? groups = Groups();
        var realPlayers = new HashSet<ulong>();
        foreach (Player player in map.Players)
        {
            if (!bots.Contains(player.Guid.Value))
            {
                realPlayers.Add(player.Guid.Value);
            }
        }

        var linked = new HashSet<ulong>();
        foreach (Player player in map.Players)
        {
            if (bots.Contains(player.Guid.Value) && GroupHoldsRealPlayer(groups, player.Guid, realPlayers, bots, world))
            {
                linked.Add(player.Guid.Value);
            }
        }

        CreatureMapSystem? creatures = map.FindUpdater<CreatureMapSystem>();
        GameObjectMapSystem? objects = map.FindUpdater<GameObjectMapSystem>();
        var summonEntries = new SortedSet<uint>();
        var ownedObjectEntries = new SortedSet<uint>();
        var owned = new List<(ulong Self, ulong Owner)>();
        if (creatures is not null)
        {
            foreach (Creature creature in creatures.Creatures)
            {
                ObjectGuid owner = creature.CharmerOrOwnerGuid;
                if (!owner.IsEmpty)
                {
                    owned.Add((creature.Guid.Value, owner.Value));
                    summonEntries.Add(creature.Entry);
                }
            }
        }

        if (objects is not null)
        {
            foreach (GameObject gameObject in objects.GameObjects)
            {
                ObjectGuid owner = gameObject.OwnerGuid;
                if (!owner.IsEmpty)
                {
                    owned.Add((gameObject.Guid.Value, owner.Value));
                    ownedObjectEntries.Add(gameObject.Entry);
                }
            }
        }

        // Owner chains: an entity whose owner is a real player, or a linked entity, is linked (depth three).
        var root = new HashSet<ulong>(realPlayers);
        root.UnionWith(linked);
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            bool grew = false;
            foreach ((ulong self, ulong owner) in owned)
            {
                if (root.Contains(owner) && root.Add(self))
                {
                    linked.Add(self);
                    grew = true;
                }
            }

            if (!grew)
            {
                break;
            }
        }

        return new Scan(timestamp, [.. linked.Order()], [.. summonEntries], [.. ownedObjectEntries]);
    }

    private static bool GroupHoldsRealPlayer(GroupManager? groups, ObjectGuid bot, HashSet<ulong> realPlayers, HashSet<ulong> bots, WorldRuntime world)
    {
        Group? group = groups?.GetGroup(bot);
        if (group is null)
        {
            return false;
        }

        foreach (GroupMemberSlot member in group.Members)
        {
            // A member on another map is a real player all the same: the group itself is the link, wherever its members stand.
            if (realPlayers.Contains(member.Guid.Value) || (!bots.Contains(member.Guid.Value) && world.IsOnline(member.Guid)))
            {
                return true;
            }
        }

        return false;
    }

    private HashSet<ulong> BotGuids()
    {
        var guids = new HashSet<ulong>();
        if (_services.GetService<ManagedBotRegistry>() is { Count: > 0 } registry)
        {
            foreach (ManagedBot bot in registry.Bots)
            {
                if (bot.Session.Player is { } player)
                {
                    guids.Add(player.Guid.Value);
                }
            }
        }

        return guids;
    }

    private GroupManager? Groups()
    {
        try
        {
            return _services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException)
        {
            return null; // the social feature is not attached
        }
    }

    private static bool TryUInt(JsonElement root, ReadOnlySpan<byte> name, out uint value, bool optional = false)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return optional;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out value);
    }
}

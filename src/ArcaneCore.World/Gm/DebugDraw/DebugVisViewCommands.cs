using System.Numerics;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>
/// <c>.debug vis botstall|leases|invasion</c>: server state drawn as markers only the invoking GM sees, rendered by the pure
/// <see cref="DebugVisViews"/> from a snapshot taken here. Added to <c>debug vis</c> as an <see cref="ICommandExtension"/>. All three
/// run on the world thread; the lease read goes to the lease store synchronously, as the cluster role filter's own checks do.
/// </summary>
public sealed class DebugVisViewCommands : ICommandExtension
{
    private const string Prefix = "DebugDraw: ";

    public string Path => "debug vis";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("botstall", AccountSecurity.GameMaster, "Syntax: .debug vis botstall\nMark every playerbot that reports a stall or an error where it stands (red) and list their codes.", BotStall, RetailLevel: 3),
        new("leases", AccountSecurity.GameMaster, "Syntax: .debug vis leases\nList who owns each realm-authority lease (economy, social, instances) with its fencing token and expiry, one marker per scope above you.", Leases, RetailLevel: 3),
        new("invasion", AccountSecurity.GameMaster, "Syntax: .debug vis invasion\nList the Scourge invasion state per zone and mark the Mouth of Kel'Thuzad of the zones on your map (red while attacked).", Invasion, RetailLevel: 3),
    ];

    private static bool BotStall(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        IPlayerbotService? service = context.Session.Services.GetService<IPlayerbotService>();
        if (service is null)
        {
            context.Reply(Prefix + "the playerbots are not loaded.");
            return true;
        }

        var entries = new List<BotStallEntry>();
        foreach (PlayerbotStatus status in service.Snapshot())
        {
            // Only a bot on the invoker's own map instance can be drawn for the invoker (another instance of the same map id is out of reach).
            Player? bot = context.World.FindOnlinePlayer(status.Name);
            Vector3? at = bot is not null && bot.Map is { } map && ReferenceEquals(map, context.Player.Map) ? new Vector3(bot.X, bot.Y, bot.Z) : null;
            entries.Add(new BotStallEntry(status.Name, status.State.ToString(), status.ErrorCode, at));
        }

        Show(context, DebugVisViews.BotStall(entries));
        return true;
    }

    private static bool Leases(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        IRealmAuthorityLeaseStore? store = context.Session.Services.GetService<IRealmAuthorityLeaseStore>();
        if (store is null)
        {
            context.Reply(Prefix + "no lease store (this is not a cluster node).");
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entries = new List<LeaseEntry>();
        foreach (string scope in new[] { RealmAuthorityScopes.Economy, RealmAuthorityScopes.Social, RealmAuthorityScopes.Instances })
        {
            RealmAuthorityLease? lease = store.GetCurrent(scope, now);
            entries.Add(new LeaseEntry(scope, lease?.OwnerNodeId, lease?.OwnerIncarnation, lease?.FencingToken ?? 0,
                store.GetHighWaterToken(scope), lease?.ExpiresAtUtc));
        }

        Player player = context.Player;
        Show(context, DebugVisViews.Leases(entries, now, new Vector3(player.X, player.Y, player.Z)));
        return true;
    }

    private static bool Invasion(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        ScourgeInvasionFeature? feature = context.Session.Services.GetService<ScourgeInvasionFeature>();
        if (feature is null)
        {
            context.Reply(Prefix + "the Scourge invasion is not loaded.");
            return true;
        }

        ScourgeInvasionSnapshot snapshot = feature.Snapshot;
        var zones = new List<InvasionZoneEntry>();
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
        {
            long next = snapshot.Zones.FirstOrDefault(z => z.ZoneId == zone.ZoneId)?.NextAttackUnix ?? 0;
            Vector3? mouth = ScourgeInvasionCatalog.MouthPositions.TryGetValue(zone.ZoneId, out ScourgeInvasionPosition p) ? new Vector3(p.X, p.Y, p.Z) : null;
            zones.Add(new InvasionZoneEntry(zone.ZoneId, zone.MapId, snapshot.Remaining(zone.ZoneId), zone.Necropolises, next, mouth));
        }

        var view = new InvasionSnapshot(snapshot.State == ScourgeInvasionState.Enabled, snapshot.BattlesWon, snapshot.LastAttackZone, zones);
        Show(context, DebugVisViews.Invasion(view, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), context.Player.Map?.MapId ?? uint.MaxValue));
        return true;
    }

    private static void Show(CommandContext context, DebugVisView view)
    {
        foreach (string line in view.Lines)
        {
            context.Reply(Prefix + line);
        }

        var requests = new List<DebugMarkerRequest>(view.Markers.Count);
        foreach (DebugVisMarker marker in view.Markers)
        {
            requests.Add(new DebugMarkerRequest(marker.Kind, marker.Position, marker.Label, marker.Emphasis));
        }

        DebugDrawFeature feature = context.Session.Services.GetRequiredService<DebugDrawFeature>();
        DebugDrawResult result = feature.Draw(context.Player, view.Title, requests);
        context.Reply(DebugDrawCommands.Drawn(result, feature.Options.LifetimeSeconds));
    }
}

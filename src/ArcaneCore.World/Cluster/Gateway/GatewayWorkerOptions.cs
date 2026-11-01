using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// Worker mode of the world daemon (Tier 3a gateway passthrough, <c>docs/areas/gateway.md</c>): an extra listener that an
/// <c>ArcaneCore.Gateway</c> process connects to and forwards its clients through. Off by default; with it off nothing is
/// registered and the world runs in Combined mode exactly as before. The world's own client listener (World:Port) stays open
/// either way.
/// </summary>
public sealed class GatewayWorkerOptions
{
    public const string SectionName = "Cluster:Worker";

    /// <summary>`true` opens the gateway listener (worker mode) and makes character fencing mandatory: Cluster:Fencing:Enabled must be true, and the characters write fence is attached either way. `false`, the default, is Combined mode: no listener, no change.</summary>
    public bool Enabled { get; set; }

    /// <summary>Standby (Tier 3b handoff target; needs Enabled): start the process, build its services and link to the gateway, but admit no session, claim no characters ownership, run no schema DDL, take no writer lock until promoted (promotion takes it and is refused while the old world holds it), open no World:Port listener, and attach no feature nor start the simulation until promoted by a handoff (features read the characters database, which the active world still owns). `false`, the default, is an active worker.</summary>
    public bool Standby { get; set; }

    /// <summary>Address of the gateway listener. Keep it on loopback or a private network: the link is not encrypted.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Port of the gateway listener; must equal the gateway's Gateway:WorkerPort.</summary>
    public int Port { get; set; } = 8095;

    /// <summary>Shared secret a gateway proves (HMAC-SHA256 of a per-link challenge) before it may open sessions; must equal Gateway:LinkSecret. Required in worker mode on every bind address, loopback included: at least 16 characters, or the world refuses to start. An admitted gateway opens sessions for any account it authenticated, so treat the secret like a database password.</summary>
    public string LinkSecret { get; set; } = string.Empty;

    /// <summary>This worker's node id in the link manifest (shown in the gateway's log).</summary>
    public string NodeId { get; set; } = "world";

    /// <summary>Realm id both ends of the link must agree on.</summary>
    public int RealmId { get; set; } = 1;

    /// <summary>`true` advertises the versions the databases record (characters and world schema version rows, and a content revision derived from the world schema version; an unreadable version refuses to link) instead of the three labels below, so a worker and gateway on mixed versions are refused at admission. `false`, the default, advertises the labels (the explicit override; tests and operators who pin them). The gateway has no database access: its Gateway:* labels must be set to the values the worker reads.</summary>
    public bool VersionsFromDatabase { get; set; }

    /// <summary>Content revision label both ends of the link must agree on; an override, used only while <see cref="VersionsFromDatabase"/> is false.</summary>
    public string ContentRevision { get; set; } = "dev";

    /// <summary>Characters schema version both ends must agree on (an override label, used only while <see cref="VersionsFromDatabase"/> is false).</summary>
    public int CharactersSchemaVersion { get; set; } = 1;

    /// <summary>World schema version both ends must agree on (an override label, used only while <see cref="VersionsFromDatabase"/> is false).</summary>
    public int WorldSchemaVersion { get; set; } = 1;

    /// <summary>The maps this worker owns, as a list of ids and inclusive ranges (<c>0,1,30-40</c>; separators comma, semicolon or space). Empty, the default, owns every map, which keeps the combined behaviour. With a list, the world never creates or enters a map outside it (login, teleport, spawn, bot travel refuse cleanly until redirects and far transfers to the owning worker exist), and the list is announced in the link manifest. Lists of different workers must not overlap (the supervisor's Processes:*:Maps check).</summary>
    public string OwnedMaps { get; set; } = string.Empty;

    /// <summary>With an OwnedMaps list: whether instance copies (dungeons, raids, battlegrounds) of maps outside the list also run on this worker. `true`, the default, lets a continent worker host the instances it hands out; `false` refuses them. Ignored while OwnedMaps is empty.</summary>
    public bool AllowInstanceMaps { get; set; } = true;

    /// <summary>The manifest this worker admits gateways against, advertising the option labels.</summary>
    public ClusterNodeManifest ToManifest(Guid incarnation)
        => ToManifest(incarnation, new GatewayManifestVersions(CharactersSchemaVersion, WorldSchemaVersion, ContentRevision));

    /// <summary>The manifest this worker admits gateways against, advertising <paramref name="versions"/> (see <see cref="GatewayManifestVersions"/>).</summary>
    public ClusterNodeManifest ToManifest(Guid incarnation, GatewayManifestVersions versions) => GatewayLinkManifest.Create(
        ClusterRole.Worker, NodeId, incarnation, RealmId, versions.ContentRevision, versions.CharactersSchemaVersion, versions.WorldSchemaVersion,
        TryGetOwnership(out MapOwnership ownership, out _) ? ownership.ToListText() : OwnedMaps.Trim(), AllowInstanceMaps);

    /// <summary>Parses <see cref="OwnedMaps"/> and <see cref="AllowInstanceMaps"/>; false with <paramref name="error"/> when the list is malformed.</summary>
    public bool TryGetOwnership(out MapOwnership ownership, out string? error)
        => MapOwnership.TryParse(OwnedMaps, AllowInstanceMaps, out ownership, out error);
}

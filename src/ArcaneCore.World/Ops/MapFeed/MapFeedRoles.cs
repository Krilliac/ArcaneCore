namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The role of a feed connection (docs/areas/atlas-actions.md, "Roles"): who may do what on it is decided by the pipe the connection
/// arrived on, not by what a request says. Bound once and never changed.
/// </summary>
internal enum FeedRole : byte
{
    /// <summary>Nothing bound yet: the first extension op decides (a <c>role</c> op binds <see cref="Web"/>, any other binds <see cref="Local"/>). Core ops behave as they always did.</summary>
    Unbound,

    /// <summary>The local tool: the daemon account's authority (packet tools when on, the local action actor).</summary>
    Local,

    /// <summary>A public listener's pooled connection: frames and web-ticket actions only; never the packet tools, queries or the local actor.</summary>
    Web,
}

/// <summary>The role gate: which ops a role may not use. Pure; any thread.</summary>
internal static class MapFeedRoles
{
    /// <summary>The op or actor does not belong to this connection's role.</summary>
    public const string RoleMismatch = "role-mismatch";

    /// <summary>The role is already bound and cannot change.</summary>
    public const string RoleLocked = "role-locked";

    /// <summary>The wire name of a role.</summary>
    public static string Name(FeedRole role) => role == FeedRole.Web ? "web" : "local";

    /// <summary>
    /// The refusal for a core op on a <see cref="FeedRole.Web"/> connection, or null: <c>console</c>, <c>inject</c>, <c>send</c>, <c>tap</c>,
    /// <c>untap</c> and <c>query</c> (any kind) are refused whatever the packet tools' setting is; <c>hello</c>, <c>subscribe</c> and
    /// <c>unsubscribe</c> are served.
    /// </summary>
    public static FeedError? RefuseCoreOp(FeedRole role, FeedOp op)
        => role == FeedRole.Web && op is FeedOp.Console or FeedOp.Inject or FeedOp.Send or FeedOp.Tap or FeedOp.Untap or FeedOp.Query
            ? new FeedError(RoleMismatch, $"{KindOf(op)} is not available on a web connection")
            : null;

    /// <summary>The <c>kind</c> a refusal of <paramref name="op"/> carries.</summary>
    public static string KindOf(FeedOp op) => op switch
    {
        FeedOp.Console => "console",
        FeedOp.Inject => "inject",
        FeedOp.Send => "send",
        FeedOp.Tap => "tap",
        FeedOp.Untap => "untap",
        FeedOp.Query => "query",
        _ => "op",
    };
}

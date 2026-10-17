namespace ArcaneCore.World.Contracts;

/// <summary>
/// The version of the world contracts (this assembly). Planned for packs (tier 2b, docs/design/hot-reload-tiers.md, section
/// 4.1; not built yet, nothing calls <see cref="IsCompatible"/> today): a pack records the version it was built against and the
/// host refuses a pack whose version differs, since a pack compiled against other contracts would otherwise fail at JIT time
/// in the middle of a tick instead of at load.
/// Bump <see cref="Current"/> on every change to a public type of this assembly that a compiled pack could notice
/// (a member added to, removed from or changed on an interface, a renamed or moved type, a changed signature).
/// </summary>
public static class ContractsVersion
{
    /// <summary>The contracts version this server was built with.</summary>
    public const int Current = 1;

    /// <summary>Whether a pack built against <paramref name="builtAgainst"/> may be loaded: only the exact same version.</summary>
    public static bool IsCompatible(int builtAgainst) => builtAgainst == Current;
}

using ArcaneCore.Game.Maps;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;

namespace ArcaneCore.World.HotCode;

/// <summary>A rescan of the default map updaters that is not in force yet.</summary>
/// <param name="Types">Every default updater type the rescan found.</param>
/// <param name="Commit">Make the rescan the set new maps get; returns the types that were new. World thread.</param>
public sealed record MapUpdaterScan(IReadOnlyList<Type> Types, Func<IReadOnlyList<Type>> Commit);

/// <summary>
/// Where a refresh looks for extension types: a seam so tests can present "a group that was added
/// by an edit" without touching process-wide state. <see cref="AssemblyHotCodeCatalog"/> is the real one.
/// </summary>
public interface IHotCodeCatalog
{
    /// <summary>Every <see cref="IOpcodeHandlerGroup"/> type that exists now.</summary>
    IReadOnlyList<Type> OpcodeGroupTypes();

    /// <summary>Every <see cref="ICommandGroup"/> type that exists now.</summary>
    IReadOnlyList<Type> CommandGroupTypes();

    /// <summary>The default map updater types in force now.</summary>
    IReadOnlyList<Type> CurrentMapUpdaterTypes();

    /// <summary>Rescan for default map updaters (may throw when a marked type is invalid).</summary>
    MapUpdaterScan ScanMapUpdaters();
}

/// <summary>The real catalog: reflection over the world assembly and <see cref="DefaultMapUpdaters"/>.</summary>
public sealed class AssemblyHotCodeCatalog : IHotCodeCatalog
{
    public IReadOnlyList<Type> OpcodeGroupTypes() => AssemblyDiscovery.FindTypes<IOpcodeHandlerGroup>();

    public IReadOnlyList<Type> CommandGroupTypes() => AssemblyDiscovery.FindTypes<ICommandGroup>();

    public IReadOnlyList<Type> CurrentMapUpdaterTypes() => DefaultMapUpdaters.Types;

    public MapUpdaterScan ScanMapUpdaters()
    {
        DefaultMapUpdaterCandidate candidate = DefaultMapUpdaters.DiscoverCandidate();
        return new MapUpdaterScan(candidate.Types, () => DefaultMapUpdaters.Commit(candidate));
    }
}

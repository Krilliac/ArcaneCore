using System.Reflection;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Marks an <see cref="IMapUpdater"/> in this assembly that every map gets when it is created
/// (<see cref="WorldRuntime.GetMap"/>). The type needs a constructor taking
/// <c>(Map, WorldRuntime)</c>; it may be internal. Systems that only some maps need, or that
/// live in another assembly, attach themselves with <see cref="Map.AddUpdater"/> instead
/// (docs/integration/seams.md).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DefaultMapUpdaterAttribute : Attribute
{
    /// <summary>Lower runs first; ties are ordered by full type name.</summary>
    public int Order { get; init; }
}

/// <summary>Discovery and attachment of the <see cref="DefaultMapUpdaterAttribute"/> systems.</summary>
public static class DefaultMapUpdaters
{
    private static volatile UpdaterSet s_current = Discover([typeof(DefaultMapUpdaters).Assembly]);

    /// <summary>The default updater types, in attach order (deterministic).</summary>
    public static IReadOnlyList<Type> Types => s_current.Types;

    /// <summary>
    /// Scan this assembly again, for a code hot reload that added a <see cref="DefaultMapUpdaterAttribute"/>
    /// type (docs/areas/code-hot-reload.md). Nothing is changed: hand the result to
    /// <see cref="Commit"/> on the world thread. A marked type that is not a valid updater throws
    /// here (fail closed) and the current set stays in force.
    /// </summary>
    public static DefaultMapUpdaterCandidate DiscoverCandidate() => new(Discover([typeof(DefaultMapUpdaters).Assembly]));

    /// <summary>Same, scanning other assemblies as well (tests).</summary>
    internal static DefaultMapUpdaterCandidate DiscoverCandidate(params Assembly[] assemblies)
        => new(Discover([typeof(DefaultMapUpdaters).Assembly, .. assemblies]));

    /// <summary>
    /// Make <paramref name="candidate"/> the set that new maps get (world thread). Maps that
    /// already exist keep the updaters they have; nothing is attached to them. Returns the types
    /// that were not in the previous set.
    /// </summary>
    public static IReadOnlyList<Type> Commit(DefaultMapUpdaterCandidate candidate)
    {
        UpdaterSet previous = s_current;
        s_current = candidate.Set;
        return candidate.Set.Types.Except(previous.Types).ToArray();
    }

    /// <summary>Create every default updater for a new map and attach it (world thread, before the map is used).</summary>
    internal static void AttachTo(Map map, WorldRuntime world)
    {
        foreach ((Type type, ConstructorInfo constructor) in s_current.Entries)
        {
            var updater = (IMapUpdater)(constructor.Invoke([map, world])
                ?? throw new InvalidOperationException($"could not create {type.FullName}"));
            map.AddUpdater(updater);
        }
    }

    internal sealed class UpdaterSet((Type Type, ConstructorInfo Constructor)[] entries)
    {
        public (Type Type, ConstructorInfo Constructor)[] Entries { get; } = entries;

        public Type[] Types { get; } = entries.Select(u => u.Type).ToArray();
    }

    /// <summary>
    /// Every marked type in <paramref name="assemblies"/>. A marked type that is not a concrete
    /// <see cref="IMapUpdater"/> or lacks the <c>(Map, WorldRuntime)</c> constructor fails here,
    /// at first use, instead of being skipped (fail closed).
    /// </summary>
    private static UpdaterSet Discover(Assembly[] assemblies)
    {
        var found = new List<(Type Type, ConstructorInfo Constructor, int Order)>();
        foreach (Type type in assemblies.Distinct().SelectMany(a => a.GetTypes()))
        {
            if (type.GetCustomAttribute<DefaultMapUpdaterAttribute>() is not { } attribute)
            {
                continue;
            }

            if (type.IsAbstract || !typeof(IMapUpdater).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"{type.FullName} is marked [DefaultMapUpdater] but is not a concrete IMapUpdater");
            }

            ConstructorInfo constructor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(Map), typeof(WorldRuntime)])
                ?? throw new InvalidOperationException($"{type.FullName} is marked [DefaultMapUpdater] but has no (Map, WorldRuntime) constructor");
            found.Add((type, constructor, attribute.Order));
        }

        return new UpdaterSet(found
            .OrderBy(u => u.Order)
            .ThenBy(u => u.Type.FullName, StringComparer.Ordinal)
            .Select(u => (u.Type, u.Constructor))
            .ToArray());
    }
}

/// <summary>A scanned set of default updaters, not yet in force (see <see cref="DefaultMapUpdaters.DiscoverCandidate()"/>).</summary>
public sealed class DefaultMapUpdaterCandidate
{
    internal DefaultMapUpdaterCandidate(DefaultMapUpdaters.UpdaterSet set) => Set = set;

    internal DefaultMapUpdaters.UpdaterSet Set { get; }

    public IReadOnlyList<Type> Types => Set.Types;
}

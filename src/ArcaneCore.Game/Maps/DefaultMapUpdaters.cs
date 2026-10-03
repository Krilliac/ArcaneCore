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
    private static readonly (Type Type, ConstructorInfo Constructor)[] s_updaters = Discover();

    /// <summary>The default updater types, in attach order (deterministic).</summary>
    public static IReadOnlyList<Type> Types { get; } = s_updaters.Select(u => u.Type).ToArray();

    /// <summary>Create every default updater for a new map and attach it (world thread, before the map is used).</summary>
    internal static void AttachTo(Map map, WorldRuntime world)
    {
        foreach ((Type type, ConstructorInfo constructor) in s_updaters)
        {
            var updater = (IMapUpdater)(constructor.Invoke([map, world])
                ?? throw new InvalidOperationException($"could not create {type.FullName}"));
            map.AddUpdater(updater);
        }
    }

    /// <summary>
    /// Every marked type in this assembly. A marked type that is not a concrete
    /// <see cref="IMapUpdater"/> or lacks the <c>(Map, WorldRuntime)</c> constructor fails here,
    /// at first use, instead of being skipped (fail closed).
    /// </summary>
    private static (Type, ConstructorInfo)[] Discover()
    {
        var found = new List<(Type Type, ConstructorInfo Constructor, int Order)>();
        foreach (Type type in typeof(DefaultMapUpdaters).Assembly.GetTypes())
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

        return found
            .OrderBy(u => u.Order)
            .ThenBy(u => u.Type.FullName, StringComparer.Ordinal)
            .Select(u => (u.Type, u.Constructor))
            .ToArray();
    }
}

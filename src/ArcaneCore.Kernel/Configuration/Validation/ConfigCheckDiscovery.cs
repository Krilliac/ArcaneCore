using System.Reflection;

namespace ArcaneCore.Kernel.Configuration.Validation;

/// <summary>
/// Finds the <see cref="IConfigCheck"/> implementations of a daemon's assemblies, so a new check is run by <c>check-config</c> and the
/// start-up report without anyone editing a list (a hand-written list is how <c>AntiCheatConfigChecks</c> and
/// <c>PlayerbotQaConfigChecks</c> went unrun). A daemon scans the Kernel assembly plus its own; a check is created through a
/// parameterless constructor (any visibility), so one that needs arguments (<see cref="Conf.ConfKeyCheck"/>) is supplied by the daemon
/// explicitly, and the wiring audit lists any such check that nobody supplied. Pure reflection over loaded metadata; no I/O.
/// </summary>
public static class ConfigCheckDiscovery
{
    /// <summary>Every concrete <see cref="IConfigCheck"/> class declared in <paramref name="assemblies"/>, ordered by full name (deterministic), duplicates removed.</summary>
    public static IReadOnlyList<Type> ConcreteTypes(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return [.. assemblies.Distinct()
            .SelectMany(SafeTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IConfigCheck).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>One instance of every <see cref="ConcreteTypes"/> class that has a parameterless constructor, in the same order.</summary>
    public static IReadOnlyList<IConfigCheck> Create(IEnumerable<Assembly> assemblies)
    {
        var checks = new List<IConfigCheck>();
        foreach (Type type in ConcreteTypes(assemblies))
        {
            if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is not null)
            {
                checks.Add((IConfigCheck)Activator.CreateInstance(type, nonPublic: true)!);
            }
        }

        return checks;
    }

    /// <summary>The concrete check classes of <paramref name="assemblies"/> that have no instance in <paramref name="checks"/>: declared, but never run.</summary>
    public static IReadOnlyList<Type> NotRun(IEnumerable<Assembly> assemblies, IEnumerable<IConfigCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        HashSet<Type> run = [.. checks.Select(c => c.GetType())];
        return [.. ConcreteTypes(assemblies).Where(t => !run.Contains(t))];
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A partially loadable assembly still has its loadable types; a type that cannot load cannot be run either.
            return [.. ex.Types.OfType<Type>()];
        }
    }
}

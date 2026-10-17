using System.Reflection;
using ArcaneCore.Kernel.Reflection;

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

    /// <summary>
    /// Every concrete <see cref="IConfigCheck"/> class of a <see cref="CodeAssemblies"/> set (built-ins plus packs), in set order:
    /// assembly name, then full type name. Unlike the <see cref="IEnumerable{T}"/> overload this fails closed: a type that cannot
    /// load throws <see cref="CodeAssemblyLoadException"/> instead of being dropped, so a pack check is never silently unrun.
    /// </summary>
    public static IReadOnlyList<Type> ConcreteTypes(CodeAssemblies assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return [.. assemblies.GetLoadableTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IConfigCheck).IsAssignableFrom(t))];
    }

    /// <summary>One instance of every <see cref="ConcreteTypes(IEnumerable{Assembly})"/> class that has a parameterless constructor, in the same order.</summary>
    public static IReadOnlyList<IConfigCheck> Create(IEnumerable<Assembly> assemblies) => Instantiate(ConcreteTypes(assemblies));

    /// <summary>One instance of every <see cref="ConcreteTypes(CodeAssemblies)"/> class that has a parameterless constructor, in the same order (fails closed).</summary>
    public static IReadOnlyList<IConfigCheck> Create(CodeAssemblies assemblies) => Instantiate(ConcreteTypes(assemblies));

    private static IReadOnlyList<IConfigCheck> Instantiate(IEnumerable<Type> types)
    {
        var checks = new List<IConfigCheck>();
        foreach (Type type in types)
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

    /// <summary>The concrete check classes of a <see cref="CodeAssemblies"/> set that have no instance in <paramref name="checks"/> (fails closed).</summary>
    public static IReadOnlyList<Type> NotRun(CodeAssemblies assemblies, IEnumerable<IConfigCheck> checks)
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

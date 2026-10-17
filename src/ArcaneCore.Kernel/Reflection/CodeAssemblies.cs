using System.Reflection;

namespace ArcaneCore.Kernel.Reflection;

/// <summary>
/// The ordered, immutable set of assemblies that contribute server code to a discovery registry: the built-in assemblies
/// plus any registered "pack" assemblies (a code hot reload or an out-of-tree module, docs/areas/code-hot-reload.md).
/// Registries scan <see cref="GetLoadableTypes"/> instead of <c>typeof(X).Assembly.GetTypes()</c>, so a pack's types are
/// found by the same rules as the built-ins.
/// <para>
/// Fail closed: an assembly can be in the set once (by simple name, so a reloaded pack cannot sit next to its previous
/// build), and a type that cannot load makes <see cref="GetLoadableTypes"/> throw <see cref="CodeAssemblyLoadException"/>
/// naming the assembly and the loader errors, instead of silently dropping the type. Ordering is deterministic:
/// assemblies by simple name (ordinal), then types by full name (ordinal), never reflection enumeration order.
/// </para>
/// </summary>
public sealed class CodeAssemblies
{
    private readonly Assembly[] _builtIns;
    private readonly Assembly[] _packs;

    private CodeAssemblies(Assembly[] builtIns, Assembly[] packs)
    {
        _builtIns = builtIns;
        _packs = packs;
        Assemblies = [.. builtIns.Concat(packs).OrderBy(NameOf, StringComparer.Ordinal)];
    }

    /// <summary>The built-in assemblies, in the order given to <see cref="Of"/>.</summary>
    public IReadOnlyList<Assembly> BuiltIns => _builtIns;

    /// <summary>The registered pack assemblies, in registration order.</summary>
    public IReadOnlyList<Assembly> Packs => _packs;

    /// <summary>Every assembly of the set (built-ins and packs), ordered by simple name (ordinal).</summary>
    public IReadOnlyList<Assembly> Assemblies { get; }

    /// <summary>
    /// A set of only <paramref name="builtIns"/> (at least one). Throws <see cref="ArgumentException"/> when an assembly
    /// appears twice (by simple name).
    /// </summary>
    public static CodeAssemblies Of(params Assembly[] builtIns)
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        if (builtIns.Length == 0)
        {
            throw new ArgumentException("a code assembly set needs at least one built-in assembly", nameof(builtIns));
        }

        Assembly[] copy = [.. builtIns];
        RequireDistinct(copy, nameof(builtIns));
        return new CodeAssemblies(copy, []);
    }

    /// <summary>
    /// A new set with <paramref name="pack"/> added (this set is unchanged). Throws <see cref="ArgumentException"/> when an
    /// assembly of the same simple name is already in the set; replace a reloaded pack with <see cref="WithoutPack"/> first.
    /// </summary>
    public CodeAssemblies WithPack(Assembly pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Assembly[] packs = [.. _packs, pack];
        RequireDistinct([.. _builtIns, .. packs], nameof(pack));
        return new CodeAssemblies(_builtIns, packs);
    }

    /// <summary>
    /// A new set without the pack named <paramref name="simpleName"/>. Throws <see cref="ArgumentException"/> when no pack
    /// has that name (built-ins cannot be removed).
    /// </summary>
    public CodeAssemblies WithoutPack(string simpleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(simpleName);
        Assembly[] packs = [.. _packs.Where(p => !string.Equals(NameOf(p), simpleName, StringComparison.Ordinal))];
        if (packs.Length == _packs.Length)
        {
            throw new ArgumentException($"no pack assembly named '{simpleName}' is registered", nameof(simpleName));
        }

        return new CodeAssemblies(_builtIns, packs);
    }

    /// <summary>True when <paramref name="assembly"/> itself (not just one of the same name) is in the set.</summary>
    public bool Contains(Assembly assembly) => assembly is not null && Assemblies.Contains(assembly);

    /// <summary>
    /// Every type of every assembly of the set, ordered by assembly simple name, then type full name (both ordinal).
    /// Throws <see cref="CodeAssemblyLoadException"/> when any type of any assembly cannot be loaded.
    /// </summary>
    public IReadOnlyList<Type> GetLoadableTypes() => [.. Assemblies.SelectMany(GetLoadableTypes)];

    /// <summary>
    /// Every type of <paramref name="assembly"/>, ordered by full name (ordinal). Throws <see cref="CodeAssemblyLoadException"/>
    /// (wrapping the <see cref="ReflectionTypeLoadException"/> and naming each loader error) when any type cannot be loaded.
    /// </summary>
    public static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            throw new CodeAssemblyLoadException(assembly, ex);
        }

        return [.. types.OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    private static string NameOf(Assembly assembly) => assembly.GetName().Name ?? assembly.FullName ?? string.Empty;

    private static void RequireDistinct(Assembly[] assemblies, string parameter)
    {
        foreach (Assembly assembly in assemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly, parameter);
        }

        string? duplicate = assemblies.GroupBy(NameOf, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException($"assembly '{duplicate}' is already in the code assembly set", parameter);
        }
    }
}

/// <summary>
/// A code assembly of a <see cref="CodeAssemblies"/> set has types that cannot be loaded (a missing dependency, a pack built
/// against another server build). Discovery fails closed on it instead of running with part of the assembly.
/// </summary>
public sealed class CodeAssemblyLoadException : Exception
{
    /// <summary>Wraps <paramref name="inner"/> for <paramref name="assembly"/>.</summary>
    public CodeAssemblyLoadException(Assembly assembly, ReflectionTypeLoadException inner)
        : base(Describe(assembly, inner), inner)
    {
        AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? "?";
        LoaderErrors = [.. inner.LoaderExceptions.OfType<Exception>().Select(e => e.Message).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Simple name of the assembly whose types failed to load.</summary>
    public string AssemblyName { get; }

    /// <summary>The distinct loader error messages.</summary>
    public IReadOnlyList<string> LoaderErrors { get; }

    private static string Describe(Assembly assembly, ReflectionTypeLoadException inner)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(inner);
        int failed = inner.Types.Count(t => t is null);
        string[] errors = [.. inner.LoaderExceptions.OfType<Exception>().Select(e => e.Message).Distinct(StringComparer.Ordinal)];
        string detail = errors.Length == 0 ? "no loader error reported" : string.Join("; ", errors);
        return $"code assembly '{assembly.GetName().Name ?? assembly.FullName}' has {failed} type(s) that cannot be loaded, "
            + $"so discovery over it is refused (fail closed): {detail}";
    }
}

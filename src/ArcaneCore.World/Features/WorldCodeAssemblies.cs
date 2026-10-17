using System.Reflection;
using System.Runtime.Loader;
using ArcaneCore.Kernel.Reflection;

namespace ArcaneCore.World.Features;

/// <summary>
/// The world daemon's startup code set (docs/design/hot-reload-tiers.md, sections 3.1 and 4.6): this assembly plus the built-in
/// packs the host registers before anything is discovered. Every startup discovery of the world (features, service modules,
/// opcode handler groups, command groups and extensions, script modules, content reloadables, the hot-code catalog, the config
/// checks and the wiring audit) reads <see cref="Startup"/>, so a pack registered here is found exactly like World's own types.
/// <para>
/// Tier 2b: the Playerbots pack (<see cref="PlayerbotsAssemblyName"/>) is a separate assembly that references World, so World cannot
/// reference it; the host registers it here instead. The daemon's <c>Program.cs</c> loads it from beside the executable
/// (<see cref="LoadBuiltInPack"/>); an in-process host (tests, the mock client) that references the pack project registers its
/// assembly directly. Both load into the default context: nothing here unloads (that is tier 2c).
/// </para>
/// <para>
/// The set is fixed at its first read: a registration after that throws, because the static discovery lists computed from the
/// first read would not contain the late pack. Thread-safe.
/// </para>
/// </summary>
public static class WorldCodeAssemblies
{
    /// <summary>The simple name of the Playerbots pack assembly (<c>src/ArcaneCore.Playerbots</c>).</summary>
    public const string PlayerbotsAssemblyName = "ArcaneCore.Playerbots";

    private static readonly Lock Gate = new();
    private static readonly List<Assembly> Registered = [];
    private static CodeAssemblies? _startup;

    /// <summary>The world assembly plus every registered built-in pack, in <see cref="CodeAssemblies"/> order. The first read fixes it.</summary>
    public static CodeAssemblies Startup
    {
        get
        {
            lock (Gate)
            {
                return _startup ??= CodeAssemblies.Of([typeof(WorldCodeAssemblies).Assembly, .. Registered]);
            }
        }
    }

    /// <summary>The startup set in registration order: this assembly first, then the built-in packs as registered. Reading it fixes the set.</summary>
    public static IReadOnlyList<Assembly> Ordered => Startup.BuiltIns;

    /// <summary>Whether <see cref="Startup"/> has been read (no further registration is possible).</summary>
    public static bool IsFixed
    {
        get
        {
            lock (Gate)
            {
                return _startup is not null;
            }
        }
    }

    /// <summary>The registered built-in packs (not the world assembly), in registration order.</summary>
    public static IReadOnlyList<Assembly> BuiltInPacks
    {
        get
        {
            lock (Gate)
            {
                return [.. Registered];
            }
        }
    }

    /// <summary>
    /// Add <paramref name="assembly"/> to the startup set as a built-in. Registering the same assembly again is a no-op. Throws when the
    /// set was already read (<see cref="IsFixed"/>), or for a collectible assembly (a built-in never unloads).
    /// </summary>
    public static void RegisterBuiltIn(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.IsCollectible)
        {
            throw new ArgumentException($"{assembly.GetName().Name} is collectible; a built-in pack loads into the default context.", nameof(assembly));
        }

        lock (Gate)
        {
            if (Registered.Contains(assembly) || assembly == typeof(WorldCodeAssemblies).Assembly)
            {
                return;
            }

            if (_startup is not null)
            {
                throw new InvalidOperationException(
                    $"{assembly.GetName().Name} was registered after the world's startup code set was read; register built-in packs first thing in the host.");
            }

            Registered.Add(assembly);
        }
    }

    /// <summary>
    /// The built-in pack <paramref name="simpleName"/> in the default context: the assembly already loaded under that name, else the
    /// file <c>&lt;simpleName&gt;.dll</c> in <paramref name="directory"/> (default: the application's base directory). Throws
    /// <see cref="FileNotFoundException"/> when neither exists (the build puts the pack beside the world executable; a world built
    /// alone has no pack: build the solution or the pack project).
    /// </summary>
    public static Assembly LoadBuiltInPack(string simpleName, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simpleName);
        foreach (Assembly loaded in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.Ordinal))
            {
                return loaded;
            }
        }

        string path = Path.Combine(directory ?? AppContext.BaseDirectory, simpleName + ".dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The built-in pack {simpleName} is missing: {path} does not exist. The build copies it next to the world executable; "
                + $"build the solution (or src/{simpleName}), not ArcaneCore.World alone.", path);
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
    }
}

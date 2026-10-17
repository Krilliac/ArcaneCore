using System.Reflection;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Reflection;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>Marks an <see cref="InstanceData"/> type as the script of a map (vmangos <c>map_template.ScriptName</c> / cmangos <c>instance_template.ScriptName</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InstanceScriptAttribute(uint mapId) : Attribute
{
    public uint MapId { get; } = mapId;
}

/// <summary>
/// Which instance maps have a script (vmangos <c>ScriptMgr::CreateInstanceData</c>, called by Map::CreateInstanceData only when the map entry
/// has a script id). The default set is discovered by reflection: every non-abstract <see cref="InstanceData"/> with
/// <see cref="InstanceScriptAttribute"/> and a constructor taking a <see cref="Map"/>. Two scripts for one map fail at startup.
/// A map without a script has no instance data, so EventAI SET_INST_DATA on it fails as in cmangos ("attempt to set instance data without
/// instance script").
/// <para>
/// Hot swap (the hot module lane): the scripts live in an immutable table that <see cref="CommitSwap"/> replaces in one reference write, so
/// every caller holding this registry (<see cref="Default"/>, <see cref="InstanceManager.Scripts"/>) sees the new table on its next
/// <see cref="Create"/>. A swap affects NEW instance maps only: an instance already loaded keeps the <see cref="InstanceData"/> object it was
/// created with, because that object's state is the instance's progress (bosses killed, doors opened) and a fresh script would not have it.
/// <see cref="RunsCurrentScript"/> tells whether such an object still comes from the current script of its map; the instance manager counts
/// the ones that do not (<see cref="InstanceManager.RetiredScriptInstanceCount"/>).
/// </para>
/// <para>
/// Scripts registered with <see cref="Register"/> or found by <see cref="Discover(Assembly)"/> are built in: a module swap can add maps
/// without a script and replace or remove its own scripts, but never override or remove a built-in one (fail closed).
/// </para>
/// </summary>
public sealed class InstanceScriptRegistry
{
    // One registered script. Reference identity is the version: a replacement is a new Script, so data created by the old one is retired.
    private sealed record Script(uint MapId, Func<Map, InstanceData> Factory, bool BuiltIn);

    private readonly Lock _writeLock = new();

    // Which Script created each live InstanceData (weak: an unloaded instance's data is collected with it).
    private readonly ConditionalWeakTable<InstanceData, Script> _origins = new();

    // Never mutated once published; writers copy, change and publish under _writeLock.
    private volatile Dictionary<uint, Script> _scripts = [];
    private long _generation;

    /// <summary>The code set whose instance scripts are built in: the Game assembly (no pack is configured at startup).</summary>
    public static CodeAssemblies StartupAssemblies { get; } = CodeAssemblies.Of(typeof(InstanceScriptRegistry).Assembly);

    /// <summary>The scripts of the startup code set (<see cref="StartupAssemblies"/>); module swaps go through this instance so every holder of it sees them.</summary>
    public static InstanceScriptRegistry Default { get; } = Discover(StartupAssemblies.Assemblies);

    /// <summary>The maps with a script.</summary>
    public IReadOnlyCollection<uint> MapIds => _scripts.Keys;

    /// <summary>The maps whose script came from a module swap (not built in).</summary>
    public IReadOnlyCollection<uint> ModuleMapIds => [.. _scripts.Values.Where(s => !s.BuiltIn).Select(s => s.MapId)];

    /// <summary>Bumped by every <see cref="Register"/> and <see cref="CommitSwap"/>.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Add (or replace) the built-in script of <paramref name="mapId"/>; returns this registry.</summary>
    public InstanceScriptRegistry Register(uint mapId, Func<Map, InstanceData> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_writeLock)
        {
            Publish(new Dictionary<uint, Script>(_scripts) { [mapId] = new Script(mapId, factory, BuiltIn: true) });
        }

        return this;
    }

    public bool HasScript(uint mapId) => _scripts.ContainsKey(mapId);

    /// <summary>Whether the script of <paramref name="mapId"/> is built in (a module cannot override or remove it).</summary>
    public bool IsBuiltIn(uint mapId) => _scripts.TryGetValue(mapId, out Script? script) && script.BuiltIn;

    /// <summary>A fresh instance data for <paramref name="map"/> from the current script of its map, or null when its map has no script.</summary>
    public InstanceData? Create(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!_scripts.TryGetValue(map.MapId, out Script? script))
        {
            return null;
        }

        InstanceData data = script.Factory(map);
        _origins.AddOrUpdate(data, script);
        return data;
    }

    /// <summary>
    /// Whether <paramref name="data"/> was created by this registry with the script its map has now. False for data whose script a swap has
    /// since replaced or removed (retired script code still running in a live instance), and for data this registry did not create.
    /// </summary>
    public bool RunsCurrentScript(InstanceData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _origins.TryGetValue(data, out Script? origin)
            && _scripts.TryGetValue(origin.MapId, out Script? current)
            && ReferenceEquals(origin, current);
    }

    /// <summary>
    /// Whether <see cref="CommitSwap"/> would work, checked without changing anything: every map to remove has a module script, every map to add
    /// is listed once and is free once the removals are done, and no built-in script is overridden or removed. Null when it would; else why not.
    /// </summary>
    public string? ValidateSwap(IReadOnlyCollection<uint> removeMapIds, IEnumerable<uint> addMapIds)
    {
        ArgumentNullException.ThrowIfNull(removeMapIds);
        ArgumentNullException.ThrowIfNull(addMapIds);
        return Validate(_scripts, removeMapIds, addMapIds);
    }

    /// <summary>
    /// Remove the module scripts of <paramref name="removeMapIds"/> and add <paramref name="add"/> in one step (the hot module lane; call at a tick
    /// boundary on the world thread, after <see cref="ValidateSwap"/>). Re-validates and throws <see cref="InvalidOperationException"/> without
    /// changing anything when the swap is not valid. Only instance maps created afterwards use the new scripts; loaded instances keep theirs.
    /// </summary>
    public void CommitSwap(IReadOnlyCollection<uint> removeMapIds, IReadOnlyDictionary<uint, Func<Map, InstanceData>> add)
    {
        ArgumentNullException.ThrowIfNull(removeMapIds);
        ArgumentNullException.ThrowIfNull(add);
        foreach (KeyValuePair<uint, Func<Map, InstanceData>> pair in add)
        {
            if (pair.Value is null)
            {
                throw new ArgumentException($"map {pair.Key} has a null instance script factory", nameof(add));
            }
        }

        lock (_writeLock)
        {
            Dictionary<uint, Script> current = _scripts;
            if (Validate(current, removeMapIds, add.Keys) is { } error)
            {
                throw new InvalidOperationException(error);
            }

            var next = new Dictionary<uint, Script>(current);
            foreach (uint mapId in removeMapIds)
            {
                next.Remove(mapId);
            }

            foreach ((uint mapId, Func<Map, InstanceData> factory) in add)
            {
                next[mapId] = new Script(mapId, factory, BuiltIn: false);
            }

            Publish(next);
        }
    }

    /// <summary>A registry of the scripts declared in <paramref name="assembly"/>.</summary>
    public static InstanceScriptRegistry Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Discover([assembly]);
    }

    /// <summary>A registry of the scripts declared in <paramref name="assemblies"/>; two scripts for one map across them fail (fail closed).</summary>
    public static InstanceScriptRegistry Discover(IEnumerable<Assembly> assemblies)
    {
        var registry = new InstanceScriptRegistry();
        var table = new Dictionary<uint, Script>();
        foreach ((uint mapId, Func<Map, InstanceData> factory) in DiscoverFactories(assemblies))
        {
            table[mapId] = new Script(mapId, factory, BuiltIn: true);
        }

        registry.Publish(table);
        return registry;
    }

    /// <summary>
    /// The script factories declared in <paramref name="assemblies"/> by map (what a module hands to <see cref="CommitSwap"/>). Two scripts for one
    /// map, or a script without a constructor taking a <see cref="Map"/>, throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public static IReadOnlyDictionary<uint, Func<Map, InstanceData>> DiscoverFactories(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var factories = new Dictionary<uint, Func<Map, InstanceData>>();
        var owners = new Dictionary<uint, Type>();
        foreach (Assembly assembly in assemblies.Distinct())
        {
            ArgumentNullException.ThrowIfNull(assembly);
            foreach (Type type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(InstanceData).IsAssignableFrom(t))
                .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.GetCustomAttribute<InstanceScriptAttribute>() is not { } attribute)
                {
                    continue;
                }

                ConstructorInfo constructor = type.GetConstructor([typeof(Map)])
                    ?? throw new InvalidOperationException($"instance script {type.Name} has no constructor taking a Map");
                if (!owners.TryAdd(attribute.MapId, type))
                {
                    throw new InvalidOperationException($"map {attribute.MapId} has two instance scripts ({owners[attribute.MapId].Name} and {type.Name})");
                }

                factories[attribute.MapId] = map => (InstanceData)constructor.Invoke([map]);
            }
        }

        return factories;
    }

    private static string? Validate(Dictionary<uint, Script> current, IReadOnlyCollection<uint> removeMapIds, IEnumerable<uint> addMapIds)
    {
        foreach (uint mapId in removeMapIds)
        {
            if (!current.TryGetValue(mapId, out Script? script))
            {
                return $"map {mapId} has no instance script to remove";
            }

            if (script.BuiltIn)
            {
                return $"map {mapId}'s instance script is built in; a module cannot remove it";
            }
        }

        var seen = new HashSet<uint>();
        foreach (uint mapId in addMapIds)
        {
            if (!seen.Add(mapId))
            {
                return $"map {mapId} is added twice";
            }

            if (current.TryGetValue(mapId, out Script? script) && !removeMapIds.Contains(mapId))
            {
                return script.BuiltIn
                    ? $"map {mapId}'s instance script is built in; a module cannot override it"
                    : $"map {mapId} already has a module instance script (remove it in the same swap to replace it)";
            }
        }

        return null;
    }

    private void Publish(Dictionary<uint, Script> next)
    {
        _scripts = next;
        Interlocked.Increment(ref _generation);
    }
}

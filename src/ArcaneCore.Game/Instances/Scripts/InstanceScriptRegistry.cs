using System.Reflection;
using ArcaneCore.Game.Maps;

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
/// </summary>
public sealed class InstanceScriptRegistry
{
    private readonly Dictionary<uint, Func<Map, InstanceData>> _factories = [];

    /// <summary>The scripts of the Game assembly.</summary>
    public static InstanceScriptRegistry Default { get; } = Discover(typeof(InstanceScriptRegistry).Assembly);

    /// <summary>The maps with a script.</summary>
    public IReadOnlyCollection<uint> MapIds => _factories.Keys;

    /// <summary>Add (or replace) the script of <paramref name="mapId"/>; returns this registry.</summary>
    public InstanceScriptRegistry Register(uint mapId, Func<Map, InstanceData> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[mapId] = factory;
        return this;
    }

    public bool HasScript(uint mapId) => _factories.ContainsKey(mapId);

    /// <summary>A fresh instance data for <paramref name="map"/>, or null when its map has no script.</summary>
    public InstanceData? Create(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _factories.TryGetValue(map.MapId, out Func<Map, InstanceData>? factory) ? factory(map) : null;
    }

    /// <summary>A registry of the scripts declared in <paramref name="assembly"/>.</summary>
    public static InstanceScriptRegistry Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var registry = new InstanceScriptRegistry();
        foreach (Type type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(InstanceData).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.GetCustomAttribute<InstanceScriptAttribute>() is not { } attribute)
            {
                continue;
            }

            ConstructorInfo constructor = type.GetConstructor([typeof(Map)])
                ?? throw new InvalidOperationException($"instance script {type.Name} has no constructor taking a Map");
            if (registry.HasScript(attribute.MapId))
            {
                throw new InvalidOperationException($"map {attribute.MapId} has two instance scripts ({type.Name} and another)");
            }

            registry.Register(attribute.MapId, map => (InstanceData)constructor.Invoke([map]));
        }

        return registry;
    }
}

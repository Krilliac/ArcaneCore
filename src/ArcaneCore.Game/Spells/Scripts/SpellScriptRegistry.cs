using System.Reflection;

namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// The spell scripts by spell id. Two scripts claiming one spell id fail at construction (charter: fail closed), as does a script
/// without a <see cref="SpellScriptAttribute"/> or with an empty id list.
/// </summary>
public sealed class SpellScriptRegistry
{
    private readonly Dictionary<uint, ISpellScript> _byId = [];

    public SpellScriptRegistry(IEnumerable<ISpellScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        foreach (ISpellScript script in scripts)
        {
            Type type = script.GetType();
            SpellScriptAttribute attribute = type.GetCustomAttribute<SpellScriptAttribute>(inherit: false)
                ?? throw new InvalidOperationException($"spell script {type.FullName} has no {nameof(SpellScriptAttribute)}");
            if (attribute.SpellIds.Count == 0)
            {
                throw new InvalidOperationException($"spell script {type.FullName} names no spell id");
            }

            foreach (uint id in attribute.SpellIds)
            {
                if (_byId.TryGetValue(id, out ISpellScript? existing))
                {
                    throw new InvalidOperationException(
                        $"spell {id} is claimed by two scripts: {existing.GetType().FullName} and {type.FullName}");
                }

                _byId[id] = script;
            }
        }
    }

    /// <summary>The registry of every concrete <see cref="ISpellScript"/> of <paramref name="assembly"/> (ordered by full type name).</summary>
    public static SpellScriptRegistry Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var scripts = new List<ISpellScript>();
        foreach (Type type in assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(ISpellScript).IsAssignableFrom(t))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
                ?? throw new InvalidOperationException($"spell script {type.FullName} has no parameterless constructor");
            scripts.Add((ISpellScript)constructor.Invoke(null));
        }

        return new SpellScriptRegistry(scripts);
    }

    public int Count => _byId.Count;

    public IEnumerable<uint> SpellIds => _byId.Keys;

    public ISpellScript? Find(uint spellId) => _byId.GetValueOrDefault(spellId);
}

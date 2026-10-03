using System.Reflection;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// A self-contained set of spell effect and aura handlers. Implement it in a new file of this
/// assembly (or of another assembly handed to <see cref="SpellSystem.RegisterModules(Assembly)"/>) and
/// <see cref="Register"/> its handlers with <see cref="SpellSystem.RegisterEffect"/> and
/// <see cref="SpellSystem.RegisterAura"/>: nobody edits the built-in tables in
/// <c>SpellSystem.Effects.cs</c> / <c>SpellSystem.Auras.cs</c> (docs/integration/seams.md: discovery by
/// reflection, ordered by full type name, a duplicate fails at startup).
/// <para>
/// vmangos keeps these as two static tables, <c>SpellEffects[]</c> (Spell.cpp) and
/// <c>AuraHandler[]</c> (SpellAuras.cpp:63-255), edited in place. The module seam is the same table,
/// filled from several files.
/// </para>
/// </summary>
public interface ISpellHandlerModule
{
    /// <summary>
    /// Install this module's handlers. It may only add: replacing a handler that is already installed
    /// (built-in or from another module) makes <see cref="SpellSystem.RegisterModules(IEnumerable{Type})"/> throw.
    /// </summary>
    void Register(SpellSystem system);
}

/// <summary>Discovery of the <see cref="ISpellHandlerModule"/> types of an assembly.</summary>
public static class SpellHandlerModules
{
    /// <summary>The modules of this assembly, in the order <see cref="SpellSystem"/> applies them.</summary>
    public static IReadOnlyList<Type> BuiltIn { get; } = Discover(typeof(SpellHandlerModules).Assembly);

    /// <summary>
    /// Every concrete <see cref="ISpellHandlerModule"/> of <paramref name="assembly"/>, ordered by full
    /// type name (ordinal) so the order never depends on reflection enumeration. A module that cannot be
    /// created (no parameterless constructor) fails here instead of being skipped (charter: fail closed).
    /// </summary>
    public static IReadOnlyList<Type> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var found = new List<Type>();
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition
                && typeof(ISpellHandlerModule).IsAssignableFrom(type))
            {
                Create(type);
                found.Add(type);
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return found;
    }

    /// <summary>Create the module of <paramref name="type"/> (public or internal parameterless constructor).</summary>
    internal static ISpellHandlerModule Create(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !typeof(ISpellHandlerModule).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"{type.FullName} is not a concrete {nameof(ISpellHandlerModule)}");
        }

        ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
            ?? throw new InvalidOperationException($"spell handler module {type.FullName} has no parameterless constructor");
        return (ISpellHandlerModule)constructor.Invoke(null);
    }
}

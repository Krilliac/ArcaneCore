using System.Collections.Frozen;
using System.Reflection;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// The spell scripts by spell id. Two scripts claiming one spell id fail at construction (charter: fail closed), as does a script
/// without a <see cref="SpellScriptAttribute"/> or with an empty id list.
/// <para>
/// The scripts given to the constructor are the built-in ones. A hot module adds, replaces and removes its own scripts at a tick boundary with
/// <see cref="ValidateSwap"/> then <see cref="CommitSwap"/> (the shape of <c>CreatureAiFactory</c> and <c>ScriptHookRegistry</c>): the whole state is
/// one immutable snapshot that a commit replaces by a single volatile write, so a reader sees the old scripts or the new ones, never a mix, and a
/// swap that fails validation changes nothing. A module can never claim a built-in spell id nor remove a built-in script; it replaces only scripts
/// that are in its own removed list.
/// </para>
/// <para>
/// In-flight work: nothing in the spell system caches a script. Every hook (<see cref="SpellScriptDispatcher"/>'s cast check, OnCast, the effect
/// chains, the dispel wrapper, OnSummon) looks the script up through <see cref="Find"/> when it fires, so after a commit the next hook of any cast,
/// including a cast whose cast time straddles the swap, runs the new script (or none). What keeps an old script alive is only what holds it: a call
/// already on the stack (none at a tick boundary, which is when the module lane commits) and whatever the old script itself scheduled (a timer,
/// an event subscription or a closure capturing itself), which runs with the old code until it finishes. A script that schedules work past its
/// own hook therefore keeps its assembly loaded until that work ends.
/// </para>
/// </summary>
public sealed class SpellScriptRegistry
{
    private volatile Snapshot _snapshot;

    public SpellScriptRegistry(IEnumerable<ISpellScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var byId = new Dictionary<uint, ISpellScript>();
        var executeEffects = new HashSet<SpellEffectName>();
        foreach (ISpellScript script in scripts)
        {
            ArgumentNullException.ThrowIfNull(script, nameof(scripts));
            Type type = script.GetType();
            SpellScriptAttribute attribute = AttributeOf(type, out string? error) ?? throw new InvalidOperationException(error);
            executeEffects.UnionWith(attribute.ExecuteEffects);
            foreach (uint id in attribute.SpellIds)
            {
                if (byId.TryGetValue(id, out ISpellScript? existing))
                {
                    throw new InvalidOperationException(
                        $"spell {id} is claimed by two scripts: {existing.GetType().FullName} and {type.FullName}");
                }

                byId[id] = script;
            }
        }

        _snapshot = new Snapshot(byId.ToFrozenDictionary(), executeEffects.ToFrozenSet(), byId.Keys.ToFrozenSet(), []);
    }

    /// <summary>The registry of every concrete <see cref="ISpellScript"/> of <paramref name="assembly"/> (ordered by full type name).</summary>
    public static SpellScriptRegistry Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return new SpellScriptRegistry(CreateScripts(assembly));
    }

    /// <summary>
    /// The registry of every concrete <see cref="ISpellScript"/> of <paramref name="assemblies"/> (each assembly's scripts ordered by full type name,
    /// assemblies in the given order; an assembly listed twice is read once). Two scripts of different assemblies claiming one id throw.
    /// </summary>
    public static SpellScriptRegistry Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var scripts = new List<ISpellScript>();
        foreach (Assembly assembly in assemblies.Distinct())
        {
            ArgumentNullException.ThrowIfNull(assembly, nameof(assemblies));
            scripts.AddRange(CreateScripts(assembly));
        }

        return new SpellScriptRegistry(scripts);
    }

    /// <summary>
    /// One instance of every concrete <see cref="ISpellScript"/> of <paramref name="assembly"/>, ordered by full type name, without registering them
    /// (a hot module's scripts for <see cref="ValidateSwap"/> / <see cref="CommitSwap"/>). A script without a parameterless constructor throws.
    /// </summary>
    public static IReadOnlyList<ISpellScript> CreateScripts(Assembly assembly)
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

        return scripts;
    }

    public int Count => _snapshot.ById.Count;

    public IEnumerable<uint> SpellIds => _snapshot.ById.Keys;

    /// <summary>
    /// The effects (beyond DUMMY and SCRIPT_EFFECT) some current script asked to see executed (<see cref="SpellScriptAttribute.ExecuteEffects"/>),
    /// recomputed by every <see cref="CommitSwap"/>.
    /// </summary>
    public IReadOnlyCollection<SpellEffectName> ExecuteEffects => _snapshot.ExecuteEffects;

    /// <summary>The scripts a hot module added through <see cref="CommitSwap"/> and has not removed, in commit order (built-in ones are not listed).</summary>
    public IReadOnlyList<ISpellScript> HotScripts => _snapshot.Hot.AsReadOnly();

    /// <summary>Whether <paramref name="spellId"/> is served by a built-in script (one a module can neither replace nor remove).</summary>
    public bool IsBuiltIn(uint spellId) => _snapshot.BuiltInIds.Contains(spellId);

    public ISpellScript? Find(uint spellId) => _snapshot.ById.GetValueOrDefault(spellId);

    /// <summary>
    /// The script whose <see cref="ISpellScript.OnEffectExecute"/> runs before <paramref name="effect"/> of <paramref name="spellId"/>: DUMMY and
    /// SCRIPT_EFFECT always ask, any other effect only while some current script declares it (one snapshot read, so a chained effect handler follows
    /// commits: an effect no script declares any more behaves as after a restart without that script).
    /// </summary>
    internal ISpellScript? FindForEffect(uint spellId, SpellEffectName effect)
    {
        Snapshot snapshot = _snapshot;
        return effect is SpellEffectName.Dummy or SpellEffectName.ScriptEffect || snapshot.ExecuteEffects.Contains(effect)
            ? snapshot.ById.GetValueOrDefault(spellId)
            : null;
    }

    /// <summary>
    /// Whether <see cref="CommitSwap"/> would work, checked without changing anything: every script to remove is a hot script registered now (a
    /// built-in one cannot be removed), every one to add carries a <see cref="SpellScriptAttribute"/> with at least one id, is listed once and is not
    /// registered yet (unless it is also being removed), and no spell id it claims is built in, claimed by another added script, or held by a hot
    /// script that is not being removed. Null when it would; else why not.
    /// </summary>
    public string? ValidateSwap(IReadOnlyCollection<ISpellScript> removed, IReadOnlyCollection<ISpellScript> added)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(added);
        return Plan(_snapshot, removed, added, out _);
    }

    /// <summary>
    /// Remove <paramref name="removed"/> and add <paramref name="added"/> in one step (the hot module lane; world thread at a tick boundary, after
    /// <see cref="ValidateSwap"/>). The new snapshot is built completely and then published by one reference write, and
    /// <see cref="ExecuteEffects"/> is recomputed from the scripts that remain; afterwards the removed scripts are referenced by nothing here. A swap
    /// that <see cref="ValidateSwap"/> would refuse throws <see cref="InvalidOperationException"/> and changes nothing. Raises
    /// <see cref="ScriptsChanged"/> after publishing so an installed <see cref="SpellScriptDispatcher"/> chains a newly declared execute effect; when a
    /// handler throws, the previous snapshot is put back (and <see cref="ScriptsChanged"/> raised again so the subscribers follow it) and the swap
    /// throws <see cref="InvalidOperationException"/>: a new script whose execute effect could not be chained must not go live half wired.
    /// </summary>
    public void CommitSwap(IReadOnlyCollection<ISpellScript> removed, IReadOnlyCollection<ISpellScript> added)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(added);
        Snapshot current = _snapshot;
        if (Plan(current, removed, added, out Snapshot? next) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        _snapshot = next!;
        if (RaiseScriptsChanged() is { } failure)
        {
            _snapshot = current;
            _ = RaiseScriptsChanged();
            throw new InvalidOperationException(
                $"a {nameof(ScriptsChanged)} handler failed ({failure.GetType().Name}: {failure.Message}); the spell script swap was undone", failure);
        }
    }

    /// <summary>The registry's current state, to put back with <see cref="Restore"/> (the hot module lane's undo step when a later part of a swap fails).</summary>
    public Checkpoint Save() => new(this, _snapshot);

    /// <summary>
    /// Put back the state <paramref name="checkpoint"/> saved, exactly (the same script objects and order), and raise <see cref="ScriptsChanged"/>
    /// so the subscribers follow it. World thread at a tick boundary. A handler that throws here is only recorded in the fault ledger: an undo must
    /// not fail half way.
    /// </summary>
    public void Restore(Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!ReferenceEquals(checkpoint.Owner, this))
        {
            throw new ArgumentException("the checkpoint was saved by another spell script registry", nameof(checkpoint));
        }

        _snapshot = checkpoint.State;
        _ = RaiseScriptsChanged();
    }

    /// <summary>
    /// Raised at the end of <see cref="CommitSwap"/> and <see cref="Restore"/> (world thread, tick boundary). Every handler runs; one that throws is
    /// recorded in the fault ledger, and on a commit it makes the commit undo itself and throw.
    /// </summary>
    public event Action? ScriptsChanged;

    /// <summary>Run every handler; the first failure (each one is also recorded in the fault ledger), or null.</summary>
    private Exception? RaiseScriptsChanged()
    {
        if (ScriptsChanged is not { } handlers)
        {
            return null;
        }

        Exception? first = null;
        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("spell-script", ex);
                first ??= ex;
            }
        }

        return first;
    }

    private static string? Plan(Snapshot current, IReadOnlyCollection<ISpellScript> removed, IReadOnlyCollection<ISpellScript> added, out Snapshot? next)
    {
        next = null;
        var removing = new HashSet<ISpellScript>(ReferenceEqualityComparer.Instance);
        foreach (ISpellScript script in removed)
        {
            if (script is null)
            {
                return "a spell script to remove is null";
            }

            if (!current.Hot.Contains(script, ReferenceEqualityComparer.Instance))
            {
                return current.ById.Values.Contains(script, ReferenceEqualityComparer.Instance)
                    ? $"spell script {script.GetType().FullName} is built in, so a module cannot remove it"
                    : $"spell script {script.GetType().FullName} to remove is not registered";
            }

            removing.Add(script);
        }

        var byId = current.ById.Where(p => !removing.Contains(p.Value)).ToDictionary();
        var seen = new HashSet<ISpellScript>(ReferenceEqualityComparer.Instance);
        var adding = new List<ISpellScript>();
        foreach (ISpellScript script in added)
        {
            if (script is null)
            {
                return "a spell script to add is null";
            }

            Type type = script.GetType();
            if (!seen.Add(script) || (current.ById.Values.Contains(script, ReferenceEqualityComparer.Instance) && !removing.Contains(script)))
            {
                return $"spell script {type.FullName} is already registered";
            }

            if (AttributeOf(type, out string? error) is not { } attribute)
            {
                return error;
            }

            foreach (uint id in attribute.SpellIds)
            {
                if (current.BuiltInIds.Contains(id))
                {
                    return $"spell {id} has the built-in script {current.ById[id].GetType().FullName}; {type.FullName} cannot override it";
                }

                if (byId.TryGetValue(id, out ISpellScript? holder))
                {
                    return $"spell {id} is claimed by two scripts: {holder.GetType().FullName} and {type.FullName}";
                }

                byId[id] = script;
            }

            adding.Add(script);
        }

        var executeEffects = new HashSet<SpellEffectName>();
        foreach (ISpellScript script in byId.Values.Distinct(ReferenceEqualityComparer.Instance).Cast<ISpellScript>())
        {
            executeEffects.UnionWith(script.GetType().GetCustomAttribute<SpellScriptAttribute>(inherit: false)!.ExecuteEffects);
        }

        next = new Snapshot(
            byId.ToFrozenDictionary(),
            executeEffects.ToFrozenSet(),
            current.BuiltInIds,
            [.. current.Hot.Where(s => !removing.Contains(s)), .. adding]);
        return null;
    }

    private static SpellScriptAttribute? AttributeOf(Type type, out string? error)
    {
        SpellScriptAttribute? attribute = type.GetCustomAttribute<SpellScriptAttribute>(inherit: false);
        error = attribute is null ? $"spell script {type.FullName} has no {nameof(SpellScriptAttribute)}"
            : attribute.SpellIds.Count == 0 ? $"spell script {type.FullName} names no spell id"
            : null;
        return error is null ? attribute : null;
    }

    /// <summary>A saved state of one registry (<see cref="Save"/>); opaque to everyone else.</summary>
    public sealed class Checkpoint
    {
        internal Checkpoint(SpellScriptRegistry owner, Snapshot state)
        {
            Owner = owner;
            State = state;
        }

        internal SpellScriptRegistry Owner { get; }

        internal Snapshot State { get; }
    }

    /// <summary>One immutable state of the registry; replaced as a whole, never mutated.</summary>
    internal sealed record Snapshot(
        FrozenDictionary<uint, ISpellScript> ById,
        FrozenSet<SpellEffectName> ExecuteEffects,
        FrozenSet<uint> BuiltInIds,
        ISpellScript[] Hot);
}

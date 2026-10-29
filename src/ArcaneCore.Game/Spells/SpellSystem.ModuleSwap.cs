using ArcaneCore.Kernel.Reflection;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>The handlers each applied module installed (the keys it added), so a hot module's handlers can be taken out again.</summary>
    private readonly Dictionary<Type, ModuleAdds> _moduleAdds = [];

    internal sealed record ModuleAdds(
        IReadOnlyDictionary<SpellEffectName, SpellEffectHandler> Effects,
        IReadOnlyDictionary<AuraType, AuraHandler> Auras);

    /// <summary>A saved copy of the handler tables and the module list, for <see cref="RestoreModules"/>.</summary>
    public sealed class ModuleCheckpoint
    {
        internal ModuleCheckpoint(
            Dictionary<SpellEffectName, SpellEffectHandler> effects,
            Dictionary<AuraType, AuraHandler> auras,
            List<Type> modules,
            Dictionary<Type, ModuleAdds> adds)
        {
            Effects = effects;
            Auras = auras;
            Modules = modules;
            Adds = adds;
        }

        internal Dictionary<SpellEffectName, SpellEffectHandler> Effects { get; }

        internal Dictionary<AuraType, AuraHandler> Auras { get; }

        internal List<Type> Modules { get; }

        internal Dictionary<Type, ModuleAdds> Adds { get; }
    }

    private void RecordModuleAdds(
        Type type,
        Dictionary<SpellEffectName, SpellEffectHandler> effectsBefore,
        Dictionary<AuraType, AuraHandler> aurasBefore)
        => _moduleAdds[type] = new ModuleAdds(
            EffectHandlers.Where(pair => !effectsBefore.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            AuraHandlers.Where(pair => !aurasBefore.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>
    /// Apply every <see cref="ISpellHandlerModule"/> of <paramref name="assemblies"/> (built-ins plus packs, <see cref="SpellHandlerModules.Discover(CodeAssemblies)"/>:
    /// assembly name, then full type name) that is not applied yet. The constructor applies the Game assembly's modules, so a set that contains it
    /// only adds what is new; a module that replaces an installed handler fails here as it does in <see cref="RegisterModules(IEnumerable{Type})"/>.
    /// </summary>
    public void RegisterModules(CodeAssemblies assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        RegisterModules(SpellHandlerModules.Discover(assemblies).Where(type => !_modules.Contains(type)).ToList());
    }

    /// <summary>
    /// Why the swap of a hot module's handler modules cannot be applied (null when it can): a type in <paramref name="adding"/> that is applied already
    /// (and is not being <paramref name="removing"/>) or cannot be created. A module that replaces an installed handler is found when it runs, in
    /// <see cref="CommitModuleSwap"/>, which the caller rolls back with <see cref="RestoreModules"/>.
    /// </summary>
    public string? ValidateModuleSwap(IReadOnlyCollection<Type> removing, IReadOnlyCollection<Type> adding)
    {
        ArgumentNullException.ThrowIfNull(removing);
        ArgumentNullException.ThrowIfNull(adding);
        foreach (Type type in adding)
        {
            if (_modules.Contains(type) && !removing.Contains(type))
            {
                return $"spell handler module {type.FullName} is already applied";
            }

            try
            {
                SpellHandlerModules.Create(type);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        return null;
    }

    /// <summary>A copy of the handler tables and the module list as they are now (world thread).</summary>
    public ModuleCheckpoint SaveModules()
        => new(new(EffectHandlers), new(AuraHandlers), [.. _modules], new(_moduleAdds));

    /// <summary>Put the handler tables and the module list back as <paramref name="checkpoint"/> saved them.</summary>
    public void RestoreModules(ModuleCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        EffectHandlers.Clear();
        foreach ((SpellEffectName effect, SpellEffectHandler handler) in checkpoint.Effects)
        {
            EffectHandlers[effect] = handler;
        }

        AuraHandlers.Clear();
        foreach ((AuraType aura, AuraHandler handler) in checkpoint.Auras)
        {
            AuraHandlers[aura] = handler;
        }

        _modules.Clear();
        _modules.AddRange(checkpoint.Modules);
        _moduleAdds.Clear();
        foreach ((Type type, ModuleAdds adds) in checkpoint.Adds)
        {
            _moduleAdds[type] = adds;
        }
    }

    /// <summary>
    /// Take the handlers of <paramref name="removing"/> out and apply <paramref name="adding"/> (a hot module reload or unload; world thread). A handler a
    /// removed module installed is removed only while it is still the installed one; a later registration that replaced or wrapped it stays (the wrapper
    /// keeps the retired code reachable). Throws, with the tables half changed, when a new module fails: the caller restores a <see cref="SaveModules"/> copy.
    /// <para>
    /// The live auras of a removed module's aura types are removed first (their holders go whole), so the stat changes they made are reverted by the
    /// module's own handler; a failed swap that is rolled back with <see cref="RestoreModules"/> does not bring those auras back.
    /// </para>
    /// <para>
    /// LIMIT: only the effect and aura handler tables are undone. Whatever else a module's <see cref="ISpellHandlerModule.Register"/> installs through
    /// other seams (observers, cast checks, learn observers) stays until restart.
    /// </para>
    /// </summary>
    public void CommitModuleSwap(IReadOnlyCollection<Type> removing, IReadOnlyCollection<Type> adding)
    {
        ArgumentNullException.ThrowIfNull(removing);
        ArgumentNullException.ThrowIfNull(adding);
        foreach (Type type in removing)
        {
            if (!_moduleAdds.Remove(type, out ModuleAdds? adds))
            {
                _modules.Remove(type);
                continue;
            }

            foreach ((SpellEffectName effect, SpellEffectHandler handler) in adds.Effects)
            {
                if (EffectHandlers.TryGetValue(effect, out SpellEffectHandler? current) && ReferenceEquals(current, handler))
                {
                    EffectHandlers.Remove(effect);
                }
            }

            // Aura removal looks the handler up when it runs, so the live auras of the module's types go first, while the handler is still installed:
            // their Apply(false) reverts the stat changes. Once the handler is gone nothing would.
            RemoveLiveAurasOf(adds.Auras);
            foreach ((AuraType aura, AuraHandler handler) in adds.Auras)
            {
                if (AuraHandlers.TryGetValue(aura, out AuraHandler? current) && ReferenceEquals(current, handler))
                {
                    AuraHandlers.Remove(aura);
                }
            }

            _modules.Remove(type);
        }

        RegisterModules(adding);
    }

    /// <summary>
    /// Remove (<see cref="AuraRemoveMode.Default"/>, so Apply(false) runs) every live holder that carries an aura whose type is a key of
    /// <paramref name="handlers"/> and whose installed handler is still that module's own one. A holder that has such an aura among others goes whole.
    /// </summary>
    private void RemoveLiveAurasOf(IReadOnlyDictionary<AuraType, AuraHandler> handlers)
    {
        if (handlers.Count == 0 || _states.Count == 0)
        {
            return;
        }

        foreach (UnitSpellState state in _states.Values.ToArray())
        {
            foreach (SpellAuraHolder holder in state.Auras.ToArray())
            {
                foreach (SpellAura? aura in holder.AuraSpan.ToArray())
                {
                    if (aura is not null && handlers.TryGetValue(aura.Type, out AuraHandler? own)
                        && AuraHandlers.TryGetValue(aura.Type, out AuraHandler? current) && ReferenceEquals(current, own))
                    {
                        RemoveHolder(state, holder);
                        break;
                    }
                }
            }
        }
    }
}

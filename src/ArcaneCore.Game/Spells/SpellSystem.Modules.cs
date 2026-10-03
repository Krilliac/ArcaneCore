using System.Reflection;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly List<Type> _modules = [];

    /// <summary>The <see cref="ISpellHandlerModule"/> types applied to this system, in application order.</summary>
    public IReadOnlyList<Type> Modules => _modules;

    /// <summary>Apply every module of <paramref name="assembly"/> (see <see cref="SpellHandlerModules.Discover"/>).</summary>
    public void RegisterModules(Assembly assembly) => RegisterModules(SpellHandlerModules.Discover(assembly));

    /// <summary>
    /// Create and run each module, in the order given. A module may only add handlers: if one that was
    /// installed before it ran (built-in, from an earlier module, or a seam registration) is replaced, or
    /// the same module is applied twice, this throws naming the module and the effect or aura, so a
    /// duplicate claim fails at startup instead of silently changing behaviour.
    /// </summary>
    public void RegisterModules(IEnumerable<Type> moduleTypes)
    {
        ArgumentNullException.ThrowIfNull(moduleTypes);
        foreach (Type type in moduleTypes)
        {
            if (_modules.Contains(type))
            {
                throw new InvalidOperationException($"spell handler module {type.FullName} was already applied");
            }

            ISpellHandlerModule module = SpellHandlerModules.Create(type);
            var effectsBefore = new Dictionary<SpellEffectName, SpellEffectHandler>(EffectHandlers);
            var aurasBefore = new Dictionary<AuraType, AuraHandler>(AuraHandlers);
            module.Register(this);
            foreach ((SpellEffectName effect, SpellEffectHandler handler) in effectsBefore)
            {
                if (!ReferenceEquals(EffectHandlers[effect], handler))
                {
                    throw new InvalidOperationException($"spell handler module {type.FullName} replaced the handler of effect {effect}");
                }
            }

            foreach ((AuraType aura, AuraHandler handler) in aurasBefore)
            {
                if (!ReferenceEquals(AuraHandlers[aura], handler))
                {
                    throw new InvalidOperationException($"spell handler module {type.FullName} replaced the handler of aura {aura}");
                }
            }

            _modules.Add(type);
        }
    }
}

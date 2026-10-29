using ArcaneCore.Game.Spells;

namespace ArcaneCore.TestSupport;

/// <summary>
/// An <see cref="ISpellHandlerModule"/> that lives outside the Game assembly, standing in for a content pack's handler module: it installs a handler
/// for <see cref="ProbeEffect"/> (SPELL_EFFECT_127, which vmangos leaves unused and the server has no handler for). The pack-aware module discovery
/// (<see cref="SpellHandlerModules.Discover(ArcaneCore.Kernel.Reflection.CodeAssemblies)"/>) finds it by the assembly.
/// </summary>
public sealed class PackProbeSpellModule : ISpellHandlerModule
{
    public const SpellEffectName ProbeEffect = SpellEffectName.Unk127;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(ProbeEffect, static _ => { });
    }
}

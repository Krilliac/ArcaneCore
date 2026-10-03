using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Installs the form and mount cast checks (discovered <see cref="IWorldFeature"/>): mount spells refuse a disallowed form,
/// a cast while mounted dismounts first, and shapeshift-cancelled auras refuse shifted targets (see
/// <see cref="MountFormCastCheck"/>, <see cref="DismountOnCastCheck"/>, <see cref="ShiftedOrMountedTargetCastCheck"/>).
/// </summary>
public sealed class FormMountCheckFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellSystem spells = services.GetRequiredService<SpellFeature>().System;
        spells.RegisterCastCheck(new MountFormCastCheck());
        spells.RegisterCastCheck(new DismountOnCastCheck());
        spells.RegisterCastCheck(new ShiftedOrMountedTargetCastCheck(() => CombatEnvironment.For(world).ShapeshiftForms));
    }
}

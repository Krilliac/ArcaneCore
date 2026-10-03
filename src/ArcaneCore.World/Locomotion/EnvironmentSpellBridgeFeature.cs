using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// Gives the liquid rules (entering and leaving swimmable water removes the auras and channels that need land or water, vmangos
/// SetEnvironmentFlags, Player.cpp:849-854) their spell system: the daemon's <see cref="SpellFeature.System"/>.
/// </summary>
public sealed class EnvironmentSpellBridgeFeature(SpellFeature spells) : IWorldFeature, IEnvironmentSpellBridge
{
    public void Attach(WorldRuntime world) => LocomotionEnvironment.RegisterSpellBridge(world, this);

    public void RemoveAurasWithInterruptFlags(Unit unit, SpellAuraInterruptFlags flags) => spells.System.RemoveAurasWithInterruptFlags(unit, flags);

    public void InterruptChannelsWithFlags(Unit unit, SpellAuraInterruptFlags flags) => spells.System.InterruptChannelsWithFlags(unit, flags);
}

using ArcaneCore.Game.Locomotion;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_MOUNTED (78), after vmangos <c>Aura::HandleAuraMounted</c> (SpellAuras.cpp:2251-2276): the misc value is the
/// creature entry of the mount, whose display becomes the rider's mount display (<see cref="MountService"/>); removing the aura
/// dismounts. A creature entry that is not in the creature data is logged by the source's absence and leaves the unit
/// unmounted (vmangos logs a database error and returns). The mounted speed auras (32, 130, 172) are
/// <see cref="SpeedAuras"/>: they apply only while the mount display is set, so a mount spell lists the mounted aura first.
/// </summary>
public sealed class MountAura : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.Mounted, new AuraHandler(Apply, null));
    }

    private static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (!apply)
        {
            MountService.Unmount(system, holder.Target);
            return;
        }

        uint? displayId = LocomotionEnvironment.MountDisplaysFor(holder.Target.Map)?.FindMountDisplay((uint)aura.MiscValue);
        if (displayId is not { } display || display == 0)
        {
            // "AuraMounted: `creature_template`='%u' not found in database (only need its display_id)"
            return;
        }

        MountService.Mount(system, holder.Target, display);
    }
}

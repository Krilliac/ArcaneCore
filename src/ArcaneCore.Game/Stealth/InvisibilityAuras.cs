using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// SPELL_AURA_MOD_INVISIBILITY (18) and SPELL_AURA_MOD_INVISIBILITY_DETECTION (19), after vmangos Aura::HandleInvisibility and HandleInvisibilityDetect
/// (SpellAuras.cpp:3708-3780). The type masks are read from the auras by <see cref="InvisibilityVisibilityRule"/>; the handlers do what is left:
/// <list type="bullet">
/// <item>18, apply: the auras that break on invisibility (<see cref="AuraInterruptMask.StealthInvisibility"/>, flag carriers) go, a player gets the
/// invisibility glow (PLAYER_FIELD_BYTES_2 byte 1, 0x40) and everyone in range re-evaluates the unit; remove: the same re-evaluation, and the glow goes with the
/// last invisibility aura;</item>
/// <item>19: the viewer re-evaluates what it sees (vmangos <c>UpdateVisibilityForOwner</c>).</item>
/// </list>
/// The visibility veto itself is an <see cref="InvisibilityVisibilityRule"/> attached to the maps by the world feature.
/// </summary>
public sealed class InvisibilityAuras : ISpellHandlerModule
{
    /// <summary>vmangos PLAYER_FIELD_BYTE2_INVISIBILITY_GLOW (Player.h:390).</summary>
    public const byte GlowFlag = 0x40;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModInvisibility, new AuraHandler(OnInvisibility, null));
        system.RegisterAura(AuraType.ModInvisibilityDetection, new AuraHandler(OnDetection, null));
    }

    private static void OnInvisibility(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (apply)
        {
            system.RemoveAurasWithInterruptFlags(target, AuraInterruptMask.StealthInvisibility);
            if (target is Player)
            {
                target.SetByte(UpdateFields.PlayerFieldBytes2, 1, (byte)(target.GetByte(UpdateFields.PlayerFieldBytes2, 1) | GlowFlag));
            }
        }
        else if (target is Player && !system.HasAuraType(target, AuraType.ModInvisibility))
        {
            target.SetByte(UpdateFields.PlayerFieldBytes2, 1, (byte)(target.GetByte(UpdateFields.PlayerFieldBytes2, 1) & ~GlowFlag));
        }

        target.Map?.RefreshVisibility(target);
    }

    private static void OnDetection(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        _ = system;
        _ = apply;
        if (holder.Target is Player viewer)
        {
            viewer.Map?.RefreshVisibility(viewer);
        }
    }
}

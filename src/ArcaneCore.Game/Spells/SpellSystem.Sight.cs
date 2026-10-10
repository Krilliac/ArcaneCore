using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells.PersistentAreaAuras;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The camera and visibility spells of vmangos SpellEffects.cpp / SpellAuras.cpp:
/// <list type="bullet">
/// <item>SPELL_EFFECT_ADD_FARSIGHT (72; Spell::EffectAddFarsight): Far Sight and Eagle Eye put a DYNAMIC_OBJECT_FARSIGHT_FOCUS object (radius 0) at the
/// destination and move the caster's camera to it (Camera::SetView, PLAYER_FARSIGHT). The point goes with its duration or its channel, and the view
/// comes back.</item>
/// <item>SPELL_AURA_BIND_SIGHT (Aura::HandleBindSight): Mind Vision moves the caster's camera to the aura's target, and back on removal.</item>
/// <item>SPELL_AURA_UNTRACKABLE (Aura::HandleAuraUntrackable): UNIT_VIS_FLAGS_UNTRACKABLE in UNIT_FIELD_BYTES_1 byte 3, which tracking honours
/// on the client.</item>
/// <item>SPELL_AURA_MOD_UNATTACKABLE (Aura::HandleModUnattackable): UNIT_FLAG_NON_ATTACKABLE_2; the real apply also stops the target's combat and
/// removes its AURA_INTERRUPT_INVULNERABILITY_BUFF_CANCELS auras.</item>
/// </list>
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>vmangos UNIT_VIS_FLAGS_UNTRACKABLE (UnitDefines.h:149).</summary>
    public const byte UntrackableVisFlag = 0x04;

    /// <summary>vmangos AURA_INTERRUPT_INVULNERABILITY_BUFF_CANCELS (SpellDefines.h:598).</summary>
    private const uint InvulnerabilityBuffCancels = 0x00200000;

    private readonly ConditionalWeakTable<SpellCast, object> _farSightCasts = new();

    private void InstallSight()
    {
        RegisterEffect(SpellEffectName.AddFarsight, context =>
        {
            if (context.Caster is not Player player || player.Map is not { } map || _farSightCasts.TryGetValue(context.Cast, out _))
            {
                return;
            }

            _farSightCasts.Add(context.Cast, new object());
            (float x, float y, float z) = context.Cast.Targets.HasDest ? context.Cast.Targets.Dest : (context.Target.X, context.Target.Y, context.Target.Z);
            int duration = context.Cast.Duration;
            var focus = DynamicObject.Create(player, context.Spell, context.EffectIndex, x, y, z, duration, 0, positive: true, type: DynamicObject.FarSightFocus);
            map.AddObject(focus, active: false, isNewObject: true);
            _dynamicObjects.Add(focus);
            if (context.Spell.IsChanneled && context.Cast.State == SpellCastState.Casting)
            {
                player.SetUInt64(UpdateFields.UnitFieldChannelObject, focus.Guid.Value);
            }

            UnitControl.SightWatchers.AddOrUpdate(focus, player);
            CharmService.SetView(player, focus);
        });
    }

    private void InstallSightAuras()
    {
        RegisterAura(AuraType.BindSight, new AuraHandler(static (_, holder, _, apply) =>
        {
            if (holder.Target.Map?.FindPlayer(holder.CasterGuid) is not { } viewer)
            {
                UnitControl.SightWatchers.Remove(holder.Target);
                return;
            }

            if (apply)
            {
                UnitControl.SightWatchers.AddOrUpdate(holder.Target, viewer);
                CharmService.SetView(viewer, holder.Target);
            }
            else if (viewer.GetUInt64(UpdateFields.PlayerFarsight) == holder.Target.Guid.Value)
            {
                UnitControl.SightWatchers.Remove(holder.Target);
                CharmService.SetView(viewer, null);
            }
        }, null));

        RegisterAura(AuraType.Untrackable, new AuraHandler(static (_, holder, _, apply) =>
        {
            byte flags = holder.Target.GetByte(UpdateFields.UnitFieldBytes1, 3);
            holder.Target.SetByte(UpdateFields.UnitFieldBytes1, 3, apply ? (byte)(flags | UntrackableVisFlag) : (byte)(flags & ~UntrackableVisFlag));
        }, null));

        RegisterAura(AuraType.ModUnattackable, new AuraHandler(static (system, holder, _, apply) =>
        {
            Unit target = holder.Target;
            if (apply)
            {
                target.Map?.Combat.CombatStop(target);
                system.RemoveAurasWithInterruptFlags(target, (SpellAuraInterruptFlags)InvulnerabilityBuffCancels);
                target.UnitFlags |= UnitFlags.NonAttackable2;
            }
            else
            {
                target.UnitFlags &= ~UnitFlags.NonAttackable2;
            }
        }, null));
    }
}

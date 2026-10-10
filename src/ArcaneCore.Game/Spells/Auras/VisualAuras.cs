using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_MOD_SCALE (61) and the tracking auras MOD_TRACK_CREATURES (44) and MOD_TRACK_RESOURCES (45),
/// after vmangos <c>Aura::HandleAuraModScale</c> (SpellAuras.cpp:2948), <c>HandleAuraTrackCreatures</c>
/// (:2909) and <c>HandleAuraTrackResources</c> (:2923). All three write update fields only.
/// </summary>
public sealed class VisualAuras : ISpellHandlerModule
{
    /// <summary>vmangos SPELL_ATTR_ALLOW_WHILE_MOUNTED (SpellDefines.h:854).</summary>
    private const uint AttributeAllowWhileMounted = 0x01000000;

    /// <summary>vmangos SPELL_ATTR_EX_NO_AUTOCAST_AI (SpellDefines.h:887).</summary>
    private const uint AttributeExNoAutocastAi = 0x00020000;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModScale, new AuraHandler(ApplyScale, null));
        system.RegisterAura(AuraType.TrackCreatures, new AuraHandler(
            (s, h, a, apply) => ApplyTracking(s, h, a, apply, UpdateFields.PlayerTrackCreatures), null));
        system.RegisterAura(AuraType.TrackResources, new AuraHandler(
            (s, h, a, apply) => ApplyTracking(s, h, a, apply, UpdateFields.PlayerTrackResources), null));
    }

    /// <summary>
    /// ApplyPercentModFloatValue(OBJECT_FIELD_SCALE_X, amount, apply) (Object.h:248): the scale is multiplied
    /// by (100 + amount) / 100 and divided by it again on removal; -100 counts as -99.9 so it never reaches zero.
    /// Unit::UpdateModelData (Unit.cpp:9364) then sets bounding radius and combat reach to
    /// (scale / native scale) times the model's values, i.e. they follow the scale by the same factor.
    /// </summary>
    private static void ApplyScale(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        float percent = aura.Amount;
        if (percent == -100.0f)
        {
            percent = -99.9f;
        }

        float factor = apply ? (100.0f + percent) / 100.0f : 100.0f / (100.0f + percent);
        Unit target = holder.Target;
        foreach (int field in new[] { UpdateFields.ObjectFieldScaleX, UpdateFields.UnitFieldBoundingradius, UpdateFields.UnitFieldCombatreach })
        {
            target.SetFloat(field, target.GetFloat(field) * factor);
        }
        if (system.DisplayModelResolver is not null) system.UpdateDisplayModel(target);
    }

    internal static float ActiveScaleFactor(SpellSystem system, Unit target)
    {
        float factor = 1.0f;
        foreach (SpellAuraHolder holder in system.GetAuras(target))
        {
            if (holder.IsRemoved) continue;
            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is { Type: AuraType.ModScale })
                {
                    float percent = aura.Amount == -100 ? -99.9f : aura.Amount;
                    factor *= (100.0f + percent) / 100.0f;
                }
            }
        }
        return factor;
    }

    /// <summary>
    /// Players only: the aura sets or clears bit (misc value - 1) of PLAYER_TRACK_CREATURES / _RESOURCES. On
    /// apply a tracking spell first removes every other tracking spell of the target, from any caster
    /// (RemoveNoStackAurasDueToAuraHolder with SPELL_TRACKER, Unit.cpp:3355 and
    /// IsSingleFromSpellSpecificPerTarget, SpellEntry.h:140-160). SPELL_TRACKER is a spell with a tracking aura
    /// and EX_NO_AUTOCAST_AI or ALLOW_WHILE_MOUNTED (SpellEntry.cpp:152-157: "exclude Well Fed, some other always
    /// allowed cases"). The TRACK_STEALTHED variant is a later build's aura and no 1.12.1 spell uses it.
    /// </summary>
    private static void ApplyTracking(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply, int field)
    {
        if (holder.Target is not Player player || aura.MiscValue is < 1 or > 32)
        {
            return;
        }

        uint bit = 1u << (aura.MiscValue - 1);
        if (!apply)
        {
            player.SetUInt32(field, player.GetUInt32(field) & ~bit);
            return;
        }

        if (IsTracker(holder.Spell))
        {
            foreach (SpellAuraHolder other in system.GetAuras(player).ToArray())
            {
                if (!ReferenceEquals(other, holder) && !other.IsRemoved && IsTracker(other.Spell))
                {
                    system.RemoveAurasByCaster(player, other.Spell.Id, other.CasterGuid);
                }
            }
        }

        player.SetUInt32(field, player.GetUInt32(field) | bit);
    }

    private static bool IsTracker(SpellInfo spell)
        => (spell.HasAura(AuraType.TrackCreatures) || spell.HasAura(AuraType.TrackResources))
            && (((uint)spell.AttributesEx & AttributeExNoAutocastAi) != 0 || ((uint)spell.Attributes & AttributeAllowWhileMounted) != 0);
}

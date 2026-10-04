using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Mount and mounted-caster checks from vmangos Spell::CheckCast (Spell.cpp:5357-5388,
/// 5674-5692, 6373-6392). The mount's riding and level requirements belong to the
/// item or trainer data; this check does not synthesize requirements from its speed.
/// </summary>
public sealed class MountCastCheck : ISpellCastCheck
{
    private const SpellAttributes OnlyIndoors = (SpellAttributes)0x00004000;

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => SpellCastCheckOrder.Equipment - 50;

    /// <summary>vmangos MapEntry::IsMountAllowed (Maps/Map.h:95-100; map IDs SharedDefines.h:1626-1644).</summary>
    public static bool IsMountAllowed(MapTemplate map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return !map.IsDungeon || map.Entry is 209 or 269 or 309 or 509;
    }

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        Unit caster = context.Caster;
        bool mountSpell = context.Spell.Effects.Any(e => e.Effect == SpellEffectName.ApplyAura && e.AuraType == AuraType.Mounted);

        // These caster checks precede vmangos' mounted-caster check (Spell.cpp:5357-5388,
        // 5679). A rejected indoor or water cast does not dismount an existing rider.
        if (mountSpell && !context.Triggered && context.Spell.AuraInterruptFlags.HasFlag(SpellAuraInterruptFlags.UnderWaterCancels)
            && (caster.Movement.HasFlag(MovementFlags.Swimming)
                || caster is Player inWater && (inWater.Locomotion.Environment & (EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid))
                    == (EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid)))
        {
            return SpellCastResult.OnlyAbovewater;
        }

        if (mountSpell && caster.Map is { } outdoorMap && caster is Player { IsGameMaster: false }
            && (context.Spell.HasAttribute(SpellAttributes.OnlyOutdoors) || context.Spell.HasAttribute(OnlyIndoors)))
        {
            bool outdoors = outdoorMap.Collision.IsOutdoors(caster.X, caster.Y, caster.Z);
            if (context.Spell.HasAttribute(SpellAttributes.OnlyOutdoors) && !outdoors)
            {
                return SpellCastResult.OnlyOutdoors;
            }

            if (context.Spell.HasAttribute(OnlyIndoors) && outdoors)
            {
                return SpellCastResult.OnlyIndoors;
            }
        }

        // A flight mount is a real mount display but cannot be replaced by a player cast.
        // vmangos Spell.cpp:5679-5684 checks this before the per-effect mount rules.
        if (!context.Triggered && caster is Player && MountService.IsMounted(caster)
            && !context.Spell.IsPassive && !context.Spell.HasAttribute(SpellAttributes.AllowWhileMounted))
        {
            if ((caster.UnitFlags & UnitFlags.TaxiFlight) != 0)
            {
                return SpellCastResult.NotOnTaxi;
            }

            MountService.Unmount(context.System, caster);
            context.System.RemoveAurasByType(caster, AuraType.Mounted);
        }

        if (!mountSpell)
        {
            return SpellCastResult.CastOk;
        }

        if (caster is Player player && (player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            return SpellCastResult.NotOnTaxi;
        }

        if (caster.Map is { } map)
        {
            // vmangos Spell.cpp:6382-6387. A required-area override needs spell-area
            // data, which SpellInfo does not carry; the ordinary map and area rules apply.
            if (!context.Triggered && map.Template is { } template && !IsMountAllowed(template))
            {
                return SpellCastResult.NoMountsAllowed;
            }

            if (map.GetZoneAndAreaId(caster.X, caster.Y, caster.Z).AreaId == 35)
            {
                return SpellCastResult.NoMountsAllowed;
            }
        }

        return FormInterlocks.IsInDisallowedMountForm(caster) ? SpellCastResult.NotShapeshift : SpellCastResult.CastOk;
    }
}

using System.Collections.Frozen;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Crafting;

/// <summary>
/// First Aid bandages (crafting lane). The 19 bandage spells (Spell.dbc: PERIODIC_HEAL channel, spell mechanic 16 = BANDAGE, channel interrupt
/// flags 0x3C0E: damage, movement, other action) are ordinary data; the one piece of code retail has for them is the vmangos spell script
/// <c>FirstAidScript</c> (scripts/spells/spell_item.cpp:577-594): after a bandage hits its unit target, the caster casts
/// <see cref="RecentlyBandaged"/> (11196: MECHANIC_IMMUNITY to the BANDAGE mechanic, 60 s) triggered at that target with the same cast item.
/// <para>
/// <c>OnAfterHit</c> runs per target hit (Spell.cpp:1541-1542), after the miss check and before any tick: the immunity starts when the channel starts,
/// it stays when the channel is interrupted, and it is not applied to a missed or immune hit. The second bandage on the same target is therefore
/// an immune hit (<c>Unit::SpellHitResult</c> returns SPELL_MISS_IMMUNE for a victim immune to the spell, Objects/SpellCaster.cpp:175-178: the client
/// patch 1.7.0 note says even physical immunity no longer blocks Recently Bandaged) and does not extend the immunity.
/// </para>
/// </summary>
public sealed class FirstAidObserver(SpellSystem system) : ISpellCastObserver
{
    /// <summary>vmangos <c>SPELL_RECENTLY_BANDAGED</c>.</summary>
    public const uint RecentlyBandaged = 11196;

    /// <summary>The bandage spell ids of the script registration (vmangos spell_item.cpp:576: 746, 1159, 3267, 3268, 7926, 7927, 10838, 10839, 18608, 18610, 20803, 23567, 23568, 23569, 23696, 24412, 24413, 24414, 30020).</summary>
    public static FrozenSet<uint> BandageSpells { get; } = new uint[]
    {
        746, 1159, 3267, 3268, 7926, 7927, 10838, 10839, 18608, 18610, 20803, 23567, 23568, 23569, 23696, 24412, 24413, 24414, 30020,
    }.ToFrozenSet();

    /// <summary>Register the observer on <paramref name="system"/>; a second call throws.</summary>
    public static void Install(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.Observers.OfType<FirstAidObserver>().Any())
        {
            throw new InvalidOperationException("the first aid observer is already installed");
        }

        system.RegisterObserver(new FirstAidObserver(system));
    }

    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (outcome.Miss != SpellMissInfo.None || !BandageSpells.Contains(cast.Spell.Id) || !IsUnitTarget(cast, outcome.Target))
        {
            return;
        }

        var targets = SpellCastTargets.ForUnit(outcome.Target.Guid);
        if (cast.CastItem is { } item && cast.Caster is Player player)
        {
            system.CastItemSpell(player, item, RecentlyBandaged, targets, triggered: true);
        }
        else
        {
            system.CastSpell(cast.Caster, RecentlyBandaged, targets, triggered: true);
        }
    }

    /// <summary>vmangos <c>Spell::GetUnitTarget</c>: the explicit unit target of the cast (a self cast aims at the caster).</summary>
    private static bool IsUnitTarget(SpellCast cast, Unit target)
        => (cast.Targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0
            ? cast.Targets.Unit == target.Guid
            : cast.Targets.Mask == SpellCastTargetFlags.Self && ReferenceEquals(cast.Caster, target);
}

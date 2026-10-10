using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules.Immunity;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>The emperors' script immunities: Vek'lor resists physical damage; Vek'nilash resists spell schools.</summary>
public sealed class TwinEmperorSpellModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.CreatureImmunities = new TwinImmunities(system.CreatureImmunities);
        system.RegisterSpellTargetSelector(7393, SpellImplicitTarget.UnitScriptNearCaster,
            (_, cast, _, explicitTarget) =>
            {
                Unit? target = explicitTarget ?? cast.Caster.Map?.FindObject(cast.Targets.Unit) as Unit;
                return target is Creature { Entry: 15275 or 15276 } twin
                    && ReferenceEquals(twin.Map, cast.Caster.Map)
                    && twin.Map?.FindUpdater<InstanceData>() is TempleOfAhnQirajInstance
                    ? [(twin, 1f)] : [];
            });
    }

    private sealed class TwinImmunities(ICreatureImmunityProvider? previous) : ICreatureImmunityProvider
    {
        public uint MechanicImmuneMask(Unit unit) => previous?.MechanicImmuneMask(unit) ?? 0;

        public uint SchoolImmuneMask(Unit unit)
        {
            uint inherited = previous?.SchoolImmuneMask(unit) ?? 0;
            if (unit is not Creature creature || creature.Map?.FindUpdater<InstanceData>() is not TempleOfAhnQirajInstance)
                return inherited;
            return inherited | (creature.Entry switch
            {
                15276 => 1u,    // SPELL_SCHOOL_MASK_NORMAL, Vek'lor
                15275 => 0x7Eu, // all six nonphysical schools, Vek'nilash
                _ => 0u,
            });
        }
    }
}

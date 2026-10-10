using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets.Control;

public sealed partial class CharmService
{
    /// <summary>
    /// SPELL_EFFECT_SUMMON_POSSESSED (73; vmangos Spell::EffectSummonPossessed → Player::SummonPossessedMinion, Player.cpp): a player without a
    /// charm summons the effect's creature at the destination (or beside itself), for the spell's duration, at its own level and created by the
    /// spell, and possesses it at once (<see cref="ModPossess"/>: faction, charmer and possessor, camera, mover, possess bar). When the minion
    /// goes, the map registry gives the player its control and view back. The summon counter limit and the pet's temporary unsummon
    /// (UnsummonPetTemporaryIfAny) are not modelled.
    /// </summary>
    private void EffectSummonPossessed(SpellEffectContext context)
    {
        if (context.Caster is not Player caster || !caster.CharmGuid.IsEmpty || caster.Map is not { } map || _systems(map) is not { } creatures
            || _summonedPossessedCasts.TryGetValue(context.Cast, out _))
        {
            return;
        }

        _summonedPossessedCasts.Add(context.Cast, new object());
        (float x, float y, float z) = context.Cast.Targets.HasDest ? context.Cast.Targets.Dest : (0f, 0f, 0f);
        if (x == 0 && y == 0 && z == 0)
        {
            (x, y, z) = (caster.X, caster.Y, caster.Z);
        }

        uint lifetime = context.Cast.Duration > 0 ? (uint)context.Cast.Duration : 0;
        if (creatures.SummonPossessedMinion((uint)context.Effect.MiscValue, x, y, z, caster.Orientation, lifetime) is not { } minion)
        {
            return;
        }

        minion.Level = caster.Level;
        minion.SetUInt32(UpdateFields.UnitCreatedBySpell, context.Spell.Id);
        ModPossess(caster, minion, apply: true, AuraRemoveMode.Default, context.Spell);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpellCast, object> _summonedPossessedCasts = new();
}

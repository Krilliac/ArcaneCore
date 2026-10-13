using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets.Control;

public sealed partial class CharmService
{
    /// <summary>
    /// SPELL_EFFECT_SUMMON_POSSESSED (73; vmangos Spell::EffectSummonPossessed → Player::SummonPossessedMinion, Player.cpp): a player without a
    /// charm summons the effect's creature at the destination (or beside itself), for the spell's duration, at its own level and created by the
    /// spell, and possesses it at once (<see cref="ModPossess"/>: faction, charmer and possessor, camera, mover, possess bar). When the minion
    /// goes, the map registry gives the player its control and view back. A player at its creature summon limit summons nothing (vmangos
    /// GetCreatureSummonCount() >= GetCreatureSummonLimit(), Player.cpp:17528-17539; see <see cref="CreatureMapSystem.CreatureSummonLimit"/>).
    /// The player's pet is put away for the minion's life (UnsummonPetTemporaryIfAny, Player.cpp:17570) and comes back when the possession
    /// ends (Player::UnsummonPossessedMinion → ResummonPetTemporaryUnSummonedIfAny, Player.cpp:17594).
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
        if (creatures.SummonPossessedMinion((uint)context.Effect.MiscValue, x, y, z, caster.Orientation, lifetime, caster.Guid) is not { } minion)
        {
            return;
        }

        minion.Level = caster.Level;
        minion.SetUInt32(UpdateFields.UnitCreatedBySpell, context.Spell.Id);
        ModPossess(caster, minion, apply: true, AuraRemoveMode.Default, context.Spell);
        if (Summons is { } summons && summons.UnsummonPetTemporarily(caster))
        {
            _petAwayForMinion[caster.Guid] = minion.Guid;
        }
    }

    /// <summary>The players whose pet <see cref="EffectSummonPossessed"/> put away, with the minion they possess.</summary>
    private readonly Dictionary<ObjectGuid, ObjectGuid> _petAwayForMinion = [];

    /// <summary>
    /// Player::UnsummonPossessedMinion's ResummonPetTemporaryUnSummonedIfAny: the possession of <paramref name="controlled"/> ended, so the
    /// pet put away for that minion comes back (no-op for any other possession or charm).
    /// </summary>
    private void ResummonAfterPossessedMinion(Player player, Unit controlled)
    {
        if (_petAwayForMinion.TryGetValue(player.Guid, out ObjectGuid minion) && minion == controlled.Guid)
        {
            _petAwayForMinion.Remove(player.Guid);
            Summons?.ResummonTemporarilyUnsummonedPet(player);
        }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpellCast, object> _summonedPossessedCasts = new();
}

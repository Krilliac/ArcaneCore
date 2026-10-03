using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// End the running channel of <paramref name="caster"/> without an interrupt (vmangos <c>Unit::FinishSpell(CURRENT_CHANNELED_SPELL)</c>
    /// as the fishing code uses it): the channel stops (zero channel update for a player, channel fields cleared), the cast
    /// finishes as completed and the finish observers run. Returns false when the unit is not channelling.
    /// </summary>
    public bool FinishChannel(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (GetState(caster.Guid)?.CurrentCast is not { State: SpellCastState.Casting } cast)
        {
            return false;
        }

        EndChannel(cast, interrupted: false);
        Finish(cast);
        return true;
    }
}
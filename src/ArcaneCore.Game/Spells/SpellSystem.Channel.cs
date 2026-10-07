using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// End the running channel of <paramref name="caster"/> without an interrupt (vmangos <c>Unit::FinishSpell(CURRENT_CHANNELED_SPELL)</c>
    /// as the fishing code uses it): the channel stops, the cast finishes as completed and the finish observers run. It is a normal
    /// end, so the zero channel update and the cleared channel fields follow 1000 ms later (vmangos SendChannelUpdate(0) with
    /// interrupted = false, ChannelResetEvent). Returns false when the unit is not channelling.
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
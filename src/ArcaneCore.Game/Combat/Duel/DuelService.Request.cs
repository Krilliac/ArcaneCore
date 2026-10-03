using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class DuelService
{
    /// <summary>
    /// The tail of vmangos Spell::EffectDuel once the flag object exists (SpellEffects.cpp:4732-4760): SMSG_DUEL_REQUESTED (flag guid, then
    /// challenger guid) to both players, the two crossed <see cref="DuelInfo"/> halves (the target's names the challenger as initiator and as
    /// opponent, exactly as vmangos builds <c>duel2</c>), and PLAYER_DUEL_ARBITER on both. The caller has validated the request.
    /// </summary>
    public void Begin(Player challenger, Player target, GameObject flag)
    {
        ArgumentNullException.ThrowIfNull(challenger);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(flag);
        byte[] requested = DuelPackets.Requested(flag.Guid.Value, challenger.Guid.Value);
        challenger.Session.Send(WorldOpcode.SmsgDuelRequested, requested);
        target.Session.Send(WorldOpcode.SmsgDuelRequested, requested);

        challenger.Duel = new DuelInfo(challenger, target);
        target.Duel = new DuelInfo(challenger, challenger);
        challenger.DuelArbiter = flag.Guid.Value;
        target.DuelArbiter = flag.Guid.Value;
    }
}

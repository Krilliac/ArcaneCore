using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// EVENT_T_RECEIVE_EMOTE (22): EmoteId, ConditionId (cmangos CreatureEventAI.h:64 and the receive_emote union, :739-744; vmangos lists the
/// same type with EmoteId, Condition, CondValue1, CondValue2 in its older EventAI). Readied by <see cref="EventAiEngine.ReceiveEmote"/> when
/// a player aims the text emote <c>EmoteId</c> (EmotesText.dbc) at the creature (cmangos CreatureEventAI::ReceiveEmote, :1829-1842); the
/// player is the invoker. A row with a condition id fires only when the conditions table is satisfied for the invoker's player
/// (CheckEvent, :467-472: GetBeneficiaryPlayer, so a pet's owner); without a conditions table such a row never fires. It is repeatable
/// (IsRepeatableEvent) and not timer based. classic-db z2815: 90 rows (salutes, waves, bows, Elly Langston's dances).
/// </summary>
public sealed class ReceiveEmoteEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.ReceiveEmote;

    public override EventAiTrigger Trigger => EventAiTrigger.ReceiveEmote;

    public override bool MatchesEmote(CreatureAiEvent row, uint textEmote) => (uint)row.Param1 == textEmote;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker)
    {
        uint conditionId = (uint)holder.Param(1);
        if (conditionId == 0)
        {
            return true;
        }

        if (invoker?.GetCharmerOrOwnerPlayerOrSelf() is not { } player || context.System?.AiServices.Conditions is not { } conditions)
        {
            return false;
        }

        Creature me = context.Me;
        var source = new NpcInfo(me.Guid, me.Entry, me.Spawn?.Guid ?? me.Guid.Low, (NpcFlags)me.NpcFlags, me.MapId, me.X, me.Y, me.Z,
            me.BoundingRadius, me.IsAlive, IsHostile: false, me.Combat.IsInCombat, (me.UnitFlags & UnitFlags.NotSelectable) != 0,
            me.DefaultGossipMenuId);
        return conditions.IsSatisfied(conditionId, player, source);
    }
}

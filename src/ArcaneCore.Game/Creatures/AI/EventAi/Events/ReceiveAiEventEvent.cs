using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// EVENT_T_RECEIVE_AI_EVENT (30): AIEventType, Sender-Entry (0 for any) (cmangos CreatureEventAI.h:76 and the receiveAIEvent union).
/// Readied by <see cref="EventAiEngine.ReceiveAiEvent"/> when an AI event of the type arrives from a sender of the entry (cmangos
/// CreatureEventAI::ReceiveAIEvent, AI/EventAI/CreatureEventAI.cpp:1563-1575); CheckEvent has no further condition for it (:508-509). The
/// invoker is the unit that caused the event, the sender the unit that sent it. Not timer based; repeatable.
/// </summary>
public sealed class ReceiveAiEventEvent : EventAiEventHandler
{
    public override byte EventType => (byte)EventAiEventType.ReceiveAiEvent;

    public override EventAiTrigger Trigger => EventAiTrigger.ReceiveAiEvent;

    public override bool MatchesAiEvent(CreatureAiEvent row, uint eventType, Unit sender)
        => (uint)row.Param1 == eventType && (row.Param2 == 0 || (uint)row.Param2 == sender.GetUInt32(UpdateFields.ObjectFieldEntry));

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;
}

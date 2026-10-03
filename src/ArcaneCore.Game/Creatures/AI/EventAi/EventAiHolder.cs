using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// One live EventAI row of one creature (cmangos <c>CreatureEventAIHolder</c>, CreatureEventAI.h:857-870): the row
/// plus its repeat/initial timer, whether it is armed, whether it is queued for execution and the target the
/// event filled in for its actions.
/// </summary>
public sealed class EventAiHolder(CreatureAiEvent row, EventAiEventHandler? handler)
{
    public CreatureAiEvent Event { get; } = row;

    /// <summary>The handler of the row's event type, or null when the type is unsupported.</summary>
    public EventAiEventHandler? Handler { get; } = handler;

    public bool Enabled { get; set; } = true;

    /// <summary>Queued for execution at the current depth (cmangos inProgress).</summary>
    public bool InProgress { get; internal set; }

    /// <summary>Milliseconds until the event may trigger; 0 = ready.</summary>
    public uint TimerMs { get; set; }

    /// <summary>The target an event filled in for its actions (cmangos eventTarget).</summary>
    public Unit? EventTarget { get; set; }

    /// <summary>
    /// cmangos CreatureEventAIHolder::UpdateRepeatTimer (CreatureEventAI.cpp:35-48): equal bounds set the timer
    /// to them, a larger maximum draws uniformly, a smaller maximum disables the event (repeating disabled).
    /// </summary>
    public bool UpdateRepeatTimer(EventAiContext context, uint repeatMin, uint repeatMax)
    {
        if (repeatMin == repeatMax)
        {
            TimerMs = repeatMin;
        }
        else if (repeatMax > repeatMin)
        {
            TimerMs = (uint)context.Random((int)Math.Min(repeatMin, int.MaxValue), (int)Math.Min(repeatMax, int.MaxValue));
        }
        else
        {
            Enabled = false;
            return false;
        }

        return true;
    }

    /// <summary>The row's raw parameter <paramref name="index"/> (0-5) as the unsigned value cmangos stores.</summary>
    public uint Param(int index) => unchecked((uint)(index switch
    {
        0 => Event.Param1,
        1 => Event.Param2,
        2 => Event.Param3,
        3 => Event.Param4,
        4 => Event.Param5,
        5 => Event.Param6,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    }));
}

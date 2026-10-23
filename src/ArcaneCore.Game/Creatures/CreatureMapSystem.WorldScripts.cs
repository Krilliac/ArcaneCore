using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// cmangos <c>AIEventType</c> (AI/BaseAI/AIDefines.h:23-55): the AI events a script sends to a creature's AI. The EventAI custom events A-F are
/// what EVENT_T_RECEIVE_AI_EVENT (30) rows of a database creature listen for; the 1000+ custom events are for C# scripts.
/// </summary>
public static class AiEventTypes
{
    public const uint JustDied = 0, CriticalHealth = 1, LostHealth = 2, LostSomeHealth = 3, GotFullHealth = 4;
    public const uint CustomEventAiA = 5, CustomEventAiB = 6, GotCced = 7, CustomEventAiC = 8, CustomEventAiD = 9, CustomEventAiE = 10, CustomEventAiF = 11;
    public const uint CustomA = 1000, CustomB = 1001, CustomC = 1002, CustomD = 1003, CustomE = 1004, CustomF = 1005;
}

/// <summary>The creature-side helpers open-world (area trigger and world map) scripts share.</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// ScriptDev GetClosestCreatureWithEntry(source, entry, range, onlyAlive, onlyDead): the nearest creature of <paramref name="entry"/> on this
    /// map within <paramref name="range"/> yards (3D) of <paramref name="center"/>; a corpse counts unless <paramref name="aliveOnly"/>.
    /// </summary>
    public Creature? ClosestOfEntry(WorldObject center, uint entry, float range, bool aliveOnly)
    {
        ArgumentNullException.ThrowIfNull(center);
        Creature? best = null;
        float bestSq = range * range;
        foreach (Creature c in _creatures.Values)
        {
            if (c.Template.Entry != entry || c.DeathState == CreatureDeathState.Dead || (aliveOnly && !c.IsAlive) || ReferenceEquals(c, center))
            {
                continue;
            }

            float sq = WorldGeometry.DistanceSq3D(center, c);
            if (sq <= bestSq)
            {
                best = c;
                bestSq = sq;
            }
        }

        return best;
    }

    /// <summary>
    /// cmangos UnitAI::SendAIEvent(eventType, invoker, receiver) called on the receiver's own AI (AI/BaseAI/UnitAI.cpp): the receiver is both
    /// sender and receiver, <paramref name="invoker"/> the unit the event is about (TARGET_T_ACTION_INVOKER). A dead creature or one without
    /// an AI hears nothing. False when <paramref name="receiver"/> is not on this map.
    /// </summary>
    public bool SendAiEventToSelf(Creature receiver, uint eventType, Unit? invoker, uint miscValue = 0)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (!_creatures.ContainsKey(receiver.Guid))
        {
            return false;
        }

        receiver.ReceiveAiEvent(eventType, receiver, invoker, miscValue);
        return true;
    }

    /// <summary>
    /// A player's or world map script's SummonCreature(..., TEMPSPAWN_DEAD_DESPAWN, 0) with no creature summoner: a temporary creature that
    /// stays until it dies and then lies as a corpse until the corpse decays. Null for a missing template.
    /// </summary>
    public Creature? SummonDeadDespawnAt(uint entry, float x, float y, float z, float orientation)
        => _content.FindTemplate(entry) is { } template ? SpawnTemporary(template, x, y, z, orientation) : null;
}

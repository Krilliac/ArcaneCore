using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The cheap guard in front of every GM stream producer, and the subject factories. They live in the Maps namespace (not in
/// <c>ArcaneCore.Game.Diagnostics</c>) so the contract namespace references no other game feature.
/// </summary>
public static class GmTraceGuard
{
    /// <summary>
    /// True, with the sink, when a GM follows <paramref name="channel"/>. False (a null check, a volatile read, and a lock-free set lookup
    /// at most) otherwise, and in a host with no stream. Call it before decoding or formatting anything.
    /// </summary>
    public static bool Wants(WorldRuntime? world, string channel, [NotNullWhen(true)] out IGmTraceSink? sink)
    {
        sink = world?.GmTrace;
        if (sink is { IsActive: true } && sink.HasSubscribers(channel))
        {
            return true;
        }

        sink = null;
        return false;
    }

    /// <summary>A subject at the position of <paramref name="at"/> (no position when it is in no map).</summary>
    public static GmTraceSubject SubjectOf(WorldObject at, ObjectGuid actor, ObjectGuid other)
        => at.Map is { } map
            ? new GmTraceSubject(actor, other, map.MapId, map.InstanceId, at.X, at.Y, at.Z, HasPosition: true)
            : new GmTraceSubject(actor, other, 0, 0, 0f, 0f, 0f, HasPosition: false);

    /// <summary>A subject whose actor is <paramref name="actor"/>, placed at its position.</summary>
    public static GmTraceSubject SubjectOf(Unit actor, ObjectGuid other) => SubjectOf(actor, actor.Guid, other);
}

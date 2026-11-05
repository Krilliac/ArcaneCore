namespace ArcaneCore.Game.Diagnostics;

/// <summary>One channel of the GM stream (<c>.arcane stream on &lt;channel&gt;</c>): its name, what it carries, and whether a GM may follow it for everyone (<see cref="GmStreamScope.All"/>).</summary>
public readonly record struct GmStreamChannelInfo(string Name, string Carries, bool AllowsAll);

/// <summary>
/// The channels the game layer publishes to the GM stream. The stream itself lives in the World layer, which cannot be referenced from
/// here, so the names and the producer contract (<see cref="IGmTraceSink"/>, and <c>GmTraceGuard</c> in the Maps namespace) are defined in this layer.
/// </summary>
public static class GmStreamChannels
{
    /// <summary>Damage and heals (melee swings, spell damage, periodic ticks, heals, environmental damage).</summary>
    public const string Damage = "damage";

    /// <summary>Proc chance rolls.</summary>
    public const string Procs = "procs";

    /// <summary>Spell casts: start, failure, go with its hits and misses, spell misses.</summary>
    public const string Spells = "spells";

    /// <summary>Movement opcodes with position and speed.</summary>
    public const string Moves = "moves";

    /// <summary>Anticheat findings.</summary>
    public const string AntiCheat = "anticheat";

    private static readonly GmStreamChannelInfo[] Channels =
    [
        new(Damage, "damage and heals: source, target, spell, amount, school, absorb/resist/block, crit", AllowsAll: true),
        new(Procs, "proc chance rolls: aura, chance, roll, result", AllowsAll: true),
        new(Spells, "cast start, fail with reason, go with hit/miss per target, spell misses", AllowsAll: true),
        new(Moves, "movement opcodes with position and speed (target or near only)", AllowsAll: false),
        new(AntiCheat, "anticheat findings with score and verdict, and packet-heuristics reports (GUID and account id, no names)", AllowsAll: true),
    ];

    /// <summary>Every channel, in the order they are listed to a GM.</summary>
    public static IReadOnlyList<GmStreamChannelInfo> All => Channels;

    /// <summary>The channel called <paramref name="name"/> (case-insensitive), or null.</summary>
    public static GmStreamChannelInfo? Find(string name)
    {
        foreach (GmStreamChannelInfo info in Channels)
        {
            if (info.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        return null;
    }
}

/// <summary>Which events of a channel a GM's subscription receives.</summary>
public enum GmStreamScope
{
    /// <summary>The default: events whose actor or other party is the GM or the GM's selected unit, within range of the GM.</summary>
    Target,

    /// <summary>Any event within range of the GM on the same map and instance; events involving the GM always match.</summary>
    Near,

    /// <summary>Every event of the channel.</summary>
    All,
}

/// <summary>
/// Who and where an event happened, for the scope test of a subscription: the acting and the other party (empty when there is none)
/// and the position of the event. <see cref="HasPosition"/> is false for an event with no known place; it then matches by GUID alone.
/// </summary>
public readonly record struct GmTraceSubject(ObjectGuid Actor, ObjectGuid Other, uint MapId, uint InstanceId, float X, float Y, float Z, bool HasPosition);

/// <summary>
/// The producer side of the GM stream, implemented by the World layer's <c>GmStream</c>. Thread safe: map threads publish while the
/// world thread subscribes.
/// </summary>
public interface IGmTraceSink
{
    /// <summary>True while any GM follows any channel (one volatile read).</summary>
    bool IsActive { get; }

    /// <summary>Whether any GM follows <paramref name="channel"/>. Lock-free.</summary>
    bool HasSubscribers(string channel);

    /// <summary>
    /// Offer an event to every GM whose subscription to <paramref name="channel"/> matches <paramref name="subject"/>. <paramref name="format"/>
    /// is called at most once, and only when at least one such GM has a rate-limit token; never when nobody matches. It runs on the
    /// calling thread inside the call and must only read. Returns how many GMs the line was queued for.
    /// </summary>
    int Publish<TState>(string channel, in GmTraceSubject subject, TState state, Func<TState, string> format);
}

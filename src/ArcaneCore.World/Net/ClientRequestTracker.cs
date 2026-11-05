using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// Counts the client requests a real 1.12.1 client has send code for but that no reference server models
/// (<see cref="ArcaneCore.World.Handlers.ClientRequestHandlers"/>), per opcode, and keeps the last decoded summary of each so an operator or a
/// test can see what arrived. Bounded by the opcode space (one counter and one string slot per opcode value). Thread affinity: any thread;
/// <see cref="Record"/> is one <see cref="Interlocked.Increment(ref long)"/> and one reference write.
/// </summary>
public sealed class ClientRequestTracker
{
    private static readonly int Slots = Enum.GetValues<WorldOpcode>().Max(o => (int)o) + 1;

    /// <summary>The process-wide instance a session uses when its container has none.</summary>
    public static ClientRequestTracker Shared { get; } = new();

    private readonly long[] _counts = new long[Slots];
    private readonly string?[] _last = new string?[Slots];

    /// <summary>Count one request and remember its summary. Values outside the opcode space are ignored.</summary>
    public void Record(WorldOpcode opcode, string summary)
    {
        int value = (ushort)opcode;
        if (value >= Slots)
        {
            return;
        }

        Interlocked.Increment(ref _counts[value]);
        Volatile.Write(ref _last[value], summary);
    }

    /// <summary>How many requests of <paramref name="opcode"/> were recorded.</summary>
    public long Count(WorldOpcode opcode)
    {
        int value = (ushort)opcode;
        return value < Slots ? Volatile.Read(ref _counts[value]) : 0;
    }

    /// <summary>The summary of the latest request of <paramref name="opcode"/>, or null when none arrived.</summary>
    public string? Last(WorldOpcode opcode)
    {
        int value = (ushort)opcode;
        return value < Slots ? Volatile.Read(ref _last[value]) : null;
    }
}

using System.Diagnostics.CodeAnalysis;

namespace ArcaneCore.Protocol;

/// <summary>
/// The one controlled outcome of a packet that does not fit its layout. Every throwing read of
/// <see cref="PacketReader"/> ends here, so a session can catch exactly one exception type
/// (<see cref="ArgumentOutOfRangeException"/>, the contract every handler was written against and
/// that the sessions' catch clauses and existing tests pin) and disconnect. The non-throwing
/// <c>TryRead*</c> methods are the hot-path alternative and never reach this class.
/// </summary>
public static class MalformedPacket
{
    /// <summary>The parameter name every malformed-packet exception carries.</summary>
    public const string ParamName = "packet";

    /// <summary>Throw the controlled exception. Cold: never called by a well-formed packet.</summary>
    [DoesNotReturn]
    public static void Throw(string what)
        => throw new ArgumentOutOfRangeException(ParamName, "malformed packet: " + what);

    /// <summary>True for the controlled outcome (the type a session may treat as "malformed, disconnect").</summary>
    public static bool Is(Exception exception) => exception is ArgumentOutOfRangeException;
}

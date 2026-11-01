using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// The identity of a server fault that does not come from the fault ledger: the exception's type and the first <c>ArcaneCore</c> frame of its throw site, nothing else.
/// The log tap (an exception in a log entry) and the first-chance watch (the same exception thrown over and over) both use it, so one recurring fault is one
/// finding, one issue and a growing count, whichever way it was seen and from whichever logger category it was logged.
/// </summary>
internal static class PlayerbotQaFaultIds
{
    /// <summary>The frame text of a fault with no <c>ArcaneCore</c> frame (it was thrown and caught inside the framework).</summary>
    internal const string NoFrame = "(no ArcaneCore frame)";

    /// <summary>The finding id (16 hex) of an exception <paramref name="type"/> thrown at <paramref name="frame"/> (<c>Type.Method</c>).</summary>
    internal static string Of(string type, string? frame)
        => FaultLedger.HashSignature("exception|" + type + "|" + (string.IsNullOrEmpty(frame) ? NoFrame : frame));

    /// <summary>The type and the first <c>ArcaneCore</c> frame of a thrown <paramref name="exception"/> (wrappers unwrapped as the ledger does), and the frames kept for display.</summary>
    internal static string Describe(Exception exception, out string type, out string frame, out IReadOnlyList<string> frames)
    {
        FaultLedger.SignatureOf("exception", exception, out type, out frames);
        frame = frames.Count > 0 ? frames[0] : NoFrame;
        return Of(type, frame);
    }
}

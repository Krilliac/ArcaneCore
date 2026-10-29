namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

/// <summary>
/// The approved <c>parity_exception</c> list: every difference between the EventAI executor and the rule host that a reviewer accepted, each
/// with the reason. A difference that is not listed here fails the parity run; a listed one that stops happening fails it too.
/// </summary>
internal static class ParityExceptions
{
    public static IReadOnlyList<ParityException> Approved { get; } =
    [
        new("disabled-rows", ParitySide.Engine, "cast 1213 at",
            "A disabled row (chance 0, debug-only, every phase masked) is written disabled by the converter, so it no longer rolls its chance each tick; "
            + "the old executor still drew one value per tick for it. The shared map stream therefore runs ahead on the old side, and the chance-50 row "
            + "beside it rolls a different (equally valid) sequence. Nothing is lost: the disabled rows never ran."),
    ];
}

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
            "Of the three disabled rows only the chance-0 row (210) rolled in the old executor: it drew one value per tick before failing. A debug-only row "
            + "(211) is filtered when the rows load and an all-phases-masked row (212) fails the phase gate before the roll, so neither drew. The converter writes "
            + "row 210 disabled, so it no longer draws; the shared map stream therefore runs ahead on the old side, and the chance-50 row beside them rolls a "
            + "different (equally valid) sequence. Nothing is lost: the disabled rows never ran."),
    ];
}

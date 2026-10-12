namespace ArcaneCore.Game.Maps;

/// <summary>
/// An <see cref="IMapUpdater"/> that can say where its last update's time went, for the slow-map-update line
/// (<see cref="MapUpdateDiagnostics.Describe"/>). The updater records its sub-phase times on every update
/// (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reads: about ten per update plus two to six per active creature, so cost grows
/// with the creature count); the text is built only when the line is logged.
/// </summary>
internal interface IMapUpdateBreakdown
{
    /// <summary>The last update's sub-phases as one line of text (world thread; allocates).</summary>
    string DescribeLastUpdate();
}

namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>
/// One row of <c>holiday_flavour</c>: how a festival is told in speech and emotes, keyed by the client holiday id of its game event
/// (docs/superpowers/specs/2026-10-28-rule-engine-design.md, inventory 19). Holiday id 0 is the row for a holiday the table does not name.
/// </summary>
/// <param name="HolidayId">The holiday id of <c>game_event.holiday</c>; 0 = the default flavour.</param>
/// <param name="FlavourKey">The key of the festival's line templates (<c>event.&lt;key&gt;.arrive</c>).</param>
/// <param name="Name">The name a line says; empty = the game event's own description.</param>
/// <param name="Emote1">The first text emote of the crowd (<c>EmotesText</c> id), 0 = none.</param>
/// <param name="Emote2">The second text emote, 0 = none.</param>
/// <param name="Emote3">The third text emote, 0 = none.</param>
/// <param name="Gather">Whether bots gather at the festival.</param>
public sealed record HolidayFlavourRecord(uint HolidayId, string FlavourKey, string Name, uint Emote1, uint Emote2, uint Emote3, bool Gather);

/// <summary>Reads <c>holiday_flavour</c> (start-up of the playerbot pack; off the world thread).</summary>
public interface IHolidayFlavourStore
{
    /// <summary>Every row, by holiday id.</summary>
    Task<IReadOnlyList<HolidayFlavourRecord>> LoadAsync(CancellationToken cancellationToken = default);
}

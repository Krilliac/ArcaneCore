namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// A managed playerbot the population manager created and owns (World:Playerbots:Population; docs/areas/playbots-population.md).
/// Only such bots are ever parked or relocated by the population; a bot an operator created with <c>.playerbot create</c> has no row.
/// </summary>
/// <param name="BotId">The managed playerbot (<see cref="ManagedPlayerbot.BotId"/>).</param>
/// <param name="CharacterId">The bot's character; the row goes with the character when it is deleted.</param>
/// <param name="ZoneId">The zone the population last placed the bot in (0: not placed yet).</param>
/// <param name="Band">The index of the level band the bot was created for (informational; bands are matched by level).</param>
/// <param name="CreatedUnix">Unix seconds when the bot was created.</param>
/// <param name="UpdatedUnix">Unix seconds of the last change to the row.</param>
public sealed record PlayerbotPopulationMember(Guid BotId, int CharacterId, uint ZoneId, byte Band, long CreatedUnix, long UpdatedUnix);

/// <summary>The population manager's membership table (<c>playerbot_population_member</c>).</summary>
public interface IPlayerbotPopulationStore
{
    /// <summary>Every member, in BotId order.</summary>
    Task<IReadOnlyList<PlayerbotPopulationMember>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert the member, or replace the row with the same BotId (identity: the CharacterId of an existing row never changes).</summary>
    Task UpsertAsync(PlayerbotPopulationMember member, CancellationToken cancellationToken = default);

    /// <summary>Remove a member; true when a row was removed.</summary>
    Task<bool> DeleteAsync(Guid botId, CancellationToken cancellationToken = default);
}

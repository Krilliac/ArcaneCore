namespace ArcaneCore.Kernel.Characters;

/// <summary>What a managed playerbot has noted about another character. Persisted as an int: never renumber, append only.</summary>
[Flags]
public enum PlayerbotRelationFlags : int
{
    None = 0,
    PrefersTank = 1,
    PrefersHealer = 2,
    PrefersDamage = 4,
    WantsInvites = 8,
    NoWhispers = 16,
    OwesFavour = 32,
    Grouped = 64,
    OtherFaction = 128,
    Rival = 256,

    /// <summary>Only ever in <c>WarFlags</c>: the war layer has been seeded from the normal affinity (never set in <c>Flags</c>).</summary>
    WarSeeded = 512,

    /// <summary>
    /// Only ever in <c>Flags</c> of a stored row: the normal rules have evicted this relation and only the war layer still holds it. The normal
    /// fields of such a row are blank and every normal read treats it as absent (a war read, with War Mode on, sees it).
    /// </summary>
    WarOnly = 1024,

    /// <summary>
    /// The other side has done something toward this bot (chatted with it, traded, helped it, emoted kindly, apologised, grouped): set by the bot's own
    /// social memory, never by a player-facing command. Unprompted whispers and greetings to a player who has not opted in to AI chat need it.
    /// </summary>
    Interacted = 2048,
}

/// <summary>
/// One managed playerbot's memory of another character (player or bot). LastTopic is an intent/topic key and Summary is built from
/// bot-side text only: neither ever holds free player text.
/// </summary>
/// <param name="Affinity">-100..100.</param>
/// <param name="Familiarity">0.. (encounters).</param>
/// <param name="TrustPermille">0..1000, 500 is neutral.</param>
/// <param name="LastTopic">At most 32 characters.</param>
/// <param name="Summary">At most 160 characters.</param>
/// <param name="WarAffinity">-100..100: the affinity War Mode keeps on top of <c>Affinity</c> (read only while War Mode is on; meaningful once <c>WarFlags</c> has WarSeeded).</param>
/// <param name="WarFlags">The war layer's flags: Rival and WarSeeded only.</param>
/// <param name="FamiliarityExact">The unrounded familiarity as of <c>DecayAnchorUnixMs</c> (NaN: only the whole <c>Familiarity</c> is known, as of <c>LastSeenUnixMs</c>). Persisted so a restart loses no fraction of a decay.</param>
/// <param name="AffinityExact">The unrounded affinity as of <c>DecayAnchorUnixMs</c> (NaN: as <c>FamiliarityExact</c>).</param>
/// <param name="WarAffinityExact">The unrounded war affinity as of <c>WarDecayAnchorUnixMs</c> (NaN: as <c>FamiliarityExact</c>).</param>
/// <param name="DecayAnchorUnixMs">The instant whole days of decay were last counted up to; 0 means <c>LastSeenUnixMs</c>.</param>
/// <param name="WarDecayAnchorUnixMs">The same for the war layer; 0 means <c>LastSeenUnixMs</c>.</param>
public sealed record PlayerbotRelation(
    Guid BotId, int CharacterId, ulong OtherGuid, bool OtherIsBot,
    short Affinity,
    int Familiarity,
    short TrustPermille,
    PlayerbotRelationFlags Flags,
    long LastSeenUnixMs,
    string LastTopic,
    string Summary,
    short WarAffinity = 0,
    PlayerbotRelationFlags WarFlags = PlayerbotRelationFlags.None,
    double FamiliarityExact = double.NaN,
    double AffinityExact = double.NaN,
    double WarAffinityExact = double.NaN,
    long DecayAnchorUnixMs = 0,
    long WarDecayAnchorUnixMs = 0)
{
    /// <summary>Rows one bot may store: four times the largest <c>MaxRelationsPerBot</c> (1024), the war-only rows being the rest.</summary>
    public const int MaxRowsPerBot = 4096;
    public const int MaxTopicLength = 32;
    public const int MaxSummaryLength = 160;
}

public interface IPlayerbotRelationStore
{
    Task<IReadOnlyList<PlayerbotRelation>> LoadAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically replace one bot's relation rows.</summary>
    Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotRelation> rows, CancellationToken cancellationToken = default);
}

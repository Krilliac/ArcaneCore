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
    PlayerbotRelationFlags WarFlags = PlayerbotRelationFlags.None)
{
    public const int MaxRowsPerBot = 1024;
    public const int MaxTopicLength = 32;
    public const int MaxSummaryLength = 160;
}

public interface IPlayerbotRelationStore
{
    Task<IReadOnlyList<PlayerbotRelation>> LoadAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically replace one bot's relation rows.</summary>
    Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotRelation> rows, CancellationToken cancellationToken = default);
}

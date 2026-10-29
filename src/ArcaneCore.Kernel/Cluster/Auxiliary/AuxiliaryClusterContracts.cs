namespace ArcaneCore.Kernel.Cluster.Auxiliary;

/// <summary>
/// Descriptor for a single platform Warden module (<c>.bin</c> + RC4 seed key + expected SHA-256 digest).
/// </summary>
public sealed record WardenModuleDescriptor(
    string OsTag,
    ReadOnlyMemory<byte> ModulePayload,
    ReadOnlyMemory<byte> Rc4Key,
    string ExpectedSha256Hex);

/// <summary>
/// Realm-wide Warden manifest published by every Gateway in a cluster (<c>docs/CLUSTERING_DESIGN.md</c> §8 Warden).
/// All gateways of a realm must publish identical <see cref="ModuleDigestHex"/>, <see cref="OptionsSummary"/>,
/// and <see cref="CheckRevision"/> so a client cannot pick an unscanned gateway.
/// </summary>
public sealed record GatewayWardenManifest(
    int RealmId,
    bool Enabled,
    string ModuleDigestHex,
    string OptionsSummary,
    int CheckRevision,
    IReadOnlyList<WardenModuleDescriptor> Modules);

/// <summary>Penalty action carried by a gateway Warden verdict.</summary>
public enum ClusterWardenAction
{
    Log = 0,
    Kick = 1,
    Ban = 2,
}

/// <summary>
/// Advisory Warden verdict emitted by the connection-owning gateway (<c>docs/CLUSTERING_DESIGN.md</c> §8 Warden).
/// Bound to a single <see cref="SessionGeneration"/> and <see cref="OwnerEpoch"/> so stale verdicts are ignored.
/// </summary>
public sealed record ClusterWardenVerdict(
    long SessionGeneration,
    long OwnerEpoch,
    int AccountId,
    ulong CharacterGuid,
    string Reason,
    ClusterWardenAction Action,
    ushort CheckId);

/// <summary>
/// The generation counter a <see cref="ClusterWardenVerdict"/> came from: the gateway's <see cref="NodeId"/> and the
/// <see cref="NodeIncarnation"/> of the gateway process. Session generations are only monotonic within one incarnation
/// (each gateway process counts from 1 and never persists the counter), so replay state is scoped to the source. The
/// caller takes it from the admitted link's peer manifest, not from the verdict body.
/// </summary>
public readonly record struct ClusterWardenVerdictSource(string NodeId, Guid NodeIncarnation);

/// <summary>
/// Deterministic idempotency identifier for a playerbot chat ask (<c>worldIncarnation + botId + sequence</c>).
/// </summary>
public readonly record struct ClusterBotChatAskId(Guid WorldIncarnation, int BotId, long Sequence)
{
    public override string ToString() => $"{WorldIncarnation:N}:{BotId}:{Sequence}";
}

/// <summary>
/// Request sent from a map worker to the playerbot chat / LLM service (<c>docs/CLUSTERING_DESIGN.md</c> §8 Playerbot chat).
/// Contains only value data and never exposes live <c>Player</c> or <c>Map</c> references.
/// </summary>
public sealed record ClusterBotChatAsk(
    ClusterBotChatAskId AskId,
    int RealmId,
    int BotId,
    ulong BotGuid,
    string Persona,
    ulong SenderGuid,
    string SenderName,
    int SenderAccountId,
    string Channel,
    string Text,
    bool FromMaster,
    bool InviteAllowed,
    long LoginId);

/// <summary>Admission outcome for a <see cref="ClusterBotChatAsk"/>.</summary>
public enum ClusterBotChatAdmissionOutcome
{
    Accepted = 0,
    Declined = 1,
    Duplicate = 2,
    ServiceUnavailableFallback = 3,
}

/// <summary>Admission response from the bot chat service.</summary>
public sealed record ClusterBotChatAdmission(
    ClusterBotChatAskId AskId,
    ClusterBotChatAdmissionOutcome Outcome,
    string? DisclosureOrReason);

/// <summary>Generated chat line returned by the bot chat service (or built-in fallback provider).</summary>
public sealed record ClusterBotChatReply(
    ClusterBotChatAskId AskId,
    int BotId,
    ulong BotGuid,
    Guid WorldIncarnation,
    string SenderName,
    string Channel,
    string Text,
    string Provider);

/// <summary>Advisory mute emitted when safety screening or strike rules trip in the bot chat service.</summary>
public sealed record ClusterBotChatMuteAdvisory(
    int AccountId,
    string PlayerName,
    int Minutes,
    string Reason);

/// <summary>Remote or local playerbot chat service contract.</summary>
public interface IClusterBotChatService
{
    ValueTask<(ClusterBotChatAdmission Admission, ClusterBotChatReply? Reply, ClusterBotChatMuteAdvisory? Mute)> AskAsync(
        ClusterBotChatAsk ask,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Value-only snapshot of bot state passed to the optional remote planner (<c>docs/CLUSTERING_DESIGN.md</c> §8 Playerbot planning).
/// Carries no names or chat text.
/// </summary>
public readonly record struct ClusterBotPlannerFacts(
    byte Level,
    byte HealthPercent,
    bool InCombat,
    uint MapId,
    int SpellCount,
    bool HasFood);

/// <summary>
/// Closed-candidate planning request sent off the world thread to an optional local/remote model planner.
/// </summary>
public sealed record ClusterBotPlanRequest(
    string RequestId,
    int RealmId,
    Guid WorkerIncarnation,
    int BotId,
    uint MapId,
    uint InstanceId,
    long OwnerEpoch,
    ClusterBotPlannerFacts Facts,
    IReadOnlyList<int> CandidateIds,
    DateTimeOffset DeadlineUtc);

/// <summary>Candidate selection returned by the bot planner service.</summary>
public sealed record ClusterBotPlanChoice(
    string RequestId,
    Guid WorkerIncarnation,
    long OwnerEpoch,
    int? SelectedCandidateId,
    DateTimeOffset RespondedAtUtc);

/// <summary>Optional remote/local model planner contract for playerbots.</summary>
public interface IClusterBotPlannerService
{
    ValueTask<ClusterBotPlanChoice> SelectPlanAsync(
        ClusterBotPlanRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable idempotency key for an auction house bot planner proposal (<c>serviceIncarnation:sequence</c>).
/// </summary>
public readonly record struct AuctionBotIntentId(Guid ServiceIncarnation, long Sequence)
{
    public string ToKey() => $"{ServiceIncarnation:N}:{Sequence}";

    public override string ToString() => ToKey();
}

/// <summary>Listing proposal sent from the AH bot planner to the fenced auction/custody owner.</summary>
public sealed record AuctionBotListingIntent(
    AuctionBotIntentId IntentId,
    uint HouseId,
    uint ItemEntry,
    uint Count,
    uint StartBid,
    uint Buyout,
    uint Hours,
    long OwnerFencingEpoch);

/// <summary>Bid or buyout proposal sent from the AH bot planner to the fenced auction/custody owner.</summary>
public sealed record AuctionBotBuyIntent(
    AuctionBotIntentId IntentId,
    uint HouseId,
    uint AuctionId,
    bool IsBuyout,
    uint Price,
    uint ObservedBid,
    long OwnerFencingEpoch);

/// <summary>Outcome of an auction house bot intent processed by the custody owner.</summary>
public enum AuctionBotIntentOutcome
{
    Ok = 0,
    Rejected = 1,
    Duplicate = 2,
    QueueFull = 3,
}

/// <summary>Result returned by the auction custody owner for a proposed listing or buy intent.</summary>
public sealed record AuctionBotIntentResult(
    AuctionBotIntentId IntentId,
    AuctionBotIntentOutcome Outcome,
    string? Reason,
    long LedgerKey);

/// <summary>Read-only view of one open auction in a <see cref="AuctionMarketSnapshot"/>.</summary>
public readonly record struct AuctionMarketListingSnapshot(
    uint AuctionId,
    uint ItemEntry,
    uint Count,
    uint CurrentBid,
    uint Buyout);

/// <summary>Read-only market snapshot published by the auction owner for remote buy planning.</summary>
public sealed record AuctionMarketSnapshot(
    uint HouseId,
    long Version,
    IReadOnlyList<AuctionMarketListingSnapshot> OpenAuctions);

/// <summary>
/// Intent channel between the pure AH bot planner and the single fenced realm auction/custody owner
/// (<c>docs/CLUSTERING_DESIGN.md</c> §8 Auction house bot).
/// </summary>
public interface IAuctionBotIntentChannel
{
    ValueTask<AuctionBotIntentResult> ProposeListingAsync(
        AuctionBotListingIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<AuctionBotIntentResult> ProposeBuyAsync(
        AuctionBotBuyIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<AuctionBotIntentId, AuctionBotIntentResult>> QueryOutstandingIntentsAsync(
        IReadOnlyList<AuctionBotIntentId> intentIds,
        CancellationToken cancellationToken = default);

    AuctionMarketSnapshot GetMarketSnapshot(uint houseId);
}

/// <summary>
/// Cluster role, node, and incarnation labels exported alongside <c>realm</c> on every daemon's metrics series
/// (<c>docs/CLUSTERING_DESIGN.md</c> §8 Metrics exporter).
/// </summary>
public sealed record ClusterMetricsLabels(
    int RealmId,
    string Role,
    string NodeId,
    Guid NodeIncarnation)
{
    public IReadOnlyDictionary<string, string> ToTagDictionary() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["realm"] = RealmId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["role"] = Role.Trim().ToLowerInvariant(),
            ["node"] = NodeId,
            ["incarnation"] = NodeIncarnation.ToString("N"),
        };
}

/// <summary>
/// Deterministic operation key derived from <c>(RealmId, CharacterId, TemplateId)</c> so server-mail reward delivery
/// is idempotent across retries, crashes, or rapid relogs onto another map worker before <c>mail_server_character</c>
/// settles (<c>docs/CLUSTERING_DESIGN.md</c> §8 Server mail).
/// </summary>
public readonly record struct ServerMailIdempotencyKey(int RealmId, int CharacterId, uint TemplateId)
{
    public static ServerMailIdempotencyKey Derive(int realmId, int characterId, uint templateId)
    {
        if (realmId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId), "realmId must be positive");
        }

        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId), "characterId must be positive");
        }

        if (templateId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(templateId), "templateId must be non-zero");
        }

        return new ServerMailIdempotencyKey(realmId, characterId, templateId);
    }

    public string OperationId => $"server-mail:r{RealmId}:c{CharacterId}:t{TemplateId}";

    public override string ToString() => OperationId;
}

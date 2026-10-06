namespace ArcaneCore.MockClient.Playbots;

internal enum PlaybotActionKind
{
    Observe, QueryCreature, Move, Attack, StopAttack, OpenLoot, LootItem, LootMoney, CloseLoot, ReleaseSpirit, CastKnownSpell, Eat
}

// Payloads belong to the deterministic client. The model sees only IDs, kinds and priorities.
internal sealed record PlaybotCandidate(string Id, PlaybotActionKind Kind, int Priority,
    ulong Target = 0, float X = 0, float Y = 0, float Z = 0, uint Value = 0, uint ExpectedStack = 0);

internal sealed record PlaybotDecisionContext(long Revision, uint? Health, uint? MaximumHealth,
    bool? InCombat, IReadOnlyList<PlaybotCandidate> Candidates);

internal sealed record PlaybotSelection(PlaybotCandidate Candidate, string Provider, string? FallbackReason = null);

internal interface IPlaybotSelector
{
    Task<PlaybotSelection> SelectAsync(PlaybotDecisionContext context, CancellationToken cancellationToken);
}

internal sealed class DeterministicPlaybotSelector : IPlaybotSelector
{
    internal static PlaybotCandidate Choose(PlaybotDecisionContext context)
        => context.Candidates.OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal).First();

    public Task<PlaybotSelection> SelectAsync(PlaybotDecisionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlaybotSelection(Choose(context), "deterministic"));
    }
}

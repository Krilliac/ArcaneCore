using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload creature_onkill_reputation</c> (vmangos Chat.cpp:826, HandleReloadCreatureOnKillReputation): the rows are read and
/// validated against Faction.dbc off the world thread (a row with a missing faction is skipped, ObjectMgr.cpp:8935-8957), then swapped
/// in as a whole between ticks. A world without Faction.dbc has nothing to validate against and keeps the rows as loaded.
/// </summary>
public sealed class OnKillReputationReloadable(IServiceProvider services) : IContentReloadable
{
    /// <summary>Chat.cpp registers this table on its own; no all_* command reaches it (vmangos reload all), so neither does reload all here.</summary>
    public bool IncludedInAll => false;

    public string Name => "creature_onkill_reputation";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        ReputationService service = services.GetRequiredService<ReputationFeature>().Service;
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IReputationOnKillSource source = scope.ServiceProvider.GetService<IReputationOnKillSource>()
            ?? throw new InvalidOperationException("no kill reputation source is registered");
        IReadOnlyList<ReputationOnKillEntry> rows = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        ReputationOnKillValidation checkedRows = service.Factions.Count > 0
            ? ReputationContentValidator.FilterOnKill(rows, service.Factions)
            : new ReputationOnKillValidation(rows, []);
        string[] duplicates =
        [
            .. checkedRows.Entries.GroupBy(e => e.CreatureEntry).Where(g => g.Count() > 1).OrderBy(g => g.Key)
                .Select(g => $"creature_onkill_reputation has {g.Count()} rows for creature {g.Key}"),
        ];
        return new Candidate(service, checkedRows, duplicates);
    }

    private sealed class Candidate(ReputationService service, ReputationOnKillValidation rows, IReadOnlyList<string> problems) : ContentCandidate
    {
        public override string Summary => rows.Warnings.Count == 0
            ? $"{rows.Entries.Count} kill reputation entries"
            : $"{rows.Entries.Count} kill reputation entries ({rows.Warnings.Count} skipped or suspicious: {rows.Warnings[0]})";

        public override IReadOnlyList<string> Validate() => problems;

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            ReputationOnKillEntry[] previous = [.. service.OnKillEntries];
            transaction.Step("kill reputation", () => service.ReplaceOnKill(rows.Entries), () => service.ReplaceOnKill(previous));
        }
    }
}

/// <summary>
/// <c>.reload reputation_spillover_template</c> and <c>.reload reputation_reward_rate</c> (vmangos Chat.cpp:886-887,
/// ObjectMgr::LoadReputationSpilloverTemplate / LoadReputationRewardRate). Both tables become one validated, immutable
/// <see cref="ReputationContent"/> that is swapped in as a whole, so either name refreshes both (a deliberate simplification:
/// the unchanged table reloads to the same rows).
/// </summary>
public abstract class ReputationTemplatesReloadable(IServiceProvider services) : IContentReloadable
{
    /// <summary>Chat.cpp registers this table on its own; no all_* command reaches it (vmangos reload all), so neither does reload all here.</summary>
    public bool IncludedInAll => false;

    public abstract string Name { get; }

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        ReputationService service = services.GetRequiredService<ReputationFeature>().Service;
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IReputationContentSource source = scope.ServiceProvider.GetService<IReputationContentSource>()
            ?? throw new InvalidOperationException("no reputation template source is registered");
        ReputationContentRows rows = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(service, ReputationContent.Create(rows, service.Factions));
    }

    private sealed class Candidate(ReputationService service, ReputationContent content) : ContentCandidate
    {
        public override string Summary => content.Warnings.Count == 0
            ? $"{content.SpilloverCount} spillover templates and {content.RateCount} reward rates"
            : $"{content.SpilloverCount} spillover templates and {content.RateCount} reward rates ({content.Warnings.Count} rows skipped or suspicious: {content.Warnings[0]})";

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            ReputationContent previous = service.Content;
            transaction.Step("reputation templates", () => service.Content = content, () => service.Content = previous);
        }
    }
}

public sealed class ReputationSpilloverReloadable(IServiceProvider services) : ReputationTemplatesReloadable(services)
{
    public override string Name => "reputation_spillover_template";
}

public sealed class ReputationRewardRateReloadable(IServiceProvider services) : ReputationTemplatesReloadable(services)
{
    public override string Name => "reputation_reward_rate";
}

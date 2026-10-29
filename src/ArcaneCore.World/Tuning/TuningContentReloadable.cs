using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Tuning;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tuning;

/// <summary>
/// <c>.reload tuning</c> (an ArcaneCore addition, rule engine phase P5): the tuning tables are read and validated off the world thread and
/// the new catalog replaces the active one between ticks. As for <c>.reload rules</c>, a reload with any rejected row is rejected whole and
/// the current rows stay, and empty tables empty the tuning unless <c>HotReload:EmptyTables = KeepLoaded</c>. The auction bot rebuilds its
/// item pool and loot sources on its next action; ship routes, lift regions and the tram network are read from the new catalog on the next
/// bot plan. In <c>.reload all</c>.
/// </summary>
public sealed class TuningContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "tuning";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        TuningFeature feature = services.GetRequiredService<TuningFeature>();
        TuningContent content = await feature.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(content, TuningCatalog.Build(content), ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(TuningContent content, TuningCatalog catalog, bool keepEmpty) : ContentCandidate
    {
        public override string Summary
        {
            get
            {
                IReadOnlyList<string> missing = TuningFeature.MissingTables(content);
                return missing.Count == 0 ? catalog.Summary
                    : $"{catalog.Summary}; empty tables, whose behaviour is off until the next restart reseeds them: {string.Join(", ", missing)}";
            }
        }

        public override IReadOnlyList<string> Validate()
        {
            List<string> problems = [.. catalog.Rejected.Take(TuningFeature.LoggedRejections).Select(r => r.ToString())];
            if (catalog.Rejected.Count > TuningFeature.LoggedRejections)
            {
                problems.Add($"... and {catalog.Rejected.Count - TuningFeature.LoggedRejections} more rejected tuning rows");
            }

            return problems;
        }

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            if (keepEmpty && content.RowCount == 0 && TuningCatalog.Active.RowCount > 0)
            {
                reason = $"the tuning tables are empty and HotReload:EmptyTables is KeepLoaded, {TuningCatalog.Active.RowCount} tuning rows stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            TuningCatalog? previous = null;
            transaction.Step("tuning", () => previous = TuningCatalog.Replace(catalog), () => TuningCatalog.Replace(previous!));
        }
    }
}

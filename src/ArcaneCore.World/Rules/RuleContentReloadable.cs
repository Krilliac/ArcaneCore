using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Rules;

/// <summary>
/// <c>.reload rules</c> (an ArcaneCore addition, design 5.1): the rule tables are read and validated off the world thread against the
/// conditions table loaded now, and the new catalog replaces the live one between ticks. Unlike the startup load, which skips bad rows,
/// a reload with any rejected row is rejected whole and the current rules stay, so a half-fixed edit never goes live; the notes name the
/// first rejections and <c>.rules check</c> lists them after the next successful load. An empty set of tables empties the rules unless
/// <c>HotReload:EmptyTables = KeepLoaded</c>. Hosts rebind their subjects to the new catalog (a subject's cooldowns and phase restart).
/// In <c>.reload all</c>; after <c>conditions</c> when both are reloaded together.
/// </summary>
public sealed class RuleContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "rules";

    public IReadOnlyCollection<string> CommitAfter => ["conditions"];

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        RuleFeature feature = services.GetRequiredService<RuleFeature>();
        RuleContent content = await feature.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(feature, content, feature.Build(content), ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(RuleFeature feature, RuleContent content, RuleCatalog catalog, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => catalog.Summary;

        public override IReadOnlyList<string> Validate()
        {
            if (catalog.Rejected.Count == 0)
            {
                return [];
            }

            List<string> problems = [.. catalog.Rejected.Take(RuleFeature.LoggedRejections).Select(r => r.ToString())];
            if (catalog.Rejected.Count > RuleFeature.LoggedRejections)
            {
                problems.Add($"... and {catalog.Rejected.Count - RuleFeature.LoggedRejections} more rejected rule rows");
            }

            return problems;
        }

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = feature.Catalog.RuleCount;
            if (keepEmpty && content.RowCount == 0 && loaded > 0)
            {
                reason = $"the rule tables are empty and HotReload:EmptyTables is KeepLoaded, {loaded} rules stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            RuleCatalog? previous = null;
            transaction.Step("rules", () => previous = feature.Replace(catalog), () => feature.Replace(previous!));
        }
    }
}

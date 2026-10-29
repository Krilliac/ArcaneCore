using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload conditions</c> (vmangos <c>HandleReloadConditionsCommand</c>, Chat.cpp:818 → <c>ObjectMgr::LoadConditions</c>; not part of
/// <c>reload all</c>, ServerCommands.cpp:885-905): the table is read and validated off the world thread like the startup load (an
/// invalid row is skipped, as LoadConditions erases it), and the new table replaces the live one through
/// <see cref="ConditionFeature.Replace"/>, so gossip, vendors, quests, loot, spawn groups, DB scripts and rules all evaluate it from their
/// next check. The reload is rejected, and the current table stays, when a loaded rule names a condition the new table lacks (the rule
/// would fail closed for ever; reload the rules first). An empty table empties the conditions unless <c>HotReload:EmptyTables = KeepLoaded</c>.
/// </summary>
public sealed class ConditionContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "conditions";

    public bool IncludedInAll => false;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        ConditionFeature feature = services.GetRequiredService<ConditionFeature>();
        IReadOnlyList<ConditionRecord> records;
        using (IServiceScope scope = services.CreateScope())
        {
            records = scope.ServiceProvider.GetService<IConditionContentStore>() is { } store
                ? await store.LoadAsync(cancellationToken).ConfigureAwait(false)
                : throw new InvalidOperationException("no conditions store is registered");
        }

        IReadOnlySet<uint> referenced = services.GetService<RuleFeature>()?.Catalog.ReferencedConditions ?? new HashSet<uint>();
        return new Candidate(feature, ConditionTable.Build(records), referenced, ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(ConditionFeature feature, ConditionTable table, IReadOnlySet<uint> referenced, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => table.Rejected.Count == 0
            ? $"{table.Count} conditions"
            : $"{table.Count} conditions ({table.Rejected.Count} invalid rows were skipped, as at startup)";

        public override IReadOnlyList<string> Validate()
        {
            uint[] missing = [.. referenced.Where(id => table.Find(id) is null).Order()];
            return missing.Length == 0
                ? []
                : [$"loaded rules reference conditions the new table lacks: {string.Join(", ", missing.Take(20))}{(missing.Length > 20 ? $" and {missing.Length - 20} more" : "")}"];
        }

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = feature.Table.Count;
            if (keepEmpty && table.Count == 0 && loaded > 0)
            {
                reason = $"conditions is empty and HotReload:EmptyTables is KeepLoaded, {loaded} conditions stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            ConditionTable? previous = null;
            transaction.Step("conditions", () => previous = feature.Replace(table), () => feature.Replace(previous!));
        }
    }
}

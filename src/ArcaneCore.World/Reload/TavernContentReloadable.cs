using System.Collections.Frozen;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Reload;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload areatrigger_tavern</c> (vmangos <c>HandleReloadAreaTriggerTavernCommand</c>, ReloadCommands.cpp:294 → <c>ObjectMgr::LoadTavernAreaTriggers</c>,
/// ObjectMgr.cpp:1656-1700; part of <c>all_area</c>, ServerCommands.cpp:911): the inn triggers are read off the world thread, the rows
/// that name no known area trigger are dropped (vmangos logs and skips them, ObjectMgr.cpp:1686-1691), and the remaining set replaces the
/// live one. vmangos clears its set before looking at the result, so an empty table leaves no inns; so does an empty table here, unless
/// <c>HotReload:EmptyTables = KeepLoaded</c>. A player already resting in an inn keeps resting until its trigger check fails.
/// </summary>
public sealed class TavernContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "areatrigger_tavern";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        RestFeature feature = services.GetRequiredService<RestFeature>();
        WorldMaps maps = services.GetRequiredService<TeleportFeature>().Maps;
        IReadOnlyList<uint> rows;
        using (IServiceScope scope = services.CreateScope())
        {
            IAreaTriggerTavernStore store = scope.ServiceProvider.GetService<IAreaTriggerTavernStore>()
                ?? throw new InvalidOperationException("no areatrigger_tavern store is registered");
            rows = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        uint[] known = [.. rows.Where(id => maps.FindAreaTrigger(id) is not null)];
        return new Candidate(feature.Taverns, TavernTriggers.Build(known), rows.Count - known.Length, ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(TavernTriggers taverns, FrozenSet<uint> ids, int unknown, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => unknown == 0
            ? $"{ids.Count} inn triggers"
            : $"{ids.Count} inn triggers ({unknown} rows name an unknown area trigger and were skipped)";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = taverns.Count;
            if (keepEmpty && ids.Count == 0 && loaded > 0)
            {
                reason = $"areatrigger_tavern is empty and HotReload:EmptyTables is KeepLoaded, {loaded} inn triggers stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            FrozenSet<uint>? previous = null;
            transaction.Step("areatrigger_tavern", () => previous = taverns.Replace(ids), () => taverns.Replace(previous!));
        }
    }
}

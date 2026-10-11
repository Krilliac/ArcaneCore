using ArcaneCore.Data.World.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload instance_encounter</c> (an ArcaneCore table, DungeonEncounter.dbc through cmangos classic-db; no vmangos counterpart): the
/// encounters the playerbot dungeon and raid runs are built from (<see cref="PlayerbotGroupCoordinator.Encounters"/>) are read off the world
/// thread through <see cref="IInstanceEncounterStore"/> and swapped on it; the run catalogue is rebuilt from them at the next scan. A run
/// already under way keeps the plan it was formed with. An empty table leaves no runs, unless <c>HotReload:EmptyTables = KeepLoaded</c>.
/// Not part of <c>.reload all</c>.
/// </summary>
public sealed class InstanceEncounterContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "instance_encounter";

    public bool IncludedInAll => false;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        PlayerbotGroupCoordinator coordinator = services.GetService<PlayerbotGroupCoordinator>()
            ?? throw new InvalidOperationException("no playerbot group coordinator is registered (the playerbots are not part of this host)");
        IReadOnlyList<InstanceEncounter> rows;
        using (IServiceScope scope = services.CreateScope())
        {
            IInstanceEncounterStore store = scope.ServiceProvider.GetService<IInstanceEncounterStore>()
                ?? throw new InvalidOperationException("no instance_encounter store is registered");
            rows = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return new Candidate(coordinator, rows, ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(PlayerbotGroupCoordinator coordinator, IReadOnlyList<InstanceEncounter> rows, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{rows.Count} instance encounters";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = coordinator.Encounters.Count;
            if (keepEmpty && rows.Count == 0 && loaded > 0)
            {
                reason = $"instance_encounter is empty and HotReload:EmptyTables is KeepLoaded, {loaded} instance encounters stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            IReadOnlyList<InstanceEncounter>? previous = null;
            transaction.Step("instance_encounter", () => previous = coordinator.ReplaceEncounters(rows), () => coordinator.ReplaceEncounters(previous!));
        }
    }
}

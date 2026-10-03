using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>Reads the map tables of the world database for the map reloadables (a fresh scope per read).</summary>
internal static class MapContentSource
{
    public static async Task<MapContent> LoadAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        IMapDataStore store = scope.ServiceProvider.GetService<IMapDataStore>()
            ?? throw new InvalidOperationException("no map data store is registered");
        return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>.reload game_tele</c> (vmangos <c>HandleReloadGameTeleCommand</c>, ServerCommands.cpp:1638,
/// Chat.cpp:836 → <c>ObjectMgr::LoadGameTele</c>, ObjectMgr.cpp:10466): the <c>.tele</c> locations are read
/// off the world thread and replace the live list. vmangos clears its map before looking at the result,
/// so an empty table leaves no locations at all (ObjectMgr.cpp:10468, 10473-10481); so does an empty table here, unless
/// <c>HotReload:EmptyTables = KeepLoaded</c>.
/// </summary>
public sealed class GameTeleContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "game_tele";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        TeleportFeature feature = services.GetRequiredService<TeleportFeature>();
        MapContent content = await MapContentSource.LoadAsync(services, cancellationToken).ConfigureAwait(false);
        return new TeleCandidate(feature, [.. content.GameTeles], ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class TeleCandidate(TeleportFeature feature, IReadOnlyList<GameTele> teles, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{teles.Count} teleport locations";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = feature.Maps.GameTeles.Count;
            if (keepEmpty && teles.Count == 0 && loaded > 0)
            {
                reason = $"game_tele is empty and HotReload:EmptyTables is KeepLoaded, {loaded} teleport locations stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            WorldMaps maps = feature.Maps;
            IReadOnlyList<GameTele>? previous = null;
            transaction.Step("game_tele", () => previous = maps.ReplaceGameTeles(teles), () => maps.ReplaceGameTeles(previous!));
        }
    }
}

/// <summary>
/// <c>.reload areatrigger_teleport</c> (vmangos <c>HandleReloadAreaTriggerTeleportCommand</c>,
/// ServerCommands.cpp:1032, Chat.cpp:813 → <c>ObjectMgr::LoadAreaTriggerTeleports</c>, ObjectMgr.cpp:7706):
/// the area triggers and their teleports are rebuilt with the loader's rules (a teleport needs a trigger
/// row, a known target map and a non-zero position) against the maps already registered, then swapped in.
/// Rejected rows are listed in the result. The map registry, area table and terrain are not reloaded
/// (restart). As with <c>game_tele</c>, an empty table empties the teleports (vmangos clears first,
/// ObjectMgr.cpp:7708) unless <c>HotReload:EmptyTables = KeepLoaded</c>.
/// </summary>
public sealed class AreaTriggerTeleportContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "areatrigger_teleport";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        TeleportFeature feature = services.GetRequiredService<TeleportFeature>();
        MapContent content = await MapContentSource.LoadAsync(services, cancellationToken).ConfigureAwait(false);
        return new TriggerCandidate(feature, feature.Maps.BuildAreaTriggerTables(content), ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class TriggerCandidate(TeleportFeature feature, AreaTriggerTables tables, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{tables.TeleportCount} area trigger teleports on {tables.TriggerCount} area triggers";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = feature.Maps.AreaTriggerTeleportCount;
            if (keepEmpty && tables.TeleportCount == 0 && loaded > 0)
            {
                reason = $"areatrigger_teleport has no usable rows and HotReload:EmptyTables is KeepLoaded, {loaded} area trigger teleports stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            WorldMaps maps = feature.Maps;
            AreaTriggerTables? previous = null;
            transaction.Step("area trigger tables", () => previous = maps.ReplaceAreaTriggerTables(tables), () => maps.ReplaceAreaTriggerTables(previous!));
            foreach (string skipped in tables.Skipped)
            {
                transaction.Note($"{skipped}, skipped");
            }
        }
    }
}

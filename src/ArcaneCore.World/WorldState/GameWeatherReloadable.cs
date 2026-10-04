using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// <c>.reload game_weather</c> (vmangos <c>HandleReloadGameWeather</c>, Commands/ServerCommands.cpp:1813-1818, registered at
/// Chat.cpp:837): the table is read off the world thread and overlaid on the loaded chances, as
/// <c>WeatherMgr::LoadWeatherZoneChances</c> does (Weather.cpp:446-505): a listed zone takes its new row (a chance above 100
/// becomes 25), and a zone that is no longer listed keeps its old row, because vmangos writes into the map and never clears it.
/// An empty table changes nothing (vmangos logs the error and returns). Live zones read the current row at their next
/// regeneration, so no zone is touched here. Needs <c>HotReload:Commands=true</c>, like every reload.
/// </summary>
public sealed class GameWeatherReloadable(IServiceProvider services) : IContentReloadable
{
    /// <summary>Chat.cpp registers this table on its own; no all_* command reaches it (vmangos reload all), so neither does reload all here.</summary>
    public bool IncludedInAll => false;

    public string Name => "game_weather";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        WeatherFeature feature = services.GetRequiredService<WeatherFeature>();
        using IServiceScope scope = services.CreateScope();
        IWorldStateDataStore store = scope.ServiceProvider.GetService<IWorldStateDataStore>()
            ?? throw new InvalidOperationException("no world-state data store is registered");
        WorldStateContent content = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(feature, [.. content.Weather]);
    }

    private sealed class Candidate(WeatherFeature feature, IReadOnlyList<GameWeatherRecord> rows) : ContentCandidate
    {
        public override string Summary => $"{rows.Count} weather definitions";

        public override IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            foreach (GameWeatherRecord row in rows.Where(r => r.Chances.Count != 12))
            {
                problems.Add($"game_weather zone {row.Zone}: expected 12 chances, found {row.Chances.Count}");
            }

            foreach (IGrouping<uint, GameWeatherRecord> duplicate in rows.GroupBy(r => r.Zone).Where(g => g.Count() > 1))
            {
                problems.Add($"game_weather zone {duplicate.Key} is listed {duplicate.Count()} times");
            }

            return problems;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            WorldStateHooks hooks = WorldStateHooks.For(world);
            IReadOnlyDictionary<uint, Game.WorldState.Weather.ZoneWeatherChances> previous = hooks.WeatherChances.Snapshot();
            IReadOnlyList<string> messages = [];
            transaction.Step(
                "game_weather chances",
                () => messages = feature.MergeChances(rows),
                () => hooks.WeatherChances.Replace(previous));

            foreach (string message in messages)
            {
                transaction.Note(message);
            }

            if (rows.Count == 0)
            {
                transaction.Note("game_weather is empty: the loaded chances are unchanged (vmangos logs the error and returns)");
            }

            var listed = rows.Select(r => r.Zone).ToHashSet();
            int kept = previous.Keys.Count(z => !listed.Contains(z));
            if (kept > 0)
            {
                transaction.Note($"{kept} zone(s) are no longer in game_weather and keep their previous chances until restart (vmangos only overwrites listed zones)");
            }
        }
    }
}

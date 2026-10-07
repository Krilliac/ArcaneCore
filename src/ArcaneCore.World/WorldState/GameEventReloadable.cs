using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// <c>.reload game_event</c>: an ArcaneCore-only reload (vmangos has none; its game events are read once at start-up), shipped off. It exists
/// only with <c>World:GameEvents:AllowReload=true</c> and <c>HotReload:Commands=true</c>; otherwise the name is unknown like any other.
/// <para>
/// The tables are read off the world thread and loaded the way start-up loads them; a candidate with a row-level problem (event 0 or
/// out of range, a <c>linkedTo</c> that names no valid event, an unreadable date, a duplicate id) is rejected naming the rows and
/// the live state is untouched. The swap is on the world thread: events that are running and are gone from the table are stopped (their
/// spawns, quests and creature changes undone), then a new service takes over with the others still running (it starts from their
/// set and initialises at once, so nothing flickers and nothing is announced again). An event whose window changed is
/// rescheduled by the new service's first update. Limits: a manual <c>.event start</c> / <c>.event stop</c> override of the schedule does
/// not survive a reload (the table decides again), and the roll-back of a failed commit rebuilds the previous service from the previous tables.
/// </para>
/// </summary>
public sealed class GameEventReloadable(IServiceProvider services) : IContentReloadable, IOptionalReloadable
{
    public string Name => "game_event";

    /// <summary>There is no such reload in vmangos, so <c>.reload all</c> does not include it.</summary>
    public bool IncludedInAll => false;

    public bool IsEnabled => services.GetService<IConfiguration>()?.GetSection(GameEventOptions.SectionName).Get<GameEventOptions>()?.AllowReload ?? false;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        GameEventFeature feature = services.GetRequiredService<GameEventFeature>();
        using IServiceScope scope = services.CreateScope();
        IGameEventDataStore store = scope.ServiceProvider.GetService<IGameEventDataStore>()
            ?? throw new InvalidOperationException("no game-event data store is registered");
        GameEventContent content = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(feature, content);
    }

    private sealed class Candidate(GameEventFeature feature, GameEventContent content) : ContentCandidate
    {
        private GameEventLoadResult? _preview;

        public override string Summary => $"{content.Events.Count} game events";

        public override IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            foreach (IGrouping<uint, GameEventRecord> duplicate in content.Events.GroupBy(e => e.Entry).Where(g => g.Count() > 1))
            {
                problems.Add($"game_event {duplicate.Key} is listed {duplicate.Count()} times");
            }

            _preview = feature.Preview(content);
            problems.AddRange(_preview.Issues.Where(IsRowProblem));
            return problems;
        }

        /// <summary>The loader's issues that make a table unfit to go live (the others are limits or dropped rows, reported as notes).</summary>
        private static bool IsRowProblem(string issue)
            => issue.Contains("reserved or out of range", StringComparison.Ordinal)
                || issue.Contains("is linked to invalid event", StringComparison.Ordinal)
                || issue.Contains("is not a yyyy-MM-dd HH:mm:ss date", StringComparison.Ordinal)
                || issue.Contains("unreadable start or end", StringComparison.Ordinal);

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            GameEventLoadResult next = _preview ?? feature.Preview(content);
            GameEventContent previous = feature.Content;
            HashSet<ushort> running = [.. feature.ActiveEvents];
            var validNext = next.Definitions.Where(d => d.IsValid).Select(d => d.Id).ToHashSet();
            ushort[] gone = [.. running.Where(id => !validNext.Contains(id)).OrderBy(id => id)];

            transaction.Step(
                "game_event tables",
                () =>
                {
                    foreach (ushort id in gone)
                    {
                        feature.Service?.StopEvent(id);
                    }

                    feature.UseContent(content, running.Except(gone).ToHashSet(), continueRunning: true);
                },
                () => feature.UseContent(previous, running, continueRunning: true));

            foreach (string note in DescribeChanges(previous, content, gone))
            {
                transaction.Note(note);
            }

            foreach (string issue in next.Issues.Where(i => !IsRowProblem(i)).Take(20))
            {
                transaction.Note(issue);
            }
        }

        private static IEnumerable<string> DescribeChanges(GameEventContent before, GameEventContent after, ushort[] stopped)
        {
            Dictionary<uint, GameEventRecord> old = before.Events.GroupBy(e => e.Entry).ToDictionary(g => g.Key, g => g.Last());
            Dictionary<uint, GameEventRecord> now = after.Events.GroupBy(e => e.Entry).ToDictionary(g => g.Key, g => g.Last());
            Dictionary<uint, GameEventTimeRecord> oldTimes = before.Times.GroupBy(t => t.Entry).ToDictionary(g => g.Key, g => g.Last());
            Dictionary<uint, GameEventTimeRecord> newTimes = after.Times.GroupBy(t => t.Entry).ToDictionary(g => g.Key, g => g.Last());
            uint[] added = [.. now.Keys.Except(old.Keys).OrderBy(i => i)];
            uint[] removed = [.. old.Keys.Except(now.Keys).OrderBy(i => i)];
            uint[] changed = [.. now.Keys.Intersect(old.Keys).Where(id => old[id] != now[id] || oldTimes.GetValueOrDefault(id) != newTimes.GetValueOrDefault(id)).OrderBy(i => i)];
            if (added.Length > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"events added: {string.Join(", ", added)}");
            }

            if (removed.Length > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"events removed: {string.Join(", ", removed)}");
            }

            if (changed.Length > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"events changed (rescheduled by the next update): {string.Join(", ", changed)}");
            }

            if (stopped.Length > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"running events stopped because they left the table: {string.Join(", ", stopped)}");
            }
        }
    }
}

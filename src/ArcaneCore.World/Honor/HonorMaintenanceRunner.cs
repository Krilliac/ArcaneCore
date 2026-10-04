using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Characters.Creation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Honor;

/// <summary>
/// The weekly honor calculation against the store (vmangos <c>HonorMaintenancer::DoMaintenance</c>, HonorMgr.cpp:270-326).
/// For every outstanding week, oldest first: load the week's scores, plan, apply the result in ONE store transaction, move the
/// maintenance days on, and update the players who are online in memory exactly as the transaction updated the rows. A failing
/// transaction changes nothing, leaves the maintenance days where they were and is retried on the next run, so a week is never
/// applied twice. Queued writes are drained first, so what the calculation reads is what the players have earned.
/// </summary>
public sealed class HonorMaintenanceRunner(HonorFeature honor, IServiceScopeFactory scopes, ILogger logger)
{
    /// <summary>Weeks whose result was applied since this runner was created.</summary>
    public int PeriodsApplied { get; private set; }

    /// <summary>
    /// Run every period that is due on <paramref name="gameDay"/>. <paramref name="world"/> is the running world to update online
    /// players on; null before the world thread started (nobody is online). A calculation report is written to
    /// <c>World:Honor:ReportDirectory</c> when it is set. Returns the number of weeks applied.
    /// </summary>
    public async Task<int> RunDueAsync(uint gameDay, WorldRuntime? world, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        HonorOptions options = honor.Options;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IHonorStore>() is not { } store)
        {
            return 0; // a host without persistence has no weekly calculation
        }

        HonorMaintenanceState state = await store.GetMaintenanceAsync(cancellationToken).ConfigureAwait(false) ?? InitialState(gameDay, options);
        IReadOnlyList<HonorPeriod> periods = HonorMaintenancePlanner.Periods(state.LastDay, state.NextDay, gameDay);
        if (periods.Count == 0)
        {
            return 0;
        }

        logger.LogInformation("Honor maintenance: {Count} outstanding week(s) from day {Last}", periods.Count, state.LastDay);
        int applied = 0;
        foreach (HonorPeriod period in periods)
        {
            // Every queued write for anyone is attempted before the week is read (retained failures are not retried here).
            await honor.FlushAsync().ConfigureAwait(false);
            IReadOnlyList<HonorWeeklyScore> scores = await store.ListWeeklyScoresAsync(period.WeekBegin, period.WeekEnd, cancellationToken).ConfigureAwait(false);
            HonorMaintenancePlan plan = HonorMaintenancePlanner.Plan(
                scores, race => RaceClassRules.TeamForRace(race) == Team.Alliance, options.Maintenance);
            IReadOnlyCollection<int>? protectors = options.CityProtector ? CityProtectors(scores, plan.Updates) : null;

            await honor.WeekGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await store.ApplyMaintenanceAsync(
                    new HonorMaintenanceBatch(plan.Updates, period.DeleteCpBefore, new HonorMaintenanceState(period.NewLast, period.NewNext, false), protectors),
                    cancellationToken).ConfigureAwait(false);
                honor.SetWeekBegin(period.NewLast);
            }
            finally
            {
                honor.WeekGate.Release();
            }

            applied++;
            PeriodsApplied++;
            logger.LogInformation(
                "Honor maintenance applied for week {Begin}-{End}: Alliance {Alliance}, Horde {Horde}, inactive {Inactive}",
                period.WeekBegin, period.WeekEnd, plan.AllianceCount, plan.HordeCount, plan.InactiveCount);
            await WriteReportAsync(options, plan.Report, time, cancellationToken).ConfigureAwait(false);

            if (world is not null)
            {
                await ApplyToOnlinePlayersAsync(world, plan, period, protectors).ConfigureAwait(false);
            }
        }

        return applied;
    }

    /// <summary>
    /// The City Protector of each race: the ranked character with the best standing (HonorMaintenancer::SetCityRanks,
    /// HonorMgr.cpp:185-236). vmangos reads the standings stored by the PREVIOUS calculation because it assigns the titles
    /// before flushing the new ones; this assigns them from the standings just calculated (a deliberate difference, only
    /// reachable with World:Honor:CityProtector on, which defaults off like vmangos).
    /// </summary>
    public static IReadOnlyCollection<int> CityProtectors(IReadOnlyList<HonorWeeklyScore> scores, IReadOnlyList<HonorRankUpdate> updates)
    {
        Dictionary<int, HonorRankUpdate> byCharacter = updates.ToDictionary(u => u.CharacterId);
        var best = new Dictionary<byte, (uint Standing, int Id)>();
        foreach (HonorWeeklyScore score in scores)
        {
            if (score.Race is < 1 or > 8 || !byCharacter.TryGetValue(score.CharacterId, out HonorRankUpdate? update) || update.Standing == 0)
            {
                continue;
            }

            if (!best.TryGetValue(score.Race, out (uint Standing, int Id) current)
                || update.Standing < current.Standing
                || (update.Standing == current.Standing && score.CharacterId < current.Id))
            {
                best[score.Race] = (update.Standing, score.CharacterId);
            }
        }

        return [.. best.Values.Select(b => b.Id).Order()];
    }

    private static HonorMaintenanceState InitialState(uint gameDay, HonorOptions options)
    {
        uint last = HonorMaintenancePlanner.LastMaintenanceDay(gameDay, options.MaintenanceDay);
        return new HonorMaintenanceState(last, last + 7, false);
    }

    private async Task ApplyToOnlinePlayersAsync(WorldRuntime world, HonorMaintenancePlan plan, HonorPeriod period, IReadOnlyCollection<int>? protectors)
    {
        if (honor.ActiveService is not { } service)
        {
            return;
        }

        Dictionary<int, HonorRankUpdate> byCharacter = plan.Updates.ToDictionary(u => u.CharacterId);
        HashSet<int>? titled = protectors is null ? null : [.. protectors];
        await world.InvokeAsync(() =>
        {
            foreach (Player player in world.OnlinePlayers)
            {
                int id = (int)player.Guid.Low;
                service.ApplyMaintenance(player, byCharacter.GetValueOrDefault(id), period.DeleteCpBefore, titled?.Contains(id), period.NewLast);
            }

            return true;
        }).ConfigureAwait(false);
    }

    private async Task WriteReportAsync(HonorOptions options, string report, TimeProvider? time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ReportDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.ReportDirectory);
            DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
            string path = Path.Combine(options.ReportDirectory, $"HCR_{now:yyyy-MM-dd_HH-mm-ss}.txt");
            await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Can't create the honor calculation report in {Directory}", options.ReportDirectory);
        }
    }
}

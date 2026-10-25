using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The warning for one bot's think that took longer than <c>World:Playerbots:SlowThinkLogMs</c> (<see cref="PlayerbotOptions.SlowThinkLogMs"/>):
/// the bot, what drove it (its brain, party AI, bot-led group, battleground or scripted controller), its goal, target entry and quest,
/// the think's slowest phase and the rest of its <see cref="PlayerbotThinkProbe"/>. At most <see cref="MaxLinesPerWindow"/> lines per
/// <see cref="WindowMs"/> of world time; the next line logged says how many were left out.
/// </summary>
internal sealed class PlayerbotSlowThinkLog(ILogger logger)
{
    /// <summary>The most slow-think lines logged per <see cref="WindowMs"/>.</summary>
    internal const int MaxLinesPerWindow = 20;

    /// <summary>The rate-limit window (world milliseconds).</summary>
    internal const long WindowMs = 60_000;

    private long _windowStartMs = long.MinValue;
    private int _linesInWindow;
    private int _suppressed;
    private readonly HashSet<Guid> _slowEpisode = [];

    /// <summary>Slow thinks seen (logged or not) since start.</summary>
    internal long Slow { get; private set; }

    /// <summary>Lines logged since start.</summary>
    internal long Logged { get; private set; }

    /// <summary>A think of <paramref name="botId"/> that was not slow: its slow-think episode (consecutive slow thinks, reported once) is over.</summary>
    internal void EndEpisode(Guid botId) => _slowEpisode.Remove(botId);

    /// <summary>
    /// Judge one ended think (<paramref name="probe"/>) against <paramref name="thresholdMs"/> (0 or less: off) and log it when slow
    /// and the rate limit allows. Returns whether it was slow.
    /// </summary>
    /// <param name="goal">The bot's goal, target entry and quest ("n/a" when its brain did not drive this think).</param>
    internal bool Observe(PlayerbotThinkProbe probe, double thresholdMs, long nowMs, Guid botId, string name, string driver, string goal,
        Player? player = null)
    {
        if (thresholdMs <= 0 || probe.ElapsedMs < thresholdMs) return false;
        Slow++;
        if (!probe.SlowAnomalyReported && _slowEpisode.Add(botId))
        {
            // A think that is over the threshold only because the collector paused the process is not the bot's slowness: counted apart.
            bool gcStall = probe.ElapsedMs - probe.GcPauseMs < thresholdMs;
            string anomalyKey = gcStall ? "think-gc-stall" : "think-slow";
            (int g0, int g1, int g2) = probe.GcCollections;
            string anomalyDetail = string.Create(CultureInfo.InvariantCulture,
                $"think {probe.ElapsedMs:F1} ms (over {thresholdMs:F1} ms) driver={driver} goal={goal} slowestPhase={probe.SlowestPhase} gc={probe.GcPauseMs:F1} ms ({g0}/{g1}/{g2})");
            if ((player ?? probe.Player) is { } subjectPlayer)
            {
                PlayerbotAnomalies.Report(subjectPlayer, "brain", anomalyKey, anomalyDetail, AnomalySeverity.Warning);
            }
            else
            {
                string subject = string.IsNullOrWhiteSpace(name) ? $"bot {botId}" : $"bot {name} (id {botId})";
                AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + "brain", anomalyKey, subject, anomalyDetail, AnomalySeverity.Warning);
            }
            probe.SlowAnomalyReported = true;
        }
        if (_windowStartMs == long.MinValue || nowMs - _windowStartMs >= WindowMs)
        {
            _windowStartMs = nowMs;
            _linesInWindow = 0;
        }

        if (_linesInWindow >= MaxLinesPerWindow)
        {
            _suppressed++;
            return true;
        }

        _linesInWindow++;
        Logged++;
        string suppressed = _suppressed == 0 ? ""
            : string.Create(CultureInfo.InvariantCulture, $" ({_suppressed} slow thinks not logged before this one)");
        _suppressed = 0;
        logger.LogWarning("Playerbot {BotId} ({Name}) slow think {ElapsedMs} ms (over {ThresholdMs} ms) by its {Driver}: goal {Goal}; slowest phase {Phase}; {Detail}{Suppressed}",
            botId, name, Math.Round(probe.ElapsedMs, 1), thresholdMs, driver, goal, probe.SlowestPhase, probe.Describe(), suppressed);
        return true;
    }
}

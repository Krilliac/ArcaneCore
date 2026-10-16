using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Serving;

/// <summary>How the in-process engine schedules replies (from <see cref="PlayerbotChatInProcessOptions"/>; read once at load).</summary>
internal sealed record ChatServingOptions(
    int PrefixCacheTokens = 1024,
    int AmbientQueueMs = 4000,
    int PrefillChunkTokens = 64)
{
    public static ChatServingOptions Default { get; } = new();
}

/// <summary>One workload class's share of <see cref="ChatServingSnapshot"/> (latencies in milliseconds, over the recent replies).</summary>
internal readonly record struct ChatClassServing(
    WorkloadClass Workload,
    ulong Submitted,
    ulong Completed,
    ulong Cancelled,
    ulong Dropped,
    ulong Failed,
    ulong Preemptions,
    ulong GeneratedTokens,
    double TtftP50Ms,
    double TtftP95Ms,
    double InterTokenP50Ms,
    double InterTokenP95Ms,
    double QueueP50Ms,
    double QueueP95Ms);

/// <summary>
/// The scheduler and prefix-cache state the engine loop publishes (after each reply ends, and at most once a second while busy):
/// what <c>.playerbot chat status</c> prints. Immutable; read from any thread.
/// </summary>
internal sealed record ChatServingSnapshot(
    ulong Steps,
    int Active,
    int Queued,
    ulong PreemptionsPriority,
    ulong PreemptionsKvPressure,
    ulong RecomputedTokens,
    ulong QueueTimeoutDrops,
    ulong StarvationPromotions,
    double OldestQueueMs,
    IReadOnlyList<ChatClassServing> Classes,
    long PrefixRestoredTokens,
    long PrefixRestores,
    long PrefixRegisteredTokens,
    int PrefixCachedBlocks,
    int PrefixTotalBlocks,
    ulong PrefixEvictions,
    int WarmPrefixTokens)
{
    public static ChatServingSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0);

    internal static ChatServingSnapshot From(SchedulerStats s, KvPrefixStore? prefix, int warmPrefixTokens)
    {
        var classes = new ChatClassServing[SchedulerNames.WorkloadClassCount];
        for (int i = 0; i < classes.Length; i++)
        {
            ClassStats c = s.PerClass[i];
            classes[i] = new ChatClassServing((WorkloadClass)i, c.Submitted, c.Completed, c.Cancelled, c.Dropped, c.Failed, c.Preemptions,
                c.GeneratedTokens, Ms(c.Ttft.P50), Ms(c.Ttft.P95), Ms(c.InterToken.P50), Ms(c.InterToken.P95), Ms(c.QueueTime.P50), Ms(c.QueueTime.P95));
        }

        KvCacheStats cache = prefix?.Stats() ?? default;
        return new ChatServingSnapshot(s.Steps, s.ActiveSequences, s.QueuedSequences, s.PreemptionsPriority, s.PreemptionsKvPressure,
            s.RecomputedTokens, s.QueueTimeoutDrops, s.StarvationPromotions, Ms(s.CurrentOldestQueueAge), classes,
            prefix?.RestoredTokens ?? 0, prefix?.Restores ?? 0, prefix?.RegisteredTokens ?? 0, cache.CachedBlocks, cache.TotalBlocks,
            cache.Evictions, warmPrefixTokens);
    }

    private static double Ms(long microseconds) => microseconds / 1000.0;

    /// <summary>The scheduler part of the status line.</summary>
    public string Describe()
    {
        var b = new StringBuilder(256);
        CultureInfo c = CultureInfo.InvariantCulture;
        b.Append(c, $"sched active={Active} queued={Queued} preempt={PreemptionsPriority}p/{PreemptionsKvPressure}kv dropped={QueueTimeoutDrops}");
        foreach (ChatClassServing k in Classes)
        {
            if (k.Submitted == 0) continue;
            b.Append(c, $" | {k.Workload.ToName()} n={k.Completed}/{k.Submitted} ttft={k.TtftP50Ms:0}/{k.TtftP95Ms:0}ms itl={k.InterTokenP50Ms:0}/{k.InterTokenP95Ms:0}ms queue={k.QueueP50Ms:0}/{k.QueueP95Ms:0}ms");
            if (k.Cancelled + k.Dropped + k.Failed > 0) b.Append(c, $" cancelled={k.Cancelled} dropped={k.Dropped} failed={k.Failed}");
        }

        b.Append(c, $" | prefix-cache warm={WarmPrefixTokens} restored={PrefixRestoredTokens} in {PrefixRestores} stored={PrefixRegisteredTokens} blocks={PrefixCachedBlocks}/{PrefixTotalBlocks} evicted={PrefixEvictions}");
        return b.ToString();
    }
}

/// <summary>The scheduler's instruments, on the bot chat meter (p50/p95 above are over the recent replies; these are per reply).</summary>
internal static class ChatServingMeters
{
    /// <summary>By <c>workload</c>: time from the request to its first token.</summary>
    internal static Histogram<double> TimeToFirstToken { get; } = PlayerbotChatMeters.Meter.CreateHistogram<double>(
        "arcanecore.playerbot.chat.inprocess.ttft", "ms", "In-process chat model: time from a reply's request to its first token.");

    /// <summary>By <c>workload</c>: a reply's mean gap between tokens.</summary>
    internal static Histogram<double> InterToken { get; } = PlayerbotChatMeters.Meter.CreateHistogram<double>(
        "arcanecore.playerbot.chat.inprocess.itl", "ms", "In-process chat model: a reply's mean time between tokens.");

    /// <summary>By <c>workload</c>: time a reply waited for a slot (requeues after preemption included).</summary>
    internal static Histogram<double> QueueTime { get; } = PlayerbotChatMeters.Meter.CreateHistogram<double>(
        "arcanecore.playerbot.chat.inprocess.queue", "ms", "In-process chat model: time a reply waited for a generation slot.");

    /// <summary>By <c>workload</c> (of the reply preempted).</summary>
    internal static Counter<long> Preemptions { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.preemptions", "{reply}", "In-process chat replies preempted for a more urgent one (recomputed later).");

    /// <summary>Ambient replies dropped after waiting past their queue deadline.</summary>
    internal static Counter<long> Dropped { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.dropped", "{reply}", "In-process chat replies dropped for waiting too long (stale ambient chatter).");

    /// <summary>Prompt tokens served from the shared prefix cache instead of computed.</summary>
    internal static Counter<long> PrefixTokens { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.prefix_tokens", "{token}", "In-process chat prompt tokens copied from the shared prefix cache instead of computed.");
}

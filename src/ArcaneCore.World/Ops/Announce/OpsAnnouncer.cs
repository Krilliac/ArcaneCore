using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.Announce;

/// <summary>
/// What an announcement is about. Each category has its own section under <c>World:Announce</c>
/// (<see cref="AnnounceOptions"/>), its own rate limit and its own severity floor. A new source adds a member here,
/// a property to <see cref="AnnounceOptions"/>, a case in <see cref="OpsAnnouncer.OptionsFor"/> and its name to
/// <c>AnnounceConfigChecks.Categories</c>.
/// </summary>
public enum AnnounceCategory
{
    /// <summary>Data reloads, hot modules and code hot reload.</summary>
    HotSwap,

    /// <summary>Anti-cheat detections, kicks and bans.</summary>
    AntiCheat,

    /// <summary>Caught server faults, slow or hung world ticks, crashes.</summary>
    Faults,

    /// <summary>Start-up wiring audit.</summary>
    Wiring,

    /// <summary>Scheduled shutdown and restart.</summary>
    Shutdown,

    /// <summary>Character save queue trouble.</summary>
    Saves,

    /// <summary>A new playerbot QA finding.</summary>
    BotQa,
}

/// <summary>One name/value pair of staff-only detail.</summary>
public readonly record struct AnnounceField(string Name, string Value);

/// <summary>What the sink delivers. A null text is not sent.</summary>
/// <param name="StaffChat">Chat line for every online account above Player.</param>
/// <param name="StaffNotify">On-screen notification for the same accounts.</param>
/// <param name="WorldChat">System chat line for every online player.</param>
/// <param name="StaffHandled">The caller already told the staff itself: they are not sent <paramref name="WorldChat"/> as well.</param>
public sealed record AnnounceDelivery(string? StaffChat, string? StaffNotify, string? WorldChat, bool StaffHandled = false);

/// <summary>Sends a delivery to the online players. Any thread may call it; implementations hop to the world thread.</summary>
public interface IAnnounceSink
{
    void Deliver(AnnounceDelivery delivery);
}

/// <summary>
/// The one entry point for operational announcements. Any thread may call <see cref="Announce"/>; it never blocks and never throws.
/// </summary>
public interface IOpsAnnouncer
{
    /// <summary>
    /// Log, count and (subject to the category's switches, severity floor and rate limit) deliver an announcement.
    /// </summary>
    /// <param name="category">The source; selects the options section.</param>
    /// <param name="severity">Compared with the category's <c>MinimumSeverity</c>.</param>
    /// <param name="shortText">One line that is safe for players: it is the world message and the first part of the staff line. No account or character names.</param>
    /// <param name="gmDetail">Staff-only detail (who triggered it, durations, hashes). Appended to the staff line only when the category's <c>IncludeDetails</c> is on. Never sent to players.</param>
    /// <param name="fields">Staff-only name/value detail, same rules as <paramref name="gmDetail"/>.</param>
    /// <param name="shortTextWithNames">A world-message text that names characters. Used only when the category's <c>AllowNamesInWorldMessage</c> is on; otherwise <paramref name="shortText"/> is sent.</param>
    /// <param name="gmOnly">Never send a world message for this one (a failure players need not hear about).</param>
    /// <param name="dedupKey">Identity for the duplicate window; defaults to <paramref name="shortText"/>.</param>
    /// <param name="staffHandled">The caller already told the staff online itself (full detail, outside this category's switches and rate limit); send only the world message, and only if the category has one and <paramref name="gmOnly"/> is off.</param>
    void Announce(
        AnnounceCategory category,
        AnnounceSeverity severity,
        string shortText,
        string? gmDetail = null,
        IReadOnlyList<AnnounceField>? fields = null,
        string? shortTextWithNames = null,
        bool gmOnly = false,
        string? dedupKey = null,
        bool staffHandled = false);
}

/// <summary>How the announcer treated a call (the <c>outcome</c> tag of <c>arcanecore.announce</c>).</summary>
public static class AnnounceOutcome
{
    public const string Sent = "sent";
    public const string Disabled = "disabled";
    public const string BelowMinimum = "below_minimum";
    public const string Off = "off";

    /// <summary>
    /// Nothing for the announcer to deliver, because the caller told the staff itself (<c>staffHandled</c>) and there is no world
    /// line: the staff were notified directly, not left uninformed as <see cref="Off"/> would suggest.
    /// </summary>
    public const string StaffDirect = "staff_direct";
    public const string RateLimited = "rate_limited";
    public const string Duplicate = "duplicate";
}

internal static class AnnounceMeters
{
    private static readonly Meter Meter = new("ArcaneCore.Announce", "1.0");

    public static readonly Counter<long> Announcements = Meter.CreateCounter<long>(
        "arcanecore.announce", "{announcement}", "Operational announcements by category, severity, outcome (sent, disabled, below_minimum, off, staff_direct, rate_limited, duplicate) and staff_direct (the caller sent the staff line itself).");
}

/// <summary>
/// <see cref="IOpsAnnouncer"/> for the world daemon. Thread-safe. State: one token bucket and one duplicate window per category,
/// guarded by a single lock held for a few dictionary operations; delivery happens outside the lock through <see cref="IAnnounceSink"/>.
/// </summary>
public sealed class OpsAnnouncer : IOpsAnnouncer
{
    private const int MaxShort = 200;
    private const int MaxDetail = 500;
    private const int MaxDedupEntries = 256;

    private readonly AnnounceOptions _options;
    private readonly IAnnounceSink _sink;
    private readonly ILogger _logger;
    private readonly Func<long> _nowMs;
    private readonly Lock _gate = new();
    private readonly Dictionary<AnnounceCategory, CategoryState> _states = [];

    public OpsAnnouncer(AnnounceOptions options, IAnnounceSink sink, ILogger logger, Func<long>? nowMs = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    /// <summary>The options of <paramref name="category"/>.</summary>
    public AnnounceCategoryOptions OptionsFor(AnnounceCategory category) => category switch
    {
        AnnounceCategory.HotSwap => _options.HotSwap,
        AnnounceCategory.AntiCheat => _options.AntiCheat,
        AnnounceCategory.Faults => _options.Faults,
        AnnounceCategory.Wiring => _options.Wiring,
        AnnounceCategory.Shutdown => _options.Shutdown,
        AnnounceCategory.Saves => _options.Saves,
        AnnounceCategory.BotQa => _options.BotQa,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "no options for this category"),
    };

    public void Announce(
        AnnounceCategory category,
        AnnounceSeverity severity,
        string shortText,
        string? gmDetail = null,
        IReadOnlyList<AnnounceField>? fields = null,
        string? shortTextWithNames = null,
        bool gmOnly = false,
        string? dedupKey = null,
        bool staffHandled = false)
    {
        try
        {
            AnnounceCore(category, severity, shortText, gmDetail, fields, shortTextWithNames, gmOnly, dedupKey, staffHandled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "announcement ({Category}) failed", category);
        }
    }

    private void AnnounceCore(
        AnnounceCategory category,
        AnnounceSeverity severity,
        string shortText,
        string? gmDetail,
        IReadOnlyList<AnnounceField>? fields,
        string? shortTextWithNames,
        bool gmOnly,
        string? dedupKey,
        bool staffHandled)
    {
        AnnounceCategoryOptions options = OptionsFor(category);
        string text = Clean(shortText, MaxShort);
        string detail = BuildDetail(gmDetail, fields);
        bool toStaff = options.GmNotify && !staffHandled;
        bool toWorld = options.WorldMessage && !gmOnly;

        string outcome = Decide(category, severity, options, toStaff || toWorld, dedupKey ?? text, out int suppressed);
        if (outcome == AnnounceOutcome.Off && staffHandled)
        {
            outcome = AnnounceOutcome.StaffDirect; // the staff already have the line; only the (switched off) world line is missing
        }

        AnnounceMeters.Announcements.Add(
            1,
            new KeyValuePair<string, object?>("category", category.ToString()),
            new KeyValuePair<string, object?>("severity", severity.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("staff_direct", staffHandled));

        // A repeat inside the duplicate window is expected noise (a SQLite lock storm repeats every few seconds): Debug, and counted in the metric.
        _logger.Log(
            outcome == AnnounceOutcome.Duplicate ? LogLevel.Debug : severity >= AnnounceSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
            "Announce [{Category}/{Severity}] {Text}{Detail} ({Outcome})",
            category, severity, text, detail.Length == 0 ? string.Empty : " | " + detail, outcome);
        if (outcome != AnnounceOutcome.Sent)
        {
            return;
        }

        string tag = $"[{category}]";
        string? staffChat = null;
        string? staffNotify = null;
        if (toStaff)
        {
            string line = text;
            if (options.IncludeDetails && detail.Length > 0)
            {
                line += " | " + detail;
            }

            if (suppressed > 0)
            {
                line += $" (+{suppressed} suppressed by the rate limit)";
            }

            staffChat = $"|cff00ccff{tag}|r {line}";
            staffNotify = $"{tag} {text}";
        }

        string? worldChat = null;
        if (toWorld)
        {
            string world = options.AllowNamesInWorldMessage && shortTextWithNames is not null ? Clean(shortTextWithNames, MaxShort) : text;
            worldChat = $"|cffffcc00[Server]|r {world}";
        }

        if (staffChat is not null || worldChat is not null)
        {
            _sink.Deliver(new AnnounceDelivery(staffChat, staffNotify, worldChat, staffHandled));
        }
    }

    /// <summary>The outcome for this call; consumes a rate-limit token and records the duplicate window when it is <see cref="AnnounceOutcome.Sent"/>.</summary>
    private string Decide(AnnounceCategory category, AnnounceSeverity severity, AnnounceCategoryOptions options, bool anyoneToTell, string key, out int suppressed)
    {
        suppressed = 0;
        if (!_options.Enabled)
        {
            return AnnounceOutcome.Disabled;
        }

        if (severity < options.MinimumSeverity)
        {
            return AnnounceOutcome.BelowMinimum;
        }

        if (!anyoneToTell)
        {
            return AnnounceOutcome.Off;
        }

        long now = _nowMs();
        lock (_gate)
        {
            if (!_states.TryGetValue(category, out CategoryState? state))
            {
                state = new CategoryState(options.RateBurst, now);
                _states[category] = state;
            }

            if (options.DedupSeconds > 0)
            {
                long window = options.DedupSeconds * 1000L;
                if (state.Recent.TryGetValue(key, out long last) && now - last < window)
                {
                    state.Suppressed++;
                    return AnnounceOutcome.Duplicate;
                }

                if (state.Recent.Count >= MaxDedupEntries)
                {
                    foreach (string stale in state.Recent.Where(p => now - p.Value >= window).Select(p => p.Key).ToArray())
                    {
                        state.Recent.Remove(stale);
                    }

                    if (state.Recent.Count >= MaxDedupEntries)
                    {
                        state.Recent.Clear();
                    }
                }
            }

            // Critical (a hung world thread, a crash) is never held back by the rate limit: a fault storm must not hide it. The duplicate window still applies.
            if (options.RateBurst > 0 && severity < AnnounceSeverity.Critical)
            {
                double refill = options.RatePerMinute * (now - state.RefillMs) / 60_000.0;
                state.Tokens = Math.Min(options.RateBurst, state.Tokens + Math.Max(0, refill));
                state.RefillMs = now;
                if (state.Tokens < 1)
                {
                    state.Suppressed++;
                    return AnnounceOutcome.RateLimited;
                }

                state.Tokens -= 1;
            }

            if (options.DedupSeconds > 0)
            {
                state.Recent[key] = now;
            }

            suppressed = state.Suppressed;
            state.Suppressed = 0;
            return AnnounceOutcome.Sent;
        }
    }

    /// <summary>The GM-only detail line: the cleaned detail then the cleaned fields, cut at <c>MaxDetail</c> characters without splitting a surrogate pair.</summary>
    public static string BuildDetail(string? gmDetail, IReadOnlyList<AnnounceField>? fields)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(gmDetail))
        {
            sb.Append(Clean(gmDetail, MaxDetail));
        }

        if (fields is not null)
        {
            foreach (AnnounceField field in fields)
            {
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Clean(field.Name, 40)).Append('=').Append(Clean(field.Value, 120));
            }
        }

        if (sb.Length <= MaxDetail)
        {
            return sb.ToString();
        }

        // Clean only lets whole pairs through, so a high surrogate at the cut is always followed by its low half: cut before it.
        int cut = char.IsHighSurrogate(sb[MaxDetail - 1]) ? MaxDetail - 1 : MaxDetail;
        return sb.ToString(0, cut);
    }

    /// <summary>One line, no control or bidi format characters, whole surrogate pairs only, no client escape codes (a pipe would let typed text inject colours or links), at most <paramref name="max"/> characters.</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text.Length, max));
        string trimmed = text.Trim();
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsHighSurrogate(c) && i + 1 < trimmed.Length && char.IsLowSurrogate(trimmed[i + 1]))
            {
                if (sb.Length + 2 > max)
                {
                    break; // never cut a surrogate pair in half
                }

                sb.Append(c).Append(trimmed[i + 1]);
                i++;
                continue;
            }

            if (sb.Length >= max)
            {
                break;
            }

            if (char.IsSurrogate(c))
            {
                sb.Append('?'); // a lone surrogate is not valid text
            }
            else if (IsBidiFormat(c))
            {
                // dropped: direction overrides could make an announcement read differently from what it says
            }
            else if (char.IsControl(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c == '|' ? '/' : c);
            }
        }

        return sb.ToString();
    }

    private static bool IsBidiFormat(char c) => c is '\u061C' or '\u200E' or '\u200F' || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');

    private sealed class CategoryState(int burst, long nowMs)
    {
        public double Tokens { get; set; } = burst;

        public long RefillMs { get; set; } = nowMs;

        public int Suppressed { get; set; }

        public Dictionary<string, long> Recent { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Delivers to the online players on the world thread (<see cref="WorldRuntime.Post"/>): staff are the accounts above
/// <see cref="AccountSecurity.Player"/>, the same set <c>.gmnotify</c> uses.
/// </summary>
public sealed class WorldAnnounceSink(Func<WorldRuntime> world) : IAnnounceSink
{
    public void Deliver(AnnounceDelivery delivery)
    {
        WorldRuntime runtime = world();
        runtime.Post(() =>
        {
            byte[]? staffChat = delivery.StaffChat is null ? null : ChatPackets.BuildSystemMessage(delivery.StaffChat);
            byte[]? staffNotify = delivery.StaffNotify is null ? null : ChatPackets.BuildNotification(delivery.StaffNotify);
            byte[]? worldChat = delivery.WorldChat is null ? null : ChatPackets.BuildSystemMessage(delivery.WorldChat);
            foreach (Player player in runtime.OnlinePlayers)
            {
                bool staff = player.Security > AccountSecurity.Player;

                // Staff who get the (longer) staff line are not sent the world line as well.
                if (worldChat is not null && !(staff && (staffChat is not null || delivery.StaffHandled)))
                {
                    player.Session.Send(WorldOpcode.SmsgMessagechat, worldChat);
                }

                if (staff)
                {
                    if (staffChat is not null)
                    {
                        player.Session.Send(WorldOpcode.SmsgMessagechat, staffChat);
                    }

                    if (staffNotify is not null)
                    {
                        player.Session.Send(WorldOpcode.SmsgNotification, staffNotify);
                    }
                }
            }
        });
    }
}

/// <summary>DI registration of the announcement service and the hot swap reporter.</summary>
public static class OpsAnnouncerServiceCollectionExtensions
{
    public static IServiceCollection AddOpsAnnouncer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AnnounceOptions>(configuration.GetSection(AnnounceOptions.SectionName));
        services.TryAddSingleton<IAnnounceSink>(sp => new WorldAnnounceSink(sp.GetRequiredService<WorldRuntime>));
        services.TryAddSingleton<IOpsAnnouncer>(sp => new OpsAnnouncer(
            sp.GetRequiredService<IOptions<AnnounceOptions>>().Value,
            sp.GetRequiredService<IAnnounceSink>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.Announce")));
        services.TryAddSingleton<IHotSwapReporter>(sp => new HotSwapReporter(
            sp.GetRequiredService<IOpsAnnouncer>(),
            sp.GetRequiredService<IOptions<AnnounceOptions>>().Value.HotSwap));
        return services;
    }
}

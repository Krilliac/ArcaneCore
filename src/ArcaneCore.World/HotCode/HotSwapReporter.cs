using System.Diagnostics.Metrics;
using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Ops.Announce;

namespace ArcaneCore.World.HotCode;

/// <summary>Which live-change lane a swap came through.</summary>
public enum HotSwapLane
{
    /// <summary>Database content reloaded without a restart (<c>.reload</c>).</summary>
    Data,

    /// <summary>A hot module loaded, replaced or unloaded (<c>.hotmodule</c>).</summary>
    Module,

    /// <summary>The runtime applied a code edit (<c>dotnet watch</c>, Development / Staging only).</summary>
    Code,

    /// <summary>A world feature detached, attached or cycled (<c>.feature</c>, docs/design/hot-reload-tiers.md section 4.2).</summary>
    Feature,
}

/// <summary>How a swap ended.</summary>
public enum HotSwapResult
{
    /// <summary>The change is live.</summary>
    Applied,

    /// <summary>The change was checked and refused; nothing changed.</summary>
    Rejected,

    /// <summary>The change was attempted and failed (rolled back where the lane can).</summary>
    Failed,

    /// <summary>Nothing needed changing (the loaded content was kept).</summary>
    Unchanged,
}

/// <summary>Who asked for a swap and why. Staff-only: never put into a world message except the reason, and only when the options allow it.</summary>
public sealed record HotSwapOrigin(string Actor, string Reason = "")
{
    /// <summary>The server console.</summary>
    public static HotSwapOrigin Console { get; } = new("console");

    /// <summary>Done by the server itself while starting (<c>LoadOnStart</c>).</summary>
    public static HotSwapOrigin Startup { get; } = new("startup");

    /// <summary>A change the runtime applied on its own (a code edit from <c>dotnet watch</c>).</summary>
    public static HotSwapOrigin Runtime { get; } = new("dotnet watch");

    /// <summary>The actor of a chat command: <c>console</c>, or the account name and character name of the staff member (staff-only text).</summary>
    public static HotSwapOrigin FromCommand(CommandContext context, string reason = "")
    {
        ArgumentNullException.ThrowIfNull(context);
        return new HotSwapOrigin(
            context.IsConsole ? "console" : $"{context.Session.AccountName} ({context.Player.Name})",
            reason);
    }

    /// <summary>The same actor with <paramref name="reason"/>.</summary>
    public HotSwapOrigin WithReason(string reason) => this with { Reason = reason };
}

/// <summary>One swap, as reported by a lane.</summary>
/// <param name="Lane">Which lane.</param>
/// <param name="Action">What was asked: reload, load, replace, unload, metadata-update, refresh.</param>
/// <param name="Area">What it touched: the reloadable name, the module name, the assemblies of a code edit.</param>
/// <param name="Result">How it ended.</param>
/// <param name="Origin">Who asked, and why.</param>
/// <param name="Duration">How long it took (zero when unknown).</param>
/// <param name="Detail">What changed: counts, registrations, the reason it was refused.</param>
/// <param name="Fields">Extra staff-only name/value pairs (hash prefix, opcode counts).</param>
public sealed record HotSwapEvent(
    HotSwapLane Lane,
    string Action,
    string Area,
    HotSwapResult Result,
    HotSwapOrigin Origin,
    TimeSpan Duration,
    string Detail,
    IReadOnlyList<AnnounceField>? Fields = null);

/// <summary>What every lane reports to after a swap was applied or turned down. Any thread; never throws.</summary>
public interface IHotSwapReporter
{
    void Report(HotSwapEvent swap);
}

/// <summary>A reporter that does nothing (a lane built without announcements, e.g. in a unit test).</summary>
public sealed class NullHotSwapReporter : IHotSwapReporter
{
    public static NullHotSwapReporter Instance { get; } = new();

    public void Report(HotSwapEvent swap)
    {
    }
}

/// <summary>
/// Turns lane reports into announcements (<see cref="IOpsAnnouncer"/>, category <see cref="AnnounceCategory.HotSwap"/>),
/// and always counts them in <c>arcanecore.hotswap{kind,result}</c>, whatever the announcement switches say.
/// Staff get who/when/how long/what; the world message is one short player-safe line: the area, the result word and
/// (if the options allow) the reason the staff member typed, never an account name, count, hash or duration.
/// </summary>
public sealed class HotSwapReporter : IHotSwapReporter
{
    private static readonly Meter MeterInstance = new("ArcaneCore.HotSwap", "1.0");

    private static readonly Counter<long> Swaps = MeterInstance.CreateCounter<long>(
        "arcanecore.hotswap", "{swap}", "Live changes by kind (data, module, code) and result (applied, rejected, failed, unchanged).");

    private readonly IOpsAnnouncer _announcer;
    private readonly HotSwapAnnounceOptions _options;

    public HotSwapReporter(IOpsAnnouncer announcer, HotSwapAnnounceOptions options)
    {
        _announcer = announcer ?? throw new ArgumentNullException(nameof(announcer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Report(HotSwapEvent swap)
    {
        ArgumentNullException.ThrowIfNull(swap);
        Swaps.Add(
            1,
            new KeyValuePair<string, object?>("kind", swap.Lane.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("result", swap.Result.ToString().ToLowerInvariant()));

        if (!LaneSelected(swap.Lane))
        {
            return;
        }

        bool failed = swap.Result is HotSwapResult.Rejected or HotSwapResult.Failed;
        AnnounceSeverity severity = swap.Result switch
        {
            HotSwapResult.Failed => AnnounceSeverity.Error,
            HotSwapResult.Rejected => AnnounceSeverity.Warning,
            _ => AnnounceSeverity.Info,
        };

        string reason = AnnounceTexts.Reason(swap.Origin.Reason);
        string shortText = $"{Describe(swap)} {ResultWord(swap.Result)}";
        if (!failed && _options.WorldMessageIncludeReason && reason.Length > 0)
        {
            shortText += $" ({reason})";
        }

        var staff = new List<string> { $"by {swap.Origin.Actor}" };
        if (reason.Length > 0)
        {
            staff.Add($"reason: {reason}");
        }

        if (swap.Duration > TimeSpan.Zero)
        {
            staff.Add(string.Create(CultureInfo.InvariantCulture, $"{swap.Duration.TotalMilliseconds:0} ms"));
        }

        if (!string.IsNullOrWhiteSpace(swap.Detail))
        {
            staff.Add(swap.Detail);
        }

        _announcer.Announce(
            AnnounceCategory.HotSwap,
            severity,
            shortText,
            string.Join("; ", staff),
            swap.Fields,
            gmOnly: failed && !_options.WorldMessageOnFailure || swap.Result == HotSwapResult.Unchanged,
            dedupKey: $"{swap.Lane}|{swap.Action}|{swap.Area}|{swap.Result}|{swap.Origin.Actor}");
    }

    private bool LaneSelected(HotSwapLane lane)
        => _options.Lanes.Count == 0 || _options.Lanes.Any(l => string.Equals(l?.Trim(), lane.ToString(), StringComparison.OrdinalIgnoreCase));

    private static string Describe(HotSwapEvent swap) => swap.Lane switch
    {
        HotSwapLane.Data => $"Live data reload '{swap.Area}'",
        HotSwapLane.Module => $"Server module '{swap.Area}' {ActionWord(swap.Action)}",
        HotSwapLane.Feature => $"Server feature '{swap.Area}' {swap.Action}",
        _ => $"Server code update ({swap.Area})",
    };

    private static string ActionWord(string action) => action switch
    {
        "load" => "load",
        "replace" or "reload" => "replace",
        "unload" => "unload",
        _ => action,
    };

    private static string ResultWord(HotSwapResult result) => result switch
    {
        HotSwapResult.Applied => "applied",
        HotSwapResult.Rejected => "was REJECTED, nothing changed",
        HotSwapResult.Failed => "FAILED",
        _ => "needed no change",
    };
}

/// <summary>Text helpers shared by the lanes that take a free-text reason.</summary>
public static class AnnounceTexts
{
    /// <summary>Longest reason kept.</summary>
    public const int MaxReason = 120;

    /// <summary>The reason on one line without control characters or client escape codes, at most <see cref="MaxReason"/> characters; empty when blank.</summary>
    public static string Reason(string? reason) => OpsAnnouncer.Clean(reason, MaxReason);

    /// <summary>
    /// Split the argument of <c>.reload</c> / <c>.hotmodule</c> into the first word and the optional free-text reason after it.
    /// Old syntax (one word) gives an empty reason.
    /// </summary>
    public static (string Word, string Reason) SplitReason(string args)
    {
        string trimmed = (args ?? string.Empty).Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], Reason(trimmed[(space + 1)..]));
    }
}

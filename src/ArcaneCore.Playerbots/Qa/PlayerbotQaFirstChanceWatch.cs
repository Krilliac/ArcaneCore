using System.Collections.Concurrent;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Turns the crash handler's exception stream (<see cref="FaultObservers"/>) into <see cref="QaSignalKind.ServerFault"/> signals, without going through log text.
/// <list type="bullet">
/// <item>First-chance exceptions (every throw in the process, most of them handled and benign) are grouped by exception type and the first <c>ArcaneCore</c> frame of
/// the throw site. A group is reported once it was thrown <see cref="PlayerbotQaFaultOptions.FirstChanceMinRepeats"/> times inside
/// <see cref="PlayerbotQaFaultOptions.FirstChanceWindowSeconds"/>; what it is thrown afterwards is added to the same finding by <see cref="Flush"/>. A type on
/// <see cref="PlayerbotQaFaultOptions.FirstChanceDenylist"/> (or <see cref="CrashHandler.IsControlledProbe"/>) is never counted.</item>
/// <item>An unobserved task exception or an unhandled exception is always reported, at once, with no threshold and no denylist, unless the crash hook already
/// recorded it in the fault ledger (which QA reads: <see cref="PlayerbotQaFaultFeed"/>), so one fault is never filed twice.</item>
/// </list>
/// Threads: <see cref="Observe"/> runs on the throwing thread (the world thread included) and on the finalizer thread; it takes one short per-group lock and a
/// non-blocking bus write, walks the stack once per counted first-chance throw (bounded to <see cref="MaxStackWalksPerSecond"/> a second) and never throws.
/// The finding identity is <see cref="PlayerbotQaFaultIds"/>, shared with the log tap. Memory: at most <see cref="MaxGroups"/> groups.
/// </summary>
internal sealed class PlayerbotQaFirstChanceWatch : IFaultObserver
{
    internal const int MaxGroups = 2048;
    internal const int MaxStackWalksPerSecond = 4000;
    private const int MaxChainDepth = 8;
    private static readonly TimeSpan ReportedGroupIdle = TimeSpan.FromHours(6);

    private readonly PlayerbotQaSignalBus _bus;
    private readonly PlayerbotQaFaultOptions _options;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _denyTypes = new(StringComparer.Ordinal);
    private readonly List<(string Type, string Frame)> _denySites = [];
    private readonly ConcurrentDictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private long _bucketSecond;
    private int _bucketCount;
    private long _signals;
    private long _denied;
    private long _throttled;
    private long _overflowed;

    public PlayerbotQaFirstChanceWatch(PlayerbotQaSignalBus bus, PlayerbotQaFaultOptions options, TimeProvider? time = null)
    {
        _bus = bus;
        _options = options;
        _time = time ?? TimeProvider.System;
        foreach (string raw in options.FirstChanceDenylist ?? [])
        {
            string entry = raw.Trim();
            int at = entry.IndexOf('@', StringComparison.Ordinal);
            if (at < 0)
            {
                _denyTypes.Add(entry);
            }
            else if (at > 0 && at < entry.Length - 1)
            {
                _denySites.Add((entry[..at].Trim(), entry[(at + 1)..].Trim()));
            }
        }
    }

    /// <summary>Signals written to the bus.</summary>
    internal long Signals => Interlocked.Read(ref _signals);

    /// <summary>First-chance throws skipped because their type or site is on the denylist (or is a controlled probe).</summary>
    internal long Denied => Interlocked.Read(ref _denied);

    /// <summary>First-chance throws not looked at because more than <see cref="MaxStackWalksPerSecond"/> arrived in one second.</summary>
    internal long Throttled => Interlocked.Read(ref _throttled);

    /// <summary>First-chance throws of a new group dropped because <see cref="MaxGroups"/> groups were tracked.</summary>
    internal long Overflowed => Interlocked.Read(ref _overflowed);

    /// <summary>The groups tracked now.</summary>
    internal int Groups => _groups.Count;

    public void Observe(FaultObservation kind, Exception exception, bool ledgerRecorded)
    {
        if (kind == FaultObservation.FirstChance)
        {
            ObserveFirstChance(exception, null);
        }
        else if (!ledgerRecorded)
        {
            ReportAlways(kind, exception);
        }
    }

    /// <summary>One first-chance throw; <paramref name="frame"/> is the throw site (null: found by walking the calling thread's stack).</summary>
    internal void ObserveFirstChance(Exception exception, string? frame)
    {
        try
        {
            if (!_options.FirstChanceEnabled)
            {
                return;
            }

            if (CrashHandler.IsControlledProbe(exception) || IsDenied(exception, null))
            {
                Interlocked.Increment(ref _denied);
                return;
            }

            if (!TakeStackWalk())
            {
                Interlocked.Increment(ref _throttled);
                return;
            }

            frame ??= ThrowSite();
            if (_denySites.Count > 0 && IsDenied(exception, frame))
            {
                Interlocked.Increment(ref _denied);
                return;
            }

            string type = exception.GetType().FullName ?? exception.GetType().Name;
            string key = type + "|" + frame;
            DateTime now = _time.GetUtcNow().UtcDateTime;
            if (!_groups.TryGetValue(key, out Group? group))
            {
                if (_groups.Count >= MaxGroups)
                {
                    Interlocked.Increment(ref _overflowed);
                    return;
                }

                group = _groups.GetOrAdd(key, _ => new Group(type, frame, now));
            }

            long report = 0;
            lock (group)
            {
                if (!group.Reported && now - group.WindowStart > TimeSpan.FromSeconds(_options.FirstChanceWindowSeconds))
                {
                    group.WindowStart = now;
                    group.InWindow = 0;
                }

                group.InWindow++;
                group.LastSeen = now;
                if (group.Reported)
                {
                    group.Unflushed++;
                }
                else if (group.InWindow >= _options.FirstChanceMinRepeats)
                {
                    group.Reported = true;
                    report = group.InWindow;
                }
            }

            if (report > 0)
            {
                Write(FirstChanceSignal(group, report, now));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The first-chance hook runs on every throw in the process: nothing here may throw into it.
        }
    }

    /// <summary>
    /// Pass thread: send what the reported groups were thrown since the last call (one signal per group), and forget groups that can no longer reach the threshold
    /// or have been quiet for hours. Returns the signals written.
    /// </summary>
    internal int Flush()
    {
        int written = 0;
        DateTime now = _time.GetUtcNow().UtcDateTime;
        TimeSpan window = TimeSpan.FromSeconds(_options.FirstChanceWindowSeconds);
        foreach (KeyValuePair<string, Group> pair in _groups)
        {
            Group group = pair.Value;
            long delta;
            bool forget;
            lock (group)
            {
                delta = group.Unflushed;
                group.Unflushed = 0;
                forget = group.Reported ? now - group.LastSeen > ReportedGroupIdle : now - group.WindowStart > window;
            }

            if (delta > 0)
            {
                Write(FirstChanceSignal(group, delta, now));
                written++;
            }

            if (forget)
            {
                _groups.TryRemove(pair.Key, out _);
            }
        }

        return written;
    }

    private void ReportAlways(FaultObservation kind, Exception exception)
    {
        try
        {
            string id = PlayerbotQaFaultIds.Describe(exception, out string type, out string frame, out IReadOnlyList<string> frames);
            string site = kind == FaultObservation.Unhandled ? "unhandled" : "unobserved-task";
            string condensed = FaultLedger.FormatCondensedStack(exception);
            Write(new QaSignal
            {
                Kind = QaSignalKind.ServerFault,
                AtUtc = _time.GetUtcNow().UtcDateTime,
                FaultId = id,
                Goal = site,
                ExceptionType = type,
                Reason = type,
                Detail = frames.Count > 0 ? string.Join(" > ", frames) : frame,
                Diag = condensed.Length > 0 ? condensed : frame,
                Message = string.Empty,
                Occurrences = 1,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Called from the finalizer thread or a dying process: never throws.
        }
    }

    private QaSignal FirstChanceSignal(Group group, long occurrences, DateTime now) => new()
    {
        Kind = QaSignalKind.ServerFault,
        AtUtc = now,
        FaultId = PlayerbotQaFaultIds.Of(group.Type, group.Frame),
        Goal = "first-chance",
        ExceptionType = group.Type,
        Reason = group.Type,
        Detail = group.Frame,
        Diag = $"{group.Type} thrown at {group.Frame} and caught; reported after {_options.FirstChanceMinRepeats} throws within {_options.FirstChanceWindowSeconds} s",
        Message = string.Empty,
        Occurrences = occurrences,
    };

    private void Write(QaSignal signal)
    {
        _bus.TryWrite(signal);
        Interlocked.Increment(ref _signals);
    }

    private bool TakeStackWalk()
    {
        long second = Environment.TickCount64 / 1000;
        if (Interlocked.Exchange(ref _bucketSecond, second) != second)
        {
            Interlocked.Exchange(ref _bucketCount, 0);
        }

        return Interlocked.Increment(ref _bucketCount) <= MaxStackWalksPerSecond;
    }

    /// <summary>The first <c>ArcaneCore</c> frame of the calling thread's stack that is not this class.</summary>
    private static string ThrowSite()
    {
        foreach (string frame in FaultLedger.CurrentStackFrames(8))
        {
            if (!frame.StartsWith(nameof(PlayerbotQaFirstChanceWatch) + ".", StringComparison.Ordinal))
            {
                return frame;
            }
        }

        return PlayerbotQaFaultIds.NoFrame;
    }

    /// <summary>
    /// Whether <paramref name="exception"/>, a type it derives from, or an exception in its inner chain is on the denylist; with a <paramref name="frame"/>, the
    /// <c>Type@Frame</c> entries count too (without one only the type-only entries do).
    /// </summary>
    private bool IsDenied(Exception exception, string? frame)
    {
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < MaxChainDepth; depth++, current = current.InnerException)
        {
            for (Type? type = current.GetType(); type is not null; type = type.BaseType)
            {
                string? name = type.FullName;
                if (name is null)
                {
                    continue;
                }

                if (frame is null ? _denyTypes.Contains(name) : SiteDenied(name, frame))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool SiteDenied(string typeName, string frame)
    {
        foreach ((string Type, string Frame) site in _denySites)
        {
            if (string.Equals(site.Type, typeName, StringComparison.Ordinal) && frame.Contains(site.Frame, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Group(string type, string frame, DateTime start)
    {
        public string Type { get; } = type;

        public string Frame { get; } = frame;

        public DateTime WindowStart = start;
        public DateTime LastSeen = start;
        public long InWindow;
        public long Unflushed;
        public bool Reported;
    }
}

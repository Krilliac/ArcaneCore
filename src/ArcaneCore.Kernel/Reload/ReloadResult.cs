namespace ArcaneCore.Kernel.Reload;

/// <summary>How one reload ended. Everything except <see cref="Applied"/> leaves the running content untouched.</summary>
public enum ReloadStatus
{
    /// <summary>The candidate was built, validated and swapped in on the world thread.</summary>
    Applied,

    /// <summary>
    /// The source was readable but deliberately not applied, so what is loaded stays (vmangos
    /// <c>SpellMgr::LoadSpells</c> returns before touching the table when <c>spell_template</c> is
    /// empty — SpellMgr.cpp:3702-3724).
    /// </summary>
    KeptCurrent,

    /// <summary>Validation of the candidate found problems; nothing was swapped.</summary>
    Rejected,

    /// <summary>Building, or committing (then rolled back), failed or timed out; nothing is left half applied.</summary>
    Failed,

    /// <summary>Another reload was already running; this request did nothing.</summary>
    Busy,
}

/// <summary>The outcome of one reload request, as shown by <c>.reload</c> and <c>.reload status</c>.</summary>
/// <param name="Name">The reloadable's name (the <c>.reload</c> sub-command, e.g. <c>spell_template</c>).</param>
/// <param name="Status">How it ended.</param>
/// <param name="Message">One line for the invoker.</param>
/// <param name="Notes">Extra lines: validation problems, warnings, restart-required options.</param>
/// <param name="Elapsed">Wall time from request to outcome.</param>
/// <param name="At">When it ended.</param>
public sealed record ReloadResult(
    string Name,
    ReloadStatus Status,
    string Message,
    IReadOnlyList<string> Notes,
    TimeSpan Elapsed,
    DateTimeOffset At)
{
    /// <summary>True when the running content changed.</summary>
    public bool Changed => Status == ReloadStatus.Applied;
}

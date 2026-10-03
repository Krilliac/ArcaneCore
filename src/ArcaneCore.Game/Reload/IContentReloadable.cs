using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Reload;

/// <summary>
/// One reloadable piece of content or configuration (the <c>.reload &lt;name&gt;</c> sub-commands
/// of vmangos' reload tree, Chat.cpp:794-935). The reload runs in three phases the coordinator
/// owns: <see cref="BuildAsync"/> on a worker thread (read, build, never touch live state),
/// <see cref="ContentCandidate.Validate"/> on that worker, then
/// <see cref="ContentCandidate.Commit"/> on the world thread between ticks, so live readers see
/// either the old content or the new content, never a mixture.
/// </summary>
public interface IContentReloadable
{
    /// <summary>The <c>.reload</c> sub-command, lower case with underscores (e.g. <c>spell_template</c>).</summary>
    string Name { get; }

    /// <summary>Whether <c>.reload all</c> includes it. vmangos' <c>reload all</c> (ServerCommands.cpp:885-905) does not reload the config.</summary>
    bool IncludedInAll => true;

    /// <summary>Build a candidate from the current source of truth without touching live state (worker thread).</summary>
    Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>A fully built replacement for live content, waiting to be validated and swapped in.</summary>
public abstract class ContentCandidate
{
    /// <summary>What the candidate holds, for the success line (e.g. "1234 spells").</summary>
    public abstract string Summary { get; }

    /// <summary>Problems that make the candidate unfit to go live; any entry rejects the reload (worker thread).</summary>
    public virtual IReadOnlyList<string> Validate() => [];

    /// <summary>
    /// Whether the live content should be kept instead of swapped (world thread, just before
    /// <see cref="Commit"/>), with the reason. The retail early-out for an empty source table.
    /// </summary>
    public virtual bool TryKeepCurrent(WorldRuntime world, out string reason)
    {
        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Swap the candidate in (world thread). Apply through <see cref="ReloadTransaction.Step"/> so
    /// a failure part-way rolls the earlier steps back.
    /// </summary>
    public abstract void Commit(WorldRuntime world, ReloadTransaction transaction);
}

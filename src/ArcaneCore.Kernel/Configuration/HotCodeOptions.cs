namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// Code hot reload for a development runner, bound from "World:HotCode" (docs/areas/code-hot-reload.md).
/// Everything is OFF by default: a vanilla server never reloads code. Turning it on is only
/// accepted in the Development or Staging host environment (see <c>HotCodeGuard</c>).
/// </summary>
public sealed class HotCodeOptions
{
    public const string SectionName = "World:HotCode";

    /// <summary>
    /// Allow the process to take method-body edits while it runs (the <c>dotnet watch</c> runner,
    /// scripts/hot-runner.ps1). When false the server refuses to start if the runtime is
    /// already set up for metadata updates, because the watch agent cannot be vetoed later.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Append-only text file that receives one line per hot-code decision (start allowed,
    /// start refused, refresh applied or rejected). Empty means no file. Not tamper-proof.
    /// </summary>
    public string AuditLogPath { get; set; } = string.Empty;

    /// <summary>
    /// With hot code enabled, a map updater that throws in this many consecutive ticks is skipped
    /// until the next applied code edit (the edit may be the fix) instead of failing and logging
    /// every tick. 0 disables the breaker. Ignored when <see cref="Enabled"/> is false, and an
    /// explicit <c>World:MaxConsecutiveUpdaterFaults</c> takes precedence.
    /// </summary>
    public int MaxConsecutiveFaults { get; set; } = 50;
}

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

    /// <summary>
    /// The module lane: load, replace and unload code (opt-in extension assemblies) in the running
    /// process without the SDK and without a Debug build. Independent of <see cref="Enabled"/>.
    /// </summary>
    public HotModuleOptions Modules { get; set; } = new();
}

/// <summary>
/// Bound from "World:HotCode:Modules". OFF by default. A module is an assembly the operator drops
/// into <see cref="Directory"/> and loads on demand with <c>.hotmodule</c>; it runs with the
/// server's full trust, so the directory must be writable by the server operator only.
/// </summary>
public sealed class HotModuleOptions
{
    /// <summary>Allow modules to be loaded. When false no module host exists at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Folder holding one subfolder per module: <c>&lt;Directory&gt;/&lt;name&gt;/&lt;name&gt;.dll</c>
    /// plus any private dependencies. Required when <see cref="Enabled"/> (the server refuses to start without it).
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Modules are accepted in the Development and Staging environments only, unless this is set.
    /// Setting it is the operator saying that code may be loaded into this (deployed) instance.
    /// </summary>
    public bool AllowAnyEnvironment { get; set; }

    /// <summary>
    /// Path of a text file of SHA-256 hashes (hex, one per line, <c>#</c> comments) of the module dlls that may be loaded.
    /// Empty adds no restriction (any dll in <see cref="Directory"/> can be loaded by an Administrator). When set it is
    /// fail-closed: a missing, unreadable or malformed file, or a dll whose hash is not listed, refuses the load and is
    /// audited. Read on every load, so editing it needs no restart. Required when <see cref="AllowAnyEnvironment"/> lets
    /// modules into a Production instance.
    /// </summary>
    public string Allowlist { get; set; } = string.Empty;

    /// <summary>Module names to load once the world is running (asynchronously; a failure is logged, not fatal).</summary>
    public List<string> LoadOnStart { get; set; } = [];
}
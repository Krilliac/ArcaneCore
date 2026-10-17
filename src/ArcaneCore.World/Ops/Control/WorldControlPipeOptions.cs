namespace ArcaneCore.World.Ops.Control;

/// <summary>
/// Configuration of the world daemon's local control pipe (docs/areas/server-console.md, "Control pipe"): the console commands, sent
/// by a local tool such as <c>tools/ops/live/redeploy.ps1</c> instead of typed at the console window.
/// </summary>
public sealed class WorldControlPipeOptions
{
    public const string SectionName = "World:Control:Pipe";

    /// <summary>The token in <see cref="Name"/> that is replaced by the world process id.</summary>
    public const string PidToken = "{pid}";

    /// <summary>
    /// Accept console commands on a local Windows named pipe. Each line runs exactly like a line typed at the console: on the world
    /// thread, Administrator authority, no character, logged and GM-audited as the console. The pipe admits only the account the
    /// daemon runs as and SYSTEM, refuses network clients, and is not created on other operating systems. Default off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The pipe name (without <c>\\.\pipe\</c>); <c>{pid}</c> is replaced by the world process id, so two daemons on one machine never
    /// share a name. The daemon refuses to serve a name another process already created. Letters, digits, '.', '_', '-' and the
    /// token only.
    /// </summary>
    public string Name { get; set; } = "arcanecore-world-" + PidToken;

    /// <summary>Seconds a connected client may stay silent before the daemon closes the connection (frees the single pipe instance).</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    /// <summary>The longest command line in bytes; a longer line closes the connection without running anything.</summary>
    public int MaxLineLength { get; set; } = 4096;
}

namespace ArcaneCore.World.Ops.ServerConsole;

/// <summary>Configuration for the world daemon's console (docs/areas/server-console.md).</summary>
public sealed class WorldConsoleOptions
{
    public const string SectionName = "World:Console";

    /// <summary>
    /// Accept typed GM commands at the daemon's console window when standard input is an interactive console. The commands
    /// run on the world thread with Administrator authority and no character. Never starts a reader when input is
    /// redirected (services, tests, deploy scripts). Default on.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

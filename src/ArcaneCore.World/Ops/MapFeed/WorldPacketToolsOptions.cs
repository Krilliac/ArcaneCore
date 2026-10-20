namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Configuration of the map feed's packet tools (docs/areas/map-feed.md, "Packet tools"): a developer debugging aid that lets a local tool
/// (<c>tools/ArcaneCore.Atlas</c>) run a console line, hand a client opcode to an online session as if its client had sent it, queue a
/// server opcode to a session's client and watch a session's packets, all over the map feed's pipe. Off by default, and refused outside
/// the Development environment unless <see cref="AllowOutsideDevelopment"/> is also set.
/// </summary>
public sealed class WorldPacketToolsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "World:Control:PacketTools";

    /// <summary>The only host environment the tools run in without <see cref="AllowOutsideDevelopment"/>.</summary>
    public const string DevelopmentEnvironment = "Development";

    /// <summary>
    /// Offer the packet tools on the map feed's pipe (World:Control:MapFeed:Enabled must be on too). A connected tool may then run console
    /// commands with the console's Administrator authority, inject client opcodes as an online player or bot, send server opcodes to its
    /// client and watch its packets. Every console line, injection and send is logged and counted. Refused, with a warning at start, outside
    /// the Development environment unless AllowOutsideDevelopment is also true. Changing it needs a restart. Default off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A second, deliberate opt-in: let Enabled take effect when the host environment is not Development (Staging, Production or unset).
    /// The tools then start with a warning naming the environment. Never set this on a server players use. Default off.
    /// </summary>
    public bool AllowOutsideDevelopment { get; set; }

    /// <summary>
    /// Whether the tools run in a host whose environment is <paramref name="environmentName"/>: <see cref="Enabled"/>, and either the
    /// Development environment or <see cref="AllowOutsideDevelopment"/>. Pure; any thread.
    /// </summary>
    public bool PermitsEnvironment(string environmentName)
        => Enabled && (AllowOutsideDevelopment || string.Equals(environmentName, DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase));
}

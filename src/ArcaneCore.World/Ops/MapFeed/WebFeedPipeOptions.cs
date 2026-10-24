using ArcaneCore.World.Ops.Control;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Configuration of the separate web pipe (docs/areas/atlas-actions.md, design 0.10): a second map-feed pipe whose connections are the
/// <c>web</c> role by construction, for a public Atlas listener that runs as its own, low-privilege Windows account. Its access list admits
/// only <see cref="ClientAccount"/> (not the daemon's account, not SYSTEM, never the network), so code running in that process cannot reach the
/// local pipe's authority. Off by default; changing any value needs a restart.
/// </summary>
public sealed class WebFeedPipeOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "World:Control:MapFeed:Web";

    /// <summary>The pipe name (without <c>\\.\pipe\</c>); <c>{pid}</c> is the world process id. Fixed, not a configuration key.</summary>
    public const string PipeNameTemplate = "arcanecore-mapfeed-web-" + WorldControlPipeOptions.PidToken;

    /// <summary>The most web connections (one pipe instance each).</summary>
    public const int MaxMaxConnections = 8;

    /// <summary>Serve the web pipe (World:Control:MapFeed:Enabled must be on too). Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The one Windows account allowed to open the web pipe: a SID (<c>S-1-5-21-...</c>) or an account name (<c>MACHINE\atlas-public</c>). Required
    /// when <see cref="Enabled"/> is set; without it the pipe is not created.
    /// </summary>
    public string? ClientAccount { get; set; }

    /// <summary>The most web connections at once, independent of the local pipe's count. 1 to 8. Default 2.</summary>
    public int MaxConnections { get; set; } = 2;

    /// <summary>Everything wrong with the values when the pipe is on (empty when valid). Pure; any thread.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientAccount))
        {
            problems.Add("World:Control:MapFeed:Web:ClientAccount is not set; name the one Windows account (a SID or MACHINE\\name) that may open the web pipe");
        }

        if (MaxConnections is < 1 or > MaxMaxConnections)
        {
            problems.Add($"MaxConnections is {MaxConnections}; it must be 1 to {MaxMaxConnections}");
        }

        return problems;
    }
}

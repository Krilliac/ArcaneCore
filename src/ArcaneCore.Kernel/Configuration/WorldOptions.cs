namespace ArcaneCore.Kernel.Configuration;

/// <summary>Configuration for the world daemon.</summary>
public sealed class WorldOptions
{
    public const string SectionName = "World";

    /// <summary>Interface to bind the world listener to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>World TCP port. Vanilla default is 8085 (Charter §3).</summary>
    public int Port { get; set; } = 8085;

    /// <summary>Global cap on simultaneous world connections; 0 = unlimited. Hardening (no vmangos equivalent).</summary>
    public int MaxConnections { get; set; } = 4096;

    /// <summary>Cap per client IP address; 0 = unlimited. Hardening: a retail client holds one connection.</summary>
    public int MaxConnectionsPerIp { get; set; } = 64;
}

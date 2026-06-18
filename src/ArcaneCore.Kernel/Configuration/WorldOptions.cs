namespace ArcaneCore.Kernel.Configuration;

/// <summary>Configuration for the world daemon.</summary>
public sealed class WorldOptions
{
    public const string SectionName = "World";

    /// <summary>Interface to bind the world listener to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>World TCP port. Vanilla default is 8085 (Charter §3).</summary>
    public int Port { get; set; } = 8085;
}

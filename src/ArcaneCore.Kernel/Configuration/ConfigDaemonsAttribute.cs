namespace ArcaneCore.Kernel.Configuration;

/// <summary>The daemons of a profile. A key that a daemon does not read belongs in the other daemon's .conf file.</summary>
[Flags]
public enum ConfigDaemon
{
    None = 0,
    World = 1,
    Realm = 2,
    Both = World | Realm,
}

/// <summary>
/// Names the daemon(s) that read the options type (or, on a property, that one key). The configuration catalog reads it to
/// decide which <c>.conf.dist</c> files carry a key and which daemon warns that a key in its <c>.conf</c> is not its own.
/// A property attribute overrides the one on its class; an options type with no attribute belongs to the World.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class ConfigDaemonsAttribute(ConfigDaemon daemons) : Attribute
{
    public ConfigDaemon Daemons { get; } = daemons;
}

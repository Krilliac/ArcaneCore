namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The values an option really accepts, in words, for the configuration catalog's "Allowed" line (the generated
/// <c>.conf.dist</c> comment blocks and docs/reference/configuration.md). Without it the line names only the type's own range
/// ("a whole number, -2147483648 to 2147483647"), which says nothing of the option's validation. The text must agree with the
/// option's own check; it is documentation, not a check.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ConfigAllowedAttribute(string allowed) : Attribute
{
    public string Allowed { get; } = allowed;
}

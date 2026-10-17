using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Ops.Control;

/// <summary>
/// The security of the control pipe (docs/areas/server-console.md, "Control pipe"). The descriptor is protected (nothing is
/// inherited), owned by the daemon's own account, and has exactly three entries: deny NETWORK everything, allow the daemon's
/// account full control, allow SYSTEM read and write. Administrators, other users and everyone else get nothing. Pure; any thread.
/// </summary>
public static partial class ControlPipeSecurity
{
    /// <summary>The longest pipe name Windows accepts after <c>\\.\pipe\</c> (256 characters for the whole path).</summary>
    public const int MaxNameLength = 200;

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameCharacters();

    /// <summary>LocalSystem (S-1-5-18).</summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier LocalSystem => new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>NETWORK (S-1-5-2): present in the token of every network logon.</summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier Network => new(WellKnownSidType.NetworkSid, null);

    /// <summary>The account this process runs as.</summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("the process token has no user SID");
    }

    /// <summary>The descriptor of a pipe only <paramref name="user"/> (and SYSTEM) may open, from this machine only.</summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity Create(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The pipe name for <paramref name="template"/> with <see cref="WorldControlPipeOptions.PidToken"/> replaced by
    /// <paramref name="processId"/>, or null with <paramref name="problem"/> set when the result is not a plain, short name.
    /// </summary>
    public static string? ResolveName(string? template, int processId, out string? problem)
    {
        string name = (template ?? string.Empty).Trim().Replace(WorldControlPipeOptions.PidToken, processId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0)
        {
            problem = "the pipe name is empty";
            return null;
        }

        if (name.Length > MaxNameLength)
        {
            problem = $"the pipe name is longer than {MaxNameLength} characters";
            return null;
        }

        if (!NameCharacters().IsMatch(name))
        {
            problem = "the pipe name may hold only letters, digits, '.', '_', '-' and " + WorldControlPipeOptions.PidToken;
            return null;
        }

        problem = null;
        return name;
    }
}

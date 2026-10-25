using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using ArcaneCore.World.Ops.Control;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Configuration of the separate web pipe (docs/areas/atlas-actions.md, design 0.10): a second map-feed pipe whose connections are the
/// <c>web</c> role by construction, for a public Atlas listener that runs as its own, low-privilege Windows account. Its access list admits
/// only <see cref="ClientAccount"/> (one created account: never a group, a built-in or service principal, the daemon's own account, SYSTEM or the network), so code running in that process cannot reach the
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
    /// when <see cref="Enabled"/> is set; without it the pipe is not created. A group or built-in principal is refused (Everyone, Authenticated Users,
    /// SYSTEM, LOCAL/NETWORK SERVICE, <c>BUILTIN\</c>, <c>NT AUTHORITY\</c> and <c>NT SERVICE\</c> names, <c>S-1-5-32-*</c>, any account with a RID
    /// below 1000), and so is the daemon's own account: <see cref="Problems"/> checks the text at start-up, the pipe checks the resolved SID.
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
        else if (RefuseClientText(ClientAccount.Trim()) is { } why)
        {
            problems.Add($"World:Control:MapFeed:Web:ClientAccount '{ClientAccount.Trim()}' is refused: {why}");
        }

        if (MaxConnections is < 1 or > MaxMaxConnections)
        {
            problems.Add($"MaxConnections is {MaxConnections}; it must be 1 to {MaxMaxConnections}");
        }

        return problems;
    }

    private static readonly string[] RefusedNamePrefixes = ["BUILTIN\\", "NT AUTHORITY\\", "NT SERVICE\\"];

    private static readonly string[] RefusedSids = ["S-1-1-0", "S-1-5-11", "S-1-5-18", "S-1-5-19", "S-1-5-20"];

    /// <summary>
    /// The string-level part of the client-account rules, so a bad value fails the configuration check at start-up without a lookup: <c>Everyone</c>,
    /// names under <c>BUILTIN\</c>, <c>NT AUTHORITY\</c> and <c>NT SERVICE\</c>, and the SIDs of Everyone, Authenticated Users, SYSTEM, LOCAL SERVICE,
    /// NETWORK SERVICE and the <c>S-1-5-32-*</c> built-in groups. Null when the text passes (the resolved SID is judged again by <see cref="RefuseClientSid"/>).
    /// </summary>
    internal static string? RefuseClientText(string account)
    {
        if (account.Equals("Everyone", StringComparison.OrdinalIgnoreCase))
        {
            return "Everyone is every account; name the one account the public listener runs as";
        }

        foreach (string prefix in RefusedNamePrefixes)
        {
            if (account.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return $"{prefix.TrimEnd('\\')} names are groups or built-in and service principals, not one created account";
            }
        }

        foreach (string sid in RefusedSids)
        {
            if (account.Equals(sid, StringComparison.OrdinalIgnoreCase))
            {
                return $"{sid} is a well-known group or service principal, not one created account";
            }
        }

        return account.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) ? "S-1-5-32-* are the built-in groups (Users, Administrators, ...), not one created account" : null;
    }

    [SupportedOSPlatform("windows")]
    private static readonly WellKnownSidType[] RefusedWellKnown =
    [
        WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.LocalServiceSid,
        WellKnownSidType.NetworkServiceSid, WellKnownSidType.InteractiveSid, WellKnownSidType.NetworkSid, WellKnownSidType.ServiceSid,
        WellKnownSidType.BuiltinUsersSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinGuestsSid,
    ];

    /// <summary>
    /// Why the resolved <paramref name="sid"/> may not be the web client, or null when it may: it is the daemon's own account, a group or
    /// well-known principal, not an account SID, or a built-in account (RID below 1000). Pure: no pipe, no name translation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string? RefuseClientSid(SecurityIdentifier sid, SecurityIdentifier daemon)
    {
        ArgumentNullException.ThrowIfNull(sid);
        ArgumentNullException.ThrowIfNull(daemon);
        if (sid == daemon)
        {
            return "it is the daemon's own account; the public listener must run as a different, low-privilege account";
        }

        foreach (WellKnownSidType type in RefusedWellKnown)
        {
            if (sid.IsWellKnown(type))
            {
                return $"it is {type}, a group or built-in principal, not one account";
            }
        }

        string text = sid.Value;
        if (text.StartsWith("S-1-5-32-", StringComparison.Ordinal))
        {
            return "it is one of the built-in groups (S-1-5-32-*), not one account";
        }

        if (!sid.IsAccountSid())
        {
            return "it is not an account SID (S-1-5-21-...-RID)";
        }

        return uint.TryParse(text.AsSpan(text.LastIndexOf('-') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint rid) && rid >= 1000
            ? null
            : "its RID is below 1000, so it is a built-in or well-known account or group, not a created account";
    }
}

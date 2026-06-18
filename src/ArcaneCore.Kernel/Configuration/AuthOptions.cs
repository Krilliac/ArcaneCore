namespace ArcaneCore.Kernel.Configuration;

/// <summary>Configuration for the logon/realm daemon.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Interface to bind the logon listener to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Logon/realm TCP port. Vanilla default is 3724 (Charter §3).</summary>
    public int Port { get; set; } = 3724;

    /// <summary>
    /// WCell-style auto-create-on-login. When enabled, an unknown account whose login
    /// proof validates with password == username is created on the spot
    /// (see WCell Services/WCell.AuthServer/Authentication.cs).
    /// </summary>
    public bool AutocreateAccounts { get; set; }
}

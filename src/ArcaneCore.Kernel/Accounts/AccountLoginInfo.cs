namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// What the realm learned about the client at an account's last successful logon (auth schema v6). Empty strings and a
/// zero build mean "never recorded" (an account that has not logged on since the columns were added).
/// </summary>
/// <param name="LastLoginUtc">When the logon succeeded, UTC; null if never recorded.</param>
/// <param name="Os">The client's OS tag from the logon challenge (for example <c>Win</c>).</param>
/// <param name="Platform">The client's platform tag from the logon challenge (for example <c>x86</c>).</param>
/// <param name="Locale">The client's locale tag (for example <c>enUS</c>).</param>
/// <param name="Build">The client build number from the logon challenge.</param>
public sealed record AccountLoginInfo(DateTime? LastLoginUtc, string Os, string Platform, string Locale, ushort Build);

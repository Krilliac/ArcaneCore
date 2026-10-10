namespace ArcaneCore.Kernel.Accounts;

/// <summary>Text forms of <see cref="AccountSecurity"/>, shared by the account tool and the realm console.</summary>
public static class AccountSecurityText
{
    /// <summary>Parse a level number (0-3) or name (player, moderator, gamemaster, administrator; any case).</summary>
    public static bool TryParse(string text, out AccountSecurity security)
    {
        if (byte.TryParse(text, out byte level) && Enum.IsDefined((AccountSecurity)level))
        {
            security = (AccountSecurity)level;
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out security) && Enum.IsDefined(security);
    }
}

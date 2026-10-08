namespace ArcaneCore.Kernel.Accounts;

/// <summary>Validation shared by CLI, GM and persistence; no secret appears in errors.</summary>
public static class AccountLoginSecurityPolicy
{
    public static bool IsValid(AccountLockFlags flags, string info)
    {
        const AccountLockFlags known = AccountLockFlags.IpLock | AccountLockFlags.FixedPin |
            AccountLockFlags.Totp | AccountLockFlags.AlwaysEnforce;
        if ((flags & ~known) != 0) return false;
        bool pin = flags.HasFlag(AccountLockFlags.FixedPin);
        bool totp = flags.HasFlag(AccountLockFlags.Totp);
        if (pin && totp || flags.HasFlag(AccountLockFlags.AlwaysEnforce) && !pin && !totp) return false;
        if (pin) return info.Length is >= 4 and <= 10 && info.All(c => c is >= '0' and <= '9');
        if (totp) return info.Length is >= 16 and <= 103 && info.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '2' and <= '7');
        return info.Length == 0;
    }
}

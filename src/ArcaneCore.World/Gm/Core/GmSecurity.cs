using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Gm.Core;

/// <summary>vmangos HasLowerSecurity (D:\refs\vmangos\src\game\Chat\Chat.cpp:1521-1563).</summary>
public static class GmSecurity
{
    /// <summary>
    /// True when <paramref name="caller"/> may NOT act on an account of <paramref name="target"/>
    /// security. Staff callers skip the check on non-strong calls when
    /// <see cref="GmOptions.LowerSecurity"/> is off (Chat.cpp:1546-1547); otherwise a higher
    /// target refuses, and a strong check also refuses an equal one (Chat.cpp:1558).
    /// </summary>
    public static bool HasLowerSecurity(AccountSecurity caller, AccountSecurity target, bool strong, GmOptions options)
    {
        if (caller > AccountSecurity.Player && !strong && !options.LowerSecurity)
        {
            return false;
        }

        return caller < target || (strong && caller <= target);
    }
}

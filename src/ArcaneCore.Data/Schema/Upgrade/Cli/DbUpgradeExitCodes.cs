namespace ArcaneCore.Data.Schema.Upgrade.Cli;

/// <summary>
/// Process exit codes of <c>arcane-db</c>. They differ from the content importer's
/// (<c>ArcaneCore.Data.Content.Import.ExitCodes</c>: 3 wrong source schema, 4 database error,
/// 5 repository path, 6 verify) because the two tools answer different questions; each tool's
/// codes are listed in its own usage text and in docs/ops/database-upgrade.md.
/// </summary>
public static class DbUpgradeExitCodes
{
    /// <summary>Success: current, upgraded, or drift clean.</summary>
    public const int Ok = 0;

    /// <summary>An unexpected failure (a bug, or an input the tool did not anticipate); the message says what.</summary>
    public const int Failure = 1;

    /// <summary>Bad command line or missing configuration: unknown command or option, missing value, no connection string.</summary>
    public const int Usage = 2;

    /// <summary><c>status</c> or <c>plan</c>: an upgrade is pending (a normal report, not an error; <c>--no-fail-on-pending</c> turns it into 0).</summary>
    public const int UpgradePending = 3;

    /// <summary>Refused: the database is newer than the code, in an unknown state, blocked (duplicate rows, conflicting index, mismatching table) or has other sessions; nothing was changed.</summary>
    public const int Refused = 4;

    /// <summary><c>check</c>, or the check after <c>upgrade</c>: the database differs from the model.</summary>
    public const int Drift = 5;

    /// <summary>The database server could not be reached or refused the connection (the message is scrubbed of secrets).</summary>
    public const int Unreachable = 6;

    /// <summary>The wait for another process's schema lock ran out.</summary>
    public const int LockTimeout = 7;

    /// <summary><c>upgrade</c> with pending steps and no confirmed backup (or a backup that could not be made); nothing was changed.</summary>
    public const int BackupNotConfirmed = 8;
}

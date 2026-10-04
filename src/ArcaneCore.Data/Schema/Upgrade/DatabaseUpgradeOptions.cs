using Microsoft.Extensions.Options;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>
/// What a daemon or tool may do to the database schema when it starts, bound from <c>Database:Upgrade</c>.
/// <list type="bullet">
/// <item><c>Policy</c>: <see cref="SchemaPolicy.Always"/> (default, the historic behaviour: create and upgrade),
/// <see cref="SchemaPolicy.CreateOnly"/> (create an empty database, refuse to upgrade an existing one: the retail-like
/// setting for production, where <c>arcane-db upgrade</c> applies updates), <see cref="SchemaPolicy.Never"/> (verify only).</item>
/// <item><c>LockTimeoutSeconds</c>: how long a start waits for another process's schema work (default 60).</item>
/// </list>
/// The retail references never create or upgrade on start (D:\refs\vmangos\src\mangosd\Master.cpp:415-470 refuses a
/// database that lacks migrations); <c>Never</c> is that behaviour and the default stays <c>Always</c> until the
/// operator opts in (docs/ops/database-upgrade.md).
/// </summary>
public sealed class DatabaseUpgradeOptions
{
    public const string SectionName = "Upgrade";

    public SchemaPolicy Policy { get; set; } = SchemaPolicy.Always;

    public int LockTimeoutSeconds { get; set; } = 60;

    /// <summary>The bootstrapper options these settings mean; a lock timeout outside 1..86400 s is a configuration error.</summary>
    public SchemaUpgradeOptions ToSchemaOptions()
    {
        if (LockTimeoutSeconds is < 1 or > 86400)
        {
            throw new InvalidOperationException(
                $"Database:Upgrade:LockTimeoutSeconds must be between 1 and 86400, not {LockTimeoutSeconds}.");
        }

        if (!Enum.IsDefined(Policy))
        {
            throw new InvalidOperationException($"Database:Upgrade:Policy must be Always, CreateOnly or Never, not {Policy}.");
        }

        return new SchemaUpgradeOptions { Policy = Policy, LockTimeout = TimeSpan.FromSeconds(LockTimeoutSeconds) };
    }
}

/// <summary>What the daemons' and tools' start-up does with the schema policy and how a refusal is reported.</summary>
public static class DatabaseStartup
{
    /// <summary>The configured policy (the historic <see cref="SchemaPolicy.Always"/> when none is configured).</summary>
    public static SchemaUpgradeOptions OptionsFrom(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        DatabaseOptions? options = (services.GetService(typeof(IOptions<DatabaseOptions>)) as IOptions<DatabaseOptions>)?.Value;
        return (options?.Upgrade ?? new DatabaseUpgradeOptions()).ToSchemaOptions();
    }

    /// <summary>
    /// Run a daemon's schema initialization. A refusal (<see cref="SchemaMismatchException"/>: policy, newer database,
    /// unknown state, lock timeout) is one line on <paramref name="error"/> with every configured password removed and
    /// an exit code (<see cref="Cli.DbUpgradeExitCodes.Refused"/> or <see cref="Cli.DbUpgradeExitCodes.LockTimeout"/>)
    /// instead of an unhandled-exception crash. A server that cannot be reached (<see cref="Resilience.DatabaseTransience"/>)
    /// is retried with the <c>Resilience:Database:Bootstrap</c> backoff when a <see cref="Resilience.DatabaseGuard"/> is
    /// registered, then reported the same way with <see cref="Cli.DbUpgradeExitCodes.Unreachable"/> (docs/ops/resilience.md).
    /// Any other failure propagates unchanged.
    /// </summary>
    /// <returns>0 when initialized, otherwise the exit code the process should return.</returns>
    public static async Task<int> InitializeAsync(Func<Task> initialize, IServiceProvider? services, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (services?.GetService(typeof(Resilience.DatabaseGuard)) is Resilience.DatabaseGuard guard)
            {
                await guard.BootstrapAsync(initialize).ConfigureAwait(false);
            }
            else
            {
                await initialize().ConfigureAwait(false);
            }

            return Cli.DbUpgradeExitCodes.Ok;
        }
        catch (SchemaMismatchException ex)
        {
            await error.WriteLineAsync($"database schema refused: {Scrub(ex.Message, services)}").ConfigureAwait(false);
            return ex.Reason == SchemaMismatchReason.LockTimeout ? Cli.DbUpgradeExitCodes.LockTimeout : Cli.DbUpgradeExitCodes.Refused;
        }
        catch (Exception ex) when (Resilience.DatabaseTransience.IsTransient(ex))
        {
            await error.WriteLineAsync($"database unreachable: {Scrub(Resilience.DatabaseCircuits.Reason(ex), services)}").ConfigureAwait(false);
            return Cli.DbUpgradeExitCodes.Unreachable;
        }
    }

    /// <summary>One line with every configured connection string's secrets removed.</summary>
    private static string Scrub(string text, IServiceProvider? services)
    {
        string message = text.ReplaceLineEndings(" ");
        DatabaseOptions? options = (services?.GetService(typeof(IOptions<DatabaseOptions>)) as IOptions<DatabaseOptions>)?.Value;
        foreach (string? connectionString in new[] { options?.ConnectionString, options?.Auth?.ConnectionString, options?.Characters?.ConnectionString, options?.World?.ConnectionString })
        {
            if (!string.IsNullOrEmpty(connectionString))
            {
                message = Content.Import.ConnectionStringRedactor.Scrub(message, connectionString);
            }
        }

        return Content.Import.ConnectionStringRedactor.Scrub(message, string.Empty);
    }
}

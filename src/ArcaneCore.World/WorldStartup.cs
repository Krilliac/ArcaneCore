using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.World;

/// <summary>The world daemon's start-up and run-to-end order, extracted from Program so tests can drive it.</summary>
internal static class WorldStartup
{
    /// <summary>
    /// First the single-writer lock (<see cref="WorldWriterGuard"/>), then the schema initializers (<see cref="DatabaseStartup"/>): a second world on the same
    /// characters file is refused before anything opens, upgrades or checkpoints it. A lock that is held elsewhere, and one that cannot be created at all (permissions,
    /// a full disk), are both one line on <paramref name="error"/> and the refusal exit code instead of an unhandled crash.
    /// </summary>
    /// <returns>0 when ready, otherwise the exit code the process should return.</returns>
    public static async Task<int> PrepareDatabasesAsync(IServiceProvider services, Func<Task> initialize, TextWriter error)
    {
        try
        {
            services.GetService<WorldWriterGuard>()?.Acquire();
        }
        catch (Exception ex) when (ex is CharacterDatabaseAlreadyOwnedException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"database refused: {DatabaseStartup.Scrub(ex.Message, services)}").ConfigureAwait(false);
            return DbUpgradeExitCodes.Refused;
        }

        return await DatabaseStartup.InitializeAsync(initialize, services, error).ConfigureAwait(false);
    }

    /// <summary>
    /// Program's body after the host is built: the databases, then <see cref="HostingAbstractionsHostExtensions.RunAsync(IHost, CancellationToken)"/>, then the exit code.
    /// <c>RunAsync</c> disposes the container after the hosted services stop, which releases the <see cref="WorldWriterGuard"/> last; nothing here touches the provider afterwards,
    /// so a failure of <c>RunAsync</c> surfaces unchanged and the exit code of a clean stop (2 for a restart) is read from <paramref name="exitCode"/>.
    /// </summary>
    public static async Task<int> RunAsync(IHost host, Func<Task> initialize, TextWriter error, Func<int> exitCode)
    {
        int startup = await PrepareDatabasesAsync(host.Services, initialize, error).ConfigureAwait(false);
        if (startup != 0) return startup;

        await host.RunAsync().ConfigureAwait(false);
        return exitCode();
    }
}

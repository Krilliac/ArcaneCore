using System.Runtime.InteropServices;
using ArcaneCore.Atlas.Host.Cli;

namespace ArcaneCore.Atlas;

/// <summary>Entry point: verb dispatch for serve, ctl, run, script, mcp, layers and help (docs/areas/atlas.md, section 13).</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var shutdown = new CancellationTokenSource();
        int signals = 0;

        // The first Ctrl+C / SIGTERM asks the verb to stop gracefully (serve deletes its discovery file); a second one is not
        // intercepted, so a wedged shutdown can still be killed from the terminal.
        void OnSignal(PosixSignalContext context)
        {
            if (Interlocked.Increment(ref signals) > 1)
            {
                context.Cancel = false;
                return;
            }

            context.Cancel = true;
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Main already returned.
            }
        }

        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        return await AtlasCli.RunAsync(args, CliEnvironment.ForProcess(), shutdown.Token).ConfigureAwait(false);
    }
}

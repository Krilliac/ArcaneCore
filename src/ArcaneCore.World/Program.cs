using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World;
using ArcaneCore.World.Ops.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Operations verbs (check-config) run instead of the daemon: nothing binds, no schema is touched.
if (OpsCli.TryRun(args, builder.Configuration, Console.Out, out int verbExitCode))
{
    return verbExitCode;
}

// Fail fast, listing every configuration problem at once (exit 78; supervisors must not restart on it).
ArcaneCore.Kernel.Configuration.Validation.ConfigReport startupReport = OpsCli.Validate(builder.Configuration);
if (startupReport.Issues.Count > 0)
{
    startupReport.Write(Console.Error);
}

if (startupReport.IsInvalid)
{
    return ExitCodes.InvalidConfiguration;
}

// The HostOptions section (e.g. ShutdownTimeout) is not bound by the default builder; a full save
// drain for many players must not be cut short by the host default (docs/areas/ops-perf.md).
builder.Services.Configure<HostOptions>(builder.Configuration.GetSection("HostOptions"));
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddCharacterDatabase(builder.Configuration);
builder.Services.AddWorldDatabase(builder.Configuration);
builder.Services.AddWorldDaemon(builder.Configuration);

IHost host = builder.Build();

// Bring every schema this daemon touches to the current version (fail closed on mismatch).
await host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync().ConfigureAwait(false);
await host.Services.GetRequiredService<CharacterDbInitializer>().InitializeAsync().ConfigureAwait(false);
await host.Services.GetRequiredService<WorldDbInitializer>().InitializeAsync().ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);

// 0 normal stop, 2 restart request (.server restart), see ExitCodes.
return ExitCodes.Current;

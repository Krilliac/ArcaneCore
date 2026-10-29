using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.World;
using ArcaneCore.World.Features;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The command line is read before anything is built: --help, a mistyped command and a mistyped option are answered here (exit 64) instead of
// starting a live server against the live profile, because the host's command-line provider drops them silently (docs/guide/configuration.md).
DaemonCommand command = DaemonCommandLine.Parse(args, ConfigDaemon.World, ConfCatalog.Embedded);
if (ConfStartup.TryAnswerEarly(command, ConfigDaemon.World, Console.Out, Console.Error, out int earlyExit))
{
    return earlyExit;
}

// Built-in packs (docs/design/hot-reload-tiers.md, 4.6): the Playerbots pack is its own assembly, which references World, so this
// executable cannot reference it; the build puts it beside this one and it is registered here, before anything discovers the
// startup code set (features, service modules, commands, config checks, the wiring audit). Default context: it never unloads.
try
{
    WorldCodeAssemblies.RegisterBuiltIn(WorldCodeAssemblies.LoadBuiltInPack(WorldCodeAssemblies.PlayerbotsAssemblyName));
}
catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
{
    Console.Error.WriteLine($"ArcaneCore.World refuses to start: {ex.Message}");
    return ExitCodes.InvalidConfiguration; // EX_CONFIG: an incomplete install, a supervisor must not restart it
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder([.. command.HostArgs]);
ConfAttachment? conf = ConfStartup.TryAttach(builder, ConfigDaemon.World, command, Console.Error, out int confExit);
if (conf is null)
{
    return confExit;
}

// Client data (docs/areas/client-data.md): fill every unset per-file DBC key from ClientData:DbcDirectory before anything binds, so
// check-config, the validation below and every feature see the same effective configuration.
ArcaneCore.Data.ClientData.ClientDataReport clientData = ArcaneCore.World.ClientData.ClientDataStartup.Apply(builder.Configuration);

// Operations verbs (check-config, write-config) run instead of the daemon: nothing binds, no schema is touched.
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

// Supervisor mode (Cluster:Supervisor:Enabled, docs/CLUSTERING_DESIGN.md M15.4): this executable only launches and watches the configured
// child processes. It runs before anything below opens a database, a listener or a diagnostics hook, and never calls AddWorldProductionServices.
if (ArcaneCore.World.Cluster.Supervision.ClusterSupervisorHosting.IsEnabled(builder.Configuration))
{
    return await ArcaneCore.World.Cluster.Supervision.ClusterSupervisorHosting.RunAsync(builder, Console.Error).ConfigureAwait(false);
}

// Name the console window (worldserver), so an operator running both daemons can tell them apart (no window: skipped).
ArcaneCore.Kernel.Ops.ServerConsole.ServerConsoleTitle.TrySet("worldserver", builder.Configuration["World:Port"]);

ArcaneCore.World.Ops.Diagnostics.WorldDiagnosticsExtensions.UseWorldDiagnostics(builder); // crash hooks, invariants and the tick context (docs/ops/invariants.md); after the validation so a bad Diagnostics key is listed with the rest

// Code hot reload gate (docs/areas/code-hot-reload.md). It runs before the host is built and
// before any schema initializer below touches a database, so a refused start has changed nothing.
HotCodeOptions hotCode = builder.Configuration.GetSection(HotCodeOptions.SectionName).Get<HotCodeOptions>() ?? new HotCodeOptions();
HotCodeVerdict hotCodeVerdict;
try
{
    hotCodeVerdict = HotCodeGuard.Enforce(
        hotCode,
        new SystemRuntimeProbe(builder.Environment.EnvironmentName),
        new HotCodeAudit(hotCode.AuditLogPath));
}
catch (HotCodeRefusedException ex)
{
    Console.Error.WriteLine($"ArcaneCore.World refuses to start: {ex.Message}");
    return ExitCodes.InvalidConfiguration; // EX_CONFIG
}

// Every registration the daemon runs with, in one place the CI guard also builds with scope and build validation on (WiringAuditTests).
builder.Services.AddWorldProductionServices(builder.Configuration, clientData);

IHost host = builder.Build();
conf.Reporter.Attach(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.Config"));
ArcaneCore.World.ClientData.ClientDataStartup.Log(
    clientData, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ArcaneCore.World.ClientData.ClientDataStartup.LogCategory));

if (hotCode.Enabled)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.HotCode").LogWarning(
        "Code hot reload is ENABLED (World:HotCode:Enabled, environment {Environment}); runtime metadata updates active: {Active}. Development runner only.",
        builder.Environment.EnvironmentName,
        hotCodeVerdict.HotReloadActive);
}

// Bring every schema this daemon touches to the current version (fail closed on mismatch). Database:Upgrade:Policy
// decides whether an existing database may be upgraded here; a refusal is one line and an exit code, not a crash.
// The single-writer lock on the characters database is taken first (WorldStartup), before any initializer opens, upgrades or checkpoints the file; RunAsync disposes
// the container after the hosted services stop, and that releases the lock last. 0 normal stop, 2 restart request (.server restart), see ExitCodes.
return await WorldStartup.RunAsync(
    host,
    async () =>
    {
        await host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync().ConfigureAwait(false);
        await host.Services.GetRequiredService<CharacterDbInitializer>().InitializeAsync().ConfigureAwait(false);
        await host.Services.GetRequiredService<WorldDbInitializer>().InitializeAsync().ConfigureAwait(false);
    },
    Console.Error,
    () => ExitCodes.Current).ConfigureAwait(false);

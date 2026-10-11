using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Ops.Cli;
using ArcaneCore.Realm.Ops.ServerConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

// The command line is read before anything is built: --help, a mistyped command and a mistyped option are answered here (exit 64) instead of
// starting a live realm, because the host's command-line provider drops them silently (docs/guide/configuration.md).
DaemonCommand command = DaemonCommandLine.Parse(args, ConfigDaemon.Realm, ConfCatalog.Embedded);
if (ConfStartup.TryAnswerEarly(command, ConfigDaemon.Realm, Console.Out, Console.Error, out int earlyExit))
{
    return earlyExit;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder([.. command.HostArgs]);
ConfAttachment? conf = ConfStartup.TryAttach(builder, ConfigDaemon.Realm, command, Console.Error, out int confExit);
if (conf is null)
{
    return confExit;
}

if (command.Kind == DaemonCommandKind.Verb)
{
    return RealmOpsCli.Run(command, builder.Configuration, Console.Out, Console.Error);
}

// Fail fast, listing every configuration problem at once (exit 78; supervisors must not restart on it).
ArcaneCore.Kernel.Configuration.Validation.ConfigReport startupReport = RealmOpsCli.Validate(builder.Configuration);
if (startupReport.Issues.Count > 0)
{
    startupReport.Write(Console.Error);
}

if (startupReport.IsInvalid)
{
    return ExitCodes.InvalidConfiguration;
}

// Name the console window (realmserver), so an operator running both daemons can tell them apart (no window: skipped).
ArcaneCore.Kernel.Ops.ServerConsole.ServerConsoleTitle.TrySet("realmserver", builder.Configuration["Auth:Port"]);

ArcaneCore.Kernel.Diagnostics.DiagnosticsHostingExtensions.UseArcaneDiagnostics(builder); // crash hooks and invariants (docs/ops/invariants.md), installed before anything else can fail

builder.Services.AddArcaneCoreLogging(builder.Configuration);
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<AutoPatchOptions>(builder.Configuration.GetSection(AutoPatchOptions.SectionName)); // logon auto-patcher, off by default
builder.Services.Configure<RealmSeedOptions>(builder.Configuration.GetSection(RealmSeedOptions.SectionName));
builder.Services.AddNetProtection(builder.Configuration);
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddRealmResilience(builder.Configuration); // after AddAuthDatabase: guards the auth stores (docs/ops/resilience.md)
builder.Services.AddSingleton<LogonServer>(); // one instance: the hosted service and the console (its session count) share it
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogonServer>());
builder.Services.Configure<RealmConsoleOptions>(builder.Configuration.GetSection(RealmConsoleOptions.SectionName));
builder.Services.TryAddSingleton(_ => ConsoleInputSource.Default());
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddHostedService<RealmConsole>(); // typed commands at the console window, off when stdin is redirected (docs/areas/server-console.md)
builder.Services.AddRealmWatchdog(builder.Configuration);
// Ops:Metrics, off by default (docs/ops/metrics.md). The Realm role binds Ops:Metrics:RealmPrometheusPrefix (default port 9465),
// so a profile shared with the World (which keeps 9464) runs both endpoints.
ArcaneCore.Kernel.Ops.Metrics.MetricsServiceCollectionExtensions.AddOpsMetrics(builder.Services, builder.Configuration, "arcanecore-realm", MetricsRole.Realm);

IHost host = builder.Build();
conf.Reporter.Attach(host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("ArcaneCore.Config"));

// Bring the auth schema to the current version and seed configured realms before accepting connections.
// Database:Upgrade:Policy decides whether an existing database may be upgraded here; a refusal is one line and an exit code.
int startup = await DatabaseStartup.InitializeAsync(
    () => host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync(),
    host.Services,
    Console.Error).ConfigureAwait(false);
if (startup != 0)
{
    return startup;
}

await host.RunAsync().ConfigureAwait(false);

// 0 normal stop, 1 when a watchdog action stopped the host (Ops:Watchdog:Memory:Action=Stop), see ExitCodes.
return ExitCodes.Current;

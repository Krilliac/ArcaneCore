using ArcaneCore.Realm;
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

// Every registration the realm runs with, in one place the CI guard also builds with scope and build validation on.
builder.Services.AddRealmProductionServices(builder.Configuration);

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

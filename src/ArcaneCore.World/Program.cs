using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.World;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Ops.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    return 78; // EX_CONFIG
}

// The HostOptions section (e.g. ShutdownTimeout) is not bound by the default builder; a full save
// drain for many players must not be cut short by the host default (docs/areas/ops-perf.md).
builder.Services.Configure<HostOptions>(builder.Configuration.GetSection("HostOptions"));
builder.Services.AddArcaneCoreLogging(builder.Configuration);
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddCharacterDatabase(builder.Configuration);
builder.Services.AddWorldDatabase(builder.Configuration);
builder.Services.AddWorldDaemon(builder.Configuration);

IHost host = builder.Build();

if (hotCode.Enabled)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.HotCode").LogWarning(
        "Code hot reload is ENABLED (World:HotCode:Enabled, environment {Environment}); runtime metadata updates active: {Active}. Development runner only.",
        builder.Environment.EnvironmentName,
        hotCodeVerdict.HotReloadActive);
}

// Bring every schema this daemon touches to the current version (fail closed on mismatch). Database:Upgrade:Policy
// decides whether an existing database may be upgraded here; a refusal is one line and an exit code, not a crash.
int startup = await DatabaseStartup.InitializeAsync(
    async () =>
    {
        await host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync().ConfigureAwait(false);
        await host.Services.GetRequiredService<CharacterDbInitializer>().InitializeAsync().ConfigureAwait(false);
        await host.Services.GetRequiredService<WorldDbInitializer>().InitializeAsync().ConfigureAwait(false);
    },
    host.Services,
    Console.Error).ConfigureAwait(false);
if (startup != 0)
{
    return startup;
}

await host.RunAsync().ConfigureAwait(false);

// 0 normal stop, 2 restart request (.server restart), see ExitCodes.
return ExitCodes.Current;

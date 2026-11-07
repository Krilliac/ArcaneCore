using ArcaneCore.Gateway;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Tier 3a gateway daemon (docs/areas/gateway.md). Configuration, lowest first: the generic host defaults (appsettings.json), gateway.conf
// (or --config <file>), gateway.json next to the executable, environment variables, command line. It needs Database:Auth:* (the realm's
// auth database), Gateway:* and a World started with Cluster:Worker:Enabled=true. `--check-config` (or the check-config verb) validates
// and exits (0 valid, 78 invalid); `write-config` writes the documented defaults or the effective configuration.
// A supervised child takes the supervisor's per-run token out of the environment before anything copies it (the host's configuration builder,
// processes this one starts); the protocol readers get it from here (SupervisorProtocol.ProcessEnvironment). No supervisor: nothing happens.
ArcaneCore.Kernel.Ops.ServerConsole.SupervisorProtocol.CaptureProcessToken();

int? exit = GatewayCli.Prepare(args, Console.Out, Console.Error, out HostApplicationBuilder? builder);
if (exit is { } code)
{
    return code;
}

ArcaneCore.Kernel.Diagnostics.DiagnosticsHostingExtensions.UseArcaneDiagnostics(builder!);
builder!.Services.AddGateway(builder.Configuration);

IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return ExitCodes.Success;

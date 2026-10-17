using ArcaneCore.Gateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Tier 3a gateway daemon (docs/areas/gateway.md). Configuration: the generic host defaults (appsettings.json, environment,
// command line) plus gateway.json next to the executable. It needs Database:Auth:* (the realm's auth database), Gateway:*
// and a World started with Cluster:Worker:Enabled=true.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "gateway.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
ArcaneCore.Kernel.Diagnostics.DiagnosticsHostingExtensions.UseArcaneDiagnostics(builder);
builder.Services.AddGateway(builder.Configuration);

IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;

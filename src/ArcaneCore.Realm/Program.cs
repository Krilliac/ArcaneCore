using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
ArcaneCore.Kernel.Diagnostics.DiagnosticsHostingExtensions.UseArcaneDiagnostics(builder); // crash hooks and invariants (docs/ops/invariants.md), installed before anything else can fail

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<RealmSeedOptions>(builder.Configuration.GetSection(RealmSeedOptions.SectionName));
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddHostedService<LogonServer>();

IHost host = builder.Build();

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
return 0;

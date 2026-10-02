using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<RealmSeedOptions>(builder.Configuration.GetSection(RealmSeedOptions.SectionName));
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddHostedService<LogonServer>();

IHost host = builder.Build();

// Bring the auth schema to the current version and seed configured realms before accepting connections.
await host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync().ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);

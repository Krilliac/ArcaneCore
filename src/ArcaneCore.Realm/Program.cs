using ArcaneCore.Data;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<RealmSeedOptions>(builder.Configuration.GetSection(RealmSeedOptions.SectionName));
builder.Services.AddArcaneCoreData(builder.Configuration);
builder.Services.AddSingleton<ArcaneCoreDbInitializer>();
builder.Services.AddHostedService<LogonServer>();

IHost host = builder.Build();

// Ensure the schema exists and seed configured realms before accepting connections.
await host.Services.GetRequiredService<ArcaneCoreDbInitializer>().InitializeAsync().ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);

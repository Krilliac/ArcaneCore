using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorldOptions>(builder.Configuration.GetSection(WorldOptions.SectionName));
builder.Services.AddArcaneCoreData(builder.Configuration);
builder.Services.AddArcaneCoreCharacterData(builder.Configuration);
builder.Services.AddHostedService<WorldServer>();

IHost host = builder.Build();

// Ensure the character schema exists and seed the DB-driven world data before serving.
await host.Services.GetRequiredService<CharacterDbInitializer>().InitializeAsync().ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);

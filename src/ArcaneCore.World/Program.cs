using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddCharacterDatabase(builder.Configuration);
builder.Services.AddWorldDatabase(builder.Configuration);
builder.Services.AddWorldDaemon(builder.Configuration);

IHost host = builder.Build();

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
return 0;

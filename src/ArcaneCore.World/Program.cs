using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

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

// Bring every schema this daemon touches to the current version (fail closed on mismatch).
await host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync().ConfigureAwait(false);
await host.Services.GetRequiredService<CharacterDbInitializer>().InitializeAsync().ConfigureAwait(false);
await host.Services.GetRequiredService<WorldDbInitializer>().InitializeAsync().ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);
return 0;

using ArcaneCore.Data;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// arcane-spell-import <DBFilesClient directory>
// Reads Spell.dbc, SpellCastTimes.dbc, SpellDuration.dbc, SpellRange.dbc and SpellRadius.dbc
// from a 1.12.1 (build 5875) client and replaces the matching world-database tables
// (docs/areas/spells.md § Loading spell data). The DBCs are game data: extract them from your
// own client; they are never committed.
if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: arcane-spell-import <path to DBFilesClient> [--Database:ConnectionString=...]");
    return 1;
}

string directory = args[0];
if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"directory '{directory}' does not exist");
    return 1;
}

SpellDbcContent content;
try
{
    content = SpellDbcImporter.ReadDirectory(directory);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"cannot read the DBCs: {ex.Message}");
    return 1;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args[1..]);
builder.Services.AddWorldDatabase(builder.Configuration);
using IHost host = builder.Build();

await host.Services.GetRequiredService<WorldDbInitializer>().InitializeAsync().ConfigureAwait(false);

using IServiceScope scope = host.Services.CreateScope();
await scope.ServiceProvider.GetRequiredService<ISpellContentStore>().ReplaceDbcTablesAsync(content).ConfigureAwait(false);

Console.WriteLine(
    $"Imported {content.Spells.Count} spells, {content.CastTimes.Count} cast times, {content.Durations.Count} durations, " +
    $"{content.Ranges.Count} ranges and {content.Radii.Count} radii.");
return 0;

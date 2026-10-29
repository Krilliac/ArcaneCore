using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Threat;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Threat;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Reload;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Threat;

/// <summary>
/// Rows written by <c>arcane-content-importer spell-threat</c> reach the live table: loaded at startup through the EF store, and swapped by
/// <c>.reload spell_threats</c> after a second import (the hot-reload path of the importer's output).
/// </summary>
public sealed class SpellThreatImportedRowsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-threatrows-" + Guid.NewGuid().ToString("N"));

    public SpellThreatImportedRowsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task ImportAsync(string name, string sql, string database)
    {
        string dump = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(dump, sql);
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(["spell-threat", dump, "--database", database], output, error, CancellationToken.None);
        Assert.True(code == ExitCodes.Ok, error + output.ToString());
    }

    [Fact]
    public async Task ImportedRows_LoadAtStartup_AndAReloadPicksUpTheNextImport()
    {
        string database = Path.Combine(_directory, "world.db");
        await ImportAsync("first.sql", "INSERT INTO `spell_threat` (`entry`,`threat`,`multiplier`) VALUES (7386,100,1),(8092,0,2),(300,0,0);", database);

        await using var host = WorldTestHost.Start();
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options;
        ServiceCollection services = new();
        services.AddSingleton(Options.Create(new HotReloadOptions()));
        services.AddScoped(_ => new WorldDbContext(options));
        services.AddScoped<ISpellThreatDataStore, EfSpellThreatStore>();
        var feature = new SpellThreatFeature(services.BuildServiceProvider(), NullLogger<SpellThreatFeature>.Instance);
        feature.Attach(host.World);

        Assert.Equal(100, feature.Table.Find(7386)!.Threat); // Sunder Armor: flat threat
        Assert.Equal(0, feature.Table.Find(300)!.Threat);
        Assert.Equal(0f, feature.Table.Find(300)!.Multiplier); // a zero multiplier row yields zero threat
        Assert.Equal(2f, feature.Table.Find(8092)!.Multiplier);

        ServiceProvider withFeature = services.AddSingleton(feature).BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellThreatReloadable(withFeature));
        await ImportAsync("second.sql", "INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (7386,45);", database);

        ReloadResult result = await coordinator.ReloadAsync("spell_threats");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(45, feature.Table.Find(7386)!.Threat);
        Assert.Null(feature.Table.Find(300));
    }
}

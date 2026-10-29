using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Tests.ContentImport.Reputation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>The <c>import</c> command writes and reports the reputation template tables (backlog item 43).</summary>
public sealed class ReputationTemplatesCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-reptpl-" + Guid.NewGuid().ToString("N"));

    public ReputationTemplatesCliTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public async Task Import_WritesTwelveSpilloverAndThreeRateRows_AndVerifyCountsThem()
    {
        string dump = Path.Combine(_directory, "rep.sql");
        File.WriteAllText(dump, ReputationTemplatesImportTests.Dump);
        string database = Path.Combine(_directory, "world.db");

        (int plan, string planOut) = await RunAsync("plan", dump);
        (int import, string importOut) = await RunAsync("import", dump, "--database", database);
        (int dry, string dryOut) = await RunAsync("import", dump, "--database", Path.Combine(_directory, "dry.db"), "--dry-run");

        Assert.Equal(ExitCodes.Ok, plan);
        Assert.Contains("reputation_spillover_template", planOut, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Ok, import);
        Assert.Contains("reputation_spillover_template  12", importOut, StringComparison.Ordinal);
        Assert.Contains("reputation_reward_rate  3", importOut, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Ok, dry);
        Assert.Contains("reputation_spillover_template  12", dryOut, StringComparison.Ordinal);
        await using WorldDbContext db = new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options);
        Assert.Equal(12, await db.Set<ReputationSpilloverTemplateRow>().CountAsync());
        Assert.Equal(3, await db.Set<ReputationRewardRateRow>().CountAsync());
        (_, string verifyOut) = await RunAsync("verify", "--database", database);
        Assert.Contains("reputation_spillover_template  12", verifyOut, StringComparison.Ordinal);
        Assert.Contains("reputation_reward_rate  3", verifyOut, StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, new StringWriter(), CancellationToken.None);
        return (code, output.ToString());
    }
}

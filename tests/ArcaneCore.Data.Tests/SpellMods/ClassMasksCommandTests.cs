using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.SpellMods;

/// <summary>The <c>class-masks</c> command of the content importer: spell_affect dump in, class-mask overlay file out.</summary>
public sealed class ClassMasksCommandTests : IDisposable
{
    private const string Dump = """
        CREATE TABLE `spell_affect` (`entry` smallint unsigned NOT NULL, `effectId` tinyint unsigned NOT NULL, `SpellFamilyMask` bigint unsigned NOT NULL, PRIMARY KEY (`entry`,`effectId`));
        INSERT INTO `spell_affect` VALUES (11083,0,12714007),(12536,0,275427498743),(17904,0,0);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-masks-" + Guid.NewGuid().ToString("N"));

    public ClassMasksCommandTests() => Directory.CreateDirectory(_directory);

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

    private string WriteDump(string text, string name = "world.sql")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task WritesTheOverlay_AndSaysHowManyMasksNeedMoreThanThirtyTwoBits()
    {
        string overlay = Path.Combine(_directory, "masks.txt");

        (int code, string output, string error) = await RunAsync("class-masks", WriteDump(Dump), "--class-mask-file", overlay);

        Assert.True(string.IsNullOrEmpty(error), error);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("3 row(s), 1 above 32 bits, 1 empty", output, StringComparison.Ordinal);
        string[] lines = [.. File.ReadAllLines(overlay).Where(l => !l.StartsWith('#'))];
        Assert.Equal([$"11083 0 0x{12714007UL:X16}", $"12536 0 0x{275427498743UL:X16}", $"17904 0 0x{0UL:X16}"], lines);
    }

    [Fact]
    public async Task DryRun_WritesNothing()
    {
        string overlay = Path.Combine(_directory, "masks.txt");

        (int code, _, _) = await RunAsync("class-masks", WriteDump(Dump), "--class-mask-file", overlay, "--dry-run");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.False(File.Exists(overlay));
    }

    [Fact]
    public async Task ADumpWithoutSpellAffect_IsAWrongSchema_AndWritesNothing()
    {
        string overlay = Path.Combine(_directory, "masks.txt");

        (int code, _, string error) = await RunAsync("class-masks", WriteDump("CREATE TABLE `other` (`a` int);\nINSERT INTO `other` VALUES (1);"), "--class-mask-file", overlay);

        Assert.Equal(ExitCodes.Schema, code);
        Assert.Contains("spell_affect", error, StringComparison.Ordinal);
        Assert.False(File.Exists(overlay));
    }

    [Fact]
    public async Task TheFileOption_AndADump_AreRequired()
    {
        (int noFile, _, _) = await RunAsync("class-masks", WriteDump(Dump));
        (int noDump, _, _) = await RunAsync("class-masks", "--class-mask-file", Path.Combine(_directory, "x.txt"));

        Assert.Equal(ExitCodes.Usage, noFile);
        Assert.Equal(ExitCodes.Usage, noDump);
    }

    [Fact]
    public async Task APathInsideAGitWorkTree_IsRefused()
    {
        string repo = Path.Combine(_directory, "repo");
        Directory.CreateDirectory(repo);
        using (var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await git.WaitForExitAsync();
        }

        (int code, _, _) = await RunAsync("class-masks", WriteDump(Dump), "--class-mask-file", Path.Combine(repo, "masks.txt"));

        Assert.Equal(ExitCodes.RepositoryPath, code);
        Assert.False(File.Exists(Path.Combine(repo, "masks.txt")));
    }
}

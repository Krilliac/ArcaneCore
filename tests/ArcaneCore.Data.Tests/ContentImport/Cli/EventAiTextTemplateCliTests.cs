using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

public sealed class EventAiTextTemplateCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-text-template-" + Guid.NewGuid().ToString("N"));
    public EventAiTextTemplateCliTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(991, -5, true)]
    [InlineData(992, -5, false)]
    [InlineData(991, -6, false)]
    public async Task ImportReportsStringChoicesAndVerifyChecksSignedReferences(int positive, int negative, bool valid)
    {
        string source = Path.Combine(_directory, "input.sql"), db = Path.Combine(_directory, "world.db"), report = Path.Combine(_directory, "report.json");
        await File.WriteAllTextAsync(source, $"""
            INSERT INTO `broadcast_text` (`Id`,`Text`) VALUES (991,'Synthetic broadcast');
            INSERT INTO `creature_ai_texts` (`entry`,`content_default`,`type`,`language`,`emote`) VALUES (-5,'Synthetic AI text',0,0,0);
            INSERT INTO `dbscript_random_templates` (`id`,`type`,`target_id`,`chance`) VALUES
            (1,0,{positive},25),(1,0,{negative},0),(2,0,0,0),(1,1,999,0);
            """);
        using var output = new StringWriter();
        using var error = new StringWriter();
        int imported = await ContentImporterCli.RunAsync(["import", source, "--database", db, "--report", report], output, error, CancellationToken.None);
        Assert.True(imported == ExitCodes.Ok, error.ToString());
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.Equal(3, json.RootElement.GetProperty("imported").GetProperty("dbscript_random_templates").GetInt32());
        output.GetStringBuilder().Clear();
        int verified = await ContentImporterCli.RunAsync(["verify", "--database", db], output, error, CancellationToken.None);
        Assert.Equal(valid ? ExitCodes.Ok : ExitCodes.Verify, verified);
        Assert.Contains("creature_ai_text_template  3", output.ToString(), StringComparison.Ordinal);
        if (!valid) Assert.Contains("1 creature_ai_text_template choice(s) reference missing", output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}

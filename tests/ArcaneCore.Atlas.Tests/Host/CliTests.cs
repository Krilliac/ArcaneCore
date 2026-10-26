using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Cli;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

public sealed class ParsedArgumentsTests
{
    private static readonly Dictionary<string, bool> Allowed = new() { ["--data"] = true, ["--port"] = true, ["--open"] = false };

    [Fact]
    public void ValuesCanFollowTheNameOrBeJoinedWithAnEqualsSign_AndFlagsTakeNone()
    {
        ParsedArguments parsed = ParsedArguments.Parse(["--data", "D:/x y", "--port=8080", "--open", "status", "a=b"], Allowed);

        Assert.Equal("D:/x y", parsed.Value("--data"));
        Assert.Equal("8080", parsed.Value("--port"));
        Assert.True(parsed.Has("--open"));
        Assert.Equal(string.Empty, parsed.Value("--open"));
        Assert.Equal(new[] { "status", "a=b" }, parsed.Positionals);
        Assert.False(ParsedArguments.Parse([], Allowed).Has("--data"));
        Assert.Null(ParsedArguments.Parse([], Allowed).Value("--data"));
    }

    [Fact]
    public void ADoubleDashEndsOptionParsing_AndSingleDashWordsAreArguments()
    {
        ParsedArguments parsed = ParsedArguments.Parse(["--open", "--", "--data", "-x", "-"], Allowed);

        Assert.True(parsed.Has("--open"));
        Assert.False(parsed.Has("--data"));
        Assert.Equal(new[] { "--data", "-x", "-" }, parsed.Positionals);
    }

    [Fact]
    public void ValuesMayContainEqualsSigns()
    {
        ParsedArguments parsed = ParsedArguments.Parse(["--data=a=b"], Allowed);

        Assert.Equal("a=b", parsed.Value("--data"));
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--nope=1")]
    [InlineData("--open=1")]
    public void UnknownOptionsAndValuesOnFlags_AreUsageErrors(string argument)
    {
        Assert.Throws<UsageException>(() => ParsedArguments.Parse([argument], Allowed));
    }

    [Fact]
    public void ARepeatedOptionOrAMissingValue_IsAUsageError()
    {
        Assert.Throws<UsageException>(() => ParsedArguments.Parse(["--open", "--open"], Allowed));
        Assert.Throws<UsageException>(() => ParsedArguments.Parse(["--data", "a", "--data=b"], Allowed));
        Assert.Throws<UsageException>(() => ParsedArguments.Parse(["--port"], Allowed));
    }
}

public sealed class CommandLineArgumentsTests
{
    [Theory]
    [InlineData("42", "42")]
    [InlineData("-9462.66", "-9462.66")]
    [InlineData("1e3", "1e3")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("[1,2,3]", "[1,2,3]")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("\"quoted\"", "\"quoted\"")]
    public void JsonLookingValuesAreParsedAsJson(string text, string expectedJson)
    {
        Assert.Equal(expectedJson, CommandLineArguments.ParseValue(text)!.ToJsonString());
    }

    [Theory]
    [InlineData("-9462.66,16.19,57.05")]
    [InlineData("Eastern Kingdoms")]
    [InlineData("007")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData(".5")]
    [InlineData("+5")]
    [InlineData("0x10")]
    [InlineData("True")]
    [InlineData("")]
    [InlineData("1,2")]
    [InlineData("{not json}")]
    [InlineData("D:\\maps\\0054040.map")]
    public void EverythingElseStaysAString(string text)
    {
        JsonNode? value = CommandLineArguments.ParseValue(text);

        Assert.NotNull(value);
        Assert.Equal(System.Text.Json.JsonValueKind.String, value.GetValueKind());
        Assert.Equal(text, value.GetValue<string>());
    }

    [Fact]
    public void TheWordNull_IsJsonNull()
    {
        Assert.Null(CommandLineArguments.ParseValue("null"));
    }

    [Fact]
    public async Task PairsAndAJsonObjectAreMerged_WithPairsRefusedWhenTheyRepeatAKey()
    {
        JsonObject args = await CommandLineArguments.BuildAsync(["x=1", "at=1,2,3", "note=a=b"], """{"yaw":5,"flag":true}""", TextReader.Null, CancellationToken.None);

        Assert.Equal("""{"yaw":5,"flag":true,"x":1,"at":"1,2,3","note":"a=b"}""", args.ToJsonString());
        await Assert.ThrowsAsync<UsageException>(() => CommandLineArguments.BuildAsync(["yaw=6"], """{"yaw":5}""", TextReader.Null, CancellationToken.None));
        await Assert.ThrowsAsync<UsageException>(() => CommandLineArguments.BuildAsync(["x=1", "x=2"], null, TextReader.Null, CancellationToken.None));
    }

    [Fact]
    public async Task TheJsonObjectCanComeFromStandardInput_WithOrWithoutAUtf8Bom()
    {
        JsonObject args = await CommandLineArguments.BuildAsync([], "-", new StringReader("""{"a":[1,2]}"""), CancellationToken.None);
        JsonObject bomStdin = await CommandLineArguments.BuildAsync([], "-", new StringReader("\uFEFF{\"a\":[1,2]}"), CancellationToken.None);
        JsonObject bomArg = await CommandLineArguments.BuildAsync([], "\uFEFF{\"a\":3}", TextReader.Null, CancellationToken.None);

        Assert.Equal("""{"a":[1,2]}""", args.ToJsonString());
        Assert.Equal("""{"a":[1,2]}""", bomStdin.ToJsonString());
        Assert.Equal("""{"a":3}""", bomArg.ToJsonString());
        Assert.Empty(await CommandLineArguments.BuildAsync([], null, TextReader.Null, CancellationToken.None));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("")]
    [InlineData("""{"a":1,"a":2}""")]
    public async Task JsonThatIsNotAnObject_IsAUsageError_NotACrash(string json)
    {
        await Assert.ThrowsAsync<UsageException>(() => CommandLineArguments.BuildAsync([], json, TextReader.Null, CancellationToken.None));
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=value")]
    [InlineData("1x=1")]
    [InlineData("a b=1")]
    [InlineData("a/b=1")]
    public async Task APairNeedsAnArgumentNameThatIsAnIdentifier(string pair)
    {
        await Assert.ThrowsAsync<UsageException>(() => CommandLineArguments.BuildAsync([pair], null, TextReader.Null, CancellationToken.None));
    }
}

public sealed class ScriptFileTests
{
    [Fact]
    public void EachNonBlankLineIsAStep_NumberedByItsLine_WhateverTheLineEndingsOrLeadingUtf8Bom()
    {
        IReadOnlyList<ScriptStep> steps = ScriptFile.Parse("\uFEFF\r\n{\"command\":\"status\"}\r\n\r\n  {\"command\":\"camera.goto\",\"args\":{\"x\":1}}  \r\n{\"command\":\"help\",\"args\":{}}");
        IReadOnlyList<ScriptStep> bomFirstLine = ScriptFile.Parse("\uFEFF{\"command\":\"status\"}");

        Assert.Equal(new[] { 2, 4, 5 }, steps.Select(s => s.Line));
        Assert.Equal(new[] { "status", "camera.goto", "help" }, steps.Select(s => s.Command));
        Assert.Empty(steps[0].Args);
        Assert.Equal("""{"x":1}""", steps[1].Args.ToJsonString());
        Assert.Equal("status", Assert.Single(bomFirstLine).Command);
    }

    [Fact]
    public void AMalformedScript_IsRefusedAsAWhole_ListingEveryBadLine()
    {
        UsageException e = Assert.Throws<UsageException>(() => ScriptFile.Parse(
            "{\"command\":\"status\"}\n# a comment\n{\"command\":\"\"}\n{\"command\":\"x\",\"args\":[1]}\n{\"command\":\"x\",\"extra\":1}\n[1]\n{\"args\":{}}\n{\"command\":5}\nnot json"));

        foreach (int line in new[] { 2, 3, 4, 5, 6, 7, 8, 9 })
        {
            Assert.Contains($"line {line}:", e.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("line 1:", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstTenProblemsAreListed()
    {
        string script = string.Join('\n', Enumerable.Repeat("garbage", 25));

        UsageException e = Assert.Throws<UsageException>(() => ScriptFile.Parse(script));

        Assert.Equal(10, e.Message.Split("line ").Length - 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n  \n")]
    public void AScriptWithNoCommandsIsAnError(string text)
    {
        Assert.Throws<UsageException>(() => ScriptFile.Parse(text));
    }

    [Fact]
    public void ADuplicateKeyInALine_IsALineProblem_NotACrash()
    {
        UsageException e = Assert.Throws<UsageException>(() => ScriptFile.Parse("""{"command":"status","command":"help"}"""));

        Assert.Contains("line 1:", e.Message, StringComparison.Ordinal);
    }
}

public sealed class BrowserDiscoveryTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        Dictionary<string, string> map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void OnWindows_EdgeComesBeforeChrome_AndTheX86FolderBeforeTheNativeOne()
    {
        string x86Edge = Path.Combine("C:/pf86", @"Microsoft\Edge\Application\msedge.exe");
        string edge = Path.Combine("C:/pf", @"Microsoft\Edge\Application\msedge.exe");
        string chrome = Path.Combine("C:/pf", @"Google\Chrome\Application\chrome.exe");
        Func<string, string?> env = Env(("ProgramFiles(x86)", "C:/pf86"), ("ProgramFiles", "C:/pf"), ("LocalAppData", "C:/local"));

        Assert.Equal(("Microsoft Edge", x86Edge), SystemBrowserLauncher.Find(BrowserPlatform.Windows, env, p => p == x86Edge || p == edge || p == chrome));
        Assert.Equal(("Microsoft Edge", edge), SystemBrowserLauncher.Find(BrowserPlatform.Windows, env, p => p == edge || p == chrome));
        Assert.Equal(("Google Chrome", chrome), SystemBrowserLauncher.Find(BrowserPlatform.Windows, env, p => p == chrome));
    }

    [Fact]
    public void OnWindows_ChromeIsAlsoLookedForInThePerUserFolder_AndNothingInstalledIsNull()
    {
        string perUser = Path.Combine("C:/local", @"Google\Chrome\Application\chrome.exe");
        Func<string, string?> env = Env(("ProgramFiles(x86)", "C:/pf86"), ("ProgramFiles", "C:/pf"), ("LocalAppData", "C:/local"));

        Assert.Equal(("Google Chrome", perUser), SystemBrowserLauncher.Find(BrowserPlatform.Windows, env, p => p == perUser));
        Assert.Null(SystemBrowserLauncher.Find(BrowserPlatform.Windows, env, _ => false));
        Assert.Null(SystemBrowserLauncher.Find(BrowserPlatform.Windows, Env(), _ => true));
    }

    [Fact]
    public void OnMacOs_TheKnownApplicationBundlesAreUsed()
    {
        Assert.Equal(
            ("Google Chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"),
            SystemBrowserLauncher.Find(BrowserPlatform.MacOs, Env(), p => p.Contains("Google Chrome", StringComparison.Ordinal)));
        Assert.Equal(
            ("Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"),
            SystemBrowserLauncher.Find(BrowserPlatform.MacOs, Env(), _ => true));
    }

    [Fact]
    public void OnLinux_ThePathIsSearchedForTheBrowserExecutables()
    {
        string path = string.Join(Path.PathSeparator, "/usr/local/bin", "/opt/bin");
        Func<string, string?> env = Env(("PATH", path));

        (string Name, string Path)? chrome = SystemBrowserLauncher.Find(BrowserPlatform.Linux, env, p => p == Path.Combine("/opt/bin", "google-chrome"));
        (string Name, string Path)? edge = SystemBrowserLauncher.Find(BrowserPlatform.Linux, env, p => p.EndsWith("microsoft-edge", StringComparison.Ordinal) || p.EndsWith("google-chrome", StringComparison.Ordinal));

        Assert.Equal(("Google Chrome", Path.Combine("/opt/bin", "google-chrome")), chrome);
        Assert.Equal("Microsoft Edge", edge?.Name);
        Assert.Null(SystemBrowserLauncher.Find(BrowserPlatform.Linux, Env(), _ => true));
    }
}

/// <summary>The verbs, run in-process against fixture modules with their own state folder.</summary>
public sealed class CliVerbTests
{
    private static string[] Words(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Help_PrintsTheUsage_ForTheVerbAndTheUsualFlags()
    {
        using var rig = new CliRig();

        foreach (string[] args in new string[][] { ["help"], ["-h"], ["--help"], ["help", "extra"], ["run", "--help"], ["serve", "-h"] })
        {
            CliResult result = await rig.RunAsync(args);

            Assert.Equal(0, result.Exit);
            Assert.Equal(AtlasCli.Usage.ReplaceLineEndings("\n"), string.Join('\n', result.Lines));
            Assert.Equal(string.Empty, result.Error);
        }

        foreach (string verb in new[] { "serve", "ctl", "url", "run", "script", "mcp", "layers", "help", "ARCANECORE_ATLAS_STATE_DIR" })
        {
            Assert.Contains(verb, AtlasCli.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoVerbAndAnUnknownVerb_AreUsageErrors_WithNothingOnStandardOutput()
    {
        using var rig = new CliRig();

        CliResult none = await rig.RunAsync();
        CliResult unknown = await rig.RunAsync("frobnicate");

        Assert.Equal(2, none.Exit);
        Assert.Contains("a verb is required", none.Error, StringComparison.Ordinal);
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("unknown verb 'frobnicate'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("arcane-atlas help", unknown.Error, StringComparison.Ordinal);
        Assert.Empty(none.Lines);
        Assert.Empty(unknown.Lines);
    }

    [Fact]
    public async Task Layers_ListsTheCatalogOneLayerPerLine()
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync("layers");
        CliResult extra = await rig.RunAsync("layers", "all");

        Assert.Equal(0, result.Exit);
        Assert.Equal(LayerCatalog.All.Select(l => l.Id), result.Lines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
        Assert.Equal(2, extra.Exit);
    }

    [Fact]
    public async Task Run_ExecutesOneCommandHeadless_AndPrintsItsResultAsIndentedJson()
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync("run", "status");

        Assert.Equal(0, result.Exit);
        Assert.True(result.Lines.Count > 3, "the default form is indented");
        JsonNode document = result.Document();
        Assert.True(document.Flag("ok"));
        Assert.Equal("status", document.Str("command"));
        Assert.Equal("headless", document.Str("data.mode"));
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task Run_Compact_PrintsOneLine()
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync("run", "--compact", "test.echo", "text=hello");

        Assert.Equal(0, result.Exit);
        JsonNode line = result.Json(0);
        Assert.Single(result.Lines);
        Assert.Equal("hello", line.Str("data.echo"));
        Assert.Equal("Cli", line.Str("data.origin"));
        Assert.True(line.Flag("data.headless"));
    }

    [Fact]
    public async Task Run_TurnsKeyValueWordsIntoTypedArguments()
    {
        using var rig = new CliRig();

        CliResult point = await rig.RunAsync("run", "--compact", "camera.goto", "at=-9450,20,60", "yaw=10", "mode=top");
        CliResult parts = await rig.RunAsync("run", "--compact", "camera.goto", "x=-9450", "y=20", "z=60", "distance=77");
        CliResult mixed = await rig.RunAsync("run", "--compact", "view.set", "--json", """{"camera":{"yaw":5}}""", "hud=false");

        Assert.Equal(0, point.Exit);
        Assert.Equal(-9450, point.Json().Num("data.view.camera.target.0"));
        Assert.Equal("top", point.Json().Str("data.view.camera.mode"));
        Assert.Equal(77, parts.Json().Num("data.view.camera.distance"));
        Assert.Equal(0, mixed.Exit);
        Assert.Equal(5, mixed.Json().Num("data.view.camera.yaw"));
        Assert.False(mixed.Json().Flag("data.view.hud"));
    }

    [Fact]
    public async Task Run_ReadsTheJsonArgumentsFromStandardInput()
    {
        using var rig = new CliRig();
        rig.Stdin = new StringReader("""{"text":"from stdin"}""");

        CliResult result = await rig.RunAsync("run", "--compact", "test.echo", "--json", "-");

        Assert.Equal(0, result.Exit);
        Assert.Equal("from stdin", result.Json().Str("data.echo"));
    }

    [Fact]
    public async Task Run_ACommandThatFails_PrintsItsError_AndExitsWithOne()
    {
        using var rig = new CliRig();

        CliResult unknown = await rig.RunAsync("run", "--compact", "no.such");
        CliResult bad = await rig.RunAsync("run", "--compact", "camera.goto");

        Assert.Equal(1, unknown.Exit);
        Assert.False(unknown.Json().Flag("ok"));
        Assert.Equal(CommandException.UnknownCommand, unknown.Json().Str("error.code"));
        Assert.Equal(1, bad.Exit);
        Assert.Equal(CommandException.BadArguments, bad.Json().Str("error.code"));
        Assert.Equal(string.Empty, unknown.Error);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run status extra")]
    [InlineData("run status --bogus")]
    [InlineData("run status --data")]
    [InlineData("run status --json {}x")]
    [InlineData("run status --json [1]")]
    [InlineData("run camera.goto x=1 x=2")]
    [InlineData("run status =5")]
    [InlineData("ctl")]
    [InlineData("script")]
    [InlineData("script a.jsonl b.jsonl")]
    [InlineData("script --pid 5 a.jsonl")]
    [InlineData("script --attach --data x a.jsonl")]
    [InlineData("serve extra")]
    [InlineData("serve --port abc")]
    [InlineData("serve --port 70000")]
    [InlineData("serve --port -1")]
    [InlineData("serve --port ")]
    [InlineData("mcp extra")]
    [InlineData("mcp --attach --headless")]
    [InlineData("mcp --headless --pid 5")]
    public async Task MalformedCommandLines_AreUsageErrors_ThatPrintNothingOnStandardOutput(string commandLine)
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync(Words(commandLine));

        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Lines);
        Assert.StartsWith("arcane-atlas: ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_UsesTheDataAndClientDirectoriesFromTheEnvironment_UnlessTheOptionsSayOtherwise()
    {
        using var rig = new CliRig();
        string envData = Path.Combine(rig.State.Root, "env-data");
        string optionData = Path.Combine(rig.State.Root, "option-data");
        string client = Path.Combine(rig.State.Root, "client");
        string extractedClient = Path.Combine(rig.State.Root, "client-extract");
        Directory.CreateDirectory(Path.Combine(envData, "maps"));
        Directory.CreateDirectory(Path.Combine(optionData, "maps"));
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        Directory.CreateDirectory(Path.Combine(extractedClient, "World"));
        rig.Env[AtlasPaths.TerrainEnvironmentVariable] = envData;

        CliResult fromEnvironment = await rig.RunAsync("run", "--compact", "status");
        CliResult overridden = await rig.RunAsync("run", "--compact", "--data", optionData, "--client", client, "status");
        CliResult extracted = await rig.RunAsync("run", "--compact", "--data", optionData, "--client", extractedClient, "status");

        Assert.Equal(envData, fromEnvironment.Json().Str("data.paths.terrainData"));
        Assert.Null(fromEnvironment.Json().At("data.paths.clientRoot"));
        Assert.Equal(optionData, overridden.Json().Str("data.paths.terrainData"));
        Assert.Equal(client, overridden.Json().Str("data.paths.clientRoot"));
        Assert.Empty(overridden.Json().At("data.problems")!.AsArray());
        Assert.Equal(extractedClient, extracted.Json().Str("data.paths.clientRoot"));
        Assert.Empty(extracted.Json().At("data.problems")!.AsArray());
    }

    [Fact]
    public async Task Run_ReportsAMissingDataDirectoryAsAProblem_NotAsAFailure()
    {
        using var rig = new CliRig();
        string missing = Path.Combine(rig.State.Root, "nowhere");

        CliResult result = await rig.RunAsync("run", "--compact", "--data", missing, "status");

        Assert.Equal(0, result.Exit);
        Assert.Contains("does not exist", result.Json().Str("data.problems.0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_EachInvocationStartsFromTheDefaultView_ButBookmarksAreSavedInTheStateFolder()
    {
        using var rig = new CliRig();

        await rig.RunAsync("run", "camera.goto", "x=-9450", "y=20", "z=60");
        CliResult view = await rig.RunAsync("run", "--compact", "view.get");
        await rig.RunAsync("run", "bookmark.add", "name=Saved by run");
        CliResult bookmarks = await rig.RunAsync("run", "--compact", "bookmarks.list");

        Assert.Equal(ViewState.Default.Camera.Target.X, view.Json().Num("data.camera.target.0"), 6);
        Assert.Contains("Saved by run", bookmarks.Json().At("data")!.AsArray().Select(b => b.Str("name")));
        Assert.True(File.Exists(rig.State.BookmarksFile));
    }

    [Fact]
    public async Task Run_Capture_UsesTheHeadlessRenderer_AndWritesIntoTheRequestedFolder()
    {
        using var rig = new CliRig();
        string shots = Path.Combine(rig.State.Root, "shots");

        CliResult result = await rig.RunAsync("run", "--compact", "capture", "width=64", "height=32", $"out={shots}");

        Assert.Equal(0, result.Exit);
        string path = result.Json().Str("data.path");
        Assert.StartsWith(shots, path, StringComparison.Ordinal);
        Assert.Equal((64, 32), CaptureCommand.PngSize(await File.ReadAllBytesAsync(path)));
        Assert.Equal("headless", result.Json().Str("data.source"));
        Assert.Single(rig.Renderer.Views);
    }

    [Fact]
    public async Task Run_RefusesACommandThatNeedsARunningInstance()
    {
        var instanceOnly = new CommandDefinition("test.instance", "Needs serve.", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()), CommandSurfaces.Instance);
        using var rig = new CliRig(register: c => c.Commands.Register(instanceOnly));

        CliResult result = await rig.RunAsync("run", "--compact", "test.instance");

        Assert.Equal(1, result.Exit);
        Assert.Equal(CommandException.Unavailable, result.Json().Str("error.code"));
        Assert.Contains("arcane-atlas serve", result.Json().Str("error.message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_RunsEveryLine_PrintsOneJsonLinePerCommand_AndKeepsTheViewBetweenSteps()
    {
        using var rig = new CliRig();
        string file = Path.Combine(rig.State.Root, "tour.jsonl");
        Directory.CreateDirectory(rig.State.Root);
        await File.WriteAllTextAsync(file, "{\"command\":\"camera.goto\",\"args\":{\"x\":-9450,\"y\":20,\"z\":60,\"yaw\":33}}\n\n{\"command\":\"view.get\"}\r\n{\"command\":\"test.echo\",\"args\":{\"text\":\"end\"}}\n");

        CliResult result = await rig.RunAsync("script", file);

        Assert.Equal(0, result.Exit);
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(new[] { 1, 3, 4 }, result.Lines.Select((_, i) => (int)result.Json(i).Num("line")));
        Assert.Equal(33, result.Json(1).Num("data.camera.yaw"));
        Assert.Equal("Script", result.Json(2).Str("data.origin"));
        Assert.True(result.Json(2).Flag("data.headless"));
    }

    [Fact]
    public async Task Script_StopsAtTheFirstFailure_UnlessKeepGoingIsGiven()
    {
        using var rig = new CliRig();
        string file = Path.Combine(rig.State.Root, "bad.jsonl");
        Directory.CreateDirectory(rig.State.Root);
        await File.WriteAllTextAsync(file, "{\"command\":\"status\"}\n{\"command\":\"no.such\"}\n{\"command\":\"test.echo\"}\n");

        CliResult stopped = await rig.RunAsync("script", file);
        CliResult kept = await rig.RunAsync("script", "--keep-going", file);

        Assert.Equal(1, stopped.Exit);
        Assert.Equal(2, stopped.Lines.Count);
        Assert.False(stopped.Json(1).Flag("ok"));
        Assert.Equal(2, stopped.Json(1).Num("line"));
        Assert.Equal(1, kept.Exit);
        Assert.Equal(3, kept.Lines.Count);
        Assert.True(kept.Json(2).Flag("ok"));
    }

    [Fact]
    public async Task Script_ARunWhereEveryCommandSucceeds_ExitsZeroEvenWithKeepGoing()
    {
        using var rig = new CliRig();
        rig.Stdin = new StringReader("{\"command\":\"status\"}\n{\"command\":\"view.get\"}\n");

        CliResult result = await rig.RunAsync("script", "--keep-going", "-");

        Assert.Equal(0, result.Exit);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public async Task Script_AMalformedScript_RunsNothing_AndSaysWhichLinesAreWrong()
    {
        using var rig = new CliRig();
        rig.Stdin = new StringReader("{\"command\":\"camera.goto\",\"args\":{\"x\":1,\"y\":2,\"z\":3}}\nnot json\n{\"command\":\"status\",\"bogus\":1}\n");

        CliResult result = await rig.RunAsync("script", "-");

        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Lines);
        Assert.Contains("line 2:", result.Error, StringComparison.Ordinal);
        Assert.Contains("line 3:", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_AFileThatCannotBeRead_IsAFailureNotAUsageError()
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync("script", Path.Combine(rig.State.Root, "missing.jsonl"));

        Assert.Equal(1, result.Exit);
        Assert.Contains("cannot read script", result.Error, StringComparison.Ordinal);
    }
}

/// <summary>ctl, script --attach and mcp --attach against a real in-process server found through its discovery file.</summary>
public sealed class CliInstanceTests
{
    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task Ctl_SendsOneCommandToTheRunningInstance_LabelledAsTheCli()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);

        CliResult status = await cli.RunAsync("ctl", "--compact", "status");
        CliResult echo = await cli.RunAsync("ctl", "--compact", "test.echo", "text=hi");
        CliResult move = await cli.RunAsync("ctl", "--compact", "camera.goto", "x=-9450", "y=20", "z=60", "yaw=44");

        Assert.Equal(0, status.Exit);
        Assert.Equal("serve", status.Json().Str("data.mode"));
        Assert.Equal("hi", echo.Json().Str("data.echo"));
        Assert.Equal("Cli", echo.Json().Str("data.origin"));
        Assert.False(echo.Json().Flag("data.headless"));
        Assert.Equal(0, move.Exit);
        Assert.Equal(44, server.Context.View.Current.Camera.Yaw);
        JsonNode viewEvent = Assert.Single(server.EventsOf(EventHub.Topics.View));
        Assert.Equal("cli", viewEvent.Str("origin"));
    }

    [Fact]
    public async Task Ctl_APrintedFailure_ExitsOne_AndTheIndentedFormIsTheDefault()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);

        CliResult failed = await cli.RunAsync("ctl", "no.such");
        CliResult plain = await cli.RunAsync("ctl", "status");

        Assert.Equal(1, failed.Exit);
        Assert.Equal(CommandException.UnknownCommand, failed.Document().Str("error.code"));
        Assert.True(plain.Lines.Count > 3);
    }

    [Fact]
    public async Task Ctl_WithoutARunningInstance_ExplainsWhereItLooked_AndExitsOne()
    {
        using var cli = new CliRig();

        CliResult result = await cli.RunAsync("ctl", "status");

        Assert.Equal(1, result.Exit);
        Assert.Contains("no running 'arcane-atlas serve' instance", result.Error, StringComparison.Ordinal);
        Assert.Contains(cli.State.InstancesDirectory, result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task Ctl_PrunesTheFilesOfDeadInstances_AndStillFindsTheLiveOne()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);
        string stale = Discovery.Write(server.State, new DiscoveryRecord(4_000_001, 9, "gone", DateTimeOffset.UtcNow.AddHours(-1), "x", null, null));
        cli.IsAlive = record => record.Pid == Environment.ProcessId;

        CliResult result = await cli.RunAsync("ctl", "--compact", "status");

        Assert.Equal(0, result.Exit);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(server.Server!.DiscoveryFile));
    }

    [Fact]
    public async Task Ctl_WithSeveralInstances_NeedsPid_AndPidChoosesOne()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);
        Discovery.Write(server.State, new DiscoveryRecord(4_000_002, 9, "other", DateTimeOffset.UtcNow.AddHours(-1), "x", null, null));
        cli.IsAlive = _ => true;

        CliResult ambiguous = await cli.RunAsync("ctl", "status");
        CliResult chosen = await cli.RunAsync("ctl", "--compact", "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "status");
        CliResult unknown = await cli.RunAsync("ctl", "--pid", "999", "status");
        CliResult notANumber = await cli.RunAsync("ctl", "--pid", "abc", "status");
        CliResult zero = await cli.RunAsync("ctl", "--pid", "0", "status");

        Assert.Equal(2, ambiguous.Exit);
        Assert.Contains("2 instances are running", ambiguous.Error, StringComparison.Ordinal);
        Assert.Contains("4000002", ambiguous.Error, StringComparison.Ordinal);
        Assert.Equal(0, chosen.Exit);
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("pid 999", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(2, notANumber.Exit);
        Assert.Equal(2, zero.Exit);
    }

    [Fact]
    public async Task Ctl_AnInstanceThatCannotBeReached_IsReportedAsUnavailable()
    {
        using var cli = new CliRig();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Discovery.Write(cli.State, new DiscoveryRecord(4_000_003, port, "token", DateTimeOffset.UtcNow, "x", null, null));
        cli.IsAlive = _ => true;

        CliResult result = await cli.RunAsync("ctl", "--compact", "status");

        Assert.Equal(1, result.Exit);
        Assert.False(result.Json().Flag("ok"));
        Assert.Equal(CommandException.Unavailable, result.Json().Str("error.code"));
        Assert.Contains("Cannot reach", result.Json().Str("error.message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ctl_ATokenTheInstanceDoesNotKnow_IsReportedAsUnauthorized_WithoutLeakingAnything()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(new AtlasState(Path.Combine(server.Directory, "other-state")));
        Discovery.Write(cli.State, new DiscoveryRecord(4_000_004, server.Server!.Port, "not-the-token", DateTimeOffset.UtcNow, "x", null, null));
        cli.IsAlive = _ => true;

        CliResult result = await cli.RunAsync("ctl", "--compact", "status");

        Assert.Equal(1, result.Exit);
        Assert.Equal("unauthorized", result.Json().Str("error.code"));
        Assert.DoesNotContain(server.Server!.Token, string.Concat(result.Lines) + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptAttach_RunsTheStepsOnTheInstance_LabelledAsAScript()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);
        cli.Stdin = new StringReader("{\"command\":\"camera.goto\",\"args\":{\"x\":-9450,\"y\":20,\"z\":60,\"yaw\":71}}\n{\"command\":\"test.echo\"}\n");

        CliResult result = await cli.RunAsync("script", "--attach", "-");

        Assert.Equal(0, result.Exit);
        Assert.Equal(71, server.Context.View.Current.Camera.Yaw);
        Assert.Equal("Script", result.Json(1).Str("data.origin"));
        Assert.False(result.Json(1).Flag("data.headless"));
        Assert.Equal("script", Assert.Single(server.EventsOf(EventHub.Topics.View)).Str("origin"));
        Assert.Equal(new[] { 1, 2 }, result.Lines.Select((_, i) => (int)result.Json(i).Num("line")));
    }

    [Fact]
    public async Task ScriptAttach_WithoutAnInstance_FailsWithExitOne()
    {
        using var cli = new CliRig();
        cli.Stdin = new StringReader("{\"command\":\"status\"}\n");

        CliResult result = await cli.RunAsync("script", "--attach", "-");

        Assert.Equal(1, result.Exit);
        Assert.Contains("no running", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_Attach_ServesTheInstancesCommandsAsTools_AndMovesItsView()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);
        var input = new InputPipe();
        cli.Stdin = input;
        await using CliRun run = cli.Start("mcp", "--attach");

        input.Send(Args("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""));
        input.Send(Args("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        input.Send(Args("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
        input.Send(Args("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"camera_goto","arguments":{"x":-9450,"y":20,"z":60,"yaw":88}}}"""));
        IReadOnlyList<string> lines = await run.Out.WaitForLinesAsync(3);
        input.End();
        CliResult result = await run.CompleteAsync();

        Assert.Equal(0, result.Exit);
        Assert.Equal(3, lines.Count);
        // tools/list and tools/call run concurrently, so the answers are matched by id, not by position.
        JsonNode list = lines.Select(l => JsonNode.Parse(l)!).Single(m => m.Num("id") == 2);
        Assert.Contains("test_echo", list.At("result.tools")!.AsArray().Select(t => t.Str("name")));
        JsonNode call = lines.Select(l => JsonNode.Parse(l)!).Single(m => m.Num("id") == 3);
        Assert.False(call.Flag("result.isError"));
        Assert.Equal(88, server.Context.View.Current.Camera.Yaw);
        Assert.Equal("mcp", Assert.Single(server.EventsOf(EventHub.Topics.View)).Str("origin"));
        Assert.Contains("attached to the instance", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_WithAnInstanceRunning_AttachesByDefault_AndRefusesDataOptions()
    {
        await using HostRig server = await HostRig.StartAsync();
        using var cli = new CliRig(server.State);
        var input = new InputPipe();
        input.End();
        cli.Stdin = input;

        CliResult attached = await cli.RunAsync("mcp");
        CliResult withData = await cli.RunAsync("mcp", "--data", cli.State.Root);
        CliResult headless = await cli.RunAsync("mcp", "--headless", "--data", cli.State.Root);

        Assert.Equal(0, attached.Exit);
        Assert.Contains("attached to the instance", attached.Error, StringComparison.Ordinal);
        Assert.Equal(2, withData.Exit);
        Assert.Contains("--headless", withData.Error, StringComparison.Ordinal);
        Assert.Equal(0, headless.Exit);
        Assert.Contains("headless", headless.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_Attach_WithoutAnInstance_FailsWithExitOne_AndMcpAloneRunsHeadless()
    {
        using var cli = new CliRig();
        var input = new InputPipe();
        input.End();
        cli.Stdin = input;

        CliResult attach = await cli.RunAsync("mcp", "--attach");
        CliResult alone = await cli.RunAsync("mcp");

        Assert.Equal(1, attach.Exit);
        Assert.Contains("no running", attach.Error, StringComparison.Ordinal);
        Assert.Equal(0, alone.Exit);
        Assert.Contains("headless", alone.Error, StringComparison.Ordinal);
        Assert.Empty(alone.Lines);
    }

    [Fact]
    public async Task Mcp_Headless_AnswersOnStandardOutputOnly_AndStopsWhenInputEnds()
    {
        using var cli = new CliRig();
        cli.Stdin = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"nope\"}\n");

        CliResult result = await cli.RunAsync("mcp", "--headless");

        Assert.Equal(0, result.Exit);
        Assert.Equal(2, result.Lines.Count);
        Assert.All(result.Lines, line => Assert.Equal("2.0", JsonNode.Parse(line).Str("jsonrpc")));
        Assert.Equal(McpServer.MethodNotFound, result.Json(1).Num("error.code"));
    }
}

/// <summary>serve, in-process: the address line, the discovery file, the browser launch and a clean stop.</summary>
public sealed class CliServeTests
{
    private static async Task<HttpResponseMessage> GetAsync(JsonNode address, DiscoveryRecord record, string path)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address.Str("url")) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", record.Token);
        return await http.GetAsync(path);
    }

    [Fact]
    public async Task Serve_PrintsOneAddressLine_PublishesTheDiscoveryFile_AndStopsCleanlyOnCancel()
    {
        using var cli = new CliRig();
        await using CliRun run = cli.Start("serve");

        IReadOnlyList<string> first = await run.Out.WaitForLinesAsync(1);
        JsonNode address = JsonNode.Parse(first[0])!;
        DiscoveryRecord record = Assert.Single(Discovery.Live(cli.State));
        using HttpResponseMessage status = await GetAsync(address, record, "api/status");
        using HttpResponseMessage index = await GetAsync(address, record, "/");

        Assert.Equal(Environment.ProcessId, address.Num("pid"));
        Assert.Equal(record.Port, address.Num("port"));
        Assert.Equal($"http://127.0.0.1:{record.Port}/", address.Str("url"));
        Assert.Equal(Path.Combine(cli.State.InstancesDirectory, $"{Environment.ProcessId}.json"), address.Str("discovery"));
        Assert.Equal(AtlasVersion.Current, address.Str("version"));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(FakeAssets.IndexHtml, await index.Content.ReadAsStringAsync());
        Assert.DoesNotContain(record.Token, run.Out.Text + run.Error.Text, StringComparison.Ordinal);
        Assert.Contains("listening on", run.Error.Text, StringComparison.Ordinal);
        Assert.Equal(0, cli.Browser.Opens);

        CliResult result = await run.StopAsync();

        Assert.Equal(0, result.Exit);
        Assert.Single(result.Lines);
        Assert.False(File.Exists(address.Str("discovery")));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(address, record, "api/status"));
    }

    [Fact]
    public async Task Serve_Open_StartsTheBrowserWithTheTokenInTheUrlFragment_AndNowhereElse()
    {
        using var cli = new CliRig();
        await using CliRun run = cli.Start("serve", "--open");

        JsonNode address = JsonNode.Parse((await run.Out.WaitForLinesAsync(1))[0])!;
        await Wait.UntilAsync(() => cli.Browser.Opens == 1, "the browser launch");
        DiscoveryRecord record = Assert.Single(Discovery.Live(cli.State));

        Assert.Equal($"http://127.0.0.1:{record.Port}/#t={record.Token}", cli.Browser.Opened!.AbsoluteUri);
        Assert.Equal(address.Str("url"), cli.Browser.Opened.GetLeftPart(UriPartial.Path));
        Assert.DoesNotContain(record.Token, run.Out.Text + run.Error.Text, StringComparison.Ordinal);
        await run.StopAsync();
    }

    [Fact]
    public async Task Serve_OpenWithoutAnInstalledBrowser_PrintsTheAddressWithTheTokenOnStandardErrorOnly()
    {
        using var cli = new CliRig { Browser = new RecordingBrowser(name: null) };
        await using CliRun run = cli.Start("serve", "--open");

        await run.Out.WaitForLinesAsync(1);
        await Wait.UntilAsync(() => run.Error.Text.Contains("no Microsoft Edge or Google Chrome", StringComparison.Ordinal), "the no-browser notice");
        DiscoveryRecord record = Assert.Single(Discovery.Live(cli.State));

        Assert.Contains($"#t={record.Token}", run.Error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(record.Token, run.Out.Text, StringComparison.Ordinal);
        await run.StopAsync();
    }

    [Fact]
    public async Task Serve_APortGivenOnTheCommandLine_IsTheOneUsed_AndAPortInUseIsAFailure()
    {
        using var cli = new CliRig();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int busy = ((IPEndPoint)probe.LocalEndpoint).Port;
        try
        {
            CliResult conflict = await cli.RunAsync("serve", "--port", busy.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.Equal(1, conflict.Exit);
            Assert.Contains($"cannot listen on 127.0.0.1:{busy}", conflict.Error, StringComparison.Ordinal);
            Assert.Empty(conflict.Lines);
            Assert.Empty(Discovery.Live(cli.State));
        }
        finally
        {
            probe.Stop();
        }

        await using CliRun run = cli.Start("serve", "--port", busy.ToString(System.Globalization.CultureInfo.InvariantCulture));
        JsonNode address = JsonNode.Parse((await run.Out.WaitForLinesAsync(1))[0])!;
        Assert.Equal(busy, address.Num("port"));
        await run.StopAsync();
    }

    [Fact]
    public async Task Serve_ShowsTheConfiguredDataDirectoryInItsStatus()
    {
        using var cli = new CliRig();
        string data = Path.Combine(cli.State.Root, "data");
        Directory.CreateDirectory(Path.Combine(data, "maps"));
        await using CliRun run = cli.Start("serve", "--data", data);
        JsonNode address = JsonNode.Parse((await run.Out.WaitForLinesAsync(1))[0])!;
        DiscoveryRecord record = Assert.Single(Discovery.Live(cli.State));

        using HttpResponseMessage status = await GetAsync(address, record, "api/status");

        Assert.Equal(data, JsonNode.Parse(await status.Content.ReadAsStringAsync()).Str("paths.terrainData"));
        Assert.Equal(data, record.TerrainData);
        await run.StopAsync();
    }
}

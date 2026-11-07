using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Diff;
using ArcaneCore.Atlas.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Diff;

/// <summary>
/// The native vmap probe integration: the query file and answer formats of <c>tools/terrain/vmap-oracle/main.cpp</c>, the comparison with the server's
/// answers, and the child process run against a stand-in executable (a script that answers every query line). Nothing here needs vmangos or game data;
/// the comparison against the real <c>VMapProbe</c> is the Game test <c>VMapNativeOracleTests</c> and <c>diff.vmap.native</c> on a machine that has it.
/// </summary>
public sealed class VMapNativeProbeTests : IDisposable
{
    private static readonly IndoorSample Outside = new(false, 0, 0, 0, 0, 0, true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-atlas-vmapprobe-test-" + Guid.NewGuid().ToString("N"));

    public VMapNativeProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a killed stand-in may still hold its working folder for a moment; the OS clears the temp folder
        }
    }

    /// <summary>Writes a stand-in probe: one output line per query line in the file named by its third argument (<c>dir --batch file</c>).</summary>
    private string Script(string answerLine, int exitCode = 0, bool hang = false)
    {
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(_dir, "probe.cmd");
            string body = hang
                ? "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n"
                : $"@echo off\r\nfor /f \"usebackq tokens=*\" %%a in (\"%~3\") do echo {answerLine}\r\nexit /b {exitCode}\r\n";
            File.WriteAllText(path, body);
            return path;
        }

        string unix = Path.Combine(_dir, "probe.sh");
        string script = hang
            ? "#!/bin/sh\nsleep 60\n"
            : $"#!/bin/sh\nwhile read -r l; do echo \"{answerLine}\"; done < \"$3\"\nexit {exitCode}\n";
        File.WriteAllText(unix, script);
        File.SetUnixFileMode(unix, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return unix;
    }

    private static NativeVMapQuery Query(float x = 1.5f) => new(0, new Vector3(x, 2.25f, 3f), new Vector3(10f, 20f, 30f));

    private static JsonObject Compare(NativeVMapAnswer atFrom, NativeVMapAnswer atProbeZ, bool los, float? z, IndoorSample indoor)
        => VMapNativeProbe.Compare(atFrom, atProbeZ, los, z, indoor);

    [Fact]
    public void TheQueryFile_HasOneInvariantLinePerQuery_MapThenFromThenTo()
    {
        string text = VMapNativeProbe.FormatQueries([Query(), new NativeVMapQuery(36, new Vector3(-16.4f, -383.07f, 61.78f), new Vector3(0, 0, 0))]);

        Assert.Equal("0 1.5 2.25 3 10 20 30\n36 -16.4 -383.07 61.78 0 0 0\n", text);
    }

    [Fact]
    public void AnswersAreParsedFieldForField_AndTheNoFloorSentinelMeansNoFloor()
    {
        IReadOnlyList<NativeVMapAnswer> answers = VMapNativeProbe.ParseAnswers("12.5000 1 1 4294967295 -2 17\n-200000.0000 0 0 0 0 0\n", 2);

        Assert.Equal(new NativeVMapAnswer(12.5f, true, true, uint.MaxValue, -2, 17), answers[0]);
        Assert.Equal(12.5f, answers[0].Floor);
        Assert.False(answers[1].LineOfSight);
        Assert.Null(answers[1].Floor);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("1 1 1 0 0 0\n", 2)]
    [InlineData("1 1 1 0 0\n", 1)]
    [InlineData("x 1 1 0 0 0\n", 1)]
    [InlineData("1 2 1 0 0 0\n", 1)]
    [InlineData("1 1 1 -1 0 0\n", 1)]
    [InlineData("NaN 1 1 0 0 0\n", 1)]
    public void AMalformedOrShortAnswer_IsRefused(string output, int expected)
        => Assert.Throws<InvalidDataException>(() => VMapNativeProbe.ParseAnswers(output, expected));

    [Fact]
    public void Compare_AgreeingAnswers_MatchOnEveryCount()
    {
        var inside = new NativeVMapAnswer(0, true, true, 0x8000, 5, 6);
        var floor = new NativeVMapAnswer(40.001f, true, false, 0, 0, 0);

        JsonObject json = Compare(inside, floor, true, 40.0f, new IndoorSample(true, 0x8000, 0, 5, 6, 40f, false));

        Assert.True(json["allMatch"]!.GetValue<bool>());
    }

    [Fact]
    public void Compare_EachDisagreementIsNamed()
    {
        var native = new NativeVMapAnswer(0, true, false, 0, 0, 0);
        var floor = new NativeVMapAnswer(40f, true, false, 0, 0, 0);

        Assert.False(Compare(native, floor, false, 40f, Outside)["lineOfSight"]!["match"]!.GetValue<bool>());
        Assert.False(Compare(native, floor, true, 40.5f, Outside)["height"]!["match"]!.GetValue<bool>());
        Assert.False(Compare(native, floor, true, null, Outside)["height"]!["match"]!.GetValue<bool>());
        Assert.False(Compare(native, floor, true, 40f, new IndoorSample(true, 1, 0, 1, 1, 0, false))["area"]!["match"]!.GetValue<bool>());
        Assert.True(Compare(native, floor, true, 40f, Outside)["allMatch"]!.GetValue<bool>());
    }

    [Fact]
    public void Compare_NoFloorOnBothSides_Matches_AndAWmoGroupNeedsTheSameIds()
    {
        var noFloor = new NativeVMapAnswer(-200000f, true, true, 8, 5, 6);

        Assert.True(Compare(noFloor, noFloor, true, null, new IndoorSample(true, 8, 0, 5, 6, 0, false))["allMatch"]!.GetValue<bool>());
        Assert.False(Compare(noFloor, noFloor, true, null, new IndoorSample(true, 8, 0, 5, 7, 0, false))["area"]!["match"]!.GetValue<bool>());
        Assert.False(Compare(noFloor, noFloor, true, null, new IndoorSample(true, 9, 0, 5, 6, 0, false))["area"]!["match"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_AnswersOneLinePerQuery_FromTheChildProcess()
    {
        string probe = Script("12.5000 1 0 0 0 0");

        IReadOnlyList<NativeVMapAnswer> answers = await VMapNativeProbe.RunAsync(probe, _dir, [Query(), Query(2f), Query(3f)], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(3, answers.Count);
        Assert.All(answers, a => Assert.Equal(new NativeVMapAnswer(12.5f, true, false, 0, 0, 0), a));
    }

    [Fact]
    public async Task RunAsync_AFailingProbe_IsFailedWithItsExitCode()
    {
        string probe = Script("1 1 1 0 0 0", exitCode: 3);

        CommandException e = await Assert.ThrowsAsync<CommandException>(
            () => VMapNativeProbe.RunAsync(probe, _dir, [Query()], TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("code 3", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AHangingProbe_IsStoppedAtTheTimeout()
    {
        string probe = Script(string.Empty, hang: true);
        DateTime start = DateTime.UtcNow;

        CommandException e = await Assert.ThrowsAsync<CommandException>(
            () => VMapNativeProbe.RunAsync(probe, _dir, [Query()], TimeSpan.FromMilliseconds(700), CancellationToken.None));

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("did not finish", e.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(30), "the probe was not killed at the timeout");
    }

    [Fact]
    public async Task RunAsync_ACancelledCaller_GetsTheCancellation()
    {
        string probe = Script(string.Empty, hang: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => VMapNativeProbe.RunAsync(probe, _dir, [Query()], TimeSpan.FromSeconds(60), cts.Token));
    }

    [Fact]
    public async Task RunAsync_MissingProbeMissingVmapsOrBadQueryCount_AreRefusedBeforeAnythingRuns()
    {
        CommandException missing = await Assert.ThrowsAsync<CommandException>(
            () => VMapNativeProbe.RunAsync(Path.Combine(_dir, "nope.exe"), _dir, [Query()], TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(CommandException.Unavailable, missing.Code);

        string probe = Script("1 1 1 0 0 0");
        CommandException noVmaps = await Assert.ThrowsAsync<CommandException>(
            () => VMapNativeProbe.RunAsync(probe, Path.Combine(_dir, "no-such-dir"), [Query()], TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(CommandException.Unavailable, noVmaps.Code);

        CommandException none = await Assert.ThrowsAsync<CommandException>(
            () => VMapNativeProbe.RunAsync(probe, _dir, [], TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(CommandException.BadArguments, none.Code);
    }

    [Fact]
    public void TheCommand_IsRegisteredWithTheDiffModule()
    {
        var context = new AtlasContext(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true);
        new DiffModule().Register(context);

        Assert.True(context.Commands.TryGet(VMapNativeCommands.Name, out _));
    }
}

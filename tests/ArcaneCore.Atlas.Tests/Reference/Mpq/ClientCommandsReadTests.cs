using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Extract;
using ArcaneCore.Atlas.Tests.Reference.Dbc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// How <c>client.read</c> writes its file. The guard resolves symbolic links and junctions but cannot see a hard link, so an
/// overwrite must replace the destination's directory entry rather than open the existing file for writing: otherwise every other
/// name of that file (one inside a repository or the client folder) would get the client data too.
/// </summary>
public sealed class ClientCommandsReadTests : IDisposable
{
    private const string WdtPath = "World\\Maps\\Azeroth\\Azeroth.wdt";
    private static readonly byte[] Wdt = Encoding.ASCII.GetBytes("synthetic wdt bytes");
    private static readonly byte[] Precious = [5, 6, 7, 8, 9];

    private readonly TestDirectory _dir = new();
    private readonly TestDirectory _elsewhere = new();

    public void Dispose()
    {
        _dir.Dispose();
        _elsewhere.Dispose();
    }

    [Fact]
    public async Task Overwrite_ADestinationThatIsAHardLinkToAnotherFile_ReplacesTheEntry_AndLeavesTheOtherFileUnchanged()
    {
        using AtlasContext context = Compose();
        string other = _elsewhere.Write("tracked.bin", Precious);
        string destination = Path.Combine(_dir.Path, "out", "Azeroth.wdt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ExtractPathSafetyTests.MakeHardLink(destination, other);
        try
        {
            Assert.Equal(Precious, File.ReadAllBytes(destination));

            CommandResult result = await Run(context, new JsonObject { ["path"] = WdtPath, ["out"] = destination, ["overwrite"] = true });

            Assert.True(result.Ok, result.ErrorMessage);
            Assert.Equal(Precious, File.ReadAllBytes(other));
            Assert.Equal(Wdt, File.ReadAllBytes(destination));
            Assert.Equal(destination, (string?)result.Data!["out"], ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal(new[] { "Azeroth.wdt" }, Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            // The link entry goes first, so no cleanup can reach the other folder's file through it.
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task Overwrite_AnOrdinaryFile_ReplacesIt_AndLeavesNoStagingFile()
    {
        using AtlasContext context = Compose();
        string destination = _dir.Write(Path.Combine("out", "Azeroth.wdt"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24]);

        CommandResult result = await Run(context, new JsonObject { ["path"] = WdtPath, ["out"] = destination, ["overwrite"] = true });

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(Wdt, File.ReadAllBytes(destination));
        Assert.Equal(new[] { "Azeroth.wdt" }, Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Select(Path.GetFileName).ToArray());
    }

    private AtlasContext Compose()
    {
        MemoryClientFiles tables = ClientMapDirectoryTests.SampleFiles();
        _dir.Write(
            Path.Combine("client", "Data", "dbc.MPQ"),
            MpqTestWriter.Build(
            [
                new TestMpqFile(ClientMapDirectory.MapTablePath, tables.Read(ClientMapDirectory.MapTablePath)!) { Zlib = true },
                new TestMpqFile(ClientMapDirectory.AreaTablePath, tables.Read(ClientMapDirectory.AreaTablePath)!) { Zlib = true },
                new TestMpqFile(WdtPath, Wdt),
            ]));
        return AtlasModules.Compose(new AtlasPaths(null, Path.Combine(_dir.Path, "client")), NullLoggerFactory.Instance, headless: true, [new ClientModule()]);
    }

    private static Task<CommandResult> Run(AtlasContext context, JsonObject args)
        => context.Commands.InvokeAsync("client.read", args, context.Services, CommandOrigin.Script, headless: true, CancellationToken.None);
}

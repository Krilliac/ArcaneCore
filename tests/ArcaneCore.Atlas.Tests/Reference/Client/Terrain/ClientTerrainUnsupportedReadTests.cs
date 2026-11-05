using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>
/// A patch archive can store a WDT or ADT with a compression method the MPQ reader has no code for; IClientFileSource.Read then throws
/// NotSupportedException. The terrain service must report that tile as Rejected with the reason, like any other unreadable file, and
/// never let the exception escape to the caller.
/// </summary>
public sealed class ClientTerrainUnsupportedReadTests
{
    private const string Message = "compression method 0x10 (bzip2) is not supported";

    private static readonly ClientMap Azeroth = new(0, "Azeroth", "Eastern Kingdoms", 0);

    [Fact]
    public void AWdtWithAnUnsupportedCompression_IsRejectedWithTheReason()
    {
        var files = new UnsupportedFiles(failWdt: true, failAdt: true);
        var service = new ClientTerrainService(() => files, () => new OneMap(), NullLogger.Instance);

        WdtLoad load = service.LoadWdt(0);

        Assert.Equal(TileState.Rejected, load.State);
        Assert.Contains(Message, load.Detail, StringComparison.Ordinal);
        Assert.Null(load.File);
    }

    [Fact]
    public void AnAdtWithAnUnsupportedCompression_IsRejectedWithTheReason_WhileTheWdtLoads()
    {
        var tile = new TileKey(0, 32, 48);
        var files = new MemoryClientFiles();
        files.Add(ClientMapPaths.WdtPath(Azeroth.Directory), SyntheticTerrain.Wdt([(tile.X, tile.Y)]));
        var service = new ClientTerrainService(() => new UnsupportedFiles(failWdt: false, failAdt: true, files), () => new OneMap(), NullLogger.Instance);

        AdtLoad load = service.LoadAdt(tile);

        Assert.Equal(TileState.Rejected, load.State);
        Assert.Contains(Message, load.Detail, StringComparison.Ordinal);
        Assert.Null(load.File);
    }

    private sealed class OneMap : IMapDirectory
    {
        public IReadOnlyList<ClientMap> Maps => [Azeroth];

        public bool TryGetMap(uint mapId, out ClientMap map)
        {
            map = Azeroth;
            return mapId == Azeroth.Id;
        }

        public string? AreaName(uint areaId) => null;
    }

    /// <summary>Throws for the chosen kind of file and serves everything else from <c>inner</c>.</summary>
    private sealed class UnsupportedFiles(bool failWdt, bool failAdt, IClientFileSource? inner = null) : IClientFileSource
    {
        public string Description => "unsupported-compression";

        public bool Exists(string path) => true;

        public byte[]? Read(string path)
        {
            bool wdt = path.EndsWith(".wdt", StringComparison.OrdinalIgnoreCase);
            if ((wdt && failWdt) || (!wdt && failAdt))
            {
                throw new NotSupportedException(Message);
            }

            return inner?.Read(path);
        }

        public IReadOnlyList<string> List(string prefix) => [];

        public string? ArchiveOf(string path) => null;
    }
}

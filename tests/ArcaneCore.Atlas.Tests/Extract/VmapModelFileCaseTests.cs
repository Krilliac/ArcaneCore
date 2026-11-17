using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// Model file names that differ only in case name one file on a case-insensitive file system (Windows, macOS, WSL <c>/mnt/c</c>),
/// so <see cref="VmapExtractor"/> must not write them in parallel to one file and one staging file: it writes the first in ordinal
/// order once and reports the others. The names here are synthetic: the client-name pipeline's FixNameCase makes such names unlikely,
/// so the model file step is driven directly.
/// </summary>
public sealed class VmapModelFileCaseTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    [Fact]
    public void TwoModelFileNames_ThatDifferOnlyInCase_AreWrittenOnce_TheFirstInOrdinalOrder_AndReportedOnce()
    {
        using var output = new TestDirectory();
        var built = new ConcurrentBag<string>();
        KeyValuePair<string, VmapModelInfo>[] files =
        [
            ModelFile("abc.m2.vmo", "World\\a\\abc.m2"),
            ModelFile("Abc.m2.vmo", "World\\b\\Abc.m2"),
            ModelFile("Other.m2.vmo", "World\\c\\Other.m2"),
        ];

        VmapExtractor.VmapModelFileResult result = VmapExtractor.WriteModelFiles(output.Path, files, model => Build(built, model), threads: 4, CancellationToken.None);

        // "Abc" sorts before "abc" in ordinal order: it is the one built and written, once.
        Assert.Equal(new[] { "Abc.m2.vmo", "Other.m2.vmo" }, built.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, result.Written);
        Assert.Equal(2L * Bytes.Length, result.Bytes);
        Assert.Empty(result.Failures);
        string collision = Assert.Single(result.Collisions);
        Assert.Contains("'Abc.m2.vmo'", collision, StringComparison.Ordinal);
        Assert.Contains("'abc.m2.vmo'", collision, StringComparison.Ordinal);
        Assert.Contains("differ only in case", collision, StringComparison.Ordinal);

        // Whatever the file system: exactly the two kept names, with the case they were written in, and no staging file.
        Assert.Equal(new[] { "Abc.m2.vmo", "Other.m2.vmo" }, Directory.GetFiles(output.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(output.Path, "Abc.m2.vmo")));
    }

    [Fact]
    public void ThreeSpellingsOfOneName_AreOneFile_AndOneProblemNamesThemAll()
    {
        using var output = new TestDirectory();
        var built = new ConcurrentBag<string>();
        KeyValuePair<string, VmapModelInfo>[] files =
        [
            ModelFile("cube.m2.vmo", "World\\x\\cube.m2"),
            ModelFile("CUBE.m2.vmo", "World\\y\\CUBE.m2"),
            ModelFile("Cube.m2.vmo", "World\\z\\Cube.m2"),
        ];

        VmapExtractor.VmapModelFileResult result = VmapExtractor.WriteModelFiles(output.Path, files, model => Build(built, model), threads: 3, CancellationToken.None);

        Assert.Equal(new[] { "CUBE.m2.vmo" }, built.ToArray());
        Assert.Equal(1, result.Written);
        string collision = Assert.Single(result.Collisions);
        foreach (string name in new[] { "'CUBE.m2.vmo'", "'Cube.m2.vmo'", "'cube.m2.vmo'" })
        {
            Assert.Contains(name, collision, StringComparison.Ordinal);
        }

        Assert.Equal(new[] { "CUBE.m2.vmo" }, Directory.GetFiles(output.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void NamesThatDifferBeyondCase_AreAllWritten_AndNothingIsReported()
    {
        using var output = new TestDirectory();
        var built = new ConcurrentBag<string>();
        KeyValuePair<string, VmapModelInfo>[] files = [ModelFile("Abc.m2.vmo", "a"), ModelFile("Abd.m2.vmo", "b"), ModelFile("Abc.wmo.vmo", "c")];

        VmapExtractor.VmapModelFileResult result = VmapExtractor.WriteModelFiles(output.Path, files, model => Build(built, model), threads: 3, CancellationToken.None);

        Assert.Equal(3, result.Written);
        Assert.Empty(result.Collisions);
        Assert.Equal(new[] { "Abc.m2.vmo", "Abc.wmo.vmo", "Abd.m2.vmo" }, built.Order(StringComparer.Ordinal).ToArray());
    }

    private static KeyValuePair<string, VmapModelInfo> ModelFile(string fileName, string clientPath)
        => new(fileName, new VmapModelInfo(fileName, clientPath, IsWmo: false, HeaderCount: 1, VmapWmoDoodads.None, Array.Empty<Vector3>()));

    private static byte[] Build(ConcurrentBag<string> built, VmapModelInfo model)
    {
        built.Add(model.Name);
        return Bytes;
    }
}

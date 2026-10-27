using System.Security.Cryptography;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// Ground-truth oracles for the MPQ reader over the real 1.12.1 client: the DBC set must hash to the checksum list made by
/// mpqcli (StormLib) on 2026-10-08, and a deterministic sample spread over every extracted archive must equal, byte for byte,
/// a StormLib extraction of the same archives. Both oracles come from a reader this repository did not write.
/// </summary>
public sealed class MpqRealDataTests(ITestOutputHelper output)
{
    // The archives the StormLib extraction was made from, in its patch order (atlas-logs\client-extract.log).
    private static readonly string[] ExtractedArchives = ["dbc.MPQ", "terrain.MPQ", "wmo.MPQ", "model.MPQ", "misc.MPQ", "patch.MPQ", "patch-2.MPQ"];

    // client-dbc-5875 README.txt:13-15: patch.MPQ carries deletion entries for exactly these four DBCs.
    private static readonly string[] DeletedDbcs =
        ["CharacterCreateCameras.dbc", "SoundCharacterMacroLines.dbc", "SpellAuraNames.dbc", "SpellEffectNames.dbc"];

    [ClientDbcFact]
    public void EveryClientDbc_MatchesTheStormLibChecksumList()
    {
        Dictionary<string, string> sums = ReadSums(Path.Combine(ClientArchiveData.DbcDirectory!, "SHA256SUMS"));
        using MpqFileSystem files = MpqFileSystem.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data"));

        string[] listed = [.. files.List("DBFilesClient\\")
            .Where(p => p.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(p.LastIndexOf('\\') + 1)..])];
        int matched = 0;
        foreach ((string name, string hash) in sums)
        {
            byte[]? bytes = files.Read("DBFilesClient\\" + name);
            Assert.True(bytes is not null, $"{name} is not readable from the archives");
            Assert.True(
                string.Equals(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal),
                $"{name} from {files.ArchiveOf("DBFilesClient\\" + name)} does not match SHA256SUMS");
            matched++;
        }

        // The listed set is exactly the checksum list: nothing extra, nothing missing.
        Assert.Equal(sums.Keys.Order(StringComparer.OrdinalIgnoreCase), listed.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        // The four names the checksum list leaves out are hidden by patch.MPQ deletion entries, not lost.
        string[] deleted = [.. files.Archives
            .SelectMany(a => a.ListFile)
            .Where(p => p.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => files.DeletedBy(p) is not null)
            .Select(p => p[(p.LastIndexOf('\\') + 1)..])
            .Order(StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(DeletedDbcs, deleted, StringComparer.OrdinalIgnoreCase);
        Assert.All(deleted, d => Assert.Equal("patch.MPQ", files.DeletedBy("DBFilesClient\\" + d)));

        output.WriteLine($"DBCs matched SHA256SUMS: {matched} of {sums.Count}; deleted by patch.MPQ: {deleted.Length}");
        Assert.Equal(154, matched);
    }

    [ClientExtractFact]
    public void ASampleOverEveryArchive_EqualsTheStormLibExtraction()
    {
        using MpqFileSystem files = MpqFileSystem.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data"));
        var extract = new DirectoryClientFiles(ClientArchiveData.ExtractRoot!);

        // Classify every extracted path by what the archive reader resolves it to.
        var byArchive = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        var unknown = new List<string>();
        foreach (string path in extract.List(string.Empty))
        {
            if (path.StartsWith('('))
            {
                // (listfile) and (attributes) of the last archive extracted: archive metadata, not client files.
                continue;
            }

            if (files.ArchiveOf(path) is { } archive)
            {
                if (!byArchive.TryGetValue(archive, out List<string>? list))
                {
                    byArchive[archive] = list = [];
                }

                list.Add(path);
            }
            else if (files.DeletedBy(path) is not null)
            {
                deleted.Add(path);
            }
            else
            {
                unknown.Add(path);
            }
        }

        Assert.Empty(unknown);
        Assert.Equal(ExtractedArchives.Order(StringComparer.OrdinalIgnoreCase), byArchive.Keys, StringComparer.OrdinalIgnoreCase);

        // mpqcli writes a deletion entry as an empty file; the reader reports the name as deleted instead.
        Assert.Equal(5708, deleted.Count);
        Assert.All(deleted, d => Assert.Empty(extract.Read(d)!));

        // Sample: every World\Maps\Azeroth file, plus every n-th path (ordinal order) of each archive, at least 300 per archive.
        var sample = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string archive, List<string> paths) in byArchive)
        {
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            int stride = Math.Max(1, paths.Count / 300);
            for (int i = 0; i < paths.Count; i += stride)
            {
                sample.Add(paths[i]);
            }

            sample.UnionWith(paths.Where(p => p.StartsWith("World\\Maps\\Azeroth\\", StringComparison.OrdinalIgnoreCase)));
        }

        var compared = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var mismatched = new List<string>();
        foreach (string path in sample)
        {
            byte[] ours = files.Read(path)!;
            byte[] theirs = extract.Read(path)!;
            if (!ours.AsSpan().SequenceEqual(theirs))
            {
                mismatched.Add($"{path} ({ours.Length} vs {theirs.Length} bytes)");
            }

            string archive = files.ArchiveOf(path)!;
            compared[archive] = compared.GetValueOrDefault(archive) + 1;
            bytes += ours.Length;
        }

        int azeroth = sample.Count(p => p.StartsWith("World\\Maps\\Azeroth\\", StringComparison.OrdinalIgnoreCase));
        int azerothTerrain = byArchive["terrain.MPQ"].Count(p => p.StartsWith("World\\Maps\\Azeroth\\", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"extracted paths: {byArchive.Values.Sum(l => l.Count)} resolved, {deleted.Count} deleted; compared {sample.Count} files, {bytes} bytes");
        output.WriteLine($"World\\Maps\\Azeroth: {azeroth} compared ({azerothTerrain} from terrain.MPQ)");
        foreach ((string archive, int count) in compared)
        {
            output.WriteLine($"  {archive}: {count} of {byArchive[archive].Count}");
        }

        Assert.Empty(mismatched);
        Assert.True(sample.Count >= 2000, $"only {sample.Count} files sampled");
        Assert.True(azerothTerrain > 0, "terrain.MPQ resolved no World\\Maps\\Azeroth file");
        Assert.All(ExtractedArchives, a => Assert.True(compared.GetValueOrDefault(a) > 0, $"nothing compared from {a}"));
    }

    [TerrainArchiveExtractFact]
    public void EveryFileOfTerrainMpq_EqualsAStormLibExtractionOfThatArchiveAlone()
    {
        // patch.MPQ shadows most of terrain.MPQ's World\Maps\Azeroth files, so the merged extraction above cannot vouch for
        // terrain.MPQ's own copies; this compares the archive itself, every listed name, against StormLib's copy of it alone.
        using MpqArchive archive = MpqArchive.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data", "terrain.MPQ"));
        string root = ClientArchiveData.TerrainExtractRoot!;
        int compared = 0, azeroth = 0;
        long bytes = 0;
        var mismatched = new List<string>();
        // StormLib also extracts the archive's own (listfile) and (attributes) entries.
        string[] names = [.. archive.ListFile.Union(new[] { "(listfile)", "(attributes)" }.Where(archive.Contains), StringComparer.OrdinalIgnoreCase)];
        foreach (string name in names)
        {
            byte[] ours = archive.Read(name);
            byte[] theirs = File.ReadAllBytes(Path.Combine(root, name.Replace('\\', Path.DirectorySeparatorChar)));
            if (!ours.AsSpan().SequenceEqual(theirs))
            {
                mismatched.Add($"{name} ({ours.Length} vs {theirs.Length} bytes)");
            }

            compared++;
            bytes += ours.Length;
            azeroth += name.StartsWith("World\\Maps\\Azeroth\\", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        int extracted = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
        output.WriteLine($"terrain.MPQ: {compared} listed files compared ({azeroth} under World\\Maps\\Azeroth), {bytes} bytes; StormLib wrote {extracted} files");
        Assert.Empty(mismatched);
        Assert.Equal(extracted, compared);
        Assert.True(azeroth > 500, $"only {azeroth} World\\Maps\\Azeroth files in terrain.MPQ");
    }

    [ClientArchiveFact]
    public void TheMountedBaseArchives_HoldDisjointNames()
    {
        // MpqFileSystem orders the base archives by the client's chain; the order only matters if two of them share a name.
        using MpqFileSystem files = MpqFileSystem.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data"));
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shared = new List<string>();
        int names = 0;
        foreach (MpqArchive archive in files.Archives.Where(a => !a.Name.StartsWith("patch", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (string name in archive.ListFile.Where(n => !n.StartsWith('(')))
            {
                names++;
                if (!owner.TryAdd(name, archive.Name))
                {
                    shared.Add($"{name}: {owner[name]} and {archive.Name}");
                }
            }
        }

        output.WriteLine($"{files.Archives.Count} archives mounted, {names} base-archive names, {shared.Count} shared; skipped: "
            + string.Join("; ", files.Skipped.Select(s => $"{Path.GetFileName(s.Path)} ({s.Reason})")));
        Assert.True(names > 60000, $"only {names} names listed");
        Assert.Empty(shared);
        Assert.Equal(13, files.Archives.Count);
        Assert.Equal(new[] { "backup.MPQ" }, files.Skipped.Select(s => Path.GetFileName(s.Path)));
        Assert.Equal("patch-2.MPQ", files.Archives[^1].Name);
        Assert.Equal("patch.MPQ", files.Archives[^2].Name);
    }

    private static Dictionary<string, string> ReadSums(string path)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // sha256sum format: 64 hex digits, a space, '*' (binary mode), the file name.
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string name = line[(space + 1)..].TrimStart('*');
            sums[name] = line[..space].ToLowerInvariant();
        }

        return sums;
    }
}

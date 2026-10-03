using System.IO.Compression;
using System.Text;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// Dump inputs: gzip detected by content (the real classic-db dump ships as .sql.gz), several
/// files sharing one CREATE TABLE registry (the real dumps use column-less INSERTs), and a
/// ledger of the statements the importer does not apply. All SQL is synthetic.
/// </summary>
public sealed class DumpSourceTests : IDisposable
{
    private const string Schema = "CREATE TABLE `widget` (\n  `id` int unsigned NOT NULL,\n  `name` varchar(20) NOT NULL,\n  PRIMARY KEY (`id`)\n) ENGINE=InnoDB;\n";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-import-" + Guid.NewGuid().ToString("N"));

    public DumpSourceTests() => Directory.CreateDirectory(_directory);

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
    public void GzipAndPlainFiles_StageIdenticalRows()
    {
        string sql = Schema + "INSERT INTO `widget` VALUES (1,'a'),(2,'b\\'c');\n";
        string plain = Write("plain.sql", Encoding.UTF8.GetBytes(sql));
        string gz = Write("packed.sql.gz", Gzip(sql));
        // Detected by the gzip magic number, not by the file name.
        string disguised = Write("disguised.sql", Gzip(sql));

        string[][] expected = [["1", "a"], ["2", "b'c"]];
        foreach (string path in new[] { plain, gz, disguised })
        {
            using TextReader reader = DumpFiles.OpenText(path);
            string[][] rows = new MySqlDumpReader(reader).Read().OfType<DumpRow>()
                .Select(r => r.Values.Select(v => v!).ToArray()).ToArray();
            Assert.Equal(expected, rows);
        }

        Assert.True(DumpFiles.Describe(gz).Gzip);
        Assert.True(DumpFiles.Describe(disguised).Gzip);
        Assert.False(DumpFiles.Describe(plain).Gzip);
    }

    [Fact]
    public void Describe_RecordsNameSizeAndSha256_WithoutTheDirectory()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Schema);
        string path = Write("sub.sql", bytes);

        SourceFileInfo info = DumpFiles.Describe(path);

        Assert.Equal("sub.sql", info.Name);
        Assert.Equal(bytes.Length, info.Bytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), info.Sha256);
    }

    [Fact]
    public void SingleReader_RejectsColumnlessInsertWhoseCreateTableIsInAnotherFile()
    {
        var second = new MySqlDumpReader(new StringReader("INSERT INTO `widget` VALUES (3,'c');"));

        Assert.Throws<FormatException>(() => second.Read().ToList());
    }

    [Fact]
    public void SharedRegistry_LetsAColumnlessInsertUseAnEarlierFilesCreateTable()
    {
        var registry = MySqlDumpReader.NewTableRegistry();
        new MySqlDumpReader(new StringReader(Schema), registry).Read().ToList();

        DumpRow row = new MySqlDumpReader(new StringReader("INSERT INTO `widget` VALUES (3,'c');"), registry)
            .Read().OfType<DumpRow>().Single();

        Assert.Equal(["id", "name"], row.Columns);
        Assert.Equal(["3", "c"], row.Values);
        // Lookups through the registry are case-insensitive like the reader's own.
        Assert.True(registry.ContainsKey("WIDGET"));
    }

    [Fact]
    public void ChainedReader_CarriesTheRegistryAcrossFilesForTheExistingImporters()
    {
        string first = "CREATE TABLE `creature_template` (`Entry` int, `Name` text, `MinLevel` int, `MaxLevel` int);\n";
        string second = "INSERT INTO `creature_template` VALUES (4001,'Chained',3,4);\n";
        var importer = new CreatureDumpImporter();

        using TextReader chained = ChainedTextReader.Create(
        [
            DumpInput.Text("one.sql", first),
            DumpInput.Text("two.sql", second),
        ]);
        importer.Read(chained);

        CreatureTemplateRow template = Assert.Single(importer.Snapshot().Templates);
        Assert.Equal((4001u, "Chained", (byte)3, (byte)4), (template.Entry, template.Name, template.MinLevel, template.MaxLevel));
    }

    [Fact]
    public void ChainedReader_KeepsAFilesLastStatementApartFromTheNextFile()
    {
        // The first file does not end in a newline; the second starts with a comment.
        using TextReader chained = ChainedTextReader.Create(
        [
            DumpInput.Text("one.sql", Schema + "INSERT INTO `widget` VALUES (1,'a');"),
            DumpInput.Text("two.sql", "-- second file\nINSERT INTO `widget` VALUES (2,'b');"),
        ]);

        DumpRow[] rows = new MySqlDumpReader(chained).Read().OfType<DumpRow>().ToArray();

        Assert.Equal(["1", "2"], rows.Select(r => r.Values[0]));
    }

    [Fact]
    public void Reader_CountsStatementsItDoesNotApply_ByVerbAndTable()
    {
        string sql = Schema
            + "UPDATE `widget` SET `name`='x' WHERE `id`=1;\n"
            + "UPDATE `widget` SET `name`='y' WHERE `id`=2;\n"
            + "DELETE FROM `widget` WHERE `id`=3;\n"
            + "ALTER TABLE `widget` ADD COLUMN `extra` int;\n"
            + "/*!40000 ALTER TABLE `widget` DISABLE KEYS */;\n"
            + "LOCK TABLES `widget` WRITE;\n"
            + "INSERT INTO `widget` VALUES (9,'z');\n";
        var reader = new MySqlDumpReader(new StringReader(sql));

        DumpRow row = reader.Read().OfType<DumpRow>().Single();

        Assert.Equal("9", row.Values[0]);
        Assert.Equal(2, reader.UnappliedStatements["UPDATE widget"]);
        Assert.Equal(1, reader.UnappliedStatements["DELETE widget"]);
        Assert.Equal(1, reader.UnappliedStatements["ALTER widget"]);
        Assert.Equal(3, reader.UnappliedStatements.Count);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }
}

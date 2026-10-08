using System.IO.Compression;
using System.Text;
using ArcaneCore.Data.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests.Items;

/// <summary>
/// <c>page_text</c> out of a MySQL world dump (column names of vmangos and cmangos classic-db: entry, text, next_page) and the catalog's load checks
/// (vmangos ObjectMgr::LoadPageTexts, ObjectMgr.cpp:6647-6687). The rows are synthetic.
/// </summary>
public sealed class PageTextDumpReaderTests
{
    public const string Dump = """
        -- MySQL dump
        DROP TABLE IF EXISTS `page_text`;
        CREATE TABLE `page_text` (
          `entry` mediumint(8) unsigned NOT NULL DEFAULT '0',
          `text` longtext NOT NULL,
          `next_page` mediumint(8) unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8;
        INSERT INTO `page_text` VALUES (1,'Chapter one.$B$BIt\'s a start.',2),(2,'Chapter two.',0),(7,'Loose leaf',99);
        CREATE TABLE `other_table` (`entry` int, `text` text);
        INSERT INTO `other_table` VALUES (3,'not a page');
        INSERT INTO `page_text` (`entry`,`text`,`next_page`) VALUES (10,'Loop A',11),(11,'Loop B',10);
        """;

    [Fact]
    public void Read_TakesOnlyPageTextRows_WithEscapesResolved()
    {
        IReadOnlyList<PageTextRecord> pages = PageTextDumpReader.Read(new StringReader(Dump));
        Assert.Equal([1u, 2u, 7u, 10u, 11u], pages.Select(p => p.Entry));
        Assert.Equal(new PageTextRecord(1, "Chapter one.$B$BIt's a start.", 2), pages[0]);
        Assert.Equal(new PageTextRecord(2, "Chapter two.", 0), pages[1]);
    }

    [Fact]
    public void Catalog_KeepsAMissingNextPage_AndCutsALoop()
    {
        var reports = new List<string>();
        var catalog = new PageTextCatalog(PageTextDumpReader.Read(new StringReader(Dump)), reports.Add);
        Assert.Equal(5, catalog.Count);
        Assert.Equal(99u, catalog.Find(7)!.NextPage);   // reported, kept: the query answers page 99 as missing
        Assert.Contains(reports, r => r.Contains("(Id: 7)") && r.Contains("(Id:99)"));
        Assert.Equal(11u, catalog.Find(10)!.NextPage);
        Assert.Equal(0u, catalog.Find(11)!.NextPage);    // the page that closes the loop 10 → 11 → 10 ends the book
        Assert.Contains(reports, r => r.Contains("circular reference"));
        Assert.Null(catalog.Find(3));
    }

    [Fact]
    public void Load_ReadsAGzipDump()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-pages-" + Guid.NewGuid().ToString("N") + ".sql.gz");
        try
        {
            using (FileStream file = File.Create(path))
            using (var zip = new GZipStream(file, CompressionLevel.Fastest))
            {
                zip.Write(Encoding.UTF8.GetBytes(Dump));
            }

            Assert.Equal(5, PageTextDumpReader.Load(path).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_RefusesANonNumericEntry()
        => Assert.Throws<InvalidDataException>(() => PageTextDumpReader.Read(new StringReader(
            "CREATE TABLE `page_text` (`entry` int, `text` text, `next_page` int);\nINSERT INTO `page_text` VALUES ('x','a',0);")));
}

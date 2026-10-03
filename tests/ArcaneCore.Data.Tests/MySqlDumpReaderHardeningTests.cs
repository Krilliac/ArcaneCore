using ArcaneCore.Data.World.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>AC-PI-002: malformed dumps must fail fast with FormatException, never spin or grow without bound.</summary>
public sealed class MySqlDumpReaderHardeningTests
{
    /// <summary>A reader that fails the test when the parser stops making progress (many Peeks, no consumption).</summary>
    private sealed class SpinGuardReader(string text, int maxStalledPeeks = 20_000) : StringReader(text)
    {
        private int _stalled;

        public override int Peek()
        {
            if (++_stalled > maxStalledPeeks)
            {
                throw new InvalidOperationException("parser is spinning: input is not being consumed");
            }

            return base.Peek();
        }

        public override int Read()
        {
            _stalled = 0;
            return base.Read();
        }
    }

    private static void Drain(MySqlDumpReader reader)
    {
        foreach (object _ in reader.Read())
        {
        }
    }

    [Theory]
    [InlineData("INSERT INTO t (")]                  // EOF inside the column list
    [InlineData("INSERT INTO t (a, ")]
    [InlineData("INSERT INTO t (a;")]                // ';'
    [InlineData("INSERT INTO t (a, ;")]
    [InlineData("INSERT INTO t (a 'x')")]            // quote
    [InlineData("INSERT INTO t (a \"x\")")]
    [InlineData("INSERT INTO t (a = b)")]            // punctuation
    [InlineData("INSERT INTO t (a, *)")]
    [InlineData("INSERT INTO t ((a))")]
    [InlineData("INSERT INTO t (a, @x)")]
    [InlineData("INSERT INTO t (`a`, ``)")]          // empty quoted identifier
    [InlineData("INSERT INTO ``")]
    public void MalformedColumnList_IsRejected_WithFormatException_AndNoSpin(string sql)
    {
        var reader = new MySqlDumpReader(new SpinGuardReader(sql));
        Assert.Throws<FormatException>(() => Drain(reader));
    }

    [Fact]
    public void ColumnList_IsBounded()
    {
        string sql = "INSERT INTO t (" + string.Join(",", Enumerable.Range(0, 100)) + ") VALUES (1)";
        var reader = new MySqlDumpReader(new StringReader(sql), new MySqlDumpLimits { MaxColumns = 50 });
        Assert.Throws<FormatException>(() => Drain(reader));
    }

    [Fact]
    public void Tuple_IsBounded()
    {
        string sql = "INSERT INTO t (a) VALUES (" + string.Join(",", Enumerable.Range(0, 100)) + ");";
        var reader = new MySqlDumpReader(new StringReader(sql), new MySqlDumpLimits { MaxColumns = 50 });
        Assert.Throws<FormatException>(() => Drain(reader));
    }

    [Fact]
    public void CreateStatement_IsBoundedByStatementCeiling()
    {
        string sql = "CREATE TABLE t (a int);" + "CREATE TABLE u (" + new string('x', 5000) + ");";
        var reader = new MySqlDumpReader(new StringReader(sql), new MySqlDumpLimits { MaxStatementChars = 1000 });
        Assert.Throws<FormatException>(() => Drain(reader));
    }

    [Fact]
    public void OversizedString_IsBoundedByTokenCeiling()
    {
        string sql = "INSERT INTO t (a) VALUES ('" + new string('x', 5000) + "');";
        var reader = new MySqlDumpReader(new StringReader(sql), new MySqlDumpLimits { MaxTokenChars = 1000 });
        Assert.Throws<FormatException>(() => Drain(reader));
    }

    [Fact]
    public void SkippedStatements_AreNotBuffered_SoALargeOneIsNotRejected()
    {
        string sql = "LOCK TABLES `t` WRITE; SELECT '" + new string('x', 5000) + "';\nINSERT INTO t (a) VALUES (1);";
        var reader = new MySqlDumpReader(new StringReader(sql), new MySqlDumpLimits { MaxStatementChars = 1000, MaxTokenChars = 100_000 });
        Assert.Single(reader.Read().OfType<DumpRow>());
    }

    [Fact]
    public void ValidDump_StillParses_WithDefaultLimits()
    {
        var reader = new MySqlDumpReader(new StringReader("CREATE TABLE `t` (`a` int, `b` text);\nINSERT INTO `t` (`a`,`b`) VALUES (1,'x'),(2,NULL);"));
        List<DumpRow> rows = [.. reader.Read().OfType<DumpRow>()];
        Assert.Equal(2, rows.Count);
        Assert.Equal("x", rows[0].Values[1]);
        Assert.Null(rows[1].Values[1]);
    }
}

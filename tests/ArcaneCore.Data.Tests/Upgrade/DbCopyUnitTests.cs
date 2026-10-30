using ArcaneCore.Data.Schema.Upgrade.Copy;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>The pure parts of <c>arcane-db copy</c>: value conversion, catalog type mapping and foreign-key ordering.</summary>
public sealed class DbCopyUnitTests
{
    [Fact]
    public void Convert_TextDate_BecomesAUtcTimestamp_TruncatedToMicroseconds()
    {
        var tz = (DateTime)CopyValues.Convert("2026-10-30 12:34:56.1234567", CopyKind.TimestampTz);
        var naive = (DateTime)CopyValues.Convert("2026-10-30 12:34:56.1234567+02:00", CopyKind.Timestamp);

        Assert.Equal(DateTimeKind.Utc, tz.Kind);
        Assert.Equal(new DateTime(2026, 10, 30, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560), tz);
        Assert.Equal(DateTimeKind.Unspecified, naive.Kind);
        Assert.Equal(new DateTime(2026, 10, 30, 10, 34, 56).AddTicks(1234560), naive);
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    public void Convert_IntegersAndText_BecomeBooleans(object value, bool expected)
        => Assert.Equal(expected, CopyValues.Convert(value, CopyKind.Boolean));

    [Fact]
    public void Convert_Decimals_Guids_Blobs_AndNulls()
    {
        Assert.Equal(12.50m, CopyValues.Convert("12.50", CopyKind.Decimal));
        Assert.Equal(new Guid("01020304-0506-0708-090a-0b0c0d0e0f10"), CopyValues.Convert("01020304-0506-0708-090A-0B0C0D0E0F10", CopyKind.Guid));
        byte[] blob = [1, 2, 3];
        Assert.Same(blob, CopyValues.Convert(blob, CopyKind.Binary));
        Assert.Equal(DBNull.Value, CopyValues.Convert(null, CopyKind.TimestampTz));
        Assert.Equal(DBNull.Value, CopyValues.Convert(DBNull.Value, CopyKind.Integer));
        Assert.Equal(7L, CopyValues.Convert("7", CopyKind.Integer));
        Assert.Equal(1L, CopyValues.Convert(true, CopyKind.Native));
        Assert.Equal("2026-10-30 12:00:00", CopyValues.Convert(new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), CopyKind.Native));
    }

    [Fact]
    public void Convert_IsIdempotent_SoTheTargetReadBackNormalisesLikeTheSource()
    {
        foreach ((object value, CopyKind kind) in new (object, CopyKind)[]
        {
            (true, CopyKind.Boolean), (5L, CopyKind.Integer), (1.25m, CopyKind.Decimal), (2.5, CopyKind.Double), ("abc", CopyKind.Text),
            (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CopyKind.TimestampTz), (TimeSpan.FromSeconds(3), CopyKind.Time),
        })
        {
            object once = CopyValues.Convert(value, kind);
            Assert.Equal(once, CopyValues.Convert(once, kind));
        }
    }

    [Fact]
    public void Convert_AValueThatCannotFit_ThrowsFormatException()
        => Assert.Throws<FormatException>(() => CopyValues.Convert("not a date", CopyKind.TimestampTz));

    [Theory]
    [InlineData("boolean", null, "Boolean")]
    [InlineData("tinyint", "tinyint(1)", "Boolean")]
    [InlineData("tinyint", "tinyint unsigned", "Integer")]
    [InlineData("bigint", null, "Integer")]
    [InlineData("timestamp with time zone", null, "TimestampTz")]
    [InlineData("datetime", "datetime(6)", "Timestamp")]
    [InlineData("bytea", null, "Binary")]
    [InlineData("longblob", null, "Binary")]
    [InlineData("uuid", null, "Guid")]
    [InlineData("numeric", null, "Decimal")]
    [InlineData("character varying", null, "Text")]
    [InlineData("geometry", null, "Native")]
    public void KindOfServerType_MapsTheCatalogType(string dataType, string? columnType, string expected)
        => Assert.Equal(Enum.Parse<CopyKind>(expected), CopyValues.KindOfServerType(dataType, columnType));

    [Fact]
    public void Unsigned64_IsLiftedFromEachEnginesStorage_AndWrittenToSqliteAsTheSameBits()
    {
        ulong guid = 0xF130000000000001UL;
        long asSqlite = unchecked((long)guid);

        Assert.True(asSqlite < 0);
        Assert.Equal(guid, CopyValues.Lift(asSqlite, typeof(ulong)));
        Assert.Equal(guid, CopyValues.Lift((decimal)guid, typeof(ulong)));
        Assert.Equal(guid, CopyValues.Lift(guid, typeof(ulong)));
        Assert.Equal(ulong.MaxValue, CopyValues.Lift(-1L, typeof(ulong)));
        Assert.Equal(5UL, CopyValues.Lift(5L, typeof(ulong)));
        Assert.Equal((decimal)guid, CopyValues.Convert(CopyValues.Lift(asSqlite, typeof(ulong)), CopyKind.Decimal));
        Assert.Equal(asSqlite, CopyValues.Convert(guid, CopyKind.Native));
        Assert.Equal(-1L, CopyValues.Convert(ulong.MaxValue, CopyKind.Native));
        Assert.Equal(-5L, CopyValues.Lift(-5L, typeof(long)));
        Assert.Equal(DBNull.Value, CopyValues.Lift(null, typeof(ulong)));
    }

    private static CopyTable T(string name, params string[] parents) => new(name, ["id"], ["id"], parents);

    [Fact]
    public void Sort_PutsParentsFirst_DeterministicallyByName()
    {
        IReadOnlyList<CopyTable> sorted = CopyTableOrder.Sort([T("c", "b"), T("b", "a"), T("a"), T("z"), T("d", "missing")]);

        Assert.Equal(["a", "b", "c", "d", "z"], sorted.Select(t => t.Name));
    }

    [Fact]
    public void Sort_ACycle_ThrowsNamingIt()
    {
        CopyCycleException ex = Assert.Throws<CopyCycleException>(() => CopyTableOrder.Sort([T("a", "b"), T("b", "c"), T("c", "a"), T("free")]));

        Assert.Contains("a -> b -> c -> a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sort_ASelfReference_IsACycle()
        => Assert.Throws<CopyCycleException>(() => CopyTableOrder.Sort([T("tree", "tree")]));
}

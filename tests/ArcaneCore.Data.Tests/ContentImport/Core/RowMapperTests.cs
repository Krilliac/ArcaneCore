using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// The name-based row mapper: source columns are matched to row properties ignoring case and
/// underscores (so <c>stat_type1</c>, <c>StatType1</c> and <c>stattype1</c> are one name), with
/// explicit aliases for the few names that really differ, and typed, range-checked conversion.
/// </summary>
public sealed class RowMapperTests
{
    private sealed class Sample
    {
        public uint Entry { get; set; }
        public string Name { get; set; } = string.Empty;
        public byte Level { get; set; }
        public int Signed { get; set; }
        public uint Unsigned { get; set; }
        public float Ratio { get; set; }
        public bool Flag { get; set; }
        public ulong Big { get; set; }
        public uint Amount { get; set; } = 1;
        public short Small { get; set; }
        public string Renamed { get; set; } = string.Empty;
        public IReadOnlyList<string> Ignored { get; } = [];
    }

    private static readonly RowMapper<Sample> s_mapper = new(new Dictionary<string, string> { ["old_name"] = nameof(Sample.Renamed) });

    [Fact]
    public void ColumnsMatch_IgnoringCaseAndUnderscores()
    {
        Sample s = Map(["ENTRY", "na_me", "LEVEL", "sig_ned", "UN_signed", "rat_io"], ["5", "Wolf", "9", "-3", "70000", "1.25"]);

        Assert.Equal((5u, "Wolf", (byte)9, -3, 70000u, 1.25f), (s.Entry, s.Name, s.Level, s.Signed, s.Unsigned, s.Ratio));
    }

    [Fact]
    public void Aliases_MapRenamedColumns()
    {
        Assert.Equal("x", Map(["Old_Name"], ["x"]).Renamed);
        Assert.True(s_mapper.Maps("OLD_NAME"));
    }

    [Fact]
    public void AbsentColumns_KeepThePropertyDefault_AndNullBecomesTheDefault()
    {
        Sample s = Map(["entry", "name", "ratio"], ["5", null, null]);

        Assert.Equal(1u, s.Amount);
        Assert.Equal(string.Empty, s.Name);
        Assert.Equal(0f, s.Ratio);
    }

    [Fact]
    public void Maps_ReportsWhichColumnsAreRead()
    {
        Assert.True(s_mapper.Maps("Level"));
        Assert.True(s_mapper.Maps("lev_el"));
        Assert.False(s_mapper.Maps("ScriptName"));
        Assert.False(s_mapper.Maps("Ignored"));
    }

    [Fact]
    public void BooleansAndLargeIntegers_Convert()
    {
        Sample s = Map(["flag", "big"], ["1", "18446744073709551615"]);

        Assert.True(s.Flag);
        Assert.Equal(ulong.MaxValue, s.Big);
        Assert.False(Map(["flag"], ["0"]).Flag);
    }

    [Fact]
    public void IntegersWrittenAsDecimals_Convert_AndOutOfRangeValuesClampWithADiagnostic()
    {
        var diagnostics = new MapDiagnostics();

        Sample s = Map(["level", "unsigned", "small"], ["300", "-5", "40000"], diagnostics);
        Sample d = Map(["level"], ["7.0"], diagnostics);

        Assert.Equal((byte.MaxValue, 0u, short.MaxValue), (s.Level, s.Unsigned, s.Small));
        Assert.Equal((byte)7, d.Level);
        Assert.Equal(3, diagnostics.Clamped);
        Assert.Contains(diagnostics.Samples, m => m.Contains("level", StringComparison.OrdinalIgnoreCase) && m.Contains("300", StringComparison.Ordinal));
    }

    [Fact]
    public void SignedAsUnsigned_WrapsNegativeValuesInsteadOfClamping_OnlyForTheNamedProperties()
    {
        var mapper = new RowMapper<Sample>(signedAsUnsigned: [nameof(Sample.Unsigned)]);
        var diagnostics = new MapDiagnostics();

        Sample s = mapper.Map(new DumpRow("sample_table", ["unsigned", "level"], ["-1", "-1"]), diagnostics);

        Assert.Equal(uint.MaxValue, s.Unsigned);   // the bits of an int32 -1, as a uint32 server reads it
        Assert.Equal((byte)0, s.Level);            // not named: still clamps
        Assert.Equal(1, diagnostics.Clamped);
        Assert.Equal(0x80000000u, mapper.Map(new DumpRow("t", ["unsigned"], ["-2147483648"])).Unsigned);
        Assert.Equal(0u, mapper.Map(new DumpRow("t", ["unsigned"], ["-2147483649"]), diagnostics).Unsigned); // beyond int32: clamps
        Assert.Throws<ArgumentException>(() => new RowMapper<Sample>(signedAsUnsigned: [nameof(Sample.Level)]));
    }

    [Fact]
    public void ClampDiagnostics_AreSummarisedPerColumn_WithTheCountAndTheFirstValue()
    {
        var diagnostics = new MapDiagnostics();
        for (int i = 0; i < 30; i++)
        {
            Map(["level"], [(300 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)], diagnostics);
        }

        string line = Assert.Single(diagnostics.Samples);
        Assert.Contains("sample_table.level", line, StringComparison.Ordinal);
        Assert.Contains("30 value(s)", line, StringComparison.Ordinal);
        Assert.Contains("first: 300", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableValue_IsASchemaError_NamingTableAndColumn()
    {
        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => Map(["level"], ["abc"]));

        Assert.Equal("sample_table", ex.Table);
        Assert.Equal("level", ex.Column);
        Assert.Contains("abc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedRowsWithTheSameColumnList_AreMapped_AndALaterListIsReplanned()
    {
        IReadOnlyList<string> columns = ["entry", "name"];
        Sample a = s_mapper.Map(new DumpRow("sample_table", columns, ["1", "A"]));
        Sample b = s_mapper.Map(new DumpRow("sample_table", columns, ["2", "B"]));
        Sample c = s_mapper.Map(new DumpRow("sample_table", ["name", "entry"], ["C", "3"]));

        Assert.Equal(("A", 1u, "B", 2u, "C", 3u), (a.Name, a.Entry, b.Name, b.Entry, c.Name, c.Entry));
    }

    private static Sample Map(string[] columns, string?[] values, MapDiagnostics? diagnostics = null)
        => s_mapper.Map(new DumpRow("sample_table", columns, values), diagnostics);
}

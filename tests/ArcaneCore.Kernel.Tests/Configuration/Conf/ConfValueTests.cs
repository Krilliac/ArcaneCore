using System.Globalization;
using ArcaneCore.Kernel.Configuration.Conf;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

public sealed class ConfValueTests
{
    [Fact]
    public void Validate_UsesInvariantCulture_UnderAGermanCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // InvariantGlobalization is on, so a named culture carries no data: build the German separators by hand.
            var german = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            german.NumberFormat.NumberDecimalSeparator = ",";
            german.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = german;
            Assert.Equal(1.5f, float.Parse("1,5", NumberStyles.Float, CultureInfo.CurrentCulture)); // control: the current culture really reads a comma as the decimal point
            Assert.Null(ConfValue.Validate("1.5", "float", null));
            Assert.NotNull(ConfValue.Validate("1,5", "float", null));
            Assert.Null(ConfValue.Validate("0.25", "double", null));
            Assert.Null(ConfValue.Validate("00:00:10", "TimeSpan", null));
            Assert.Null(ConfValue.Validate("-5", "int", null));
            Assert.NotNull(ConfValue.Validate("1.000", "int", null));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Validate_MangosStyleBoolean1_IsATypeErrorNamingTrueAndFalse()
    {
        string? error = ConfValue.Validate("1", "bool", null);
        Assert.NotNull(error);
        Assert.Contains("true", error, StringComparison.Ordinal);
        Assert.Contains("false", error, StringComparison.Ordinal);
        Assert.Null(ConfValue.Validate("True", "bool", null));
        Assert.Null(ConfValue.Validate("false", "bool?", null));
    }

    [Fact]
    public void Validate_RangesEnumsAndEmptyValues()
    {
        Assert.NotNull(ConfValue.Validate("300", "byte", null));
        Assert.Null(ConfValue.Validate("0x1F", "int", null));
        Assert.Null(ConfValue.Validate("fast", "FixtureMode", ConfFixtureCatalog.ModeValues));
        Assert.Null(ConfValue.Validate("1", "FixtureMode", ConfFixtureCatalog.ModeValues));
        Assert.Contains("Off | Fast", ConfValue.Validate("Slow", "FixtureMode", ConfFixtureCatalog.ModeValues), StringComparison.Ordinal);
        Assert.Null(ConfValue.Validate(string.Empty, "int", null));
        Assert.NotNull(ConfValue.Validate("abc", "TimeSpan", null));
    }

    [Fact]
    public void Equivalent_NormalisesBooleanCaseAndNumericForms()
    {
        Assert.True(ConfValue.Equivalent("True", "true", "bool"));
        Assert.True(ConfValue.Equivalent("1.0", "1", "double"));
        Assert.True(ConfValue.Equivalent("0.10", "0.1", "float"));
        Assert.True(ConfValue.Equivalent("10", "10.0", "int"));
        Assert.True(ConfValue.Equivalent("0:00:10", "00:00:10", "TimeSpan"));
        Assert.True(ConfValue.Equivalent("fast", "Fast", "FixtureMode", ConfFixtureCatalog.ModeValues));
        Assert.False(ConfValue.Equivalent("1", "2", "int"));
        Assert.False(ConfValue.Equivalent("abc", "ABC", "string"));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("C:\\dir\\")]
    [InlineData("say \"hi\"")]
    [InlineData("two \\\\ slashes \\\"")]
    [InlineData("  leading and trailing  ")]
    [InlineData("a # b ; c = d")]
    [InlineData("ñ ü € 😀")]
    public void Quote_ThenParse_IsIdentity_ForTrickyStrings(string value)
    {
        ConfDocument document = ConfDocument.ParseText("K = " + ConfValue.Quote(value) + "\n", "x.conf");
        Assert.Equal(value, document.Entries[0].Value);
    }

    [Fact]
    public void Quote_RefusesALineBreak()
        => Assert.Throws<FormatException>(() => ConfValue.Quote("a\nb"));
}

using ArcaneCore.Data.Items;
using ArcaneCore.Data.Npc;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// Characterises how two readers parse and refuse dump columns today, through their public <c>Read</c> seams: the
/// <c>NumberStyles</c> each accepts (<c>conditions</c>: digits only for unsigned, a sign allowed for the signed
/// <c>type</c>; <c>item_enchantment_template</c>: <c>NumberStyles.Integer</c>, so surrounding whitespace and a plus sign
/// are accepted), and the exact exception type and message of a bad, empty or NULL value. Pins what a shared column
/// reader must keep per importer. Dump rows are synthetic; quoted values reach the parser unchanged.
/// </summary>
public sealed class DumpColumnParsingCharacterisationTests
{
    private const string ConditionsColumns = "INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES ";
    private const string EnchantColumns = "CREATE TABLE `item_enchantment_template` (`entry` int, `ench` int, `chance` float, `patch_min` tinyint, `patch_max` tinyint);\nINSERT INTO `item_enchantment_template` VALUES ";

    private static ConditionsDumpImporter ReadConditions(string tuple)
    {
        var importer = new ConditionsDumpImporter();
        importer.Read(new StringReader(ConditionsColumns + tuple + ";"));
        return importer;
    }

    private static IReadOnlyList<ItemEnchantmentChance> ReadEnchants(string tuple)
        => ItemEnchantmentTemplateDumpReader.Read(new StringReader(EnchantColumns + tuple + ";"));

    [Theory]
    [InlineData("(1,14,'-1',0,0,0,0)", "conditions.value1 \"-1\" is not an unsigned integer")]
    [InlineData("(1,14,' 7',0,0,0,0)", "conditions.value1 \" 7\" is not an unsigned integer")]
    [InlineData("(1,14,'+7',0,0,0,0)", "conditions.value1 \"+7\" is not an unsigned integer")]
    [InlineData("(1,14,0,'x',0,0,0)", "conditions.value2 \"x\" is not an unsigned integer")]
    [InlineData("(1,14,0,0,'4294967296',0,0)", "conditions.value3 \"4294967296\" is not an unsigned integer")]
    [InlineData("('1.0',14,0,0,0,0,0)", "conditions.condition_entry \"1.0\" is not an unsigned integer")]
    [InlineData("(1,'x',0,0,0,0,0)", "conditions.type \"x\" is not an integer")]
    [InlineData("(1,'2147483648',0,0,0,0,0)", "conditions.type \"2147483648\" is not an integer")]
    [InlineData("(1,' 14',0,0,0,0,0)", "conditions.type \" 14\" is not an integer")]
    public void Conditions_RefusesABadColumn_WithInvalidDataExceptionAndTheExactWording(string tuple, string message)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadConditions(tuple));
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("(1,14,'',0,0,0,0)", "value1")]
    [InlineData("(1,14,'  ',0,0,0,0)", "value1")]
    [InlineData("(1,14,0,0,0,NULL,0)", "value4")]
    [InlineData("(NULL,14,0,0,0,0,0)", "condition_entry")]
    [InlineData("(1,NULL,0,0,0,0,0)", "type")]
    [InlineData("(1,14,0,0,0,0,'')", "flags")]
    public void Conditions_TreatsAnEmptyBlankOrNullColumn_AsMissing(string tuple, string column)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadConditions(tuple));
        Assert.Equal($"conditions row has no {column} value", error.Message);
    }

    [Fact]
    public void Conditions_FlagsAboveAByte_OverflowRatherThanFailAsInvalidData()
        => Assert.Throws<OverflowException>(() => ReadConditions("(1,14,0,0,0,0,256)"));

    [Fact]
    public void Conditions_AcceptsASignedType_AndTheUnsignedBoundaries()
    {
        ConditionsDumpImporter importer = ReadConditions("(4294967295,-5,4294967295,0,0,0,255)");
        ConditionRow row = Assert.Single(importer.Rows);
        Assert.Equal(uint.MaxValue, row.ConditionEntry);
        Assert.Equal(-5, row.Type);
        Assert.Equal(uint.MaxValue, row.Value1);
        Assert.Equal((byte)255, row.Flags);
    }

    [Fact]
    public void Conditions_AMissingValueColumn_IsTheVmangosRefusal_NotAParseError()
    {
        var importer = new ConditionsDumpImporter();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => importer.Read(new StringReader(
            "INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`flags`) VALUES (1,14,0,0,0);")));
        Assert.StartsWith("the conditions rows have no value3/value4 columns: this is the vmangos layout", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(' 6 ',' +1001 ',25)", 6u, 1001u, 25f)]
    [InlineData("(6,1001,' 0.5 ')", 6u, 1001u, 0.5f)]
    [InlineData("(6,1001,'1e1')", 6u, 1001u, 10f)]
    [InlineData("(4294967295,0,'-0')", 4294967295u, 0u, -0f)]
    public void Enchantments_AcceptWhitespaceAndAPlusSign_UnderNumberStylesInteger(string tuple, uint entry, uint ench, float chance)
    {
        ItemEnchantmentChance row = Assert.Single(ReadEnchants(tuple.Replace(")", ",0,10)", StringComparison.Ordinal)));
        Assert.Equal(new ItemEnchantmentChance(entry, ench, chance), row);
    }

    [Theory]
    [InlineData("('-1',1001,25,0,10)", "item_enchantment_template: entry '-1' is not a number")]
    [InlineData("(6,'x',25,0,10)", "item_enchantment_template: ench 'x' is not a number")]
    [InlineData("(6,'4294967296',25,0,10)", "item_enchantment_template: ench '4294967296' is not a number")]
    [InlineData("(6,1001,'x',0,10)", "item_enchantment_template: chance 'x' is not a number")]
    [InlineData("(6,1001,'',0,10)", "item_enchantment_template: chance '' is not a number")]
    [InlineData("(6,1001,25,'x',10)", "item_enchantment_template: patch_min 'x' is not a number")]
    [InlineData("(6,1001,25,0,'1.5')", "item_enchantment_template: patch_max '1.5' is not a number")]
    public void Enchantments_RefuseABadColumn_WithInvalidDataExceptionAndTheExactWording(string tuple, string message)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadEnchants(tuple));
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("(NULL,1001,25,0,10)", "entry")]
    [InlineData("(6,NULL,25,0,10)", "ench")]
    [InlineData("(6,1001,NULL,0,10)", "chance")]
    public void Enchantments_ANullRequiredColumn_IsAMissingRow(string tuple, string column)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadEnchants(tuple));
        Assert.Equal($"item_enchantment_template: a row has no {column}", error.Message);
    }

    [Fact]
    public void Enchantments_AMissingRequiredColumn_IsAMissingRow()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ItemEnchantmentTemplateDumpReader.Read(new StringReader(
            "INSERT INTO `item_enchantment_template` (`entry`,`ench`) VALUES (6,1001);")));
        Assert.Equal("item_enchantment_template: a row has no chance", error.Message);
    }

    [Theory]
    [InlineData("(6,1001,25,10,10)", 1)]
    [InlineData("(6,1001,25,11,12)", 0)]
    [InlineData("(6,1001,25,0,9)", 0)]
    [InlineData("(6,1001,25,' 11 ',12)", 0)]
    [InlineData("(6,1001,25,NULL,NULL)", 1)]
    public void Enchantments_PatchRangeBoundariesAroundPatchTen(string tuple, int kept)
        => Assert.Equal(kept, ReadEnchants(tuple).Count);
}

using System.Buffers.Binary;
using ArcaneCore.Data;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests.ClientData;

public sealed class DbcDumpSignedTests
{
    [Fact]
    public async Task GeneratedSignednessAndDbcDumpPreserveNegativeCompactIntegers()
    {
        DbdLayout map = ClientDbcDbdLayouts.All["Map.dbc"];
        DbdField signed = map.Columns.Single(c => c.Name == "ParentMapID");
        DbdField unsigned = map.Columns.Single(c => c.Name == "Unk1");
        Assert.Equal(32, signed.WidthBits);
        Assert.True(signed.IsSigned);
        Assert.Equal(32, unsigned.WidthBits);
        Assert.False(unsigned.IsSigned);

        using var dir = new TempDirectory();
        byte[] image = new byte[20 + map.RecordSize + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)map.Fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)map.RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + unsigned.Offset), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + signed.Offset), uint.MaxValue);
        File.WriteAllBytes(Path.Combine(dir.Path, "Map.dbc"), image);

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await DbUpgradeCli.RunAsync(["dbc", "dump", "Map.dbc"],
            new DatabaseOptions(), output, error, dir.Path, CancellationToken.None);
        Assert.Equal(DbUpgradeExitCodes.Ok, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("ParentMapID=-1", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("Unk1=4294967295", output.ToString().Split(',').Single(part => part.Contains("Unk1=", StringComparison.Ordinal)).Trim());
    }
}

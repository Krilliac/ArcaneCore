using ArcaneCore.Data.ClientData;
using Xunit;

namespace ArcaneCore.Data.Tests.ClientData;

public sealed class DbcDumpTests
{
    [Fact]
    public async Task DumpPrintsGeneratedFieldNamesAndFiltersById()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "arcane-dbc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "BankBagSlotPrices.dbc");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WDBC"));
                writer.Write(2u); writer.Write(2u); writer.Write(8u); writer.Write(1u);
                writer.Write(1u); writer.Write(100u);
                writer.Write(2u); writer.Write(200u);
                writer.Write((byte)0);
            }
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await DbcDump.RunAsync([path, "--id", "2", "--json"], null, output, error));
            Assert.Contains("\"Cost\":200", output.ToString());
            Assert.DoesNotContain("\"Cost\":100", output.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

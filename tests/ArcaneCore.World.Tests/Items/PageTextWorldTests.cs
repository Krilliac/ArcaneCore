using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.World.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// CMSG_PAGE_TEXT_QUERY over loopback (vmangos WorldSession::HandlePageTextQueryOpcode, QueryHandler.cpp:263-299): one
/// SMSG_PAGE_TEXT_QUERY_RESPONSE per page along the next-page chain, "Item page missing." for a page that does not exist, pages read from the
/// configured <c>PageText:DumpPath</c>.
/// </summary>
public sealed class PageTextWorldTests
{
    private static (uint Page, string Text, uint Next) Decode(byte[] payload)
    {
        uint page = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        int end = Array.IndexOf(payload, (byte)0, 4);
        return (page, Encoding.UTF8.GetString(payload, 4, end - 4), BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(end + 1)));
    }

    private static byte[] Query(uint page, bool withGuid)
    {
        byte[] payload = new byte[withGuid ? 12 : 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, page);
        return payload;
    }

    [Fact]
    public async Task APageQuery_AnswersEveryPageOfTheBook_AndAMissingPage()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-pages-" + Guid.NewGuid().ToString("N") + ".sql");
        await File.WriteAllTextAsync(path, """
            CREATE TABLE `page_text` (`entry` int, `text` longtext, `next_page` int);
            INSERT INTO `page_text` VALUES (1,'First page',2),(2,'Second page',0);
            """);
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PageText:DumpPath"] = path,
                }).Build()));
            Assert.Equal(2, host.WorldServices.GetRequiredService<PageTextFeature>().Pages.Count);
            await using WorldTestClient client = await host.EnterWorldAsync("PAGES", "Bookworm");

            await client.SendAsync(WorldOpcode.CmsgPageTextQuery, Query(1, withGuid: true));
            Assert.Equal((1u, "First page", 2u), Decode(await client.ReadUntilAsync(WorldOpcode.SmsgPageTextQueryResponse)));
            Assert.Equal((2u, "Second page", 0u), Decode(await client.ReadUntilAsync(WorldOpcode.SmsgPageTextQueryResponse)));

            await client.SendAsync(WorldOpcode.CmsgPageTextQuery, Query(77, withGuid: false));
            Assert.Equal((77u, "Item page missing.", 0u), Decode(await client.ReadUntilAsync(WorldOpcode.SmsgPageTextQueryResponse)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WithoutADump_EveryPageIsMissing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PAGES2", "Nobook");
        await client.SendAsync(WorldOpcode.CmsgPageTextQuery, Query(1, withGuid: false));
        Assert.Equal((1u, "Item page missing.", 0u), Decode(await client.ReadUntilAsync(WorldOpcode.SmsgPageTextQueryResponse)));
    }
}

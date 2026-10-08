using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// /who search strings also match the zone's name (vmangos WhoListClientQueryTask, MiscHandler.cpp:170-196: the area name of
/// the player's zone through Utf8FitTo, case-insensitively), and a zone filter on one's own battleground zone lists only the
/// players of one's own instance (MiscHandler.cpp:160-176, client patch 1.7.0).
/// </summary>
public sealed class WhoAreaTests
{
    private const uint Elwynn = 12;
    private const uint Westfall = 40;
    private const uint WarsongGulch = 3277;

    private static Task LoadAreasAsync(WorldTestHost host) => host.OnWorldAsync(() => WorldMaps.Of(host.World).Load(new MapContent(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", string.Empty),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", string.Empty),
        ],
        [
            new AreaTemplate(Elwynn, 0, 0, 12, 0, 1, "Elwynn Forest", 0, 0),
            new AreaTemplate(Westfall, 0, 0, 40, 0, 10, "Westfall", 0, 0),
        ],
        [], [], [])));

    [Fact]
    public async Task ASearchString_MatchesTheNameOfTheZoneAPlayerIsIn()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient asker = await host.EnterWorldAsync("ASKER", "Asker");
        await using WorldTestClient farmer = await host.EnterWorldAsync("FARMER", "Farmer");
        await LoadAreasAsync(host);
        await host.OnWorldAsync(() =>
        {
            host.World.FindOnlinePlayer("Asker")!.ZoneId = Elwynn;
            host.World.FindOnlinePlayer("Farmer")!.ZoneId = Westfall;
        });
        await asker.CollectAsync();

        Assert.Equal(["Farmer"], await WhoAsync(asker, strings: ["westf"]));
        Assert.Equal(["Asker"], await WhoAsync(asker, strings: ["FOREST"]));
        Assert.Equal(["Asker", "Farmer"], await WhoAsync(asker, strings: ["elwynn", "west"]));
        Assert.Empty(await WhoAsync(asker, strings: ["durotar"]));
    }

    [Fact]
    public async Task AZoneFilterOnOnesOwnBattlegroundZone_ListsOnlyOnesOwnInstance()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient asker = await host.EnterWorldAsync("ASKER", "Asker");
        await using WorldTestClient mate = await host.EnterWorldAsync("MATE", "Mate");
        await LoadAreasAsync(host);
        // Both report the Warsong Gulch zone; they stand on the same continent map, which plays the asker's instance.
        await host.OnWorldAsync(() =>
        {
            host.World.FindOnlinePlayer("Asker")!.ZoneId = WarsongGulch;
            host.World.FindOnlinePlayer("Mate")!.ZoneId = WarsongGulch;
        });
        await asker.CollectAsync();
        Assert.Equal(["Asker", "Mate"], await WhoAsync(asker, zones: [WarsongGulch]));

        // Outside a battleground zone the filter is the plain zone match.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Asker")!.ZoneId = Elwynn);
        Assert.Equal(["Mate"], await WhoAsync(asker, zones: [WarsongGulch]));
    }

    private static async Task<List<string>> WhoAsync(WorldTestClient client, uint[]? zones = null, string[]? strings = null)
    {
        var who = new PacketWriter(64);
        who.WriteUInt32(0);
        who.WriteUInt32(100);
        who.WriteCString(string.Empty);
        who.WriteCString(string.Empty);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32((uint)(zones?.Length ?? 0));
        foreach (uint zone in zones ?? [])
        {
            who.WriteUInt32(zone);
        }

        who.WriteUInt32((uint)(strings?.Length ?? 0));
        foreach (string term in strings ?? [])
        {
            who.WriteCString(term);
        }

        await client.SendAsync(WorldOpcode.CmsgWho, who.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgWho));
        uint listed = reader.ReadUInt32();
        reader.ReadUInt32(); // online count
        var names = new List<string>();
        for (int i = 0; i < listed; i++)
        {
            names.Add(reader.ReadCString());
            reader.ReadCString(); // guild
            reader.ReadUInt32();  // level
            reader.ReadUInt32();  // class
            reader.ReadUInt32();  // race
            reader.ReadUInt32();  // zone
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}

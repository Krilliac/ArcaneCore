using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Weather in the running world: zone entry, <c>.wchange</c>, and the disabled switch (vmangos Player.cpp:6594-6604, ServerCommands.cpp:98-130).</summary>
public sealed class WeatherWorldTests
{
    private sealed class ByNameLocator : IZoneLocator
    {
        public Dictionary<string, uint> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (Zones.GetValueOrDefault(player.Name, 12u), 1);

        public AreaTemplate? Find(uint areaId) => new AreaTemplate(areaId, 0, 0, areaId, 0, 1, "z", 0, 0);
    }

    private static async Task<string> CommandReplyAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    [Fact]
    public async Task ZoneEntry_SendsTheWorldStatesThenTheWeather()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks hooks0 = WorldStateHooks.For(host.World);
        hooks0.Locator = new ByNameLocator();
        hooks0.WeatherSettings.Enabled = true;
        await using WorldTestClient client = await host.EnterWorldAsync("RAINY", "Rainy");

        // The login sequence stops at the world states; the weather follows right after them.
        Assert.NotEmpty(client.LoginPacket(WorldOpcode.SmsgInitWorldStates));
        byte[] weather = await client.ReadUntilAsync(WorldOpcode.SmsgWeather);
        Assert.Equal(new byte[13], weather); // fine, grade 0, no sound, smooth
    }

    [Fact]
    public async Task Wchange_SetsTheGmsZone_AndNotAPlayerOneZoneOver()
    {
        await using var host = WorldTestHost.Start();
        var locator = new ByNameLocator();
        locator.Zones["Gm"] = 12;
        locator.Zones["Far"] = 40;
        WorldStateHooks hooks1 = WorldStateHooks.For(host.World);
        hooks1.Locator = locator;
        hooks1.WeatherSettings.Enabled = true;
        await using WorldTestClient gm = await host.EnterWorldAsync("GMACC", "Gm", AccountSecurity.Administrator);
        await using WorldTestClient far = await host.EnterWorldAsync("FARACC", "Far");
        await gm.ReadUntilAsync(WorldOpcode.SmsgWeather); // login weather
        await far.ReadUntilAsync(WorldOpcode.SmsgWeather);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".wchange 1 0.5");

        Assert.Equal([1, 0, 0, 0, 0, 0, 0, 0x3F, 0x55, 0x21, 0, 0, 0], await gm.ReadUntilAsync(WorldOpcode.SmsgWeather));
        Assert.DoesNotContain(await far.CollectAsync(TimeSpan.FromMilliseconds(300)), p => p.Opcode == WorldOpcode.SmsgWeather);
        Assert.Equal(WeatherType.Rain, await host.OnWorldAsync(() => host.World.GetMap(0).FindUpdater<MapWeather>()!.Find(12)!.Type));
    }

    [Fact]
    public async Task Wchange_RefusesWhenWeatherIsDisabled_AndTheDisabledWorldSendsNoWeatherOnEntry()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks hooks = WorldStateHooks.For(host.World);
        hooks.Locator = new ByNameLocator();
        hooks.WeatherSettings.Enabled = false;
        await using WorldTestClient gm = await host.EnterWorldAsync("GMOFF", "Gmoff", AccountSecurity.Administrator);

        Assert.Equal(WeatherCommands.WeatherDisabledText, await CommandReplyAsync(gm, ".wchange 1 0.5"));
    }

    [Fact]
    public async Task Wchange_ValidatesItsArguments_AndNeedsAnAdministrator()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks hooks0 = WorldStateHooks.For(host.World);
        hooks0.Locator = new ByNameLocator();
        hooks0.WeatherSettings.Enabled = true;
        await using WorldTestClient gm = await host.EnterWorldAsync("GMARG", "Gmarg", AccountSecurity.Administrator);
        await gm.ReadUntilAsync(WorldOpcode.SmsgWeather);

        Assert.Contains("Syntax", await CommandReplyAsync(gm, ".wchange 9 0.5"), StringComparison.Ordinal); // not a weather type
        Assert.Contains("Syntax", await CommandReplyAsync(gm, ".wchange 1"), StringComparison.Ordinal);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".wchange 2 7"); // grade clamps to 1 -> normalized 0.9999
        byte[] packet = await gm.ReadUntilAsync(WorldOpcode.SmsgWeather);
        Assert.Equal(2u, BitConverter.ToUInt32(packet, 0));
        Assert.Equal(0.9999f, BitConverter.ToSingle(packet, 4));
        Assert.Equal(8538u, BitConverter.ToUInt32(packet, 8)); // heavy snow sound

        await using WorldTestClient player = await host.EnterWorldAsync("PLAYACC", "Playa");
        await player.ReadUntilAsync(WorldOpcode.SmsgWeather);
        Assert.DoesNotContain("Weather", await CommandReplyAsync(player, ".wchange 1 0.5"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceChances_SwapsTheTable_WithoutLosingLiveZoneState()
    {
        await using var host = WorldTestHost.Start();
        WorldStateHooks hooks = WorldStateHooks.For(host.World);
        hooks.Locator = new ByNameLocator();
        hooks.WeatherSettings.Enabled = true;
        await using WorldTestClient gm = await host.EnterWorldAsync("GMREP", "Gmrep", AccountSecurity.Administrator);
        await gm.ReadUntilAsync(WorldOpcode.SmsgWeather);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".wchange 3 0.5");
        await gm.ReadUntilAsync(WorldOpcode.SmsgWeather);

        var feature = host.WorldServices.GetRequiredService<WeatherFeature>();
        feature.ReplaceChances([new GameWeatherRecord(12, [20, 0, 0, 20, 0, 0, 20, 0, 0, 20, 0, 0])]);

        Assert.Equal(1, hooks.WeatherChances.Count);
        Assert.Equal(WeatherType.Storm, await host.OnWorldAsync(() => host.World.GetMap(0).FindUpdater<MapWeather>()!.Find(12)!.Type));
        Assert.Equal(20u, hooks.WeatherChances.Get(12)![WeatherSeason.Winter].Rain);
    }
}

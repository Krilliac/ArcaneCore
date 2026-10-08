using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Events;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Events;

/// <summary>
/// <c>.fx</c> end to end over real sessions: every effect's exact bytes reach exactly the sessions its scope names
/// (self, target, zone, map, server), and no other session sees that opcode.
/// </summary>
public sealed partial class LiveFxCommandTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private sealed class ByNameLocator : IZoneLocator
    {
        public Dictionary<string, uint> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (Zones.GetValueOrDefault(player.Name, 12u), 1);

        public AreaTemplate? Find(uint areaId) => new AreaTemplate(areaId, 0, 0, areaId, 0, 1, "z", 0, 0);
    }

    /// <summary>A GM (zone 12), a player beside it (zone 12) and a player in zone 40, all on map 0.</summary>
    private sealed class Realm : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }

        public required WorldTestClient Gm { get; init; }

        public required WorldTestClient Near { get; init; }

        public required WorldTestClient Far { get; init; }

        public WorldTestClient[] All => [Gm, Near, Far];

        public async ValueTask DisposeAsync()
        {
            await Gm.DisposeAsync();
            await Near.DisposeAsync();
            await Far.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static async Task<Realm> StartAsync(AccountSecurity gmSecurity = AccountSecurity.GameMaster, Dictionary<string, string?>? configuration = null)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: configuration is null ? null : services =>
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build()));
        var locator = new ByNameLocator();
        locator.Zones["Fxgm"] = 12;
        locator.Zones["Fxnear"] = 12;
        locator.Zones["Fxfar"] = 40;
        WorldStateHooks hooks = WorldStateHooks.For(host.World);
        hooks.Locator = locator;
        hooks.WeatherSettings.Enabled = true;
        WorldTestClient gm = await host.EnterWorldAsync("FXGM", "Fxgm", gmSecurity);
        WorldTestClient near = await host.EnterWorldAsync("FXNEAR", "Fxnear");
        WorldTestClient far = await host.EnterWorldAsync("FXFAR", "Fxfar");
        var realm = new Realm { Host = host, Gm = gm, Near = near, Far = far };
        await Drain(realm);
        return realm;
    }

    private static async Task Drain(Realm realm)
    {
        foreach (WorldTestClient client in realm.All)
        {
            await client.CollectAsync(Quiet);
        }
    }

    /// <summary>Send a command as the GM and return its system reply; every packet the GM got before it is in <paramref name="gmPackets"/>.</summary>
    private static async Task<string> RunAsync(Realm realm, string line, List<(WorldOpcode Opcode, byte[] Payload)> gmPackets)
    {
        await realm.Gm.SendChatAsync(ChatType.Say, Language.Common, line);
        while (true)
        {
            (WorldOpcode opcode, byte[] payload) = await realm.Gm.ReadAsync(TimeSpan.FromSeconds(10));
            if (opcode == WorldOpcode.SmsgMessagechat)
            {
                ChatMessage chat = ChatMessage.Parse(payload);
                if (chat.Type == ChatType.System)
                {
                    return chat.Text;
                }
            }

            gmPackets.Add((opcode, payload));
        }
    }

    private static List<byte[]> Of(IEnumerable<(WorldOpcode Opcode, byte[] Payload)> packets, WorldOpcode opcode)
        => [.. packets.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    /// <summary>Run a command and return, per client (GM, Near, Far), the payloads of <paramref name="opcode"/> each received.</summary>
    private static async Task<(string Reply, List<byte[]> Gm, List<byte[]> Near, List<byte[]> Far)> EffectAsync(Realm realm, string line, WorldOpcode opcode)
    {
        var gm = new List<(WorldOpcode, byte[])>();
        string reply = await RunAsync(realm, line, gm);
        gm.AddRange(await realm.Gm.CollectAsync(Quiet));
        return (reply, Of(gm, opcode), Of(await realm.Near.CollectAsync(Quiet), opcode), Of(await realm.Far.CollectAsync(Quiet), opcode));
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    [Fact]
    public async Task Music_DefaultsToSelf_AndSendsTheSoundIdToTheInvokerOnly()
    {
        await using Realm realm = await StartAsync();

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx music 8440", WorldOpcode.SmsgPlayMusic);

        Assert.Equal("Music sent to 1 player(s) (self).", reply);
        Assert.Equal([U32(8440)], gm);
        Assert.Empty(near);
        Assert.Empty(far);
    }

    [Fact]
    public async Task Sound_ZoneScope_ReachesTheGmsZoneAndNotTheNextZone()
    {
        await using Realm realm = await StartAsync();

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx sound 3439 zone", WorldOpcode.SmsgPlaySound);

        Assert.Equal("Sound sent to 2 player(s) (zone).", reply);
        Assert.Equal([U32(3439)], gm);
        Assert.Equal([U32(3439)], near);
        Assert.Empty(far);
    }

    [Fact]
    public async Task Cinematic_TargetScope_ReachesOnlyTheSelectedPlayer_AndNeedsASelection()
    {
        await using Realm realm = await StartAsync();

        Assert.Equal(GmStrings.NoCharSelected, (await EffectAsync(realm, ".fx cinematic 2 target", WorldOpcode.SmsgTriggerCinematic)).Reply);

        Player near = await realm.Host.PlayerAsync("Fxnear");
        await realm.Host.OnWorldAsync(() => realm.Host.World.FindOnlinePlayer("Fxgm")!.Selection = near.Guid);
        var (reply, gm, nearGot, far) = await EffectAsync(realm, ".fx cinematic 2 target", WorldOpcode.SmsgTriggerCinematic);

        Assert.Equal("Cinematic sent to 1 player(s) (target).", reply);
        Assert.Empty(gm);
        Assert.Equal([U32(2)], nearGot);
        Assert.Empty(far);
    }

    [Fact]
    public async Task ServerAndMapScopes_RefuseAGameMaster_AndSendNothing()
    {
        await using Realm realm = await StartAsync();

        foreach (string scope in new[] { "map", "server" })
        {
            var (reply, gm, near, far) = await EffectAsync(realm, ".fx worldstate 2313 5 " + scope, WorldOpcode.SmsgUpdateWorldState);
            Assert.Equal(GmStrings.SecurityTooLow, reply);
            Assert.Empty(gm);
            Assert.Empty(near);
            Assert.Empty(far);
        }
    }

    [Fact]
    public async Task WorldState_ServerScope_ReachesEveryone_MapScope_LeavesOutAnotherMap()
    {
        await using Realm realm = await StartAsync(AccountSecurity.Administrator);
        byte[] expected = [.. U32(2313), .. U32(5)];

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx worldstate 2313 5 server", WorldOpcode.SmsgUpdateWorldState);
        Assert.Equal("World state sent to 3 player(s) (server).", reply);
        Assert.Equal([expected], gm);
        Assert.Equal([expected], near);
        Assert.Equal([expected], far);

        // Far moves to Kalimdor (map 1).
        Player farPlayer = await realm.Host.PlayerAsync("Fxfar");
        await realm.Host.OnWorldAsync(() => Assert.True(((WorldSession)farPlayer.Session).Services.GetRequiredService<TeleportFeature>()
            .Teleports.TeleportTo(farPlayer, 1, -441.8f, -2596f, 96f, 0)));
        await realm.Far.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await realm.Far.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await realm.Far.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        await realm.Host.WaitForWorldAsync(() => farPlayer.Map?.MapId == 1, "Far on map 1");
        await Drain(realm);

        (reply, gm, near, far) = await EffectAsync(realm, ".fx worldstate 2313 6 map", WorldOpcode.SmsgUpdateWorldState);
        expected = [.. U32(2313), .. U32(6)];
        Assert.Equal("World state sent to 2 player(s) (map).", reply);
        Assert.Equal([expected], gm);
        Assert.Equal([expected], near);
        Assert.Empty(far);
    }

    [Fact]
    public async Task ZoneAttack_DefaultsToTheGmsZone_OrTakesAnArea()
    {
        await using Realm realm = await StartAsync();

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx zoneattack zone", WorldOpcode.SmsgZoneUnderAttack);
        Assert.Equal("Zone attack sent to 2 player(s) (zone).", reply);
        Assert.Equal([U32(12)], gm);
        Assert.Equal([U32(12)], near);
        Assert.Empty(far);

        // An explicit area must be in AreaTable: give the host one with zones 12 and 1519.
        AreaTable areas = await realm.Host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(realm.Host.World);
            maps.Terrain.Areas = new AreaTable([new AreaTemplate(12, 0, 0, 12, 0, 1, "z", 0, 0), new AreaTemplate(1519, 0, 0, 1519, 0, 1, "s", 0, 0)], maps.Registry);
            return maps.Areas;
        });
        uint known = 1519;
        (reply, gm, near, far) = await EffectAsync(realm, $".fx zoneattack {known}", WorldOpcode.SmsgZoneUnderAttack);
        Assert.Equal("Zone attack sent to 1 player(s) (self).", reply);
        Assert.Equal([U32(known)], gm);
        Assert.Empty(near);
        Assert.Empty(far);

        uint unknown = areas.All.Max(a => a.Entry) + 1;
        (reply, gm, _, _) = await EffectAsync(realm, $".fx zoneattack {unknown}", WorldOpcode.SmsgZoneUnderAttack);
        Assert.Equal($"Area {unknown} not found.", reply);
        Assert.Empty(gm);
    }

    [Fact]
    public async Task TimeSpeed_SendsTheGameTimeAndTheChosenSpeed_ResetIsRetail_AndBadSpeedsAreRefused()
    {
        await using Realm realm = await StartAsync();
        DateTimeOffset now = await realm.Host.OnWorldAsync(() => WorldStateHooks.For(realm.Host.World).LocalNow());

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx timespeed 1.5", WorldOpcode.SmsgLoginSettimespeed);
        Assert.Equal("Time speed sent to 1 player(s) (self).", reply);
        byte[] packet = Assert.Single(gm);
        Assert.Equal(8, packet.Length);
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        Assert.True(packed == GameTimePacker.Pack(now) || packed == GameTimePacker.Pack(now.AddMinutes(1)), "packed game time is the current minute");
        Assert.Equal(1.5f, BitConverter.ToSingle(packet, 4));
        Assert.Empty(near);
        Assert.Empty(far);

        (_, gm, _, _) = await EffectAsync(realm, ".fx timespeed reset", WorldOpcode.SmsgLoginSettimespeed);
        Assert.Equal(GameTimePacker.GameSpeedMinutesPerSecond, BitConverter.ToSingle(Assert.Single(gm), 4));

        foreach (string bad in new[] { ".fx timespeed 61", ".fx timespeed -1", ".fx timespeed fast", ".fx timespeed 1 nowhere" })
        {
            (reply, gm, _, _) = await EffectAsync(realm, bad, WorldOpcode.SmsgLoginSettimespeed);
            Assert.Contains("Syntax", reply, StringComparison.Ordinal);
            Assert.Empty(gm);
        }
    }

    [Fact]
    public async Task Message_TakesALeadingScope_AndSendsTheSizedText()
    {
        await using Realm realm = await StartAsync();

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx message zone The gates are open", WorldOpcode.SmsgAreaTriggerMessage);

        byte[] text = Encoding.UTF8.GetBytes("The gates are open");
        byte[] expected = [.. U32((uint)text.Length + 1), .. text, 0];
        Assert.Equal("Message sent to 2 player(s) (zone).", reply);
        Assert.Equal([expected], gm);
        Assert.Equal([expected], near);
        Assert.Empty(far);

        (_, gm, _, _) = await EffectAsync(realm, ".fx message hello", WorldOpcode.SmsgAreaTriggerMessage);
        Assert.Equal([[.. U32(6), .. "hello"u8.ToArray(), 0]], gm);
        Assert.Contains("Syntax", (await EffectAsync(realm, ".fx message zone", WorldOpcode.SmsgAreaTriggerMessage)).Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Visual_PlaysOnTheScopedCharacter_SeenByIt()
    {
        await using Realm realm = await StartAsync();
        Player near = await realm.Host.PlayerAsync("Fxnear");
        await realm.Host.OnWorldAsync(() => realm.Host.World.FindOnlinePlayer("Fxgm")!.Selection = near.Guid);

        var (reply, gm, nearGot, far) = await EffectAsync(realm, ".fx visual 406 target", WorldOpcode.SmsgPlaySpellVisual);

        // vmangos SendMessageToSet(self = true): the target and everyone who sees it (here all three stand together) get
        // the kit on the TARGET's guid, once each; nobody gets one on their own character.
        byte[] expected = [.. BitConverter.GetBytes(near.Guid.Value), .. U32(406)];
        Assert.Equal("Spell visual sent to 1 player(s) (target).", reply);
        Assert.Equal([expected], nearGot);
        Assert.Equal([expected], gm);
        Assert.Equal([expected], far);

        // A player on another map is not in the target's visible set.
        Player farPlayer = await realm.Host.PlayerAsync("Fxfar");
        await realm.Host.OnWorldAsync(() => Assert.True(((WorldSession)farPlayer.Session).Services.GetRequiredService<TeleportFeature>()
            .Teleports.TeleportTo(farPlayer, 1, -441.8f, -2596f, 96f, 0)));
        await realm.Far.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await realm.Far.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await realm.Host.WaitForWorldAsync(() => farPlayer.Map?.MapId == 1, "Far on map 1");
        await Drain(realm);
        (_, _, nearGot, far) = await EffectAsync(realm, ".fx visual 406 target", WorldOpcode.SmsgPlaySpellVisual);
        Assert.Equal([expected], nearGot);
        Assert.Empty(far);
    }

    [Fact]
    public async Task Weather_SetsEveryOccupiedZoneOfTheMap_AndRefusesPerPlayerScopes()
    {
        await using Realm realm = await StartAsync(AccountSecurity.Administrator);

        var (reply, gm, near, far) = await EffectAsync(realm, ".fx weather 1 0.5 map", WorldOpcode.SmsgWeather);

        Assert.Equal("Weather set in 2 zone(s) (map).", reply);
        byte[] rain = [1, 0, 0, 0, 0, 0, 0, 0x3F, 0x55, 0x21, 0, 0, 0]; // as .wchange 1 0.5
        Assert.Equal([rain], gm);
        Assert.Equal([rain], near);
        Assert.Equal([rain], far);
        MapWeather weather = await realm.Host.OnWorldAsync(() => realm.Host.World.GetMap(0).FindUpdater<MapWeather>()!);
        Assert.Equal(WeatherType.Rain, await realm.Host.OnWorldAsync(() => weather.Find(12)!.Type));
        Assert.Equal(WeatherType.Rain, await realm.Host.OnWorldAsync(() => weather.Find(40)!.Type));

        (reply, gm, near, far) = await EffectAsync(realm, ".fx weather 2 1", WorldOpcode.SmsgWeather); // default zone
        Assert.Equal("Weather set in 1 zone(s) (zone).", reply);
        Assert.Single(gm);
        Assert.Single(near);
        Assert.Empty(far);

        Assert.Equal(LiveFxCommands.WeatherScopeText, (await EffectAsync(realm, ".fx weather 1 0.5 self", WorldOpcode.SmsgWeather)).Reply);
        Assert.Contains("Syntax", (await EffectAsync(realm, ".fx weather 9 0.5", WorldOpcode.SmsgWeather)).Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_FiresEveryStepOfThePreset_ToTheScope_AndListsPresets()
    {
        await using Realm realm = await StartAsync();

        var gmPackets = new List<(WorldOpcode Opcode, byte[] Payload)>();
        string reply = await RunAsync(realm, ".fx event invasion zone", gmPackets);
        gmPackets.AddRange(await realm.Gm.CollectAsync(Quiet));
        List<(WorldOpcode Opcode, byte[] Payload)> near = await realm.Near.CollectAsync(Quiet);
        List<(WorldOpcode Opcode, byte[] Payload)> far = await realm.Far.CollectAsync(Quiet);

        Assert.Equal("Event invasion sent to 2 player(s) (zone).", reply);
        foreach (List<(WorldOpcode Opcode, byte[] Payload)> got in new[] { gmPackets, near })
        {
            Assert.Equal([U32(LiveFxCommands.BattleStartSound)], Of(got, WorldOpcode.SmsgPlaySound));
            Assert.Equal([U32(12)], Of(got, WorldOpcode.SmsgZoneUnderAttack));
            Assert.Single(Of(got, WorldOpcode.SmsgAreaTriggerMessage));
        }

        Assert.DoesNotContain(far, p => p.Opcode is WorldOpcode.SmsgPlaySound or WorldOpcode.SmsgZoneUnderAttack or WorldOpcode.SmsgAreaTriggerMessage);

        string list = await RunAsync(realm, ".fx event", []);
        foreach (LiveFxCommands.Preset preset in LiveFxCommands.Presets)
        {
            Assert.Contains(preset.Name, list, StringComparison.Ordinal);
        }

        Assert.StartsWith("Unknown preset 'bloodmoon'.", await RunAsync(realm, ".fx event bloodmoon", []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryUse_IsAudited_AndAPlayerCannotUseIt()
    {
        await using Realm realm = await StartAsync();
        await RunAsync(realm, ".fx music 8440", []);
        GmAuditFeature audit = realm.Host.WorldServices.GetRequiredService<GmAuditFeature>();
        await realm.Host.WaitForWorldAsync(() => audit.Tail(10).Any(e => e.Command == "fx music 8440"), "the .fx use is audited");

        await realm.Near.SendChatAsync(ChatType.Say, Language.Common, ".fx music 8440");
        Assert.Equal(GmStrings.CommandUnavailable, (await realm.Near.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task TheRootDoesNotExist_WhenLiveFxIsOff()
    {
        await using Realm realm = await StartAsync(configuration: new() { ["World:GmCommands:LiveFx"] = "false" });

        Assert.Equal(GmStrings.NoSuchCommand, await RunAsync(realm, ".fx music 8440", []));
    }

    [Fact]
    public void Scopes_ParseCaseInsensitively_AndOnlyMapAndServerNeedAnAdministrator()
    {
        Assert.True(FxScopes.TryParse("ZONE", out FxScope zone));
        Assert.Equal(FxScope.Zone, zone);
        Assert.False(FxScopes.TryParse("world", out _));
        Assert.Equal([FxScope.Map, FxScope.Server], Enum.GetValues<FxScope>().Where(FxScopes.NeedsAdministrator));
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>An <see cref="IGameEventDataStore"/> whose content the starting test sets (async-local, so tests stay isolated).</summary>
internal sealed class GameEventTestStore : IGameEventDataStore
{
    public static readonly AsyncLocal<GameEventContent?> Current = new();

    private readonly GameEventContent _content = Current.Value ?? GameEventContent.Empty;

    public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_content);
}

/// <summary>Registers <see cref="GameEventTestStore"/> in every test host (empty unless a game event test set content).</summary>
internal sealed class GameEventTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services) => services.AddScoped<IGameEventDataStore, GameEventTestStore>();
}

/// <summary>
/// Game-event spawns end to end in the running world daemon: the data store feeds the event feature, the event feature drives the
/// service from the world tick, and the spawn feature installs the gate on the creature system that the creature feature attaches.
/// Event windows are placed around the real clock (the host runs a real tick thread), a few hours wide, so the test never depends on
/// a duration; it waits on conditions.
/// </summary>
public sealed class GameEventSpawnWorldTests
{
    private const uint Wolf = 299;
    private const uint SpringGuid = 91001;  // listed under event 1 (positive)
    private const uint NightsGuid = 91002;  // listed under event 27 (negative)
    private const uint PlainGuid = 91003;

    private static string LocalText(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task EventSpawns_AreGated_StartWithTheirEvent_AndLeaveWithIt()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var template = new CreatureTemplate
        {
            Entry = Wolf, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 32, CreatureType = 1, MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        CreatureSpawn Spawn(uint guid, float x) => new() { Guid = guid, Entry = Wolf, MapId = 0, X = x, Y = -132f, Z = 83.5f, Orientation = 1.5f, SpawnTimeMinSeconds = 120, SpawnTimeMaxSeconds = 120 };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [Spawn(SpringGuid, -8940f), Spawn(NightsGuid, -8942f), Spawn(PlainGuid, -8944f)], [], [], []));
        GameEventTestStore.Current.Value = new GameEventContent(
            [new GameEventRecord(1, 1, 1440, 180, 0, 0, "Spring"), new GameEventRecord(27, 1, 1440, 180, 0, 0, "Nights")],
            [
                new GameEventTimeRecord(1, LocalText(now.AddMinutes(-30)), "2090-12-31 22:59:59"),   // running for the next 2.5 hours
                new GameEventTimeRecord(27, LocalText(now.AddHours(5)), "2090-12-31 22:59:59"),      // opens in five hours
            ],
            [new GameEventSpawnRecord(SpringGuid, 1), new GameEventSpawnRecord(NightsGuid, -27)],
            [], [], [], []);
        WorldTestHost started;
        try
        {
            started = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            GameEventTestStore.Current.Value = null;
        }

        await using WorldTestHost host = started;
        await using WorldTestClient client = await host.EnterWorldAsync("EVTSPAWN", "Evtspawn");
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
        CreatureWorldFeature creatures = host.WorldServices.GetRequiredService<CreatureWorldFeature>();
        ObjectGuid Guid(uint spawn) => ObjectGuid.WithEntry(HighGuid.Unit, Wolf, spawn);

        await host.WaitForWorldAsync(() => events.IsActiveEvent(1), "event 1 starts on a world tick");
        Assert.False(await host.OnWorldAsync(() => events.IsActiveEvent(27)));
        CreatureMapSystem system = await host.OnWorldAsync(() => creatures.FindSystem(0u)!);
        await host.WaitForWorldAsync(() => system.SpawnGate is not null, "the spawn gate is installed");

        // event 1 runs: its creature exists next to the player; event 27 has not started, so its negative-listed creature exists too
        await host.WaitForWorldAsync(() => system.FindCreature(Guid(SpringGuid)) is not null && system.FindCreature(Guid(NightsGuid)) is not null, "the event creatures spawn");
        Assert.True(await host.OnWorldAsync(() => system.FindCreature(Guid(PlainGuid)) is not null));

        // stop event 1 by hand: the creature is destroyed for the player, the others stay
        await host.OnWorldAsync(() => events.Service!.StopEvent(1, overwrite: true));
        Assert.Null(await host.OnWorldAsync(() => system.FindCreature(Guid(SpringGuid))));
        Assert.NotNull(await host.OnWorldAsync(() => system.FindCreature(Guid(PlainGuid))));
        byte[] destroyed = await client.ReadUntilAsync(WorldOpcode.SmsgDestroyObject);
        Assert.Equal(Guid(SpringGuid).Value, BitConverter.ToUInt64(destroyed));

        // start event 27 by hand: its negative-listed creature goes
        await host.OnWorldAsync(() => events.Service!.StartEvent(27, overwrite: true));
        Assert.Null(await host.OnWorldAsync(() => system.FindCreature(Guid(NightsGuid))));
        Assert.NotNull(await host.OnWorldAsync(() => system.FindCreature(Guid(PlainGuid))));
    }
}

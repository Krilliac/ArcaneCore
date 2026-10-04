using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Game;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>
/// Rested experience in the daemon (docs/areas/rested-xp.md): the pool and the resting flag survive a logout, the offline time is
/// added from the stored second at the rate of the place the character logged out in, a capital zone and an inn trigger start a rest,
/// leaving an inn ends it, and a write that is not durable blocks the next login instead of losing the pool. A level-1 character has
/// 400 XP to the next level, so 8 hours are worth 10 points of pool.
/// </summary>
public sealed class RestFeatureTests
{
    private const long Start = 1_700_000_000;
    private const long EightHours = 8 * 3600;
    private const uint InnTrigger = 90;
    private const uint OtherTrigger = 92;

    private sealed class FixedClock : DeathClock
    {
        private long _now = Start;

        public override long UnixSeconds => Volatile.Read(ref _now);

        public void Advance(long seconds) => Interlocked.Add(ref _now, seconds);
    }

    /// <summary>The default test map plus an inn trigger (a 5-yard sphere on the human start) and a trigger that is no inn.</summary>
    private sealed class InnMapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(GridTerrain.InMemoryMapDataStore.Content with
            {
                AreaTriggers =
                [
                    .. GridTerrain.InMemoryMapDataStore.Content.AreaTriggers,
                    new AreaTriggerTemplate(InnTrigger, 0, -8949.95f, -132.493f, 83.5312f, 5, 0, 0, 0, 0, "Test inn"),
                    new AreaTriggerTemplate(OtherTrigger, 0, -8949.95f, -132.493f, 83.5312f, 5, 0, 0, 0, 0, "Test non-inn"),
                ],
            });
    }

    private static WorldTestHost StartHost(FixedClock clock, Dictionary<string, string?>? config = null, bool withInn = false)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
            if (withInn)
            {
                services.AddSingleton<IMapDataStore, InnMapStore>();
                services.AddSingleton(new InMemoryTavernStore { Ids = { InnTrigger, 91 } }); // 91 names no area trigger
            }
        });
        DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
        return host;
    }

    private static RestFeature Feature(WorldTestHost host) => host.WorldServices.GetRequiredService<RestFeature>();

    private static PlayerProgression Progression(WorldTestHost host) => host.WorldServices.GetRequiredService<ProgressionFeature>().Progression;

    private static InMemoryRestStore Store(WorldTestHost host) => host.WorldServices.GetRequiredService<InMemoryRestStore>();

    /// <summary>The player's first zone update (login) has run, so a rest set now is not undone by it.</summary>
    private static Task ZoneSettledAsync(WorldTestHost host, string name)
        => host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { Map: { } map } player && map.FindUpdater<ZoneAreaUpdater>()!.GetZone(player) != 0, "the first zone update");

    private static async Task<(WorldTestClient Client, byte[] Key)> CreateAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        await client.LoginAsync(1);
        return (client, key);
    }

    [Fact]
    public void TheFeature_IsDiscovered_AndListensToZonesAndAreaTriggers()
    {
        Assert.Contains(typeof(RestFeature), WorldFeatures.FeatureTypes);
        Assert.Contains(typeof(RestDeleteHook), WorldFeatures.FeatureTypes);
        Assert.True(typeof(ICharacterHooks).IsAssignableFrom(typeof(RestFeature)));
        Assert.True(typeof(ICharacterDeleteHook).IsAssignableFrom(typeof(RestDeleteHook)));
        Assert.True(typeof(IAreaTriggerListener).IsAssignableFrom(typeof(RestFeature)));
        Assert.True(typeof(IPlayerLocationListener).IsAssignableFrom(typeof(RestFeature)));
    }

    [Fact]
    public async Task ThePoolAndTheRestingFlag_SurviveALogout_AndTheOfflineTimeIsAdded()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock);
        (WorldTestClient first, byte[] key) = await CreateAsync(host, "REST1", "Restone");
        await ZoneSettledAsync(host, "Restone");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Restone")!;
            Progression(host).SetRestBonus(player, 100);
            Feature(host).Rest.SetRestType(player, RestType.InCity, 0, host.World.NowMs);
        });

        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Restone") is null, "the session to leave the world");
        await Feature(host).Writes.FlushAsync();

        // The logout wrote the pool, the second and the flag.
        Assert.Equal(new CharacterRestState(100f, Start, true), Store(host).Get(1));

        // Eight hours later the character logs in again: 100 + 8 h at the full rate, because it logged out resting.
        clock.Advance(EightHours);
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("REST1", key);
        await again.LoginAsync(1);
        Assert.Equal(110f, await host.PlayerStateAsync("Restone", p => Progression(host).RestBonus(p)), 2);
        Assert.Equal(110u, await host.PlayerStateAsync("Restone", p => p.GetUInt32(UpdateFields.PlayerRestStateExperience)));
        Assert.Equal(PlayerProgression.RestStateRested, await host.PlayerStateAsync("Restone", p => p.GetByte(UpdateFields.PlayerBytes2, 3)));
    }

    [Fact]
    public async Task ACharacterThatLoggedOutOutsideARestPlace_EarnsAQuarterOfTheRate()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock, new() { ["Rest:RateOfflineInWilderness"] = "2" });
        byte[] key = await host.AddAccountAsync("REST2");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("REST2", key);
        await client.CreateCharacterAsync("Resttwo");
        Store(host).Set(1, new CharacterRestState(100f, Start, WasResting: false));
        clock.Advance(EightHours);

        await client.LoginAsync(1);

        // 10 points for 8 hours at rate 1, times the configured 2, divided by 4 for the wilderness.
        Assert.Equal(105f, await host.PlayerStateAsync("Resttwo", p => Progression(host).RestBonus(p)), 2);
    }

    [Fact]
    public async Task AStoredPoolAboveTheCap_IsClampedAtLogin()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock);
        byte[] key = await host.AddAccountAsync("REST3");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("REST3", key);
        await client.CreateCharacterAsync("Restthree");
        Store(host).Set(1, new CharacterRestState(9_999_999f, Start, WasResting: true));

        await client.LoginAsync(1);

        Assert.Equal(300f, await host.PlayerStateAsync("Restthree", p => Progression(host).RestBonus(p))); // 400 * 1.5 / 2
    }

    [Fact]
    public async Task ACreatedCharacter_DoesNotInheritTheRowOfAnEarlierOneWithItsId()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock);
        Store(host).Set(1, new CharacterRestState(250f, Start, WasResting: true));
        byte[] key = await host.AddAccountAsync("REST4");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("REST4", key);
        await client.CreateCharacterAsync("Restfour");
        Assert.Null(Store(host).Get(1));

        await client.LoginAsync(1);
        Assert.Equal(0f, await host.PlayerStateAsync("Restfour", p => Progression(host).RestBonus(p)));
    }

    [Fact]
    public async Task TheLocationListener_StartsACityRestInACapital_AndEndsItElsewhere()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock);
        var (client, _) = await CreateAsync(host, "REST5", "Restfive");
        await using (client)
        {
            await ZoneSettledAsync(host, "Restfive");
            Assert.Contains(Feature(host), WorldStateHooks.For(host.World).LocationListeners);

            var capital = new AreaTemplate(1519, 0, 0, 1, (uint)AreaFlags.Capital, 0, "Stormwind City", 2, 0);
            var elwynn = new AreaTemplate(12, 0, 0, 2, 0, 0, "Elwynn Forest", 2, 0);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Restfive")!;
                Feature(host).OnZoneChanged(player, 12, 1519, 1519, capital);
                Assert.Equal(RestType.InCity, Feature(host).Rest.GetRestType(player));
                Assert.True((player.Flags & PlayerFlags.Resting) != 0);

                Feature(host).OnZoneChanged(player, 1519, 12, 12, elwynn);
                Assert.Equal(RestType.None, Feature(host).Rest.GetRestType(player));
                Assert.True((player.Flags & PlayerFlags.Resting) == 0);
            });
        }
    }

    [Fact]
    public async Task AnInnTrigger_StartsATavernRest_ThatGainsPool_AndEndsOutdoorsOutsideTheTrigger()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock, new() { ["Rest:AccrualIntervalSeconds"] = "1" }, withInn: true);
        var (client, _) = await CreateAsync(host, "REST6", "Restsix");
        await using (client)
        {
            await ZoneSettledAsync(host, "Restsix");
            Assert.Equal(2, Feature(host).Taverns.Count); // 90 and 91 are loaded as they are: an id that names no trigger can never be entered
            Assert.True(Feature(host).Taverns.Contains(InnTrigger));

            // A trigger that is no inn changes nothing.
            await client.SendAsync(WorldOpcode.CmsgAreatrigger, TriggerPacket(OtherTrigger));
            await host.OnWorldAsync(() => Assert.Equal(RestType.None, Feature(host).Rest.GetRestType(host.World.FindOnlinePlayer("Restsix")!)));

            await client.SendAsync(WorldOpcode.CmsgAreatrigger, TriggerPacket(InnTrigger));
            await host.WaitForWorldAsync(() => Feature(host).Rest.GetRestType(host.World.FindOnlinePlayer("Restsix")!) == RestType.InTavern, "the tavern rest to start");
            Assert.Equal(InnTrigger, await host.PlayerStateAsync("Restsix", p => Feature(host).Rest.TavernTrigger(p)));

            // The world tick adds pool while the character rests (interval 1 s here, 10 s by default).
            await host.WaitForWorldAsync(() => Progression(host).RestBonus(host.World.FindOnlinePlayer("Restsix")!) > 0f, "rested experience to accrue");

            // Walking out of the trigger, outdoors, ends the rest at the next check.
            await host.PlaceAsync("Restsix", -8900f, -132.493f, 83.5312f);
            await host.WaitForWorldAsync(() => Feature(host).Rest.GetRestType(host.World.FindOnlinePlayer("Restsix")!) == RestType.None, "the tavern rest to end");
            Assert.False(await host.PlayerStateAsync("Restsix", p => (p.Flags & PlayerFlags.Resting) != 0));
            Assert.Equal(0, await host.OnWorldAsync(() => Feature(host).Rest.RestingCount));
        }
    }

    [Fact]
    public async Task TheRestedStateOfEveryOnlineCharacter_IsWrittenPeriodically()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock, new() { ["Rest:SaveIntervalSeconds"] = "1" });
        var (client, _) = await CreateAsync(host, "REST7", "Restseven");
        await using (client)
        {
            await host.OnWorldAsync(() => Progression(host).SetRestBonus(host.World.FindOnlinePlayer("Restseven")!, 42));
            clock.Advance(77);

            await WorldTestHost.WaitForAsync(() => Store(host).Get(1) is { RestBonus: 42f } row && row.LogoutUnixSeconds == Start + 77, "the periodic write");
        }
    }

    [Fact]
    public async Task WithTheIntervalOff_OnlyALogoutWrites()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock, new() { ["Rest:SaveIntervalSeconds"] = "0" });
        var (client, _) = await CreateAsync(host, "REST8", "Resteight");
        await host.OnWorldAsync(() => Progression(host).SetRestBonus(host.World.FindOnlinePlayer("Resteight")!, 42));
        await Task.Delay(1500);
        Assert.Equal(0, Store(host).Saves);

        await client.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resteight") is null, "the session to leave the world");
        await Feature(host).Writes.FlushAsync();
        Assert.Equal(42f, Store(host).Get(1)!.Value.RestBonus);
    }

    [Fact]
    public async Task AWriteThatIsStillNotDurable_BlocksTheNextLogin_AndTheRetainedPoolIsWrittenOnceTheStoreRecovers()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock);
        (WorldTestClient first, byte[] key) = await CreateAsync(host, "REST9", "Restnine");
        await ZoneSettledAsync(host, "Restnine");
        await host.OnWorldAsync(() => Progression(host).SetRestBonus(host.World.FindOnlinePlayer("Restnine")!, 77));

        Store(host).FailSaves = true;
        await first.DisposeAsync(); // logout is never blocked by a failing store
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Restnine") is null, "the session to leave the world");
        await Feature(host).Writes.FlushAsync();
        Assert.True(Feature(host).Writes.HasRetainedFailure(1));
        Assert.Null(Store(host).Get(1));

        await using (WorldTestClient refused = await host.ConnectAsync())
        {
            await refused.AuthenticateAsync("REST9", key);
            var login = new PacketWriter(8);
            login.WriteUInt64(1);
            await refused.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            await refused.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed); // fail closed: never enter with a stale pool
        }

        Store(host).FailSaves = false;
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("REST9", key);
        await again.LoginAsync(1);
        Assert.Equal(77f, await host.PlayerStateAsync("Restnine", p => Progression(host).RestBonus(p)), 2);
        Assert.False(Feature(host).Writes.HasRetainedFailure(1));
        Assert.Equal(77f, Store(host).Get(1)!.Value.RestBonus);
    }

    [Fact]
    public async Task Shutdown_WritesTheCharactersStillOnline()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock, new() { ["Rest:SaveIntervalSeconds"] = "0" });
        var (client, _) = await CreateAsync(host, "REST10", "Resten");
        await using (client)
        {
            await host.OnWorldAsync(() => Progression(host).SetRestBonus(host.World.FindOnlinePlayer("Resten")!, 55));
            Assert.Null(Store(host).Get(1));

            await Feature(host).StopAsync();

            Assert.Equal(55f, Store(host).Get(1)!.Value.RestBonus);
        }
    }

    [Fact]
    public async Task DeleteHook_ForgetsRetainedWrites()
    {
        var clock = new FixedClock();
        await using WorldTestHost host = StartHost(clock);
        (WorldTestClient first, _) = await CreateAsync(host, "REST11", "Resteleven");
        Store(host).FailSaves = true;
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resteleven") is null, "the session to leave the world");
        await Feature(host).Writes.FlushAsync();
        Assert.True(Feature(host).Writes.HasRetainedFailure(1));

        await host.WorldServices.GetRequiredService<RestDeleteHook>().OnCharacterDeletedAsync(null!, new CharacterRecord { Id = 1, Name = "Resteleven" });

        Assert.False(Feature(host).Writes.HasRetainedFailure(1));
        Store(host).FailSaves = false;
    }

    private static byte[] TriggerPacket(uint id)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(id);
        return writer.ToArray();
    }
}

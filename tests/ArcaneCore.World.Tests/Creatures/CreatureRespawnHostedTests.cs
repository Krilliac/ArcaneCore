using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// The wiring that makes respawn persistence work in the daemon (the real <see cref="CreatureWorldFeature"/> plus <see cref="CreatureRespawnFeature"/> in a
/// <see cref="WorldTestHost"/>): a death is written to the store, and a restarted host loads the creature dead. These fail if the feature stops passing
/// its persistence and clock to the creature systems, or stops saving at shutdown (vmangos Creature::SetDeathState, Objects/Creature.cpp:2262-2263,
/// Creature::LoadFromDB :1972-1989).
/// </summary>
public sealed class CreatureRespawnHostedTests
{
    private const uint WolfEntry = 299;
    private const uint SpawnGuid = 4343;

    private sealed class SharedStore : ICreatureRespawnStore
    {
        private readonly Dictionary<(uint Instance, uint Guid), CreatureRespawnRecord> _rows = [];

        public IReadOnlyList<CreatureRespawnRecord> Rows
        {
            get
            {
                lock (_rows)
                {
                    return [.. _rows.Values];
                }
            }
        }

        public Task<IReadOnlyList<CreatureRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                return Task.FromResult<IReadOnlyList<CreatureRespawnRecord>>([.. _rows.Values.Where(r => r.RespawnTime > nowUnixSeconds)]);
            }
        }

        public Task SaveAsync(IReadOnlyCollection<CreatureRespawnRecord> upserts, IReadOnlyCollection<CreatureRespawnKey> deletes, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                foreach (CreatureRespawnRecord upsert in upserts)
                {
                    _rows[(upsert.InstanceId, upsert.SpawnGuid)] = upsert;
                }

                foreach (CreatureRespawnKey delete in deletes)
                {
                    _rows.Remove((delete.InstanceId, delete.SpawnGuid));
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static WorldTestHost StartWithWolf(SharedStore store, out CreatureTestContext context)
    {
        var wolf = new CreatureTemplate
        {
            Entry = WolfEntry, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 32, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        var spawn = new CreatureSpawn { Guid = SpawnGuid, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        context = new CreatureTestContext(new CreatureContent([wolf], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start(configureServices: services => services.AddSingleton<ICreatureRespawnStore>(store));
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    private static Creature Wolf(CreatureWorldFeature feature)
        => feature.FindSystem(0)!.FindCreature(new ObjectGuid(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, SpawnGuid).Value))!;

    [Fact]
    public async Task ADeath_ReachesTheStore_AndARestartedHostLoadsTheCreatureDead()
    {
        var store = new SharedStore();

        await using (WorldTestHost first = StartWithWolf(store, out CreatureTestContext context))
        {
            await using WorldTestClient client = await first.EnterWorldAsync("RSPHOSTA", "Rsphosta");
            CreatureWorldFeature feature = context.Feature!;
            await first.WaitForWorldAsync(() => feature.FindSystem(0)?.FindCreature(new ObjectGuid(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, SpawnGuid).Value)) is not null, "the wolf loads");

            await first.OnWorldAsync(() => feature.FindSystem(0)!.KillCreature(Wolf(feature)));
            await first.WorldServices.GetRequiredService<CreatureRespawnFeature>().FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

            CreatureRespawnRecord row = Assert.Single(store.Rows);
            Assert.Equal((0u, 0u, SpawnGuid), (row.MapId, row.InstanceId, row.SpawnGuid));
        }

        await using WorldTestHost second = StartWithWolf(store, out CreatureTestContext secondContext);
        await using WorldTestClient again = await second.EnterWorldAsync("RSPHOSTB", "Rsphostb");
        CreatureWorldFeature restarted = secondContext.Feature!;
        await second.WaitForWorldAsync(() => feature_Loaded(restarted), "the wolf loads again");

        Assert.NotEqual(CreatureDeathState.Alive, await second.OnWorldAsync(() => Wolf(restarted).DeathState));
    }

    private static bool feature_Loaded(CreatureWorldFeature feature)
        => feature.FindSystem(0)?.FindCreature(new ObjectGuid(ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, SpawnGuid).Value)) is not null;
}

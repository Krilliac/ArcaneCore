using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>The imported Azshara Necropolis Health AI advances its real zone condition.</summary>
[Collection("Real battleground raid content")]
public sealed class ScourgeInvasionImportedRuntimeTests
{
    private sealed class StateStore : IScourgeInvasionStateStore
    {
        private readonly HashSet<uint> _dead = [];
        private ScourgeInvasionSnapshot _state = new(ScourgeInvasionState.Enabled, 0, 0,
            ScourgeInvasionCatalog.Zones.Select(z => new ScourgeInvasionZoneProgress(z.ZoneId, z.Necropolises, 0)).ToArray());
        public Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);
        public Task<bool> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _state = ScourgeInvasionSnapshot.Disabled;
            return Task.CompletedTask;
        }
        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default)
        {
            if (!_dead.Add(spawnGuid)) return Task.FromResult(false);
            _state = _state with { Zones = _state.Zones.Select(z => z.ZoneId == zoneId
                ? z with { Remaining = z.Remaining - 1 } : z).ToArray() };
            return Task.FromResult(true);
        }
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    [RealWorldContentFact]
    public async Task ThreeImportedZapHitsKillAzsharaHealthAndDecrementItsConditionCount()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-scourge-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!, copy);
        try
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:World:Provider"] = "Sqlite",
                ["Database:World:ConnectionString"] = $"Data Source={copy};Pooling=False",
            }).Build();
            string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
            var state = new StateStore();
            await using var host = WorldTestHost.Start(configure: options =>
            {
                if (terrain is { Length: > 0 }) options.Maps.DataDirectory = terrain;
            }, configureServices: services =>
            {
                ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
                services.AddSingleton(config);
                services.AddWorldDatabase(config);
                services.Add(appearance);
                services.AddSingleton<IScourgeInvasionStateStore>(state);
            });
            await using WorldTestClient client = await host.EnterWorldAsync("SCOURGER", "Scourger", AccountSecurity.Administrator)
                .WaitAsync(TimeSpan.FromSeconds(20));
            GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
            await host.WaitForWorldAsync(() => events.IsActiveEvent(92), "Azshara invasion event")
                .WaitAsync(TimeSpan.FromSeconds(15));

            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            bool accepted = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Scourger")!;
                player.Flags |= PlayerFlags.Gm;
                return teleports.TeleportTo(player, 1, 3299.55f, -4301.30f, 177.89f, 0);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(accepted);
            await host.WaitForWorldAsync(() => teleports.StageOf(host.World.FindOnlinePlayer("Scourger")!) == TeleportStage.Far,
                "Azshara transfer stage").WaitAsync(TimeSpan.FromSeconds(15));
            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Scourger")?.Map is { MapId: 1 },
                "Azshara arrival").WaitAsync(TimeSpan.FromSeconds(15));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Scourger")?.Map?
                .FindUpdater<CreatureMapSystem>()?.Creatures.Any(c => c.Spawn?.Guid == 97592) == true,
                "imported Azshara Necropolis Health spawn").WaitAsync(TimeSpan.FromSeconds(15));

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Scourger")!;
                Creature health = Assert.Single(player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures,
                    c => c.Spawn?.Guid == 97592);
                Assert.Equal(ScourgeInvasionCatalog.NecropolisHealth, health.Entry);
                Assert.Equal("NecropolisHealthAi", health.AI?.GetType().Name);
                SpellInfo zap = Assert.IsType<SpellInfo>(host.WorldServices.GetRequiredService<SpellFeature>()
                    .System.Store.Get(ScourgeInvasionCatalog.ZapNecropolis));
                for (int i = 0; i < 3; i++) health.AI!.OnSpellHit(player, zap);
                Assert.Equal(CreatureDeathState.Corpse, health.DeathState);
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<ScourgeInvasionFeature>()
                .Snapshot.Remaining(16) == 1, "Azshara Necropolis count").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(events.IsActiveEvent(92));
            Assert.Equal(true, await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ConditionFeature>()
                .Current.EvaluateWithoutSubjects(2149)));
            Assert.Equal(1, (await state.LoadAsync()).Remaining(16));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("arcane-scourge-runtime-", StringComparison.Ordinal))
                throw new InvalidOperationException("refusing cleanup outside the Scourge runtime test directory");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [RealWorldContentFact]
    public async Task ImportedCircleShardRelayAndProxyDeliverOneZapToAzsharaHealth()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-scourge-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!, copy);
        try
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:World:Provider"] = "Sqlite",
                ["Database:World:ConnectionString"] = $"Data Source={copy};Pooling=False",
            }).Build();
            string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
            var state = new StateStore();
            await using var host = WorldTestHost.Start(configure: options =>
            {
                if (terrain is { Length: > 0 }) options.Maps.DataDirectory = terrain;
            }, configureServices: services =>
            {
                ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
                services.AddSingleton(config);
                services.AddWorldDatabase(config);
                services.Add(appearance);
                services.AddSingleton<IScourgeInvasionStateStore>(state);
            });
            await using WorldTestClient client = await host.EnterWorldAsync("SCOCHAIN", "Scochain", AccountSecurity.Administrator)
                .WaitAsync(TimeSpan.FromSeconds(20));
            GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
            await host.WaitForWorldAsync(() => events.IsActiveEvent(92), "Azshara invasion event")
                .WaitAsync(TimeSpan.FromSeconds(15));

            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            bool accepted = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Scochain")!;
                player.Flags |= PlayerFlags.Gm;
                return teleports.TeleportTo(player, 1, 3337.51f, -4516.62f, 97.71f, 0);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(accepted);
            await host.WaitForWorldAsync(() => teleports.StageOf(host.World.FindOnlinePlayer("Scochain")!) == TeleportStage.Far,
                "Azshara circle transfer").WaitAsync(TimeSpan.FromSeconds(15));
            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Scochain")?.Map is { MapId: 1 },
                "Azshara circle arrival").WaitAsync(TimeSpan.FromSeconds(15));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Scochain")?.Map?
                .FindUpdater<GameObjectMapSystem>()?.GameObjects.Any(go => go.Spawn?.Guid == 67776) == true,
                "imported summon circle").WaitAsync(TimeSpan.FromSeconds(15));
            await host.OnWorldAsync(() =>
            {
                GameObject circle = Assert.Single(host.World.FindOnlinePlayer("Scochain")!.Map!
                    .FindUpdater<GameObjectMapSystem>()!.GameObjects, go => go.Spawn?.Guid == 67776);
                Assert.True(circle.IsSpawned, $"circle {circle.Entry} loaded but not spawned");
                ScourgeInvasionFeature feature = host.WorldServices.GetRequiredService<ScourgeInvasionFeature>();
                Assert.Equal(16u, feature.CircleZone(circle));
                Assert.Equal(2, feature.Snapshot.Remaining(16));
                return true;
            });
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Scochain")?.Map?
                .FindUpdater<CreatureMapSystem>()?.Creatures.Any(c => c.Entry == ScourgeInvasionCatalog.NecroticShard
                    && MathF.Abs(c.X - 3337.51f) < 3f && MathF.Abs(c.Y + 4516.62f) < 3f) == true,
                "summoned original shard").WaitAsync(TimeSpan.FromSeconds(15));

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Scochain")!;
                var creatures = player.Map!.FindUpdater<CreatureMapSystem>()!;
                Creature original = Assert.Single(creatures.Creatures, c => c.Entry == ScourgeInvasionCatalog.NecroticShard
                    && MathF.Abs(c.X - 3337.51f) < 3f && MathF.Abs(c.Y + 4516.62f) < 3f);
                Assert.Equal("NecroticShardAi", original.AI?.GetType().Name);
                creatures.KillCreature(original);
                Creature damaged = Assert.Single(creatures.Creatures, c => c.Entry == ScourgeInvasionCatalog.DamagedNecroticShard
                    && MathF.Abs(c.X - 3337.51f) < 3f && MathF.Abs(c.Y + 4516.62f) < 3f);
                Assert.Equal("NecroticShardAi", damaged.AI?.GetType().Name);
                Assert.Contains(creatures.Creatures, c => c.Entry == ScourgeInvasionCatalog.NecropolisRelay
                    && c.AI?.GetType().Name == "NecropolisRelayAi");
                Assert.Contains(creatures.Creatures, c => c.Entry == ScourgeInvasionCatalog.NecropolisProxy
                    && c.AI?.GetType().Name == "NecropolisProxyAi");
                Assert.Contains(creatures.Creatures, c => c.Spawn?.Guid == 97592 && c.IsAlive);
                creatures.KillCreature(damaged);
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Scochain")?.Map?
                .FindUpdater<CreatureMapSystem>()?.Creatures.Any(c => c.Spawn?.Guid == 97592 && c.Health < c.MaxHealth) == true,
                "relay zap reached Necropolis Health").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, (await state.LoadAsync()).Remaining(16)); // one of three circles; the Necropolis still stands
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("arcane-scourge-chain-", StringComparison.Ordinal))
                throw new InvalidOperationException("refusing cleanup outside the Scourge chain test directory");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}

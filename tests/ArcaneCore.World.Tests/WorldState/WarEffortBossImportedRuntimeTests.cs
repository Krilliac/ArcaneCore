using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Imported Silithus boss death, through the ordinary creature AI and event-quest gate.</summary>
[Collection("Real battleground raid content")]
public sealed class WarEffortBossImportedRuntimeTests
{
    private sealed class MemoryWarEffortStore : IWarEffortStateStore
    {
        public bool FailNextBossWrite { get; set; }
        private WarEffortSnapshot _state = new(WarEffortPhase.TenHourWar,
            DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds(), new long[WarEffortCatalog.ResourceCount]);

        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);

        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            _state = _state with { Phase = phase, PhaseEndsAtUnix = phaseEndsAtUnix };
            return Task.CompletedTask;
        }

        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default)
        {
            if (FailNextBossWrite)
            {
                FailNextBossWrite = false;
                throw new InvalidOperationException("simulated boss-state store failure");
            }
            byte bit = (byte)(1 << bossIndex);
            bool first = (_state.KilledBossMask & bit) == 0;
            _state = _state with { KilledBossMask = (byte)(_state.KilledBossMask | bit) };
            return Task.FromResult(first);
        }
    }

    [RealWorldContentFact]
    public async Task ImportedAshiDeathSetsTheSavedFlagAndStartsHisQuestEvent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-war-boss-" + Guid.NewGuid().ToString("N"));
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
            var state = new MemoryWarEffortStore();
            await using var host = WorldTestHost.Start(configure: options =>
            {
                if (terrain is { Length: > 0 }) options.Maps.DataDirectory = terrain;
            }, configureServices: services =>
            {
                ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
                services.AddSingleton(config);
                services.AddWorldDatabase(config);
                services.Add(appearance);
                services.AddSingleton<IWarEffortStateStore>(state);
            });
            await using WorldTestClient client = await host.EnterWorldAsync("WARBOSS", "Warboss", AccountSecurity.Administrator)
                .WaitAsync(TimeSpan.FromSeconds(20));
            GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
            await host.WaitForWorldAsync(() => events.IsActiveEvent(123), "AQ ten-hour event").WaitAsync(TimeSpan.FromSeconds(15));

            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            bool accepted = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Warboss")!;
                player.Flags |= PlayerFlags.Gm;
                return teleports.TeleportTo(player, 1, -6393.32f, 1047.05f, -50.88f, 0);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(accepted);
            await host.WaitForWorldAsync(() => teleports.StageOf(host.World.FindOnlinePlayer("Warboss")!) == TeleportStage.Far,
                "Silithus transfer stage").WaitAsync(TimeSpan.FromSeconds(15));
            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Warboss")?.Map is { MapId: 1 },
                "Silithus arrival").WaitAsync(TimeSpan.FromSeconds(15));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Warboss")?.Map?
                .FindUpdater<CreatureMapSystem>()?.Creatures.Any(c => c.Entry == WarEffortCatalog.ColossusOfAshi) == true,
                "imported Colossus of Ashi spawn").WaitAsync(TimeSpan.FromSeconds(15));

            await host.OnWorldAsync(() =>
            {
                var creatures = host.World.FindOnlinePlayer("Warboss")!.Map!.FindUpdater<CreatureMapSystem>()!;
                Creature boss = Assert.Single(creatures.Creatures, c => c.Entry == WarEffortCatalog.ColossusOfAshi);
                Assert.Equal("SilithusBossAi", boss.AI?.GetType().Name);
                state.FailNextBossWrite = true;
                creatures.KillCreature(boss);
                Assert.Equal(CreatureDeathState.Corpse, boss.DeathState);
                Assert.False(events.IsActiveEvent(125));
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            await host.WaitForWorldAsync(() => events.IsActiveEvent(125), "Ashi death event")
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((byte)1, (await state.LoadAsync()).KilledBossMask);
            Assert.False(events.IsActiveEvent(126));
            Assert.False(events.IsActiveEvent(127));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("arcane-war-boss-", StringComparison.Ordinal))
                throw new InvalidOperationException("refusing cleanup outside the war-effort test directory");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}

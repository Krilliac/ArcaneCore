using ArcaneCore.Data;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>Loads a copy of imported world content through the ordinary features and enters a real AQ40 instance map.</summary>
[Collection("Real battleground raid content")]
public sealed class Aq40ImportedRuntimeTests
{
    [RealWorldContentFact]
    public async Task ImportedBossAisAttach_AndFankrissWebTeleportsThePlayer()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-aq40-runtime-" + Guid.NewGuid().ToString("N"));
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
            await using var host = WorldTestHost.Start(configureServices: services =>
            {
                ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
                services.AddSingleton(config);
                services.AddWorldDatabase(config);
                services.Add(appearance);
            });
            await using WorldTestClient client = await host.EnterWorldAsync("aq40smoke", "Aqsmoke", AccountSecurity.Administrator);
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Flags |= PlayerFlags.Gm);
            Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(host.World.FindOnlinePlayer("Aqsmoke")!,
                531, -8085.39f, 1196.72f, -91.97f, 0)));
            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke") is { MapId: 531, Map: { InstanceId: > 0 } },
                "AQ40 instance arrival");

            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var map = player.Map!;
                Assert.IsType<TempleOfAhnQirajInstance>(map.FindUpdater<InstanceData>());
                CreatureMapSystem creatures = Assert.IsType<CreatureMapSystem>(map.FindUpdater<CreatureMapSystem>());
                Creature fankriss = Assert.Single(creatures.Creatures, c => c.Entry == 15510);
                Assert.IsType<FankrissAI>(fankriss.AI);
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System.Store;
                Assert.NotNull(spells.Get(720));
                Assert.Equal(531u, spells.GetTargetPosition(720)?.MapId);
            });

            await MoveNearAsync(host, client, teleports, -8532.09f, 1696.53f, -90.26f);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15509), "Huhuran grid load");
            await host.OnWorldAsync(() =>
            {
                var creatures = host.World.FindOnlinePlayer("Aqsmoke")!.Map!.FindUpdater<CreatureMapSystem>()!;
                Assert.IsType<HuhuranAI>(Assert.Single(creatures.Creatures, c => c.Entry == 15509).AI);
            });

            await MoveNearAsync(host, client, teleports, -8281.88f, 1688.65f, -25.94f);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15516), "Sartura grid load");
            await host.OnWorldAsync(() =>
            {
                var creatures = host.World.FindOnlinePlayer("Aqsmoke")!.Map!.FindUpdater<CreatureMapSystem>()!;
                Assert.IsType<SarturaAI>(Assert.Single(creatures.Creatures, c => c.Entry == 15516).AI);
                Assert.Equal(3, creatures.Creatures.Count(c => c.Entry == 15984 && c.AI is SarturaRoyalGuardAI));
            });

            await MoveNearAsync(host, client, teleports, -8085.39f, 1196.72f, -91.97f);
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                player.Flags &= ~PlayerFlags.Gm;
                player.Level = 60;
                player.MaxHealth = 100_000;
                player.Health = 100_000;
                var map = player.Map!;
                var raid = (TempleOfAhnQirajInstance)map.FindUpdater<InstanceData>()!;
                var creatures = map.FindUpdater<CreatureMapSystem>()!;
                Creature boss = Assert.Single(creatures.Creatures, c => c.Entry == 15510);
                Assert.True(boss.AI!.AttackStart(player) || ReferenceEquals(boss.Combat.Victim, player));
                boss.AI.OnUpdate(40_000);
                Assert.Equal(EncounterState.InProgress, raid.GetData(TempleOfAhnQirajInstance.Fankriss));
                Assert.Contains(creatures.Creatures, c => c.Entry == 15630);
                Assert.Contains(creatures.Creatures, c => c.Entry == 15962);
                Assert.Equal(TeleportStage.Near, teleports.StageOf(player));
            });
            await AcknowledgeNearAsync(host, client, teleports);
            (float webX, float webY) = await host.PlayerStateAsync("Aqsmoke", p => (p.X, p.Y));
            Assert.Contains(new[] { (-8043.6f, 1254.1f), (-8003f, 1222.9f), (-8022.3f, 1149f) },
                site => MathF.Abs(site.Item1 - webX) < 1f && MathF.Abs(site.Item2 - webY) < 1f);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("arcane-aq40-runtime-", StringComparison.Ordinal))
                throw new InvalidOperationException("AQ40 test cleanup target is outside its temporary directory");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private static async Task MoveNearAsync(WorldTestHost host, WorldTestClient client, TeleportService teleports,
        float x, float y, float z)
    {
        Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(host.World.FindOnlinePlayer("Aqsmoke")!, 531, x, y, z, 0)));
        await AcknowledgeNearAsync(host, client, teleports);
        await host.WaitForWorldAsync(() => MathF.Abs(host.World.FindOnlinePlayer("Aqsmoke")!.X - x) < 1f,
            "near teleport arrival");
    }

    private static async Task AcknowledgeNearAsync(WorldTestHost host, WorldTestClient client, TeleportService teleports)
    {
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck));
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadUInt32();
        var reply = new PacketWriter(16);
        reply.WriteUInt64(guid);
        reply.WriteUInt32(counter);
        reply.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
        await host.WaitForWorldAsync(() => teleports.StageOf(host.World.FindOnlinePlayer("Aqsmoke")!) is null,
            "near teleport acknowledgement");
    }
}

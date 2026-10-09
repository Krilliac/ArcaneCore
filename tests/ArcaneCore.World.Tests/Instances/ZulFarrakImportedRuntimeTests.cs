using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Npc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

internal sealed class ZulFarrakRuntimeFactAttribute : FactAttribute
{
    public const string WorldVariable = "ARCANECORE_TEST_WORLD_DB";
    public const string DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    public ZulFarrakRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(WorldVariable) is not { Length: > 0 } world || !File.Exists(world)
            || Environment.GetEnvironmentVariable(DbcVariable) is not { Length: > 0 } dbc
            || !File.Exists(Path.Combine(dbc, "FactionTemplate.dbc")))
            Skip = $"{WorldVariable} and {DbcVariable}/FactionTemplate.dbc are required for imported Weegli runtime acceptance.";
    }
}

/// <summary>Imported Weegli and the real gossip packet on a loopback build-5875 session.</summary>
[Collection("Real battleground raid content")]
public sealed class ZulFarrakImportedRuntimeTests
{
    [ZulFarrakRuntimeFact]
    public async Task WeegliGossipAndChargeOpenTheImportedDoorWithControlledMovementCallbacks()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-zf-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(ZulFarrakRuntimeFactAttribute.WorldVariable)!, copy);
        try
        {
            string factionDbc = Path.Combine(Environment.GetEnvironmentVariable(ZulFarrakRuntimeFactAttribute.DbcVariable)!, "FactionTemplate.dbc");
            string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:World:Provider"] = "Sqlite",
                ["Database:World:ConnectionString"] = $"Data Source={copy};Pooling=False",
                ["Quests:FactionTemplateDbcPath"] = factionDbc,
                ["Creatures:FactionTemplateDbcPath"] = factionDbc,
            }).Build();
            await using var host = WorldTestHost.Start(configure: options =>
            {
                if (terrain is not { Length: > 0 }) return;
                Assert.True(Directory.Exists(Path.Combine(terrain, "maps")));
                options.Maps.DataDirectory = terrain;
            }, configureServices: services =>
            {
                ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
                services.AddSingleton(config);
                services.AddWorldDatabase(config);
                services.Add(appearance);
            });
            await using WorldTestClient client = await host.EnterWorldAsync("ZFBOUND", "Zfbound", AccountSecurity.Administrator)
                .WaitAsync(TimeSpan.FromSeconds(20));
            TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            bool accepted = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Zfbound")!;
                player.Flags |= PlayerFlags.Gm;
                return teleports.TeleportTo(player, 209, 1881.05f, 1297.36f, 48.419f, 0);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(accepted);
            await host.WaitForWorldAsync(() => teleports.StageOf(host.World.FindOnlinePlayer("Zfbound")!) == TeleportStage.Far,
                "far transfer stage").WaitAsync(TimeSpan.FromSeconds(15));
            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Zfbound") is { MapId: 209, Map: { InstanceId: > 0 } },
                "map 209 arrival").WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsType<ZulFarrakInstance>(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Zfbound")!.Map!
                .FindUpdater<InstanceData>()).WaitAsync(TimeSpan.FromSeconds(5)));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Zfbound")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == ZulFarrakInstance.Weegli),
                "Weegli grid load").WaitAsync(TimeSpan.FromSeconds(15));
            ObjectGuid weegliGuid = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Zfbound")!;
                Creature weegli = Assert.Single(player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures,
                    c => c.Entry == ZulFarrakInstance.Weegli);
                Assert.IsType<WeegliBlastfuseAi>(weegli.AI);
                Assert.NotNull(player.Map.FindUpdater<GameObjectMapSystem>()!.FindTemplate(WeegliBlastfuseAi.ExplosiveCharge));
                Assert.IsType<ZulFarrakInstance>(player.Map.FindUpdater<InstanceData>())
                    .SetData(ZulFarrakInstance.TypePyramid, EncounterState.Done);
                return weegli.Guid;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Zfbound")!.VisibleObjects.Contains(weegliGuid),
                "Weegli visible").WaitAsync(TimeSpan.FromSeconds(15));
            (bool interactable, int options) = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Zfbound")!;
                NpcInfo? info = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services
                    .InteractableNpc(player, weegliGuid, NpcFlags.None);
                var ai = Assert.IsType<WeegliBlastfuseAi>(Assert.Single(player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures,
                    c => c.Entry == ZulFarrakInstance.Weegli).AI);
                return (info is not null, info is null ? -1 : ai.Hello(player, info)!.Items.Count);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(interactable);
            Assert.Equal(1, options);
            byte[] guid = BitConverter.GetBytes(weegliGuid.Value);
            await client.SendAsync(WorldOpcode.CmsgGossipHello, guid);
            var menu = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage)
                .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(weegliGuid.Value, menu.ReadUInt64());
            menu.ReadUInt32();
            Assert.Equal(1u, menu.ReadUInt32());
            await client.SendAsync(WorldOpcode.CmsgGossipSelectOption, [.. guid, .. BitConverter.GetBytes(0u)]);
            await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete).WaitAsync(TimeSpan.FromSeconds(10));
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Zfbound")!;
                var map = player.Map!;
                var ai = Assert.IsType<WeegliBlastfuseAi>(Assert.Single(map.FindUpdater<CreatureMapSystem>()!.Creatures,
                    c => c.Entry == ZulFarrakInstance.Weegli).AI);
                // The packet path starts the run. Controlled movement callbacks isolate the charge and door stages
                // from travel time and pathing, which still need an original-client run.
                ai.OnMovementInform(MovementGeneratorType.Point, 0);
                var objects = map.FindUpdater<GameObjectMapSystem>()!;
                GameObject charge = Assert.Single(objects.GameObjects,
                    go => go.Entry == WeegliBlastfuseAi.ExplosiveCharge && go.Spawn is null);
                GameObject door = Assert.Single(objects.GameObjects, go => go.Entry == ZulFarrakInstance.EndDoor);
                Assert.Equal(GameObjectState.Ready, door.State);
                ai.OnMovementInform(MovementGeneratorType.Point, 1);
                Assert.Equal(13259u, charge.SpellId);
                Assert.Equal(GameObjectState.Active, door.State);
                Assert.Equal(EncounterState.Done, Assert.IsType<ZulFarrakInstance>(map.FindUpdater<InstanceData>())
                    .GetData(ZulFarrakInstance.TypeEndDoor));
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("arcane-zf-runtime-", StringComparison.Ordinal))
                throw new InvalidOperationException("Zul'Farrak runtime cleanup path is outside its temp root");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}

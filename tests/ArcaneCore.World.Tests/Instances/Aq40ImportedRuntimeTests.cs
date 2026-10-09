using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
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
    public async Task ImportedBossAisAttach_FankrissWebTeleports_AndViscidusGlobUsesDatabasePosition()
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
            string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
            await using var host = WorldTestHost.Start(configure: options =>
            {
                if (terrain is not { Length: > 0 }) return;
                Assert.True(Directory.Exists(Path.Combine(terrain, "maps")), "terrain maps directory is missing");
                options.Maps.DataDirectory = terrain;
            }, configureServices: services =>
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
                if (terrain is { Length: > 0 })
                    Assert.True(map.Collision.GetHeight(player.X, player.Y, player.Z) > TerrainTile.InvalidHeight,
                        "AQ40 extracted terrain has no floor beneath Fankriss");
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

            await MoveNearAsync(host, client, teleports, -8000.18f, 928.60f, -51.90f);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15299), "Viscidus grid load");
            await host.OnWorldAsync(() =>
            {
                var map = host.World.FindOnlinePlayer("Aqsmoke")!.Map!;
                var creatures = map.FindUpdater<CreatureMapSystem>()!;
                Creature viscidus = Assert.Single(creatures.Creatures, c => c.Entry == 15299);
                var viscidusAi = Assert.IsType<ViscidusAI>(viscidus.AI);
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.True(spells.HasAura(viscidus, 25994));
                Assert.True(spells.HasAura(viscidus, 25926));
                Creature trigger = Assert.IsType<Creature>(creatures.SummonAt(viscidus, 15922,
                    viscidus.X, viscidus.Y, viscidus.Z, 0, null, 180_000));
                Assert.IsType<ViscidusToxinTriggerAI>(trigger.AI).OnUpdate(3000);
                Assert.True(spells.HasAura(trigger, 26575));
                Assert.Equal(531u, spells.Store.GetTargetPosition(25865)?.MapId);
                Assert.Contains(spells.Store.Get(25865)!.Effects, e => e.Effect == SpellEffectName.SummonWild
                    && (e.TargetA == SpellImplicitTarget.LocationDatabase || e.TargetB == SpellImplicitTarget.LocationDatabase));
                Assert.Equal(SpellCastResult.CastOk,
                    spells.CastSpell(viscidus, 25865, SpellCastTargets.ForSelf(), triggered: true));
                Creature glob = Assert.Single(creatures.Creatures, c => c.Entry == 15667);
                Assert.True(MathF.Abs(glob.X - (-8039.99f)) < 1f && MathF.Abs(glob.Y - 918.23f) < 1f,
                    $"glob spawned at {glob.X}, {glob.Y} instead of its database target");

                Player wandUser = host.World.FindOnlinePlayer("Aqsmoke")!;
                SpellInfo shoot = Assert.IsType<SpellInfo>(spells.Store.Get(5019));
                Assert.Equal(SpellSchool.Normal, shoot.School);
                wandUser.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
                Assert.Equal(InventoryResult.Ok, wandUser.Inventory.AddItem(7514, 1, out Item? wand));
                Assert.NotNull(wand);
                Assert.Equal((uint)SpellSchool.Frost, wand.Template.Damages[0].School);
                wandUser.Inventory.RemoveItem(wand.BagSlot, wand.Slot);
                wandUser.Inventory.EquipItem(InventorySlots.Ranged, wand);
                for (int i = 0; i < 200; i++) viscidusAi.OnSpellHit(wandUser, shoot);
                Assert.True(spells.HasAura(viscidus, 25937));
                spells.RemoveAuras(viscidus, 25937);
                wandUser.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);

                SpellInfo frost = Assert.IsType<SpellInfo>(spells.Store.Get(116));
                Assert.Equal(SpellSchool.Frost, frost.School);
                for (int i = 0; i < 200; i++) viscidusAi.OnSpellHit(host.World.FindOnlinePlayer("Aqsmoke")!, frost);
                Assert.True(spells.HasAura(viscidus, 25937));
                spells.RemoveAuras(viscidus, 25937);
                for (int i = 0; i < 199; i++) viscidusAi.OnSpellHit(host.World.FindOnlinePlayer("Aqsmoke")!, frost);
                Assert.False(spells.HasAura(viscidus, 25937));
                viscidusAi.OnSpellHit(host.World.FindOnlinePlayer("Aqsmoke")!, frost);
                Assert.True(spells.HasAura(viscidus, 25937));
                for (int i = 0; i < 150; i++)
                    viscidusAi.OnMeleeHitReceived(new ArcaneCore.Game.Combat.MeleeDamageInfo
                    {
                        Attacker = host.World.FindOnlinePlayer("Aqsmoke")!, Target = viscidus,
                        Outcome = ArcaneCore.Game.Combat.MeleeHitOutcome.Normal,
                    });
                Assert.False(spells.HasAura(viscidus, 25937));
                Assert.Equal(21, creatures.Creatures.Count(c => c.Entry == 15667));
                foreach (uint spellId in Enumerable.Range(25865, 20).Select(id => (uint)id))
                    Assert.Contains(creatures.Creatures, c => c.Entry == 15667
                        && c.GetUInt32(UpdateFields.UnitCreatedBySpell) == spellId);
            });

            await MoveNearAsync(host, client, teleports, -9023.67f, 1176.24f, -104.23f);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15275)
                && host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                    .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15276), "Twin Emperors grid load");
            uint twinNilashBeforeHeal = 0, twinLorBeforeHeal = 0;
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var map = player.Map!;
                var creatures = map.FindUpdater<CreatureMapSystem>()!;
                Creature nilash = Assert.Single(creatures.Creatures, c => c.Entry == 15275);
                Creature lor = Assert.Single(creatures.Creatures, c => c.Entry == 15276);
                var nilashAi = Assert.IsType<VeknilashAI>(nilash.AI);
                var lorAi = Assert.IsType<VeklorAI>(lor.AI);
                Assert.Contains(creatures.Creatures, c => c.Entry is 15316 or 15317 && c.AI is TwinBugAI);
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.True(ImmunityRules.IsImmuneToDamage(spells, nilash,
                    SpellSchoolMasks.Of(SpellSchool.Frost), spells.Store.Get(116)));
                Assert.True(ImmunityRules.IsImmuneToDamage(spells, lor,
                    SpellSchoolMasks.Of(SpellSchool.Normal), null));

                Assert.True(nilashAi.AttackStart(player) || ReferenceEquals(nilash.Combat.Victim, player));
                Assert.Equal(EncounterState.InProgress,
                    ((TempleOfAhnQirajInstance)map.FindUpdater<InstanceData>()!).GetData(TempleOfAhnQirajInstance.Twins));
                Assert.Contains(creatures.Creatures, c => c.Entry == 15277 && ReferenceEquals(c.Combat.Victim, player));
                uint beforeNilash = nilash.Health, beforeLor = lor.Health;
                map.Combat.DealDamage(player, nilash, 1000);
                Assert.Equal(beforeNilash - 1000, nilash.Health);
                Assert.Equal(beforeLor - (uint)(1000ul * lor.MaxHealth / nilash.MaxHealth), lor.Health);
                nilashAi.OnUpdate(15_001);
                Assert.Contains(creatures.Creatures, c => c.Entry is 15316 or 15317 && spells.HasAura(c, 802));
                Assert.True(spells.HasAura(nilash, 18943));
                (float nilashX, float lorX) = (nilash.X, lor.X);
                lorAi.OnUpdate(40_001);
                Assert.InRange(nilash.X, lorX - 1f, lorX + 1f);
                Assert.InRange(lor.X, nilashX - 1f, nilashX + 1f);
                Assert.Contains(creatures.Creatures, c => c.Entry is 15316 or 15317 && spells.HasAura(c, 804));
                lorAi.OnUpdate(2000);
                nilashAi.OnUpdate(2000);
                creatures.NearTeleport(lor, nilash.X + 8f, nilash.Y, nilash.Z, 0);
                nilash.Health -= 100_000;
                lor.Health -= 100_000;
                (twinNilashBeforeHeal, twinLorBeforeHeal) = (nilash.Health, lor.Health);
                lorAi.OnUpdate(1600);
            });
            await host.WaitForWorldAsync(() =>
            {
                var creatures = host.World.FindOnlinePlayer("Aqsmoke")!.Map!.FindUpdater<CreatureMapSystem>()!;
                return creatures.Creatures.Single(c => c.Entry == 15275).Health > twinNilashBeforeHeal
                    && creatures.Creatures.Single(c => c.Entry == 15276).Health > twinLorBeforeHeal;
            }, "paired Heal Brother completion");
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var map = player.Map!;
                var creatures = map.FindUpdater<CreatureMapSystem>()!;
                Creature nilash = Assert.Single(creatures.Creatures, c => c.Entry == 15275);
                Creature lor = Assert.Single(creatures.Creatures, c => c.Entry == 15276);
                map.Combat.Kill(player, nilash);
                Assert.False(lor.IsAlive);
                var raid = Assert.IsType<TempleOfAhnQirajInstance>(map.FindUpdater<InstanceData>());
                Assert.Equal(EncounterState.Done, raid.GetData(TempleOfAhnQirajInstance.Twins));
                Assert.True(raid.HasCompletedEncounter(715));
            });

            await MoveNearAsync(host, client, teleports, -9173.58f, 2103.85f, -64.79f);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15517), "Ouro spawn from imported spawner");
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var map = player.Map!;
                var creatures = map.FindUpdater<CreatureMapSystem>()!;
                Creature ouro = Assert.Single(creatures.Creatures, c => c.Entry == 15517);
                var ai = Assert.IsType<OuroAI>(ouro.AI);
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                var objects = map.FindUpdater<GameObjectMapSystem>()!;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(ouro, 26133, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Contains(objects.GameObjects, go => go.Entry == 180795 && go.IsSpawned);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(ouro, 26594, SpellCastTargets.ForSelf(), triggered: true));
                Assert.DoesNotContain(objects.GameObjects, go => go.Entry == 180795 && go.IsSpawned);
                Assert.True(ai.AttackStart(player) || ReferenceEquals(ouro.Combat.Victim, player));
                ai.OnUpdate(90_001);
                Assert.True((ouro.UnitFlags & UnitFlags.NotSelectable) != 0);
                Assert.Equal(5, creatures.Creatures.Count(c => c.Entry == 15712));
                Assert.Contains(creatures.Creatures, c => c.Entry == 15717);
                ai.OnUpdate(30_001);
                Assert.True((ouro.UnitFlags & UnitFlags.NotSelectable) == 0);
                Assert.DoesNotContain(creatures.Creatures, c => c.Entry == 15712);
                ouro.Health = ouro.MaxHealth / 5 - 1;
                ai.OnUpdate(1);
                Assert.True(spells.HasAura(ouro, 26615));
                ai.OnUpdate(10_001);
                Creature mound = Assert.Single(creatures.Creatures, c => c.Entry == 15712);
                Assert.IsType<OuroMoundAI>(mound.AI).OnUpdate(30_001);
                Assert.Equal(3, creatures.Creatures.Count(c => c.Entry == 15718));
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(ouro, 26133, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Contains(objects.GameObjects, go => go.Entry == 180795 && go.IsSpawned);
                map.Combat.Kill(player, ouro);
                Assert.DoesNotContain(objects.GameObjects, go => go.Entry == 180795 && go.IsSpawned);
                var raid = Assert.IsType<TempleOfAhnQirajInstance>(map.FindUpdater<InstanceData>());
                Assert.Equal(EncounterState.Done, raid.GetData(TempleOfAhnQirajInstance.Ouro));
                Assert.True(raid.HasCompletedEncounter(716));
            });

            await MoveNearAsync(host, client, teleports, -8578.65f, 1985.85f, 100.30f);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15727)
                && host.World.FindOnlinePlayer("Aqsmoke")!.Map!
                    .FindUpdater<CreatureMapSystem>()!.Creatures.Any(c => c.Entry == 15589), "C'Thun Eye and body grid load");
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var map = player.Map!;
                var creatures = map.FindUpdater<CreatureMapSystem>()!;
                Creature body = Assert.Single(creatures.Creatures, c => c.Entry == 15727);
                Creature eye = Assert.Single(creatures.Creatures, c => c.Entry == 15589);
                var bodyAi = Assert.IsType<CthunBodyAI>(body.AI);
                var eyeAi = Assert.IsType<CthunEyeAI>(eye.AI);
                var raid = Assert.IsType<TempleOfAhnQirajInstance>(map.FindUpdater<InstanceData>());
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.True((body.UnitFlags & UnitFlags.NotSelectable) != 0);
                Assert.Equal(body.MaxHealth, body.InvincibilityHpThreshold);
                bodyAi.AttackStart(player);
                Assert.Equal(EncounterState.InProgress, raid.GetData(TempleOfAhnQirajInstance.CThun));
                eyeAi.OnUpdate(45_001);
                Assert.True(spells.HasAura(eye, 26009) || spells.HasAura(eye, 26136));
                eyeAi.OnUpdate(3000);
                eyeAi.OnUpdate(1000);
                map.Combat.Kill(player, eye);
                bodyAi.OnUpdate(4000);
                Assert.Equal(2, creatures.Creatures.Count(c => c.Entry == 15802 && c.IsAlive));
                Assert.True(spells.HasAura(body, 26156));
                bodyAi.OnUpdate(8000);
                Assert.True((body.UnitFlags & UnitFlags.Spawning) == 0);
                foreach (Creature flesh in creatures.Creatures.Where(c => c.Entry == 15802 && c.IsAlive).ToArray())
                    map.Combat.Kill(player, flesh);
                Assert.Equal(0u, body.InvincibilityHpThreshold);
                Assert.True(spells.HasAura(body, 26235));
                bodyAi.OnUpdate(45_001);
                Assert.Equal(2, creatures.Creatures.Count(c => c.Entry == 15802 && c.IsAlive));
                Assert.True(spells.HasAura(body, 26156));
                player.InvincibilityHpThreshold = 1; // keep this single-client smoke alive while the raid is in the stomach
                bodyAi.OnUpdate(14_751);
                bodyAi.OnUpdate(3251);
                Assert.True(raid.IsInCthunStomach(player));
                Assert.Equal(TeleportStage.Near, teleports.StageOf(player));
            });
            await AcknowledgeNearAsync(host, client, teleports);
            await host.WaitForWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                return player.Z < 0 && host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(player, 26476);
            }, "C'Thun stomach Digestive Acid after near teleport");
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var raid = Assert.IsType<TempleOfAhnQirajInstance>(player.Map!.FindUpdater<InstanceData>());
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.InRange(player.Z, -97f, -95f);
                Assert.True(raid.IsInCthunStomach(player));
                Assert.True(spells.HasAura(player, 26476));
                raid.OnAreaTrigger(player, 4034);
                Assert.Equal(TeleportStage.Near, teleports.StageOf(player));
            });
            await AcknowledgeNearAsync(host, client, teleports);
            await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Aqsmoke")!;
                var map = player.Map!;
                var raid = Assert.IsType<TempleOfAhnQirajInstance>(map.FindUpdater<InstanceData>());
                var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.InRange(player.Z, 99f, 101f);
                Assert.False(raid.IsInCthunStomach(player));
                Assert.False(spells.HasAura(player, 26476));
                player.InvincibilityHpThreshold = 0;
                Creature body = Assert.Single(map.FindUpdater<CreatureMapSystem>()!.Creatures, c => c.Entry == 15727);
                map.Combat.Kill(player, body);
                Assert.Equal(EncounterState.Done, raid.GetData(TempleOfAhnQirajInstance.CThun));
            });
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

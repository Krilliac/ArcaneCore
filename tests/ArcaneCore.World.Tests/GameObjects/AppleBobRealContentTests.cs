using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Skills;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The Hallow's End Apple Bob against the imported world (a copy of <c>ARCANECORE_TEST_WORLD_DB</c>; the fixture is never written) and the
/// client DBCs (<c>ARCANECORE_TEST_DBC_DIR</c>), configured as the live realm is (skills Retail): the real spawn 39851 (entry 180523, map 1,
/// listed in game_event_gameobject for event 12), the real lock 93, loot 17378 and spell 6477 rows, with the real spell data going through
/// the application rules of the cast. Event 12 is started the way <c>.event start</c> does, a night elf rogue (an Alliance race, so the Darnassus guards leave it alone; whose create spells include 6477)
/// teleports to the apple and sends CMSG_CAST_SPELL of 6477 with target mask 0x0800, as the 5875 client does
/// (D:/WoW-RE-Research/protocol/gameobject-interaction.md). Skipped, with a message saying so, unless both variables are set.
/// </summary>
[Collection("Real battleground raid content")]
public sealed class AppleBobRealContentTests(ITestOutputHelper output)
{
    private const uint BobSpawn = 39851;
    private const ushort HallowsEndEvent = 12;

    private static WorldTestHost Start(string worldCopy)
    {
        string dbc = Environment.GetEnvironmentVariable(CactusRealContentFactAttribute.DbcVariable)!;
        string factionDbc = Path.Combine(dbc, "FactionTemplate.dbc");
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={worldCopy};Pooling=False",
            ["Quests:FactionTemplateDbcPath"] = factionDbc,
            ["Creatures:FactionTemplateDbcPath"] = factionDbc,
            ["ClientData:DbcDirectory"] = dbc,
            ["Skills:Mode"] = "Retail",
            ["Skills:SkillLineDbcPath"] = Path.Combine(dbc, "SkillLine.dbc"),
            ["Skills:SkillRaceClassInfoDbcPath"] = Path.Combine(dbc, "SkillRaceClassInfo.dbc"),
            ["Skills:SkillTiersDbcPath"] = Path.Combine(dbc, "SkillTiers.dbc"),
            ["Skills:SkillLineAbilityDbcPath"] = Path.Combine(dbc, "SkillLineAbility.dbc"),
        }).Build();
        return WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddWorldDatabase(config);
            services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        });
    }

    /// <summary>SMSG_CAST_RESULT: u32 spell, u8 status (0 success, 2 failure), and for a failure u8 SpellCastResult reason.</summary>
    private static string DescribeCastResult(byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint spell = reader.ReadUInt32();
        var status = (SpellCastResultStatus)reader.ReadByte();
        string reason = status == SpellCastResultStatus.Failure ? ((SpellCastResult)reader.ReadByte()).ToString() : "none";
        return $"SMSG_CAST_RESULT spell {spell} status {status} reason {reason} ({Convert.ToHexString(payload)})";
    }

    [CactusRealContentFact]
    public async Task WithHallowsEndRunning_CastingOpeningOnTheRealAppleBob_ShowsTheLootWindowWithFiveApples()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-applebob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(CactusRealContentFactAttribute.WorldVariable)!, copy);
        try
        {
            // The spawn's own position, read from the copy (read-only) so the test does not restate it.
            float x, y, z;
            uint entry;
            using (var connection = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT Entry, MapId, X, Y, Z FROM gameobject_spawn WHERE Guid = $guid";
                command.Parameters.AddWithValue("$guid", BobSpawn);
                using SqliteDataReader reader = command.ExecuteReader();
                Assert.True(reader.Read(), "spawn 39851 is in the world database");
                entry = (uint)reader.GetInt64(0);
                Assert.Equal(1L, reader.GetInt64(1));
                (x, y, z) = ((float)reader.GetDouble(2), (float)reader.GetDouble(3), (float)reader.GetDouble(4));
            }

            await using WorldTestHost host = Start(copy);
            await using WorldTestClient client = await host.ConnectAsync();
            byte[] key = await host.AddAccountAsync("APPLEBOB");
            await client.AuthenticateAsync("APPLEBOB", key);
            await client.CreateCharacterAsync("Bobberelf", race: 4, cls: 4);
            var account = (await host.Accounts.FindByUsernameAsync("APPLEBOB"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            await client.LoginAsync((ulong)character.Id).WaitAsync(TimeSpan.FromSeconds(20));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Bobberelf") is not null, "the night elf to be in the world");

            (bool knowsOpening, bool skills, bool started) = await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Bobberelf")!;
                bool known = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Spellbook?.HasSpell(p, AppleBobOpeningWorldTests.Opening) == true;
                bool active = host.WorldServices.GetRequiredService<SkillsFeature>().IsActive;
                bool ok = host.WorldServices.GetRequiredService<GameEventFeature>().Service?.StartEvent(HallowsEndEvent, overwrite: true) == true;
                return (known, active, ok);
            });
            Assert.True(skills, "the skills feature is active, as on the live realm");
            Assert.True(knowsOpening, "the night elf rogue's create spells include 6477 Opening");
            Assert.True(started, "game event 12 (Hallow's End) exists in the world database and started");
            Assert.True(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<GameEventFeature>().IsActiveEvent(HallowsEndEvent)));

            // Walk the player to the apple (a near teleport on map 1) and acknowledge it, so the apple's grid loads with the event active.
            ulong playerGuid = await host.PlayerStateAsync("Bobberelf", p => p.Guid.Value);
            await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Bobberelf")!;
                return host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(p, 1, x + 1.5f, y, z, p.Orientation);
            });
            await client.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
            var ack = new PacketWriter();
            ack.WriteUInt64(playerGuid);
            ack.WriteUInt32(0);
            ack.WriteUInt32(0);
            await client.SendAsync(WorldOpcode.MsgMoveTeleportAck, ack.ToArray());

            ulong bob = ObjectGuid.WithEntry(HighGuid.GameObject, entry, BobSpawn).Value;
            await host.WaitForWorldAsync(() =>
                host.World.FindOnlinePlayer("Bobberelf")!.Map?.FindUpdater<GameObjectMapSystem>()?.Find(new ObjectGuid(bob)) is { IsSpawned: true },
                "the event's Apple Bob to be spawned beside the player");
            await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, bob);

            await client.SendAsync(WorldOpcode.CmsgCastSpell, AppleBobOpeningWorldTests.CastAt(AppleBobOpeningWorldTests.Opening, bob));

            var seen = new List<WorldOpcode>();
            byte[]? lootResponse = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (lootResponse is null && DateTime.UtcNow < deadline)
            {
                WorldOpcode op;
                byte[] payload;
                try
                {
                    (op, payload) = await client.ReadAsync(TimeSpan.FromSeconds(8));
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                seen.Add(op);
                if (op == WorldOpcode.SmsgCastResult)
                {
                    output.WriteLine(DescribeCastResult(payload));
                }

                if (op == WorldOpcode.SmsgLootResponse)
                {
                    lootResponse = payload;
                }
            }

            output.WriteLine(string.Join(", ", seen.Where(o => o is not (WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject
                or WorldOpcode.SmsgMonsterMove or WorldOpcode.SmsgSplineMoveSetWalkMode))));
            Assert.Contains(WorldOpcode.SmsgSpellStart, seen);
            Assert.NotNull(lootResponse);
            (ulong source, List<(uint ItemId, uint Count)> items) = AppleBobOpeningWorldTests.ReadLootResponse(lootResponse);
            Assert.Equal(bob, source);
            Assert.Equal((AppleBobOpeningWorldTests.BobItem, AppleBobOpeningWorldTests.BobCount), Assert.Single(items));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

using ArcaneCore.Data;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.GameObjects;

internal sealed class CactusRealContentFactAttribute : FactAttribute
{
    public const string WorldVariable = "ARCANECORE_TEST_WORLD_DB";
    public const string DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    public CactusRealContentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(WorldVariable) is not { Length: > 0 } world || !File.Exists(world)
            || Environment.GetEnvironmentVariable(DbcVariable) is not { Length: > 0 } dbc
            || !File.Exists(Path.Combine(dbc, "FactionTemplate.dbc")) || !File.Exists(Path.Combine(dbc, "SkillLineAbility.dbc")))
        {
            Skip = $"{WorldVariable} (an imported SQLite world.db) and {DbcVariable} (FactionTemplate.dbc and the Skill*.dbc set) are required; "
                + "the real Cactus Apple open did NOT run.";
        }

        Skip ??= RealContent.WorldFixtureSchema.SkipReasonForEnvironment();
    }
}

/// <summary>
/// Quest 4402 "Galgar's Cactus Apple Surprise" against the imported world (a copy of <c>ARCANECORE_TEST_WORLD_DB</c>; the fixture is never
/// written), configured as the live realm is (skills Retail from the client DBCs): an orc rogue made by the real character creation, the real
/// Cactus Apple spawn nearest the Valley of Trials start, the real lock 43, loot 11500 and spell 6478 rows. The client side is what the 5875
/// binary does (WoW-RE-Research protocol/gameobject-interaction.md): the apple is clickable only once GAMEOBJECT_DYN_FLAGS bit 0x1 reached it
/// (FLAGS 0x4 INTERACT_COND), and the click then sends CMSG_CAST_SPELL of 6478 with target mask 0x0800 and the object's GUID, never
/// CMSG_GAMEOBJ_USE.
/// </summary>
[Collection("Real battleground raid content")]
public sealed class CactusAppleRealContentTests(ITestOutputHelper output)
{
    private const uint QuestId = 4402;
    private const uint Apple = 11583;
    private const uint CactusEntry = 171938;
    private const uint Opening = 6478;
    private const uint DynActivate = 0x1;

    private static WorldTestHost Start(string worldCopy, MemoryQuestStore questStore)
    {
        string dbc = Environment.GetEnvironmentVariable(CactusRealContentFactAttribute.DbcVariable)!;
        string factionDbc = Path.Combine(dbc, "FactionTemplate.dbc");
        string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={worldCopy};Pooling=False",
            ["Quests:FactionTemplateDbcPath"] = factionDbc,
            ["Creatures:FactionTemplateDbcPath"] = factionDbc,
            // As the live realm: skills Retail from the client DBCs, so the skills feature is active and owns the open-lock effect.
            ["ClientData:DbcDirectory"] = dbc,
            ["Skills:Mode"] = "Retail",
            ["Skills:SkillLineDbcPath"] = Path.Combine(dbc, "SkillLine.dbc"),
            ["Skills:SkillRaceClassInfoDbcPath"] = Path.Combine(dbc, "SkillRaceClassInfo.dbc"),
            ["Skills:SkillTiersDbcPath"] = Path.Combine(dbc, "SkillTiers.dbc"),
            ["Skills:SkillLineAbilityDbcPath"] = Path.Combine(dbc, "SkillLineAbility.dbc"),
        }).Build();
        return WorldTestHost.Start(configure: options =>
        {
            if (terrain is { Length: > 0 } && Directory.Exists(Path.Combine(terrain, "maps")))
            {
                options.Maps.DataDirectory = terrain;
            }
        }, configureServices: services =>
        {
            // The imported world's create info (the orc rogue's Valley of Trials start) replaces the test store.
            services.AddSingleton(config);
            services.AddWorldDatabase(config);
            services.AddSingleton<ICharacterQuestStore>(questStore);
            services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        });
    }

    /// <summary>
    /// The last GAMEOBJECT_DYN_FLAGS value this client was sent for <paramref name="guid"/> (create or values block), reading until it has bit
    /// <see cref="DynActivate"/> or nothing more arrives for a few seconds; null when the object never reached the client.
    /// </summary>
    private static async Task<uint?> ReceivedDynFlagsAsync(WorldTestClient client, ulong guid, IEnumerable<(WorldOpcode Opcode, byte[] Payload)> already)
    {
        uint? last = null;
        void Scan(WorldOpcode op, byte[] payload)
        {
            byte[]? body = op switch
            {
                WorldOpcode.SmsgUpdateObject => payload,
                WorldOpcode.SmsgCompressedUpdateObject => WorldTestClient.Inflate(payload),
                _ => null,
            };
            if (body is null)
            {
                return;
            }

            foreach (UpdateBlock block in UpdateBlockReader.Parse(body))
            {
                if (block.Guid != guid)
                {
                    continue;
                }

                if (block.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2)
                {
                    last = block.Values.GetValueOrDefault(UpdateFields.GameobjectDynFlags);
                }
                else if (block.Values.TryGetValue(UpdateFields.GameobjectDynFlags, out uint value))
                {
                    last = value;
                }
            }
        }

        foreach ((WorldOpcode op, byte[] payload) in already)
        {
            Scan(op, payload);
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while ((last & DynActivate) is null or 0 && DateTime.UtcNow < deadline)
        {
            try
            {
                (WorldOpcode op, byte[] payload) = await client.ReadAsync(TimeSpan.FromSeconds(4));
                Scan(op, payload);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return last;
    }

    /// <summary>The quest is in the persisted log when the rogue logs in, as for the reported character (quest 4402 status 3).</summary>
    [CactusRealContentFact]
    public Task WithTheQuestInTheLogAtLogin_TheAppleIsClickable_OpeningOpensIt_AndTheAppleIsLooted() => RunAsync(questAtLogin: true);

    /// <summary>The quest is accepted after the apple was already in view.</summary>
    [CactusRealContentFact]
    public Task WithTheQuestAcceptedNearTheApple_TheAppleIsClickable_OpeningOpensIt_AndTheAppleIsLooted() => RunAsync(questAtLogin: false);

    private async Task RunAsync(bool questAtLogin)
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-cactus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(CactusRealContentFactAttribute.WorldVariable)!, copy);
        try
        {
            var questStore = new MemoryQuestStore();
            await using WorldTestHost host = Start(copy, questStore);
            await using WorldTestClient client = await host.ConnectAsync();
            byte[] key = await host.AddAccountAsync("CACTUS");
            await client.AuthenticateAsync("CACTUS", key);
            await client.CreateCharacterAsync("Cactusorc", race: 2, cls: 4);
            var account = (await host.Accounts.FindByUsernameAsync("CACTUS"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            if (questAtLogin)
            {
                questStore.Seed(new CharacterQuestStatus(character.Id, QuestId, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            }

            await client.LoginAsync((ulong)character.Id).WaitAsync(TimeSpan.FromSeconds(20));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Cactusorc") is { } p
                && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true }, "the quest journal to load");

            // The nearest Cactus Apple (about 72 yd from the start, in view).
            (ulong cactus, bool knowsOpening, bool skills, string where) = await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Cactusorc")!;
                GameObject go = p.Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects
                    .Where(o => o.Entry == CactusEntry && o.IsSpawned).MinBy(o => o.DistanceTo(p))
                    ?? throw new InvalidOperationException("no Cactus Apple spawned near the orc start");
                return (go.Guid.Value, host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Spellbook?.HasSpell(p, Opening) == true,
                    host.WorldServices.GetRequiredService<SkillsFeature>().IsActive,
                    $"player {p.X:0.0},{p.Y:0.0} cactus {go.Guid} {go.X:0.0},{go.Y:0.0},{go.Z:0.0} dist {go.DistanceTo(p):0.0}");
            });
            output.WriteLine(where);
            Assert.True(skills, "the skills feature is active, as on the live realm");
            Assert.True(knowsOpening, "the orc rogue's create spells include 6478 Opening");

            if (!questAtLogin)
            {
                // Let the apple reach the client without the quest first, then accept it.
                Assert.Equal(0u, (await ReceivedDynFlagsAsync(client, cactus, client.LastLoginPackets)).GetValueOrDefault() & DynActivate);
                GmQuestAddResult added = await host.OnWorldAsync(() =>
                {
                    Player p = host.World.FindOnlinePlayer("Cactusorc")!;
                    QuestNpcServices quests = ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services;
                    return quests.GmAddQuest(p, quests.Quests.Get(QuestId)!);
                });
                Assert.Equal(GmQuestAddResult.Added, added);
            }

            // The client gate: FLAGS 0x4 makes the apple inert until DYN_FLAGS 0x1 arrives for this player.
            uint? dyn = await ReceivedDynFlagsAsync(client, cactus, questAtLogin ? client.LastLoginPackets : []);
            Assert.True(dyn is { } d && (d & DynActivate) != 0, $"the client was sent DYN_FLAGS {dyn?.ToString("X") ?? "never"} for the apple");

            // The rogue walks up to it (server side) and right-clicks: CMSG_CAST_SPELL 6478 at the object.
            await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Cactusorc")!;
                GameObject go = p.Map!.FindUpdater<GameObjectMapSystem>()!.Find(new ObjectGuid(cactus))!;
                p.Relocate(go.X + 1.5f, go.Y, go.Z, p.Orientation, host.World.NowMs);
            });
            var cast = new PacketWriter(16);
            cast.WriteUInt32(Opening);
            new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = new ObjectGuid(cactus) }.Write(cast);
            await client.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray());

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
                    output.WriteLine($"SMSG_CAST_RESULT {Convert.ToHexString(payload)}");
                }

                if (op == WorldOpcode.SmsgLootResponse)
                {
                    lootResponse = payload;
                }
            }

            output.WriteLine(string.Join(", ", seen.Where(o => o is not (WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject
                or WorldOpcode.SmsgMonsterMove or WorldOpcode.SmsgSpellGo or WorldOpcode.SmsgSplineMoveSetWalkMode))));
            Assert.Contains(WorldOpcode.SmsgSpellStart, seen);
            Assert.NotNull(lootResponse);
            var loot = new PacketReader(lootResponse);
            Assert.Equal(cactus, loot.ReadUInt64());
            loot.ReadByte();
            loot.ReadUInt32();
            Assert.True(loot.ReadByte() >= 1, "the loot window lists the apple");
            loot.ReadByte();
            Assert.Equal(Apple, loot.ReadUInt32());

            await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
            await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
            await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(cactus));
            await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
            (uint inBags, uint counter) = await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Cactusorc")!;
                QuestNpcServices quests = ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services;
                return (p.Inventory.GetItemCount(Apple), quests.StateOf(p)!.Quests.Get(QuestId)!.ItemCount[0]);
            });
            Assert.Equal((1u, 1u), (inBags, counter));
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

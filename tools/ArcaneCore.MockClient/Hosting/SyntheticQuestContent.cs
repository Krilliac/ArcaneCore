using System.Data;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.MockClient.Hosting;

public enum SyntheticQuestProfile
{
    SelfTest,
    ManualClient,
}

/// <summary>Repository-authored fixture content. Profiles select reference IDs and nearby spawn spacing.</summary>
public static class SyntheticQuestContent
{
    public const uint JournalQuestId = 900001;
    public const uint NpcQuestId = 900002;
    public const uint RewardQuestId = 900003;
    public const uint NpcEntry = 900010;
    public const uint NpcSpawn = 900020;
    public const uint TargetEntry = 900030;
    public const uint FirstTargetSpawn = 900021;
    public const uint SecondTargetSpawn = 900022;
    public const uint FixedRewardItem = 900040;
    public const uint UnchosenRewardItem = 900041;
    public const uint ChosenRewardItem = 900042;
    public const uint RewardMoney = 1234;

    public static SyntheticQuestDefinition Definition { get; } = CreateDefinition(SyntheticQuestProfile.SelfTest);
    private static SyntheticQuestDefinition ManualDefinition { get; } = CreateDefinition(SyntheticQuestProfile.ManualClient);

    public static SyntheticQuestDefinition GetDefinition(SyntheticQuestProfile profile) => profile switch
    {
        SyntheticQuestProfile.SelfTest => Definition,
        SyntheticQuestProfile.ManualClient => ManualDefinition,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>
    /// Seed a newly initialized world database in one transaction. Baseline character-creation
    /// and schema tables are retained; existing world content and caller-owned state are refused.
    /// </summary>
    public static async Task SeedAsync(WorldDbContext db, SyntheticQuestProfile profile = SyntheticQuestProfile.SelfTest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        SyntheticQuestDefinition definition = GetDefinition(profile);
        if (db.ChangeTracker.Entries().Any() || db.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Synthetic quest seeding requires a dedicated context without caller state or a transaction.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            var baseline = new HashSet<string>(StringComparer.Ordinal)
            {
                // The tables WorldDbInitializer seeds on every world database, the Warden scans (world 49) included.
                "player_create_info", "race_info", "class_info", ArcaneCore.Data.World.Warden.WardenDataModule.Table,
                WorldDbContext.Schema.VersionTable,
            };
            var tables = db.Model.GetEntityTypes()
                .Select(entity => (Table: entity.GetTableName(), Schema: entity.GetSchema()))
                .Where(table => table.Table is not null && !baseline.Contains(table.Table))
                .Distinct().OrderBy(table => table.Table, StringComparer.Ordinal);
            foreach (var table in tables)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = $"SELECT 1 FROM {sql.DelimitIdentifier(table.Table!, table.Schema)} LIMIT 1";
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidOperationException($"Synthetic quest seeding requires empty world content; table '{table.Table}' already has rows.");
                }
            }

            db.Set<QuestTemplate>().AddRange(definition.Quests);
            foreach (SyntheticCreatureDefinition creature in definition.Creatures)
            {
                db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow
                {
                    Entry = creature.Entry, Name = creature.Name, Faction = creature.Faction,
                    NpcFlags = creature.NpcFlags, DisplayId1 = creature.DisplayId,
                    MinLevelHealth = creature.Health, MaxLevelHealth = creature.Health,
                    UnitClass = 1, Civilian = true,
                });
            }

            foreach (uint display in definition.Creatures.Select(creature => creature.DisplayId).Distinct())
            {
                db.Set<CreatureModelInfoRow>().Add(new CreatureModelInfoRow
                {
                    DisplayId = display, BoundingRadius = 0.5f, CombatReach = 1.5f,
                });
            }

            foreach (SyntheticSpawnDefinition spawn in definition.Spawns)
            {
                db.Set<CreatureSpawnRow>().Add(new CreatureSpawnRow
                {
                    Guid = spawn.Guid, Entry = spawn.Entry, MapId = spawn.MapId,
                    X = spawn.X, Y = spawn.Y, Z = spawn.Z,
                    SpawnTimeMinSeconds = spawn.RespawnSeconds, SpawnTimeMaxSeconds = spawn.RespawnSeconds,
                });
            }

            foreach (SyntheticQuestRelationDefinition relation in definition.Starters)
            {
                db.Set<CreatureQuestStarterRow>().Add(new CreatureQuestStarterRow { Id = relation.Entry, Quest = relation.Quest });
            }
            foreach (SyntheticQuestRelationDefinition relation in definition.Enders)
            {
                db.Set<CreatureQuestEnderRow>().Add(new CreatureQuestEnderRow { Id = relation.Entry, Quest = relation.Quest });
            }
            foreach (SyntheticItemDefinition item in definition.Items)
            {
                db.Set<ItemTemplateRow>().Add(new ItemTemplateRow
                {
                    Entry = item.Entry, Class = 15, Name = item.Name, DisplayId = item.DisplayId,
                    Quality = 1, AllowableClass = -1, AllowableRace = -1, Stackable = 20,
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception seedError)
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Synthetic quest seed and rollback failed; discard the context.", seedError, rollbackError);
            }
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static SyntheticQuestDefinition CreateDefinition(SyntheticQuestProfile profile)
    {
        bool manual = profile == SyntheticQuestProfile.ManualClient;
        return new SyntheticQuestDefinition(
        [
            new QuestTemplate
            {
                Entry = JournalQuestId, Method = 2, MinLevel = 1, QuestLevel = 1,
                Title = "Mock journal", Details = "synthetic lifecycle query", Objectives = "synthetic progress",
                ReqCreatureOrGOId1 = 900101, ReqCreatureOrGOCount1 = 2,
            },
            new QuestTemplate
            {
                Entry = NpcQuestId, Method = 2, MinLevel = 1, QuestLevel = 1,
                Title = "Mock NPC quest", Details = "synthetic NPC acceptance", Objectives = "defeat two synthetic targets",
                ReqCreatureOrGOId1 = 900102, ReqCreatureOrGOCount1 = 2,
            },
            new QuestTemplate
            {
                Entry = RewardQuestId, Method = 2, Type = 0, MinLevel = 1, QuestLevel = 1,
                Title = "Mock combat reward", Details = "synthetic combat and durable reward",
                Objectives = "defeat two live synthetic targets", OfferRewardText = "Choose one synthetic keepsake.",
                RequestItemsText = "Defeat both synthetic combat targets before returning.",
                ReqCreatureOrGOId1 = (int)TargetEntry, ReqCreatureOrGOCount1 = 2, RewOrReqMoney = (int)RewardMoney,
                RewItemId1 = FixedRewardItem, RewItemCount1 = 1,
                RewChoiceItemId1 = UnchosenRewardItem, RewChoiceItemCount1 = 1,
                RewChoiceItemId2 = ChosenRewardItem, RewChoiceItemCount2 = 1,
            },
        ],
        [
            new(NpcEntry, "Synthetic guide", manual ? 35u : 900011u, 2, manual ? 49u : 900012u, 10),
            new(TargetEntry, "Synthetic combat target", manual ? 14u : 900011u, 0, manual ? 49u : 900012u, 1),
        ],
        [
            new(NpcSpawn, NpcEntry, 0, manual ? -8949.95f + 2 : -8948.95f, -132.493f, 83.5312f, 120),
            new(FirstTargetSpawn, TargetEntry, 0, -8949.95f + (manual ? -4 : 0.5f), -132.493f, 83.5312f, 3600),
            new(SecondTargetSpawn, TargetEntry, 0, -8949.95f + (manual ? 4 : -0.5f), -132.493f, 83.5312f, 3600),
        ],
        [new(NpcEntry, NpcQuestId), new(NpcEntry, RewardQuestId)],
        [new(NpcEntry, RewardQuestId)],
        new[] { FixedRewardItem, UnchosenRewardItem, ChosenRewardItem }
            .Select(item => new SyntheticItemDefinition(item, $"Synthetic keepsake {item}", manual ? 6418u : item + 100)));
    }
}

/// <summary>Read-only collections contain immutable authored values, never tracked mutable EF rows.</summary>
public sealed class SyntheticQuestDefinition
{
    internal SyntheticQuestDefinition(IEnumerable<QuestTemplate> quests, IEnumerable<SyntheticCreatureDefinition> creatures,
        IEnumerable<SyntheticSpawnDefinition> spawns, IEnumerable<SyntheticQuestRelationDefinition> starters,
        IEnumerable<SyntheticQuestRelationDefinition> enders, IEnumerable<SyntheticItemDefinition> items)
    {
        Quests = Array.AsReadOnly(quests.ToArray());
        Creatures = Array.AsReadOnly(creatures.ToArray());
        Spawns = Array.AsReadOnly(spawns.ToArray());
        Starters = Array.AsReadOnly(starters.ToArray());
        Enders = Array.AsReadOnly(enders.ToArray());
        Items = Array.AsReadOnly(items.ToArray());
    }

    public IReadOnlyList<QuestTemplate> Quests { get; }
    public IReadOnlyList<SyntheticCreatureDefinition> Creatures { get; }
    public IReadOnlyList<SyntheticSpawnDefinition> Spawns { get; }
    public IReadOnlyList<SyntheticQuestRelationDefinition> Starters { get; }
    public IReadOnlyList<SyntheticQuestRelationDefinition> Enders { get; }
    public IReadOnlyList<SyntheticItemDefinition> Items { get; }
}

public sealed record SyntheticCreatureDefinition(uint Entry, string Name, uint Faction, uint NpcFlags, uint DisplayId, uint Health);
public sealed record SyntheticSpawnDefinition(uint Guid, uint Entry, uint MapId, float X, float Y, float Z, uint RespawnSeconds);
public sealed record SyntheticQuestRelationDefinition(uint Entry, uint Quest);
public sealed record SyntheticItemDefinition(uint Entry, string Name, uint DisplayId);

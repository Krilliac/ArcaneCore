using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// The loot tables of the world (vmangos <c>LootStore::LoadLootTable</c>, LootMgr.cpp:94-189, behind every
/// <c>.reload *_loot_template</c>, ServerCommands.cpp:1174-1254). The whole <see cref="LootContent"/> is read through
/// <see cref="ILootDataStore"/> off the world thread and the next immutable content is composed (still off the world thread) from
/// it and the live one; the commit swaps it into the loot module (<see cref="GameObjectLootFeature.ReplaceLootContent"/>), so
/// every map's loot service generates from it from then on. Loot that was already generated keeps what it rolled: vmangos
/// rolls when the corpse or chest is filled, not when it is opened.
/// <para>
/// Retail rules: <c>LoadLootTable</c> clears its store before it looks at the query result (LootMgr.cpp:100) and an empty table
/// leaves it empty (:184-188), so an empty table empties the rows, unless <c>HotReload:EmptyTables = KeepLoaded</c>.
/// </para>
/// </summary>
public abstract class LootReloadable(
    IServiceProvider services, string name, bool includedInAll, string noun,
    Func<LootContent, int> rows, Func<LootContent, LootContent, LootContent> compose) : IContentReloadable
{
    public string Name => name;

    public bool IncludedInAll => includedInAll;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        GameObjectLootFeature feature = services.GetRequiredService<GameObjectLootFeature>();
        LootContent fresh;
        using (IServiceScope scope = services.CreateScope())
        {
            ILootDataStore store = scope.ServiceProvider.GetService<ILootDataStore>()
                ?? throw new InvalidOperationException("no loot data store is registered");
            fresh = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        LootContent next = compose(feature.LootContent, fresh);
        return new Candidate(this, feature, next, rows(next), ReloadPolicy.KeepsEmptyTables(services));
    }

    private int RowsOf(LootContent content) => rows(content);

    private sealed class Candidate(LootReloadable owner, GameObjectLootFeature feature, LootContent content, int rows, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{rows} {NounOf(owner)}";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = owner.RowsOf(feature.LootContent);
            if (keepEmpty && rows == 0 && loaded > 0)
            {
                reason = $"{owner.Name} is empty and HotReload:EmptyTables is KeepLoaded, {loaded} {NounOf(owner)} stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            LootContent? previous = null;
            transaction.Step(owner.Name, () => previous = feature.ReplaceLootContent(content), () => feature.ReplaceLootContent(previous!));
        }

        private static string NounOf(LootReloadable owner) => owner.Noun;
    }

    private string Noun => noun;

    /// <summary>The live content with the rows of <paramref name="kind"/> taken from <paramref name="fresh"/>; everything else as it is live.</summary>
    protected static LootContent WithTable(LootContent live, LootContent fresh, LootTableKind kind)
        => new(
            live.Rows.Where(r => r.Kind != kind).Concat(fresh.Rows.Where(r => r.Kind == kind)),
            live.CreatureInfos, live.FishingBaseSkills, live.PickpocketLootIds);

    /// <summary>The live content with the fishing base levels taken from <paramref name="fresh"/>.</summary>
    protected static LootContent WithFishingBase(LootContent live, LootContent fresh)
        => new(live.Rows, live.CreatureInfos, fresh.FishingBaseSkills, live.PickpocketLootIds);
}

/// <summary>
/// <c>.reload all_loot</c> (ServerCommands.cpp:916-923 → <c>LoadLootTables</c>, LootMgr.h:431; Chat.cpp:801), which <c>reload all</c> calls
/// (:891). Replaces everything the loot module reads: every <c>*_loot_template</c> table, and with them the creature loot ids and gold,
/// the pickpocket loot ids and the fishing base levels, which the loot module reads together with its tables (in vmangos those
/// columns belong to <c>creature_template</c> and <c>skill_fishing_base_level</c>, so this reload is a superset of vmangos'; see
/// docs/areas/hot-reload.md). <c>skill_fishing_base_level</c> is therefore not in <c>reload all</c> on its own.
/// </summary>
public sealed class AllLootContentReloadable(IServiceProvider services)
    : LootReloadable(services, "all_loot", true, "loot rows", c => c.RowCount, (live, fresh) => fresh);

/// <summary><c>.reload skill_fishing_base_level</c> (ServerCommands.cpp:1337-1343, Chat.cpp:889 → LoadFishingBaseSkillLevel, ObjectMgr.cpp:10407, clears first :10409). Covered in <c>reload all</c> by <c>all_loot</c>.</summary>
public sealed class SkillFishingBaseLevelContentReloadable(IServiceProvider services)
    : LootReloadable(services, "skill_fishing_base_level", false, "fishing base levels", c => c.FishingBaseSkillCount, WithFishingBase);

/// <summary><c>.reload creature_loot_template</c> (ServerCommands.cpp:1174-1181, Chat.cpp:825).</summary>
public sealed class CreatureLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "creature_loot_template", false, "creature loot rows", c => c.RowCountOf(LootTableKind.Creature), (live, fresh) => WithTable(live, fresh, LootTableKind.Creature));

/// <summary><c>.reload gameobject_loot_template</c> (ServerCommands.cpp:1201-1208, Chat.cpp:841).</summary>
public sealed class GameObjectLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "gameobject_loot_template", false, "game object loot rows", c => c.RowCountOf(LootTableKind.GameObject), (live, fresh) => WithTable(live, fresh, LootTableKind.GameObject));

/// <summary><c>.reload item_loot_template</c> (ServerCommands.cpp:1210-1217, Chat.cpp:853).</summary>
public sealed class ItemLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "item_loot_template", false, "item loot rows", c => c.RowCountOf(LootTableKind.Item), (live, fresh) => WithTable(live, fresh, LootTableKind.Item));

/// <summary><c>.reload skinning_loot_template</c> (ServerCommands.cpp:1247-1254, Chat.cpp:890).</summary>
public sealed class SkinningLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "skinning_loot_template", false, "skinning loot rows", c => c.RowCountOf(LootTableKind.Skinning), (live, fresh) => WithTable(live, fresh, LootTableKind.Skinning));

/// <summary><c>.reload reference_loot_template</c> (ServerCommands.cpp:1237-1245, Chat.cpp:885).</summary>
public sealed class ReferenceLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "reference_loot_template", false, "reference loot rows", c => c.RowCountOf(LootTableKind.Reference), (live, fresh) => WithTable(live, fresh, LootTableKind.Reference));

/// <summary><c>.reload fishing_loot_template</c> (ServerCommands.cpp:1192-1199, Chat.cpp:834).</summary>
public sealed class FishingLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "fishing_loot_template", false, "fishing loot rows", c => c.RowCountOf(LootTableKind.Fishing), (live, fresh) => WithTable(live, fresh, LootTableKind.Fishing));

/// <summary><c>.reload pickpocketing_loot_template</c> (ServerCommands.cpp:1219-1226, Chat.cpp:874).</summary>
public sealed class PickpocketingLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "pickpocketing_loot_template", false, "pickpocketing loot rows", c => c.RowCountOf(LootTableKind.Pickpocketing), (live, fresh) => WithTable(live, fresh, LootTableKind.Pickpocketing));

/// <summary><c>.reload disenchant_loot_template</c> (ServerCommands.cpp:1183-1190, Chat.cpp:831).</summary>
public sealed class DisenchantLootTemplateContentReloadable(IServiceProvider services)
    : LootReloadable(services, "disenchant_loot_template", false, "disenchant loot rows", c => c.RowCountOf(LootTableKind.Disenchant), (live, fresh) => WithTable(live, fresh, LootTableKind.Disenchant));

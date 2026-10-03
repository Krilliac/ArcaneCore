using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// The contract of <c>.reload all</c> (vmangos <c>HandleReloadAllCommand</c>, ServerCommands.cpp:885-905,
/// with its sub-commands :907-1002): which reloadables it reaches is pinned name by name, so a new
/// reloadable that forgets to say whether <c>all</c> includes it fails here instead of silently
/// under-reloading (or over-reloading) while still reporting Applied. Every registered name must also be a
/// real vmangos reload-table name (Chat.cpp:794-910) or a documented ArcaneCore name.
/// </summary>
public sealed class ReloadAllMembershipTests
{
    /// <summary>
    /// Every reloadable and whether vmangos' <c>reload all</c> reaches it, with the vmangos line that says so.
    /// A reloadable missing here fails <see cref="EveryRegisteredReloadable_IsClassifiedHere"/>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, bool> Expected = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        // all_area: areatrigger_teleport (ServerCommands.cpp:910).
        ["areatrigger_teleport"] = true,

        // reload all calls HandleReloadGameTeleCommand (:902).
        ["game_tele"] = true,

        // all_spell calls HandleReloadSpellTemplateCommand (:971).
        ["spell_template"] = true,

        // all_quest calls HandleReloadQuestTemplateCommand (:938) and reloads the quest relations (:940-942).
        ["quest_template"] = true,

        // all_npc: npc_gossip, npc_trainer, npc_vendor, points_of_interest (ServerCommands.cpp:928-931); all_gossips: gossip_menu,
        // gossip_menu_option, npc_gossip, points_of_interest (:987-992).
        ["npc_gossip"] = true,
        ["npc_trainer"] = true,
        ["npc_vendor"] = true,
        ["points_of_interest"] = true,
        ["gossip_menu"] = true,
        ["gossip_menu_option"] = true,

        // all_loot calls LoadLootTables, which reloads every *_loot_template at once (:891, :916-922, LootMgr.h:431). The per-table
        // commands are not in all on their own (all_loot covers them), and skill_fishing_base_level (:887) is covered by all_loot too,
        // because the loot content object holds the fishing base levels (docs/areas/hot-reload.md).
        ["all_loot"] = true,
        ["creature_loot_template"] = false,
        ["gameobject_loot_template"] = false,
        ["item_loot_template"] = false,
        ["skinning_loot_template"] = false,
        ["reference_loot_template"] = false,
        ["fishing_loot_template"] = false,
        ["pickpocketing_loot_template"] = false,
        ["disenchant_loot_template"] = false,
        ["skill_fishing_base_level"] = false,

        // Not reached by any all_* command: npc_text (neither all_npc nor all_gossips lists it), the config (Chat.cpp:808
        // registers it on its own), item_template (all_item :996-1002 only reloads page_text, item_enchantment and
        // item_required_target) and creature_template (all_npc :925-933 leaves it out).
        ["npc_text"] = false,
        ["config"] = false,
        ["item_template"] = false,
        ["creature_template"] = false,
    };

    /// <summary>
    /// The reload-table names vmangos registers that ArcaneCore uses as reloadable names (Chat.cpp:794-910);
    /// anything else must be listed in <see cref="ArcaneCoreNames"/>.
    /// </summary>
    private static readonly HashSet<string> VmangosNames = new(StringComparer.Ordinal)
    {
        "areatrigger_teleport", "config", "creature_template", "game_tele", "item_template", "quest_template", "spell_template",
        "npc_gossip", "npc_text", "npc_trainer", "npc_vendor", "points_of_interest", "gossip_menu", "gossip_menu_option",
        "all_loot", "creature_loot_template", "gameobject_loot_template", "item_loot_template", "skinning_loot_template",
        "reference_loot_template", "fishing_loot_template", "pickpocketing_loot_template", "disenchant_loot_template",
        "skill_fishing_base_level",
    };

    /// <summary>Names with no vmangos counterpart (none yet; each needs a reason in docs/areas/hot-reload.md).</summary>
    private static readonly HashSet<string> ArcaneCoreNames = new(StringComparer.Ordinal);

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    [Fact]
    public async Task EveryRegisteredReloadable_IsClassifiedHere()
    {
        await using var host = WorldTestHost.Start();

        string[] registered = [.. Coordinator(host).Names];

        Assert.Equal([.. Expected.Keys.Order(StringComparer.Ordinal)], [.. registered.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task EveryRegisteredName_IsAVmangosReloadTable_OrADocumentedAddition()
    {
        await using var host = WorldTestHost.Start();

        foreach (string name in Coordinator(host).Names)
        {
            Assert.True(VmangosNames.Contains(name) || ArcaneCoreNames.Contains(name), $"'{name}' is neither a vmangos reload table (Chat.cpp:794-910) nor a documented ArcaneCore name");
        }
    }

    [Fact]
    public async Task ReloadAll_ReachesExactlyTheReloadablesVmangosAllReaches()
    {
        await using var host = WorldTestHost.Start();

        IReadOnlyList<ReloadResult> results = await Coordinator(host).ReloadAllAsync();

        string[] reached = [.. results.Select(r => r.Name).Order(StringComparer.Ordinal)];
        string[] wanted = [.. Expected.Where(p => p.Value).Select(p => p.Key).Order(StringComparer.Ordinal)];
        Assert.Equal(wanted, reached);
    }
}

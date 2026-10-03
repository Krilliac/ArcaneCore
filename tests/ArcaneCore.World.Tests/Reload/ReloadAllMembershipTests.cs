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

        // Not reached by any all_* command: the config (Chat.cpp:808 registers it on its own), item_template
        // (all_item :996-1002 only reloads page_text, item_enchantment and item_required_target) and
        // creature_template (all_npc :925-933 leaves it out).
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
        "areatrigger_teleport", "config", "creature_template", "game_tele", "item_template", "spell_template",
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

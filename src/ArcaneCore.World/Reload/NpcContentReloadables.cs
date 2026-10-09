using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// One NPC service table of the live <see cref="NpcStore"/> (vmangos reloads each of these with its own command and
/// its own loader, Chat.cpp:847-880). The whole <see cref="NpcContent"/> is read through <see cref="INpcContentStore"/> off the
/// world thread; only this table's rows are taken from it and put into a copy of the live content, from which the next immutable
/// <see cref="NpcStore"/> is built (still off the world thread). The commit swaps that store into the live
/// <see cref="QuestNpcServices"/>, so gossip, vendor and trainer windows opened afterwards, and every NPC query, see it.
/// The tables this reload does not name keep exactly what is live, including the flight network, which the reload never touches.
/// <para>
/// Retail rules: every one of these loaders clears its map before it looks at the query result and an empty table leaves it empty
/// (ObjectMgr.cpp:10867, 6829, 10604-10607, 10784-10787, 9083, 10933, 11015), so an empty table empties the rows, unless
/// <c>HotReload:EmptyTables = KeepLoaded</c>. A window a player already has open keeps the list it was sent (vmangos sends its
/// lists when the window opens, too).
/// </para>
/// </summary>
public abstract class NpcTableReloadable(
    IServiceProvider services, string name, bool includedInAll, string noun,
    Func<NpcContent, int> rows, Func<NpcContent, NpcContent, NpcContent> takeFrom) : IContentReloadable
{
    public string Name => name;

    public bool IncludedInAll => includedInAll;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        QuestNpcFeature feature = services.GetRequiredService<QuestNpcFeature>();
        NpcContent fresh;
        using (IServiceScope scope = services.CreateScope())
        {
            INpcContentStore store = scope.ServiceProvider.GetService<INpcContentStore>()
                ?? throw new InvalidOperationException("no NPC content store is registered");
            fresh = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        NpcContent next = takeFrom(feature.Services.Npcs.Content, fresh);
        return new Candidate(this, feature, new NpcStore(next, feature.Services.Npcs.ScriptedTaxiPathIds), rows(next), ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(NpcTableReloadable owner, QuestNpcFeature feature, NpcStore store, int rows, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{rows} {owner.Noun}";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = owner.RowsOf(feature.Services.Npcs.Content);
            if (keepEmpty && rows == 0 && loaded > 0)
            {
                reason = $"{owner.Name} is empty and HotReload:EmptyTables is KeepLoaded, {loaded} {owner.Noun} stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            NpcStore? previous = null;
            transaction.Step(owner.Name, () => previous = feature.Services.ReplaceNpcs(store), () => feature.Services.ReplaceNpcs(previous!));
        }
    }

    private string Noun => noun;

    private int RowsOf(NpcContent content) => rows(content);
}

/// <summary><c>.reload npc_gossip</c> (ServerCommands.cpp:1264-1270, Chat.cpp:867 → LoadNpcGossips, ObjectMgr.cpp:10865); in <c>reload all</c> through all_npc and all_gossips (:928, :991).</summary>
public sealed class NpcGossipContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "npc_gossip", true, "npc_gossip rows", c => c.NpcGossips.Count, (live, fresh) => live with { NpcGossips = fresh.NpcGossips });

/// <summary><c>.reload npc_text</c> (ServerCommands.cpp:1272-1278, Chat.cpp:868 → LoadNPCText, ObjectMgr.cpp:6827); no all_* command reaches it.</summary>
public sealed class NpcTextContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "npc_text", false, "NPC texts", c => c.NpcTexts.Count, (live, fresh) => live with { NpcTexts = fresh.NpcTexts });

/// <summary>
/// <c>.reload npc_trainer</c> (ServerCommands.cpp:1280-1290, Chat.cpp:869 → LoadTrainers, ObjectMgr.cpp:10600); in <c>reload all</c> through all_npc (:929).
/// vmangos also reloads its <c>npc_trainer_template</c> here; ArcaneCore has no separate template table (a trainer entry is one list).
/// </summary>
public sealed class NpcTrainerContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "npc_trainer", true, "trainer spells", c => c.TrainerSpells.Count, (live, fresh) => live with { TrainerSpells = fresh.TrainerSpells });

/// <summary>
/// <c>.reload npc_vendor</c> (ServerCommands.cpp:1292-1303, Chat.cpp:870 → LoadVendors, ObjectMgr.cpp:10780); in <c>reload all</c> through all_npc (:930).
/// vmangos also reloads its <c>npc_vendor_template</c> here; ArcaneCore has no separate template table.
/// </summary>
public sealed class NpcVendorContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "npc_vendor", true, "vendor items", c => c.VendorItems.Count, (live, fresh) => live with { VendorItems = fresh.VendorItems });

/// <summary><c>.reload points_of_interest</c> (ServerCommands.cpp:1305-1311, Chat.cpp:880 → LoadPointsOfInterest, ObjectMgr.cpp:9081); in <c>reload all</c> through all_npc and all_gossips (:931, :992).</summary>
public sealed class PointsOfInterestContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "points_of_interest", true, "points of interest", c => c.PointsOfInterest.Count, (live, fresh) => live with { PointsOfInterest = fresh.PointsOfInterest });

/// <summary><c>.reload gossip_menu</c> (ServerCommands.cpp:1071-1080, Chat.cpp:847 → LoadGossipMenu, ObjectMgr.cpp:10931); in <c>reload all</c> through all_gossips (:987).</summary>
public sealed class GossipMenuContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "gossip_menu", true, "gossip menu texts", c => c.GossipMenus.Count, (live, fresh) => live with { GossipMenus = fresh.GossipMenus });

/// <summary><c>.reload gossip_menu_option</c> (ServerCommands.cpp:1082-1091, Chat.cpp:848 → LoadGossipMenuItems, ObjectMgr.cpp:11013); in <c>reload all</c> through all_gossips (:988).</summary>
public sealed class GossipMenuOptionContentReloadable(IServiceProvider services)
    : NpcTableReloadable(services, "gossip_menu_option", true, "gossip menu options", c => c.GossipMenuOptions.Count, (live, fresh) => live with { GossipMenuOptions = fresh.GossipMenuOptions });

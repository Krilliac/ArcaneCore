using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The four Rallying Cry heralds' gossip: mangos-classic ScriptDev2 <c>npc_overlord_runthalak</c> and <c>npc_high_overlord_saurfang</c>
/// (kalimdor/orgrimmar.cpp:31-63) and <c>npc_major_mattingly</c> and <c>npc_field_marshal_afrasiabi</c> (eastern_kingdoms/stormwind_city.cpp
/// :1320-1352). While the dragon's head their city raises is up within 100 yd, they show the database menu that talks about it (6024 in
/// Orgrimmar, 5754 and 6027 in Stormwind); otherwise their own menu.
/// </summary>
public sealed class RallyingCryGossip : INpcGossipScript
{
    public const uint NpcRunthalak = 14392, NpcSaurfang = 14720, NpcMattingly = 14394, NpcAfrasiabi = 14721;
    public const uint GoHeadOfNefarianOg = 179881, GoHeadOfOnyxiaOg = 179556, GoHeadOfNefarianSw = 179882, GoHeadOfOnyxiaSw = 179558;
    public const uint GossipHeadOg = 6024, GossipHeadMattingly = 5754, GossipHeadAfrasiabi = 6027;
    public const float SearchRange = 100f;

    /// <summary>Herald entry to the head it looks for and the menu it then shows, as the source pairs them.</summary>
    public static readonly IReadOnlyDictionary<uint, (uint Head, uint Menu)> Heralds = new Dictionary<uint, (uint, uint)>
    {
        [NpcRunthalak] = (GoHeadOfNefarianOg, GossipHeadOg),
        [NpcSaurfang] = (GoHeadOfOnyxiaOg, GossipHeadOg),
        [NpcMattingly] = (GoHeadOfOnyxiaSw, GossipHeadMattingly),
        [NpcAfrasiabi] = (GoHeadOfNefarianSw, GossipHeadAfrasiabi),
    };

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!Heralds.TryGetValue(npc.Entry, out (uint Head, uint Menu) herald)
            || player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return null;
        }

        float rangeSq = SearchRange * SearchRange;
        bool raised = objects.GameObjects.Any(go => go.Entry == herald.Head && go.IsSpawned
            && ((go.X - npc.X) * (go.X - npc.X)) + ((go.Y - npc.Y) * (go.Y - npc.Y)) + ((go.Z - npc.Z) * (go.Z - npc.Z)) <= rangeSq);
        return raised ? ScriptedGossipMenu.Database(herald.Menu) : null;
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;
}

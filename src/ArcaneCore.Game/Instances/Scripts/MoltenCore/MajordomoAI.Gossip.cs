using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic boss_majordomo_executus.cpp GossipHello/Select_boss_majordomo;
/// option text from sql/scriptdev2/scriptdev2.sql -3409000..-3409002.</summary>
public sealed partial class MajordomoAI : INpcGossipScript
{
    private static ScriptedGossipMenu Menu(uint step) => step switch
    {
        1 => new(false, 4995, [new(0, "Tell me more.", 1, 1)]),
        2 => new(false, 5011, [new(0, "What else do you have to say?", 1, 2)]),
        _ => new(false, 5012, [new(0, "You challenged us and we have come. Where is this master you speak of?", 1, 3)])
    };
    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        => Defeated && _stage == 0 && Raid.GetData(9) is EncounterState.NotStarted or EncounterState.Fail ? Menu(1) : ScriptedGossipMenu.Nothing;
    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;
    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (sender != 1 || !Defeated || _stage != 0) return new(0, Close: true);
        if (action is 1 or 2) return new(0) { NextMenu = Menu(action + 1) };
        if (action == 3) StartSummonEvent(player);
        return new(0, Close: true);
    }
}

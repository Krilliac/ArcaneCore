using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic boss_vaelastrasz.cpp GossipHello/GossipSelect_boss_vaelastrasz
/// and boss_victor_nefarius.cpp GossipHello/GossipSelect_boss_victor_nefarius.
/// The option strings are ScriptDev2 gossip_texts IDs (-3469000..-3469004), which the importer carries into creature_ai_texts;
/// a world imported before they were carried uses their ClassicDB z2815 English text below. Only the throne-room Victor Nefarius
/// offers the encounter: the one Vaelastrasz's intro summons is uninteractible in SD2.
/// </summary>
public sealed class BlackwingLairGossip(Func<Player, int, string?>? text = null) : INpcGossipScript
{
    /// <summary>ClassicDB z2815 gossip_texts -3469000..-3469004 (GOSSIP_ITEM_NEFARIUS_1..3, GOSSIP_ITEM_VAEL_1..2).</summary>
    private static readonly Dictionary<int, string> Fallback = new()
    {
        [-3469000] = "I've made no mistakes.",
        [-3469001] = "You have lost your mind, Nefarius. You speak in riddles.",
        [-3469002] = "Please do.",
        [-3469003] = "I cannot, Vaelastrasz! Surely something can be done to heal you!",
        [-3469004] = "Vaelastrasz, no!!!",
    };

    private readonly Dictionary<ObjectGuid, int> _steps = [];
    private readonly HashSet<ObjectGuid> _vaelSecondChoice = [];

    private string? Text(Player player, int id)
        => text?.Invoke(player, id)
            ?? (player.Map?.FindUpdater<CreatureMapSystem>()?.Content.Ai.FindText(id)?.Content is { Length: > 0 } imported ? imported : null)
            ?? Fallback.GetValueOrDefault(id);

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (player.Map?.FindUpdater<InstanceData>() is not BlackwingLairInstance raid) return null;
        if (npc.Entry == 13020 && raid.GetData(1) is not (EncounterState.InProgress or EncounterState.Done)
            && Text(player, -3469003) is { Length: > 0 } vael)
        {
            _vaelSecondChoice.Remove(player.Guid);
            return new ScriptedGossipMenu(true, 7156, [new ScriptedGossipItem(0, vael, 1, 1001)]);
        }
        if (npc.Entry == 10162 && raid.IsEncounterVictor(npc.Guid)
            && raid.GetData(7) is not (EncounterState.InProgress or EncounterState.Special or EncounterState.Done)
            && Text(player, -3469000) is { Length: > 0 } victor)
        {
            _steps[player.Guid] = 0;
            return new ScriptedGossipMenu(false, 7134, [new ScriptedGossipItem(0, victor, 1, 1001)]);
        }
        return npc.Entry is 13020 or 10162 ? ScriptedGossipMenu.Nothing : null;
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action)
        => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (sender != 1 || player.Map?.FindUpdater<InstanceData>() is not BlackwingLairInstance raid) return default;
        if (npc.Entry == 13020 && player.Map.FindObject(npc.Guid) is Creature { AI: VaelastraszAI vael })
        {
            if (action == 1001 && Text(player, -3469004) is { Length: > 0 } second)
            {
                _vaelSecondChoice.Add(player.Guid);
                return new ScriptedGossipReply(7256)
                { NextMenu = new ScriptedGossipMenu(true, 7256, [new ScriptedGossipItem(0, second, 1, 1002)]) };
            }
            if (action == 1002 && _vaelSecondChoice.Remove(player.Guid) && vael.BeginSpeech())
                return new ScriptedGossipReply(0, Close: true);
        }
        if (npc.Entry == 10162 && raid.IsEncounterVictor(npc.Guid)
            && player.Map.FindObject(npc.Guid) is Creature { AI: VictorNefariusAI victor }
            && _steps.TryGetValue(player.Guid, out int step) && action == 1001 + step)
        {
            if (step < 2 && Text(player, -3469001 - step) is { Length: > 0 } next)
            {
                _steps[player.Guid] = step + 1;
                uint npcText = step == 0 ? 7198u : 7199u;
                return new ScriptedGossipReply(npcText)
                { NextMenu = new ScriptedGossipMenu(false, npcText, [new ScriptedGossipItem(0, next, 1, (uint)(1002 + step))]) };
            }
            if (step == 2)
            {
                _steps.Remove(player.Guid);
                if (victor.BeginIntro()) return new ScriptedGossipReply(0, Close: true);
            }
        }
        return default;
    }
}

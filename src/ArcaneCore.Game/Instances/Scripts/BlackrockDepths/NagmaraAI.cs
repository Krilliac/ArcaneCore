using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic blackrock_depths/blackrock_depths.cpp npc_mistress_nagmaraAI (DoPotionOfLoveIfCan, UpdateAI) and
/// QuestRewarded_npc_mistress_nagmara. Rewarding Potion of Love (quest 4201) walks her to Rocknot, she gives him the potion and sets
/// TYPE_NAGMARA SPECIAL, which starts the second part of <see cref="RocknotAI"/>'s escort; under the stair she kisses him in a loop.
/// GossipHello/GossipSelect_npc_mistress_nagmara: once quest 4201 is rewarded she offers (npc text 2729) the line that starts the same scene again,
/// and choosing it closes the menu and runs <see cref="DoPotionOfLoveIfCan"/>; before that her database menu (npc text 2727) is used. World thread.
/// </summary>
public sealed class NagmaraAI(Creature creature, BlackrockDepthsInstance instance) : CreatureAI(creature), INpcGossipScript
{
    public const uint QuestPotionOfLove = 4201;
    public const uint SpellPotionLove = 14928;
    public const uint SpellNagmaraRocknot = 15064;
    public const uint GossipNpcTextRepeat = 2729;
    public const uint ActionRepeat = 1001; // GOSSIP_ACTION_INFO_DEF + 1
    public const string GossipItemRepeat = "Why don't you and Rocknot go find somewhere private..."; // GOSSIP_ITEM_NAGMARA
    private const byte IconChat = 0;
    private const uint SenderMain = 1;
    public const int SayNagmara1 = -1230066;
    public const int SayNagmara2 = -1230067;
    public const int TextEmoteNagmara = -1230068;
    public const int TextEmoteRocknot = -1230069;

    /// <summary>aPosNagmaraRocknot: her place under the stair.</summary>
    public const float RocknotSpotX = 878.1779f, RocknotSpotY = -222.0662f, RocknotSpotZ = -49.96714f;

    private Creature? _rocknot;
    private int _phase;
    private uint _phaseMs;

    public int Phase => _phase;

    public override void OnRespawn()
    {
        _phase = 0;
        _phaseMs = 0;
    }

    public override void OnQuestRewarded(Player player, uint questId)
    {
        base.OnQuestRewarded(player, questId);
        if (questId == QuestPotionOfLove)
        {
            DoPotionOfLoveIfCan();
        }
    }

    /// <summary>Player::GetQuestRewardStatus(QUEST_POTION_LOVE) through the map's quest seam; false without it.</summary>
    private bool PotionQuestRewarded(Player player)
        => player.Map?.FindUpdater<IInstanceQuestRewards>()?.IsRewarded(player, QuestPotionOfLove) == true;

    /// <summary>GossipHello_npc_mistress_nagmara: after quest 4201 the quest list, the repeat line and npc text 2729; otherwise the database menu.</summary>
    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (npc.Guid != Me.Guid || !PotionQuestRewarded(player))
        {
            return null;
        }

        bool questGiver = (Me.NpcFlags & (uint)NpcFlags.QuestGiver) != 0;
        return new ScriptedGossipMenu(questGiver, GossipNpcTextRepeat, [new ScriptedGossipItem(IconChat, GossipItemRepeat, SenderMain, ActionRepeat)]);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    /// <summary>GossipSelect_npc_mistress_nagmara: GOSSIP_ACTION_INFO_DEF + 1 closes the menu and restarts the scene.</summary>
    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (action != ActionRepeat || npc.Guid != Me.Guid || !PotionQuestRewarded(player))
        {
            return default;
        }

        DoPotionOfLoveIfCan();
        return new ScriptedGossipReply(0, Close: true);
    }

    /// <summary>DoPotionOfLoveIfCan: needs Rocknot; both lose their quest giver flag, she loses gossip too, and she heads for him.</summary>
    public void DoPotionOfLoveIfCan()
    {
        if (instance.StoredCreature(BlackrockDepthsInstance.NpcRocknot) is not { } rocknot)
        {
            return;
        }

        _rocknot = rocknot;
        Me.NpcFlags &= ~((uint)NpcFlags.Gossip | (uint)NpcFlags.QuestGiver);
        rocknot.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        System?.MoveIdle(Me);
        Me.Motion.MoveChase(rocknot, 2f);
        _phase = 1;
    }

    /// <summary>RocknotAI case 34: the lovers are under the stair; phase 4 starts after 5 s.</summary>
    public void BeginKissingPhase()
    {
        _phase = 4;
        _phaseMs = 5000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_phaseMs != 0)
        {
            if (_phaseMs > diffMs)
            {
                _phaseMs -= diffMs;
                return;
            }

            _phaseMs = 0;
        }

        if (_rocknot is not { } rocknot)
        {
            return;
        }

        switch (_phase)
        {
            case 1:
                {
                    float dx = rocknot.X - Me.X, dy = rocknot.Y - Me.Y;
                    if (dx * dx + dy * dy <= 25f)
                    {
                        System?.MoveIdle(Me);
                        RocknotAI.FaceTo(System, Me, rocknot);
                        RocknotAI.FaceTo(System, rocknot, Me);
                        System?.SayText(Me, SayNagmara1);
                        _phase++;
                        _phaseMs = 5000;
                    }
                    else
                    {
                        Me.Motion.MoveChase(rocknot, 2f);
                    }

                    break;
                }
            case 2:
                System?.SayText(Me, SayNagmara2);
                _phaseMs = 4000;
                _phase++;
                break;
            case 3:
                if (DoCast(Me, SpellPotionLove) == CreatureCastResult.Ok)
                {
                    _phase = 0;
                    instance.SetData(BlackrockDepthsInstance.TypeNagmara, EncounterState.Special);
                }

                break;
            case 4:
                RocknotAI.FaceTo(System, Me, rocknot);
                RocknotAI.FaceTo(System, rocknot, Me);
                _phaseMs = 4000;
                _phase++;
                instance.SetData(BlackrockDepthsInstance.TypeNagmara, EncounterState.Done);
                break;
            case 5:
                System?.SayText(Me, TextEmoteNagmara);
                System?.SayText(rocknot, TextEmoteRocknot);
                DoCast(Me, SpellNagmaraRocknot);
                System?.CastSpell(rocknot, SpellNagmaraRocknot, rocknot, triggered: false);
                _phaseMs = 12_000;
                break;
        }
    }
}

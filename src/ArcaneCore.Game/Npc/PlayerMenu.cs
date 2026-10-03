using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>One prepared gossip line and its action (vmangos GossipMenuItem + GossipMenuItemData).</summary>
public sealed record GossipMenuItem(
    byte Icon,
    string Message,
    bool Coded,
    GossipOption OptionId,
    string BoxMessage,
    int ActionMenu,
    uint ActionPoi);

/// <summary>vmangos GossipDef.h Gossip_Option (gossip_menu_option.option_id).</summary>
public enum GossipOption : byte
{
    None = 0,
    Gossip = 1,
    QuestGiver = 2,
    Vendor = 3,
    TaxiVendor = 4,
    Trainer = 5,
    SpiritHealer = 6,
    SpiritGuide = 7,
    Innkeeper = 8,
    Banker = 9,
    Petitioner = 10,
    TabardDesigner = 11,
    Battlefield = 12,
    Auctioneer = 13,
    StablePet = 14,
    Armorer = 15,
    UnlearnTalents = 16,
    UnlearnPetSkills = 17,
}

/// <summary>
/// The menu a player is currently shown (vmangos PlayerMenu = GossipMenu + QuestMenu). Selecting
/// an option refers to it by index, so it is kept per player between packets. World thread.
/// </summary>
public sealed class PlayerMenu
{
    /// <summary>GOSSIP_MAX_MENU_ITEMS (vmangos GossipDef.h).</summary>
    public const int MaxMenuItems = 32;

    private readonly List<GossipMenuItem> _gossip = [];
    private readonly List<QuestMenuItem> _quests = [];

    public uint MenuId { get; set; }

    /// <summary>vmangos GossipMenu::IsJustDiscoveredNode (a flight node was learned while preparing).</summary>
    public bool DiscoveredNode { get; set; }

    public IReadOnlyList<GossipMenuItem> GossipItems => _gossip;

    public IReadOnlyList<QuestMenuItem> QuestItems => _quests;

    public void ClearMenus()
    {
        _gossip.Clear();
        _quests.Clear();
        MenuId = 0;
        DiscoveredNode = false;
    }

    public void ClearQuestMenu() => _quests.Clear();

    /// <summary>vmangos GossipMenu::AddMenuItem (the list is capped at GOSSIP_MAX_MENU_ITEMS).</summary>
    public void AddGossipItem(GossipMenuItem item)
    {
        if (_gossip.Count < MaxMenuItems)
        {
            _gossip.Add(item);
        }
    }

    /// <summary>vmangos QuestMenu::AddMenuItem (capped at GOSSIP_MAX_MENU_ITEMS).</summary>
    public void AddQuestItem(uint questId, DialogStatus icon)
    {
        if (_quests.Count < MaxMenuItems)
        {
            _quests.Add(new QuestMenuItem(questId, icon));
        }
    }
}

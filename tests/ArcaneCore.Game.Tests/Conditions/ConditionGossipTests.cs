using Xunit;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Conditions.ConditionTestSupport;

namespace ArcaneCore.Game.Tests.Conditions;

/// <summary>
/// The conditions table through the NPC services: gossip options and menu texts (cmangos
/// Player::PrepareGossipMenu) and vendor rows (IsVendorItemVisible). Before the evaluator existed
/// every conditioned entry evaluated false (the seam had no implementer).
/// </summary>
public sealed class ConditionGossipTests
{
    private const uint Menu = 100;
    private const uint WarriorCondition = 10;
    private const uint MageCondition = 11;
    private const uint NotMageCondition = 12;
    private const uint ScriptCondition = 13;

    private static readonly ConditionRecord[] Rows =
    [
        Row(WarriorCondition, ConditionType.RaceClass, 0, 1 << 0),
        Row(MageCondition, ConditionType.RaceClass, 0, 1 << 7),
        Row(NotMageCondition, ConditionType.Not, MageCondition),
        Row(ScriptCondition, ConditionType.WorldScript),
    ];

    private static GossipMenuOption Option(uint id, string text, uint condition = 0) => new()
    {
        MenuId = Menu,
        Id = id,
        OptionIcon = 0,
        OptionText = text,
        OptionId = (byte)GossipOption.Gossip,
        NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
        ConditionId = condition,
    };

    private static NpcContent Content() => NpcContent.Empty with
    {
        GossipMenuOptions =
        [
            Option(0, "Train me, warrior", WarriorCondition),
            Option(1, "Train me, mage", MageCondition),
            Option(2, "Anything but a mage", NotMageCondition),
            Option(3, "Quest script", ScriptCondition),
            Option(4, "Everyone"),
        ],
        GossipMenus =
        [
            new GossipMenu { Entry = Menu, TextId = 1 },
            new GossipMenu { Entry = Menu, TextId = 2, ConditionId = WarriorCondition },
            new GossipMenu { Entry = Menu, TextId = 3, ConditionId = MageCondition },
        ],
    };

    private static NpcServiceKit Kit(Class cls, ConditionEvaluator? evaluator)
    {
        var kit = new NpcServiceKit(NpcFlags.Gossip | NpcFlags.Vendor, Content(),
            new QuestNpcDependencies(Conditions: evaluator, Items: null));
        kit.Player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
        kit.Npc = kit.Npc with { GossipMenuId = Menu };
        return kit;
    }

    private static (uint TextId, string[] Options) Open(NpcServiceKit kit)
    {
        kit.Drain();
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        var r = new PacketReader(kit.Single(WorldOpcode.SmsgGossipMessage));
        r.ReadUInt64();
        uint text = r.ReadUInt32();
        uint count = r.ReadUInt32();
        var options = new string[count];
        for (int i = 0; i < count; i++)
        {
            r.ReadUInt32();
            r.ReadByte();
            r.ReadByte();
            options[i] = r.ReadCString();
        }

        return (text, options);
    }

    [Fact]
    public void WithoutAnEvaluator_EveryConditionedOptionVanishes_TheBaselineThisSliceFixes()
    {
        using NpcServiceKit kit = Kit(Class.Warrior, null);
        (uint text, string[] options) = Open(kit);
        Assert.Equal(["Everyone"], options);
        Assert.Equal(1u, text);
    }

    [Fact]
    public void AWarriorSeesTheWarriorVariant_AMageTheMageVariant()
    {
        var evaluator = new ConditionEvaluator(ConditionTable.Build(Rows), new ConditionContext());

        using (NpcServiceKit warrior = Kit(Class.Warrior, evaluator))
        {
            (uint text, string[] options) = Open(warrior);
            Assert.Equal(["Train me, warrior", "Anything but a mage", "Everyone"], options);
            Assert.Equal(2u, text);
        }

        using (NpcServiceKit mage = Kit(Class.Mage, evaluator))
        {
            (uint text, string[] options) = Open(mage);
            Assert.Equal(["Train me, mage", "Everyone"], options);
            Assert.Equal(3u, text);
        }
    }

    [Fact]
    public void AnUnsupportedConditionHidesItsOption_AndIsCounted()
    {
        var evaluator = new ConditionEvaluator(ConditionTable.Build(Rows), new ConditionContext());
        using NpcServiceKit kit = Kit(Class.Warrior, evaluator);
        Open(kit);
        Assert.DoesNotContain("Quest script", Open(kit).Options);
        Assert.True(evaluator.Unavailable[(int)ConditionType.WorldScript] >= 2);
        Assert.Equal(1, evaluator.Summarize().UnavailableByType[(int)ConditionType.WorldScript]);
    }

    [Fact]
    public void AVendorRowWithACondition_IsListedOnlyWhenItHolds()
    {
        const uint Gate = 20;
        var evaluator = new ConditionEvaluator(
            ConditionTable.Build([Row(Gate, ConditionType.Level, 5, 1)]), new ConditionContext());
        NpcContent content = NpcContent.Empty with
        {
            VendorItems =
            [
                new VendorItem { Entry = NpcServiceKit.Entry, Item = NpcServiceKit.Bread },
                new VendorItem { Entry = NpcServiceKit.Entry, Item = NpcServiceKit.Lantern, ConditionId = Gate },
            ],
        };
        using var low = new NpcServiceKit(NpcFlags.Vendor | NpcFlags.Gossip, content, new QuestNpcDependencies(Conditions: evaluator));
        low.Services.ListInventory(low.Player, low.Npc.Guid);
        Assert.Equal(1u, ListedItems(low.Single(WorldOpcode.SmsgListInventory)));

        using var high = new NpcServiceKit(NpcFlags.Vendor | NpcFlags.Gossip, content, new QuestNpcDependencies(Conditions: evaluator));
        high.Player.Level = 5;
        high.Services.ListInventory(high.Player, high.Npc.Guid);
        Assert.Equal(2u, ListedItems(high.Single(WorldOpcode.SmsgListInventory)));
    }

    private static uint ListedItems(byte[] payload)
    {
        var r = new PacketReader(payload);
        r.ReadUInt64();
        return r.ReadByte();
    }
}

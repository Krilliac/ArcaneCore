using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// A walking vendor stops when a player talks to it (vmangos Creature::PauseOutOfCombatMovement, Movement/MotionMaster.cpp:947-969, called
/// from SendListInventory ItemHandler.cpp:716-718, HandleGossipHelloOpcode NPCHandler.cpp:357-358, HandleGossipSelectOptionOpcode
/// NPCHandler.cpp:395-396, HandleQuestgiverHelloOpcode QuestHandler.cpp:93-95 and HandleBattlemasterHelloOpcode BattleGroundHandler.cpp:50-52)
/// for NPC_MOVEMENT_PAUSE_TIME (180 s, CreatureDefines.h:222). Without it the vendor walks out of INTERACTION_DISTANCE while its window is
/// open and the next sale is refused with SELL_ERR_CANT_FIND_VENDOR (3), which the 1.12.1 client shows as "That merchant doesn't like you."
/// (live report 2026-10-19).
/// </summary>
public sealed class VendorMovementPauseTests
{
    private const uint PauseMs = 180_000;

    [Fact]
    public void AWaypointVendor_StopsWhenItsInventoryIsListed_SoTheNextSaleFindsIt()
    {
        using var kit = new Kit(movementType: 2);
        Assert.True(kit.Vendor.IsMoving);

        kit.Services.ListInventory(kit.Player, kit.Vendor.Guid);
        Assert.Single(kit.Drain(WorldOpcode.SmsgListInventory));
        (float x, float y) = (kit.Vendor.X, kit.Vendor.Y);

        // 6 s at walk speed is 15 yd: a vendor that kept walking would be far outside INTERACTION_DISTANCE.
        Run(kit.World, 6000);
        kit.Services.SellItem(kit.Player, kit.Vendor.Guid, kit.ItemGuid, 1);

        // The vendor is found; the sale then fails only for the item (no items owner here), never with CantFindVendor.
        Assert.Equal(SellResult.CantFindItem, kit.SellResult());
        Assert.False(kit.Vendor.IsMoving);
        Assert.Equal((x, y), (kit.Vendor.X, kit.Vendor.Y));
    }

    [Fact]
    public void AWaypointVendor_SetsOffForTheSameNodeWhenThePauseIsOver()
    {
        using var kit = new Kit(movementType: 2);
        kit.Services.ListInventory(kit.Player, kit.Vendor.Guid);
        Run(kit.World, PauseMs - 1000, step: 1000);
        Assert.False(kit.Vendor.IsMoving);

        Run(kit.World, 2000);
        Assert.True(kit.Vendor.IsMoving);
        Assert.Equal((Kit.FarNodeX, 0f), (kit.Vendor.Spline!.EndX, kit.Vendor.Spline.EndY));
    }

    [Fact]
    public void ARandomWanderingVendor_PausesItsWanderOnGossipHello()
    {
        using var kit = new Kit(movementType: 1);
        kit.Services.GossipHello(kit.Player, kit.Vendor.Guid);
        Assert.False(kit.Vendor.IsMoving);
        (float x, float y) = (kit.Vendor.X, kit.Vendor.Y);

        Run(kit.World, 60_000, step: 500);
        Assert.Equal((x, y), (kit.Vendor.X, kit.Vendor.Y));
        Assert.True(Assert.IsType<RandomMovementGenerator>(kit.Vendor.Motion.Top).NextMoveMs > 100_000);
    }

    [Fact]
    public void AVendorWithNoMovementPause_KeepsWalking_AndASaleOutOfRangeFindsNoVendor()
    {
        // vmangos CREATURE_FLAG_EXTRA_NO_MOVEMENT_PAUSE (0x20, CreatureDefines.h:162): the handlers skip the pause.
        using var kit = new Kit(movementType: 2, extraFlags: 0x20);
        kit.Services.ListInventory(kit.Player, kit.Vendor.Guid);
        Assert.True(kit.Vendor.IsMoving);

        Run(kit.World, 6000);
        kit.Services.SellItem(kit.Player, kit.Vendor.Guid, kit.ItemGuid, 1);
        Assert.Equal(SellResult.CantFindVendor, kit.SellResult());
    }

    /// <summary>One player beside a friendly vendor that walks a two-node path (or wanders), with the real creature lookup.</summary>
    private sealed class Kit : IDisposable
    {
        public const float FarNodeX = 40f;

        public Kit(byte movementType, uint extraFlags = 0)
        {
            CreatureTemplate template = Template() with
            {
                NpcFlags = (uint)NpcFlags.Vendor,
                Faction = 2,
                ExtraFlags = extraFlags,
                ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos,
            };
            CreatureContent content = Content([template], [Spawn(1, WolfEntry, 0, 0, movementType: movementType, wander: 10)],
                waypoints: [(1u, new CreatureWaypoint(1, FarNodeX, 0, 83.5f, 100f, 0)), (1u, new CreatureWaypoint(2, 0, 0, 83.5f, 100f, 0))]);
            (World, _, CreatureMapSystem system) = CreateAiSystem(content);
            (Player, Session) = AddPlayer(World, 1, 2, 0);
            Vendor = Assert.Single(system.Creatures);
            if (movementType == 1)
            {
                // The first wander leg starts 1 s after spawn (RandomMovementGenerator.cpp:75).
                for (int i = 0; i < 50 && !Vendor.IsMoving; i++)
                {
                    World.RunTick(100);
                }

                Assert.True(Vendor.IsMoving);
            }

            var factions = new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 8, 0, 0)]);
            Services = new QuestNpcServices(new QuestStore(new QuestContent([], [], [])), NpcStore.Empty,
                new QuestNpcDependencies(Creatures: new CreatureQuestLookup(factions)), new QuestNpcOptions(), new QuestFlowKit.RecordingSink(),
                () => 100, NullLogger.Instance);
            Services.CompleteLoad(Services.Track(Player), new CharacterQuestData([], []));
            Session.Clear();
        }

        public WorldRuntime World { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Vendor { get; }

        public QuestNpcServices Services { get; }

        public ObjectGuid ItemGuid { get; } = ObjectGuid.WithEntry(HighGuid.Item, 0, 77);

        public List<byte[]> Drain(WorldOpcode opcode) => Packets(Session, opcode);

        public SellResult SellResult() => (SellResult)Assert.Single(Drain(WorldOpcode.SmsgSellItem))[16];

        public void Dispose() => World.Dispose();
    }
}

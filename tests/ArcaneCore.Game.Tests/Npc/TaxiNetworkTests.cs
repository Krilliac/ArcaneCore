using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

public sealed class TaxiNetworkTests
{
    [Fact]
    public void MaskDropsOnlyNodesWhoseOutgoingPathsAreAllScripted()
    {
        // vmangos DBCStores.cpp "Initialize global taxinodes mask": a node whose every outgoing path is a SEND_TAXI
        // (effect 123) path leaves the network; a node with no outgoing path at all stays in it.
        var content = NpcContent.Empty with
        {
            TaxiNodes = [new TaxiNode { Id = 1 }, new TaxiNode { Id = 2 }, new TaxiNode { Id = 3 },
                new TaxiNode { Id = 4 }, new TaxiNode { Id = 33 }],
            TaxiPaths = [new TaxiPath { Id = 10, FromNode = 1, ToNode = 2 },
                new TaxiPath { Id = 11, FromNode = 2, ToNode = 3 },
                new TaxiPath { Id = 12, FromNode = 2, ToNode = 4 },
                new TaxiPath { Id = 13, FromNode = 33, ToNode = 1 }],
        };

        var store = new NpcStore(content, scriptedTaxiPathIds: new HashSet<uint> { 10, 11 });
        Assert.Equal(0b1110u, store.TaxiNodesMask[0]);
        Assert.Equal(1u, store.TaxiNodesMask[1]);
        Assert.False(store.IsNetworkNode(1));   // its only path (10) is scripted
        Assert.True(store.IsNetworkNode(2));    // one scripted (11) and one ordinary (12) path
        Assert.True(store.IsNetworkNode(3));    // no outgoing path: kept, as in vmangos
        Assert.True(store.IsNetworkNode(4));
        Assert.False(store.IsNetworkNode(0));
        Assert.False(store.IsNetworkNode(257));
    }

    [Fact]
    public void NearestFlightMasterNodeSkipsTheCloserScriptOnlyNode()
    {
        var content = NpcContent.Empty with
        {
            TaxiNodes =
            [
                new TaxiNode { Id = 1, MapId = 0, X = 0, Z = 83.5f, MountAlliance = 1 },
                new TaxiNode { Id = 2, MapId = 0, X = 4, Z = 83.5f, MountAlliance = 1 },
                new TaxiNode { Id = 3, MapId = 0, X = 8, Z = 83.5f, MountAlliance = 1 },
            ],
            TaxiPaths = [new TaxiPath { Id = 10, FromNode = 1, ToNode = 3 },
                new TaxiPath { Id = 11, FromNode = 2, ToNode = 3 }],
        };
        using var kit = new NpcServiceKit(NpcFlags.FlightMaster, content);
        kit.Services.ReplaceNpcs(new NpcStore(content, new HashSet<uint> { 10 }));
        kit.State.TaxiMask[0] = 0b10; // only the ordinary node is known

        kit.Services.TaxiNodeStatusQuery(kit.Player, kit.Npc.Guid);

        Assert.Equal(1, kit.Single(WorldOpcode.SmsgTaxinodeStatus)[8]);
    }
}

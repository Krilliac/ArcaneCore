using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>A new character's starting flight paths come from its race's race_taxi_start row (vmangos PlayerTaxi::InitTaxiNodes).</summary>
public sealed class RaceTaxiStartTests
{
    [Fact]
    public void NewHuman_KnowsTheStormwindNodeBit_AndTheMaskIsSentToTheSink()
    {
        // ChrRaces.dbc race 1 (human) startingTaxiMask is 2: node 2, Stormwind.
        NpcContent content = NpcContent.Empty with { RaceTaxiStarts = [new RaceTaxiStart { Race = 1, Mask = 2 }] };

        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content);

        Assert.Equal(2u, kit.State.TaxiMask[0]);
        Assert.Equal(2u, kit.Sink.TaxiMasks[^1][0]);
    }

    [Fact]
    public void NewCharacter_OfARaceWithoutARow_KnowsNoNode()
    {
        NpcContent content = NpcContent.Empty with { RaceTaxiStarts = [new RaceTaxiStart { Race = 2, Mask = 0x400000 }] };

        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content);

        Assert.Equal(0u, kit.State.TaxiMask[0]);
        Assert.Empty(kit.Sink.TaxiMasks);
    }

    [Fact]
    public void NewNightElf_KnowsBothBitsOfItsTwoBitMask()
    {
        // ChrRaces.dbc race 4 (night elf) startingTaxiMask is 0x6000000: nodes 26 and 27 (Rut'theran Village, Auberdine).
        NpcContent content = NpcContent.Empty with { RaceTaxiStarts = [new RaceTaxiStart { Race = 4, Mask = 0x6000000 }] };

        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content, race: Race.NightElf);

        Assert.Equal(0x6000000u, kit.State.TaxiMask[0]);
        Assert.Equal(0x6000000u, kit.Sink.TaxiMasks[^1][0]);
    }

    [Fact]
    public void NewOrc_KnowsTheOrgrimmarNodeBit()
    {
        // ChrRaces.dbc race 2 (orc) startingTaxiMask is 0x400000: node 23, Orgrimmar.
        NpcContent content = NpcContent.Empty with { RaceTaxiStarts = [new RaceTaxiStart { Race = 2, Mask = 0x400000 }, new RaceTaxiStart { Race = 4, Mask = 0x6000000 }] };

        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content, race: Race.Orc);

        Assert.Equal(0x400000u, kit.State.TaxiMask[0]);
    }
}

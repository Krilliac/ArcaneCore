using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.Talents.TalentRig;
using static ArcaneCore.Game.Tests.Talents.TalentServiceLearnTests;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// ResetTalents and the respec economy. Reference: vmangos Player::ResetTalents (Player.cpp:4075-4147),
/// GetResetTalentsCost (:4053-4073), SendTalentWipeConfirm (:8259-8265).
/// </summary>
public sealed class TalentServiceResetTests
{
    private static void SpendThreePointsInTwoTrees(TalentRig rig)
    {
        rig.Learn(1, 0);        // tab 1: 1 point
        rig.Learn(6, 1);        // tab 3: rank 2 = 2 points
        Assert.Equal(3u, rig.Service.UsedPoints(rig.Player));
    }

    [Fact]
    public void NothingSpent_ReturnsFalse_AndTakesNothing()
    {
        using var rig = new TalentRig();
        uint money = rig.Player.Money;
        int resets = 0;
        rig.Service.TalentsReset += _ => resets++;

        Assert.False(rig.Service.ResetTalents(rig.Player, noCost: false));

        Assert.Equal(money, rig.Player.Money);
        Assert.Equal(default, rig.Service.StateOf(rig.Player).Respec);
        Assert.Equal(0, resets);
        Assert.Empty(rig.Session.Sent);
        Assert.Empty(rig.Sink.Respecs);
    }

    [Fact]
    public void Reset_RemovesTheClassTalents_RefundsPoints_ChargesGold_AndAdvancesTheEconomy()
    {
        using var rig = new TalentRig();
        SpendThreePointsInTwoTrees(rig);
        rig.Kit.Spellbook.Teach(rig.Player, MageR1, 100);   // another class's talent spell and an ordinary spell
        Assert.True(rig.Kit.System.HasAura(rig.Player, T6R2));
        uint allowance = TalentRules.PointsForLevel(20, 1.0);
        rig.Session.Clear();
        int resets = 0;
        rig.Service.TalentsReset += _ => resets++;

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: false));

        Assert.False(rig.Has(T1R1));
        Assert.False(rig.Has(T6R2));
        Assert.False(rig.Kit.System.HasAura(rig.Player, T6R2));
        Assert.True(rig.Has(MageR1));                     // vmangos Player.cpp:4101-4104: other classes' talent spells stay
        Assert.True(rig.Has(100));
        Assert.Equal(2, Packets(rig.Session, WorldOpcode.SmsgRemovedSpell).Count);
        Assert.Equal(allowance - 1, rig.Free);            // MageR1 still counts as a spent point in the used-point sum
        Assert.Equal(100 * Gold - Gold, rig.Player.Money);
        Assert.Equal(new RespecState(1, Now), rig.Service.StateOf(rig.Player).Respec);
        Assert.Equal([new RespecState(1, Now)], rig.Sink.Respecs);
        Assert.Equal(1, rig.Sink.CharacterChanges);
        Assert.Equal(1, resets);
    }

    [Fact]
    public void TheAuraSpellsATalentTriggersGoWithIt()
    {
        using var rig = new TalentRig();
        rig.Learn(7, 0);                                   // 3061 applies a Dummy aura and names 3062 as its trigger spell
        rig.Kit.Spellbook.Teach(rig.Player, 3062);
        rig.Kit.System.CastSpell(rig.Player, 3062, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(rig.Kit.System.HasAura(rig.Player, 3062));

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.False(rig.Kit.System.HasAura(rig.Player, 3061));
        Assert.False(rig.Kit.System.HasAura(rig.Player, 3062));   // vmangos Player.cpp:4108-4112
    }

    [Fact]
    public void TheCostChain_IsOneFiveTen()
    {
        using var rig = new TalentRig(money: 1000 * Gold);
        uint[] expected = [1, 5, 10];
        foreach (uint gold in expected)
        {
            rig.Learn(1, 0);
            uint before = rig.Player.Money;
            Assert.True(rig.Service.ResetTalents(rig.Player, noCost: false));
            Assert.Equal(gold * Gold, before - rig.Player.Money);
        }

        Assert.Equal(3u, rig.Service.StateOf(rig.Player).Respec.Multiplier);
    }

    [Fact]
    public void NotEnoughMoney_RemovesNothing_AndSendsBuyFailed()
    {
        using var rig = new TalentRig(money: Gold - 1);
        rig.Learn(1, 0);
        rig.Session.Clear();

        Assert.False(rig.Service.ResetTalents(rig.Player, noCost: false));

        Assert.True(rig.Has(T1R1));
        Assert.Equal(Gold - 1, rig.Player.Money);
        byte[] body = Assert.Single(Packets(rig.Session, WorldOpcode.SmsgBuyFailed));
        Assert.Equal(NpcPackets.BuyFailed(ObjectGuid.Empty, 0, BuyResult.NotEnoughMoney).ToArray(), body);
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgRemovedSpell));
        Assert.Equal(0u, rig.Service.StateOf(rig.Player).Respec.Multiplier);
    }

    [Fact]
    public void ExactlyEnoughMoney_Succeeds()
    {
        using var rig = new TalentRig(money: Gold);
        rig.Learn(1, 0);

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: false));

        Assert.Equal(0u, rig.Player.Money);
    }

    [Fact]
    public void NoCostReset_ChargesNothing_AndLeavesTheEconomyAlone()
    {
        using var rig = new TalentRig(money: 0);
        SpendThreePointsInTwoTrees(rig);

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.Equal(0u, rig.Player.Money);
        Assert.Equal(default, rig.Service.StateOf(rig.Player).Respec);
        Assert.Empty(rig.Sink.Respecs);
        Assert.Equal(0, rig.Sink.CharacterChanges);
        Assert.False(rig.Has(T1R1));
    }

    [Fact]
    public void ResetIsRefusedDuringAQuestSettlement()
    {
        using var rig = new TalentRig();
        rig.Learn(1, 0);
        Guid operation = Guid.NewGuid();
        Assert.True(rig.Player.BeginQuestSettlement(operation));

        Assert.False(rig.Service.ResetTalents(rig.Player, noCost: false));
        Assert.True(rig.Has(T1R1));
        Assert.Equal(100 * Gold, rig.Player.Money);

        Assert.True(rig.Player.EndQuestSettlement(operation));
        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: false));   // positive control
    }

    [Fact]
    public void PriceDecaysAMonthAtATime_FromTheStoredState()
    {
        using var rig = new TalentRig(money: 1000 * Gold);
        rig.Service.LoadState(rig.Player, new RespecState(3, Now - (RespecCost.MonthSeconds + 10)), []);
        rig.Learn(1, 0);
        uint before = rig.Player.Money;

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: false));

        Assert.Equal(10 * Gold, before - rig.Player.Money);                // 3 - 1 month = 2: 2 * 5 gold
        Assert.Equal(new RespecState(3, Now), rig.Service.StateOf(rig.Player).Respec);   // decayed multiplier 2, then + 1
    }

    [Fact]
    public void OverspendAtLogin_RunsTheNoCostReset_AndRaisesTheEvent()
    {
        using var rig = new TalentRig(level: 12);
        int resets = 0;
        rig.Service.TalentsReset += _ => resets++;
        rig.Kit.Spellbook.Teach(rig.Player, T1R3, T3R1, T6R1);   // 3 + 1 + 1 = 5 spent; level 12 allows 3

        rig.Service.InitTalentForLevel(rig.Player);

        Assert.Equal(1, resets);
        Assert.False(rig.Has(T1R3));
        Assert.Equal(TalentRules.PointsForLevel(12, 1.0), rig.Free);
        Assert.Equal(100 * Gold, rig.Player.Money);              // free
    }

    [Fact]
    public void WipeConfirm_QuotesTheCurrentPrice_AndPersistsVmangosDecayWhenItChangesTheState()
    {
        using var rig = new TalentRig();
        rig.Service.LoadState(rig.Player, new RespecState(10, Now - (RespecCost.MonthSeconds + 5)), []);
        ObjectGuid trainer = ObjectGuid.WithEntry(HighGuid.Unit, 500, 77);

        rig.Service.SendWipeConfirm(rig.Player, trainer);

        byte[] body = Assert.Single(Packets(rig.Session, WorldOpcode.MsgTalentWipeConfirm));
        Assert.Equal(TalentPackets.WipeConfirm(trainer, 45 * Gold).ToArray(), body);   // 10 - 1 month = 9 steps * 5 gold
        Assert.Equal(new RespecState(9, Now - (RespecCost.MonthSeconds + 5)), rig.Service.StateOf(rig.Player).Respec);
        Assert.Equal([new RespecState(9, Now - (RespecCost.MonthSeconds + 5))], rig.Sink.Respecs);
    }

    [Fact]
    public void WipeConfirm_IdempotentSwitch_QuotesWithoutChangingTheStoredState()
    {
        using var rig = new TalentRig(options: new TalentOptions { IdempotentRespecDecay = true });
        var stored = new RespecState(10, Now - (RespecCost.MonthSeconds + 5));
        rig.Service.LoadState(rig.Player, stored, []);

        rig.Service.SendWipeConfirm(rig.Player, ObjectGuid.WithEntry(HighGuid.Unit, 500, 77));
        rig.Service.SendWipeConfirm(rig.Player, ObjectGuid.WithEntry(HighGuid.Unit, 500, 77));

        List<byte[]> bodies = Packets(rig.Session, WorldOpcode.MsgTalentWipeConfirm);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);                       // asking twice quotes the same price
        Assert.Equal(stored, rig.Service.StateOf(rig.Player).Respec);
        Assert.Empty(rig.Sink.Respecs);
    }

    [Theory]
    [InlineData(TalentEmptyConfirmCost.Current, 1u)]
    [InlineData(TalentEmptyConfirmCost.Zero, 0u)]
    public void EmptyWipeConfirm_HasNoGuid_AndEitherTheCurrentPriceOrZero(TalentEmptyConfirmCost mode, uint gold)
    {
        // vmangos SendTalentWipeConfirm(Empty) still fills the cost (Player.cpp:8259-8265); mangos-classic writes 0.
        using var rig = new TalentRig(options: new TalentOptions { EmptyConfirmCost = mode });

        rig.Service.SendEmptyWipeConfirm(rig.Player);

        byte[] body = Assert.Single(Packets(rig.Session, WorldOpcode.MsgTalentWipeConfirm));
        Assert.Equal(TalentPackets.WipeConfirm(ObjectGuid.Empty, gold * Gold).ToArray(), body);
    }
}

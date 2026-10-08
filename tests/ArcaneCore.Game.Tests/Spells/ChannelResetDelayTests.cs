using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The end of a channel (vmangos Spell::SendChannelUpdate(0, interrupted), Spell.cpp:4801-4823, and ChannelResetEvent,
/// Spell.cpp:8341-8357): an interrupted channel clears UNIT_FIELD_CHANNEL_OBJECT and UNIT_CHANNEL_SPELL at once and sends the zero
/// MSG_CHANNEL_UPDATE (Unit::CancelSpellChannelingAnimationInstantly, Unit.cpp:10747-10755); a channel that ends normally keeps them
/// for 1000 ms ("else, we have some visual bugs (arcane projectile, last tick)") and then does the same, unless a new channel is
/// running by then. A new channel cast during that second resets the old values first (Spell.cpp:3463-3469).
/// </summary>
public sealed class ChannelResetDelayTests
{
    [Fact]
    public void NormalEnd_KeepsTheChannelFieldsForOneSecond()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        session.Clear();

        kit.Advance(5000);

        Assert.Null(kit.System.GetState(player.Guid)?.CurrentCast);
        Assert.False(kit.System.HasAura(player, ChannelSpell));
        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(player.Guid.Value, player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Empty(Packets(session, WorldOpcode.MsgChannelUpdate));

        kit.Advance(900);
        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Empty(Packets(session, WorldOpcode.MsgChannelUpdate));

        kit.Advance(100);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Packets(session, WorldOpcode.MsgChannelUpdate).Single());

        kit.Advance(2000);
        Assert.Single(Packets(session, WorldOpcode.MsgChannelUpdate));
    }

    [Fact]
    public void FinishChannel_IsANormalEnd_AndAlsoWaitsOneSecond()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        session.Clear();

        // vmangos Unit::FinishSpell(CURRENT_CHANNELED_SPELL) calls SendChannelUpdate(0) with interrupted = false.
        Assert.True(kit.System.FinishChannel(player));

        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Empty(Packets(session, WorldOpcode.MsgChannelUpdate));

        kit.Advance(1000);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Single(Packets(session, WorldOpcode.MsgChannelUpdate));
    }

    [Fact]
    public void InterruptedChannel_ResetsAtOnce()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        session.Clear();

        kit.System.CancelChannel(player);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(0ul, player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Single(Packets(session, WorldOpcode.MsgChannelUpdate));

        kit.Advance(2000);
        Assert.Single(Packets(session, WorldOpcode.MsgChannelUpdate));
    }

    [Fact]
    public void NewChannel_DuringThePendingReset_ResetsTheOldValuesFirst_AndIsNotClearedByTheOldTimer()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        kit.Advance(5000);
        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell)); // pending reset
        session.Clear();

        kit.Advance(500);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());

        // The old values went at once (zero update), then the new channel started.
        List<WorldOpcode> opcodes = Opcodes(session).Where(o => o is WorldOpcode.MsgChannelUpdate or WorldOpcode.MsgChannelStart).ToList();
        Assert.Equal([WorldOpcode.MsgChannelUpdate, WorldOpcode.MsgChannelStart], opcodes);
        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));

        // The first channel's second runs out: the new channel keeps its fields.
        kit.Advance(1000);
        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(SpellCastState.Casting, kit.System.GetState(player.Guid)?.CurrentCast?.State);
    }

    [Fact]
    public void RemovedUnit_DropsThePendingReset_WithTheFieldsCleared()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        kit.Advance(5000);

        kit.System.RemoveUnit(player);

        // ChannelResetEvent::Abort (the unit's events are killed) does the reset as well.
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Null(kit.System.GetState(player.Guid));
    }
}

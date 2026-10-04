using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// SMSG_SET_FLAT_SPELL_MODIFIER (0x266) / SMSG_SET_PCT_SPELL_MODIFIER (0x267): u8 bit, u8 op, i32 value, one packet per set
/// bit of the mod's class mask carrying the sum of every mod of that op and type that has the bit (vmangos Player::SendSpellMod,
/// Player.cpp:17650-17672; wow_messages smsg_set_flat_spell_modifier.wowm). A removal sends the sums without the removed mod.
/// The client needs them to show modified costs and cast bars.
/// </summary>
public sealed class SpellModPacketTests
{
    private const uint CostFlat = 946001;     // flat cost -2, bits 0 and 3
    private const uint CostFlat2 = 946002;    // flat cost -3, bit 3
    private const uint CritPct = 946003;      // pct crit... op 10 = casting time -100, bit 1
    private const uint HighBit = 946004;      // flat damage +4, bit 40 through the mask source
    private const uint Other = 946005;

    private static SpellTestKit Kit() => new(
        Flat(CostFlat, SpellModOp.Cost, -2, 0b1001),
        Flat(CostFlat2, SpellModOp.Cost, -3, 0b1000),
        Pct(CritPct, SpellModOp.CastingTime, -100, 0b10),
        Flat(HighBit, SpellModOp.Damage, 4, 1),
        Flat(Other, SpellModOp.Damage, 1, 0b100));

    private static byte[] Packet(byte bit, byte op, int value) => [bit, op, .. BitConverter.GetBytes(value)];

    [Fact]
    public void AFlatMod_SendsOnePacketPerMaskBit_InAscendingOrder()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, CostFlat);

        Assert.Equal([Packet(0, 14, -2), Packet(3, 14, -2)], Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
        Assert.Empty(Packets(session, WorldOpcode.SmsgSetPctSpellModifier));
    }

    [Fact]
    public void APctMod_UsesThePctOpcode()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, CritPct);

        Assert.Equal([Packet(1, 10, -100)], Packets(session, WorldOpcode.SmsgSetPctSpellModifier));
        Assert.Empty(Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
    }

    [Fact]
    public void OverlappingMods_SendTheSumPerBit()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.LearnSpell(player, CostFlat);
        session.Clear();

        kit.System.LearnSpell(player, CostFlat2);

        // Only the new mod's bit (3) is sent, with both mods summed: -2 + -3.
        Assert.Equal([Packet(3, 14, -5)], Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
    }

    [Fact]
    public void ARemoval_SendsTheRecomputedSums_AndZeroWhenNoneRemain()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.LearnSpell(player, CostFlat);
        kit.System.LearnSpell(player, CostFlat2);
        session.Clear();

        kit.System.RemoveSpell(player, CostFlat2);
        Assert.Equal([Packet(3, 14, -2)], Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));

        session.Clear();
        kit.System.RemoveSpell(player, CostFlat);
        Assert.Equal([Packet(0, 14, 0), Packet(3, 14, 0)], Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
    }

    [Fact]
    public void ABitAboveThirtyOne_IsSentAsItsOwnIndex()
    {
        using SpellTestKit kit = Kit();
        kit.System.Mods.MaskSource = new Masks(1UL << 40);
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, HighBit);

        Assert.Equal([Packet(40, 0, 4)], Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
    }

    [Fact]
    public void ModsOfDifferentOps_AreSummedSeparately()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.LearnSpell(player, CostFlat);
        session.Clear();

        kit.System.LearnSpell(player, Other);   // op 0 (damage), bit 2

        Assert.Equal([Packet(2, 0, 1)], Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
    }

    [Fact]
    public void TheSwitch_StopsTheClientPackets()
    {
        using SpellTestKit kit = Kit();
        kit.System.Mods.Options.SendClientModifiers = false;
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.LearnSpell(player, CostFlat);

        Assert.Empty(Packets(session, WorldOpcode.SmsgSetFlatSpellModifier));
        Assert.Single(kit.System.Mods.ModsOf(player, SpellModOp.Cost));   // the server side still works
    }

    private sealed class Masks(ulong mask) : IClassMaskSource
    {
        public ulong? TryGetMask(uint spellId, int effectIndex) => spellId == HighBit ? mask : null;
    }
}

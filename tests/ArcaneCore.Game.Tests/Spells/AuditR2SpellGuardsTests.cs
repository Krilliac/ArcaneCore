using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class AuditR2SpellGuardsTests
{
    [Fact]
    public void SpellGo_With300Hits_And300Misses_CapsBothAt255AndKeepsCountsConsistent()
    {
        ObjectGuid[] hits = Enumerable.Range(1, 300).Select(i => ObjectGuid.Player((uint)i)).ToArray();
        (ObjectGuid, SpellMissInfo)[] misses = Enumerable.Range(1, 300).Select(i => (ObjectGuid.Player((uint)(1000 + i)), SpellMissInfo.Resist)).ToArray();

        byte[] packet = SpellPackets.BuildSpellGo(ObjectGuid.Player(1), ObjectGuid.Player(1), 101, SpellCastFlags.Unknown9, hits, misses, SpellCastTargets.ForSelf());

        int offset = 4 + 4 + 2; // two packed GUIDs (one byte each for a low guid with a zero high part), u32 spell, u16 flags
        Assert.Equal(255, packet[offset]);
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(offset + 1)));
        int missOffset = offset + 1 + (255 * 8);
        Assert.Equal(255, packet[missOffset]);
        Assert.Equal(1001ul, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(missOffset + 1)));
        Assert.Equal((byte)SpellMissInfo.Resist, packet[missOffset + 9]);
        int targetsOffset = missOffset + 1 + (255 * 9);
        Assert.Equal((ushort)SpellCastTargetFlags.Self, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(targetsOffset)));
        Assert.Equal(targetsOffset + 2, packet.Length);
    }

    [Fact]
    public void AddCooldown_ClampsAbsurdRecoveryTimes()
    {
        const uint spellId = 49101;
        SpellInfo spell = SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = uint.MaxValue - 5, StartRecoveryTime = 0, StartRecoveryCategory = 0 };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: false));
        kit.Advance(100);
        InitialSpellCooldown active = Assert.Single(kit.System.GetActiveCooldowns(player));
        Assert.True(active.CooldownMs > 0 && active.CooldownMs <= SpellSystem.MaxReasonableCooldownMs);
    }

    [Fact]
    public void SelfTriggeringSpell_IsCutAtMaxTriggerDepth()
    {
        const uint spellId = 49102;
        SpellInfo spell = SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.TriggerSpell, 0, trigger: spellId)) with { StartRecoveryTime = 0, StartRecoveryCategory = 0 };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        string key = AnomalyLedger.KeyOf("spell", "trigger-recursion-cut");
        long before = AnomalyLedger.Shared.CountOf(key);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(before + 1, AnomalyLedger.Shared.CountOf(key));
    }

    [Fact]
    public void ClientDestination_OnSelfOnlySpell_IsRefused_ButTriggeredAndDestAreaSpellsKeepIt()
    {
        const uint selfSpell = 49103;
        const uint areaSpell = 49104;
        SpellInfo self = SpellTestKit.Spell(selfSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0));
        SpellInfo area = SpellTestKit.Spell(areaSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc)) with { RangeIndex = 4, Range = new SpellRange(0, 30) };
        using var kit = new SpellTestKit(self, area);
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, selfSpell, areaSpell);
        var dest = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (5000, 5000, 5000) };

        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(player, selfSpell, dest));
        var near = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (player.X + 5, player.Y, player.Z) };
        Assert.NotEqual(SpellCastResult.BadTargets, kit.System.HandleCastRequest(player, areaSpell, near));
    }
}

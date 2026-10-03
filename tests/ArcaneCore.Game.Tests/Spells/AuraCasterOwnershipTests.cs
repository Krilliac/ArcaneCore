using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Exact actor attribution across caster removal, GUID reuse, reapplication and real map transit.</summary>
public sealed class AuraCasterOwnershipTests
{
    private const uint AuraId = 900210;
    private const uint TriggerId = 900211;

    [Theory]
    [InlineData(AuraType.PeriodicDamage)]
    [InlineData(AuraType.PeriodicHeal)]
    [InlineData(AuraType.PeriodicEnergize)]
    [InlineData(AuraType.PeriodicTriggerSpell)]
    public void ReplacementCasterPending_DoesNotPauseOrReceiveAttributionForOriginalAura(AuraType type)
    {
        using var kit = CreateKit(type);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        target.Health = 30;
        SpellAuraHolder holder = Apply(kit, caster, target);
        kit.System.RemoveUnit(caster);
        kit.World.RemovePlayer(caster);
        (Player replacement, _) = kit.AddPlayer(1);
        Assert.NotSame(caster, replacement);
        Assert.Equal(caster.Guid, replacement.Guid);
        Assert.True(replacement.BeginQuestSettlement(Guid.NewGuid()));
        session.Clear();

        kit.Advance(1000);

        AssertProgress(holder, duration: 2000, ticks: 1);
        AssertTickResult(type, target, expectedCaster: target, sink);
        Assert.Equal(60u, replacement.Health);
        Assert.Equal(0u, SpellSystem.GetPower(replacement, PowerType.Rage));
        Assert.DoesNotContain(sink.Calls, call => ReferenceEquals(call.Caster, replacement));
        AssertTickLog(session, type, target.Guid, caster.Guid);
    }

    [Theory]
    [InlineData(AuraType.PeriodicDamage)]
    [InlineData(AuraType.PeriodicHeal)]
    [InlineData(AuraType.PeriodicTriggerSpell)]
    public void MissingCaster_PreservesTargetFallbackAndOriginalPeriodicLogGuid(AuraType type)
    {
        using var kit = CreateKit(type);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        target.Health = 30;
        SpellAuraHolder holder = Apply(kit, caster, target);
        kit.World.RemovePlayer(caster);
        Assert.Null(target.Map!.FindPlayer(caster.Guid));
        session.Clear();

        kit.Advance(1000);

        AssertProgress(holder, duration: 2000, ticks: 1);
        AssertTickResult(type, target, expectedCaster: target, sink);
        AssertTickLog(session, type, target.Guid, caster.Guid);
    }

    [Fact]
    public void SameCaster_ReapplicationStacksRefreshesAndStillPausesWhileThatActorIsPending()
    {
        using var kit = CreateKit(AuraType.PeriodicDamage);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 30;
        SpellAuraHolder holder = Apply(kit, caster, target);
        Assert.Same(holder, Apply(kit, caster, target));
        Assert.Equal(2, holder.StackAmount);
        kit.Advance(1000);
        Assert.Equal(20u, target.Health);
        Assert.Same(caster, Assert.Single(sink.Calls).Caster);
        Assert.Same(holder, Apply(kit, caster, target));
        Assert.Equal(3, holder.StackAmount);
        Assert.Same(holder, Apply(kit, caster, target));
        Assert.Equal(3, holder.StackAmount); // capped, with the normal duration refresh
        AssertProgress(holder, duration: 3000, ticks: 1);
        Guid operation = Guid.NewGuid();
        Assert.True(caster.BeginQuestSettlement(operation));

        kit.Advance(2000);

        AssertProgress(holder, duration: 3000, ticks: 1);
        Assert.Equal(20u, target.Health);
        Assert.Single(sink.Calls);
        Assert.True(caster.EndQuestSettlement(operation));
        kit.Advance(1000);
        AssertProgress(holder, duration: 2000, ticks: 2);
        Assert.Equal(5u, target.Health);
        Assert.Equal(new uint[] { 10, 15 }, sink.Calls.Select(call => call.Amount));
        Assert.All(sink.Calls, call => Assert.Same(caster, call.Caster));
    }

    [Fact]
    public void ReplacementCasterReapplication_ReplacesOldHolderAndResetsStackDurationAndTickSchedule()
    {
        using var kit = CreateKit(AuraType.PeriodicDamage);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 30;
        SpellAuraHolder oldHolder = Apply(kit, caster, target);
        Apply(kit, caster, target);
        kit.Advance(500);
        AssertProgress(oldHolder, duration: 2500, ticks: 0);
        kit.System.RemoveUnit(caster);
        kit.World.RemovePlayer(caster);
        (Player replacement, _) = kit.AddPlayer(1);

        SpellAuraHolder fresh = Apply(kit, replacement, target);

        Assert.NotSame(oldHolder, fresh);
        Assert.True(oldHolder.IsRemoved);
        Assert.Equal(1, fresh.StackAmount);
        AssertProgress(fresh, duration: 3000, ticks: 0);
        Assert.Equal(oldHolder.Slot, fresh.Slot);
        kit.Advance(500);
        AssertProgress(fresh, duration: 2500, ticks: 0);
        Assert.Equal(30u, target.Health);
        Assert.Empty(sink.Calls);
        kit.Advance(500);
        AssertProgress(fresh, duration: 2000, ticks: 1);
        Assert.Equal(25u, target.Health);
        Assert.Same(replacement, Assert.Single(sink.Calls).Caster);
    }

    [Fact]
    public void LateRemovalOfOldCaster_PreservesReplacementAuraCooldownAndAttribution()
    {
        using var kit = CreateKit(AuraType.PeriodicDamage);
        var sink = RecordDamage(kit);
        (Player oldCaster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 30;
        Apply(kit, oldCaster, target);
        kit.System.RemoveUnit(oldCaster);
        kit.World.RemovePlayer(oldCaster);
        (Player replacement, _) = kit.AddPlayer(1);
        replacement.Health = 30;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(replacement, HotSpell, SpellCastTargets.ForSelf(), triggered: true));
        SpellAuraHolder replacementHolder = Assert.Single(kit.System.GetAuras(replacement));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(replacement, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(kit.System.IsSpellReady(replacement, kit.Store.Get(CooldownSpell)!));

        kit.System.RemoveUnit(oldCaster);

        Assert.Same(replacementHolder, Assert.Single(kit.System.GetAuras(replacement)));
        Assert.False(replacementHolder.IsRemoved);
        Assert.False(kit.System.IsSpellReady(replacement, kit.Store.Get(CooldownSpell)!));
        kit.Advance(1000);
        Assert.Equal(35u, replacement.Health);
        AssertProgress(replacementHolder, duration: 2000, ticks: 1);
        Assert.Same(replacement, Assert.Single(sink.Calls, call => call.IsHeal).Caster);
        Assert.Same(target, Assert.Single(sink.Calls, call => !call.IsHeal).Caster);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevokedCaster_ReaddingSameObjectDoesNotReviveOldAuraOwnership(bool explicitRemoval)
    {
        using var kit = CreateKit(AuraType.PeriodicDamage);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 30;
        SpellAuraHolder holder = Apply(kit, caster, target);
        if (explicitRemoval)
        {
            kit.Advance(100); // its own idle state is gone; the outgoing holder remains
            Assert.Null(kit.System.GetState(caster.Guid));
            kit.System.RemoveUnit(caster);
            kit.World.RemovePlayer(caster);
        }
        else
        {
            Assert.NotNull(kit.System.GetState(caster.Guid));
            kit.World.RemovePlayer(caster);
            kit.Advance(100); // ordinary non-transit Update must Forget/revoke the old actor
            Assert.Null(kit.System.GetState(caster.Guid));
        }
        kit.World.AddPlayer(caster);
        Assert.Same(caster, target.Map!.FindPlayer(caster.Guid));

        kit.Advance(900);

        Assert.Same(holder, Assert.Single(kit.System.GetAuras(target)));
        AssertProgress(holder, duration: 2000, ticks: 1);
        Assert.Equal(25u, target.Health);
        Assert.Same(target, Assert.Single(sink.Calls).Caster);
    }

    [Fact]
    public void RealFarTransit_SameCasterReturningToTargetMapKeepsOriginalOwnership()
    {
        using var kit = CreateKit(AuraType.PeriodicDamage);
        LoadMaps(kit.World);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 30;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Passive, SpellCastTargets.ForSelf(), triggered: true));
        SpellAuraHolder ownAura = Assert.Single(kit.System.GetAuras(caster));
        SpellAuraHolder outgoing = Apply(kit, caster, target);
        var teleports = new TeleportService(kit.World, _ => { }, _ => { });
        kit.System.IsInTransit = unit => unit is Player player && teleports.IsBeingTeleportedFar(player);
        Assert.True(teleports.TeleportTo(caster, 1, 0, 0, 83.5f, 0));
        kit.World.RunTick(0);
        Assert.Null(caster.Map);
        Assert.True(teleports.IsBeingTeleportedFar(caster));

        kit.Advance(1000);

        Assert.Same(ownAura, Assert.Single(kit.System.GetAuras(caster)));
        Assert.Same(target, Assert.Single(sink.Calls).Caster); // unavailable map caster uses existing fallback
        AssertProgress(outgoing, duration: 2000, ticks: 1);
        Assert.True(teleports.HandleWorldportAck(caster));
        kit.World.RunTick(0);
        Assert.Equal(1u, caster.Map!.MapId);
        Assert.True(teleports.TeleportTo(caster, 0, 0, 0, 83.5f, 0));
        kit.World.RunTick(0);
        Assert.True(teleports.HandleWorldportAck(caster));
        kit.World.RunTick(0);
        Assert.Same(target.Map, caster.Map);
        Assert.Equal(0, teleports.PendingCount);

        kit.Advance(1000);

        Assert.Same(ownAura, Assert.Single(kit.System.GetAuras(caster)));
        Assert.Same(outgoing, Assert.Single(kit.System.GetAuras(target)));
        AssertProgress(outgoing, duration: 1000, ticks: 2);
        Assert.Equal(20u, target.Health);
        Assert.Equal(2, sink.Calls.Count);
        Assert.Same(caster, sink.Calls[1].Caster);
    }

    [Fact]
    public void TargetPending_StillPausesAnOrphanAuraWithoutCatchingUp()
    {
        using var kit = CreateKit(AuraType.PeriodicDamage);
        var sink = RecordDamage(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        target.Health = 30;
        SpellAuraHolder holder = Apply(kit, caster, target);
        kit.System.RemoveUnit(caster);
        kit.World.RemovePlayer(caster);
        Guid operation = Guid.NewGuid();
        Assert.True(target.BeginQuestSettlement(operation));
        session.Clear();

        kit.Advance(2000);

        AssertProgress(holder, duration: 3000, ticks: 0);
        Assert.Equal(30u, target.Health);
        Assert.Empty(sink.Calls);
        Assert.Empty(Packets(session, WorldOpcode.SmsgPeriodicauralog));
        Assert.True(target.EndQuestSettlement(operation));
        kit.Advance(1000);
        AssertProgress(holder, duration: 2000, ticks: 1);
        Assert.Same(target, Assert.Single(sink.Calls).Caster);
        Assert.Equal(25u, target.Health);
        AssertTickLog(session, AuraType.PeriodicDamage, target.Guid, caster.Guid);
    }

    private static SpellTestKit CreateKit(AuraType type)
        => new(Spell(AuraId, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.Unit, type,
            amplitude: 1000, misc: (int)PowerType.Rage, trigger: TriggerId)) with
        {
            Duration = new SpellDuration(3000, 0, 3000),
            RangeIndex = 4, Range = new SpellRange(0, 30), SpellVisual = 1, StackAmount = 3,
        }, Spell(TriggerId, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.Unit)));

    private static RecordingDamageSink RecordDamage(SpellTestKit kit)
    {
        var sink = new RecordingDamageSink();
        kit.System.Damage = sink;
        return sink;
    }

    private static SpellAuraHolder Apply(SpellTestKit kit, Player caster, Player target)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, AuraId, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        return Assert.Single(kit.System.GetAuras(target));
    }

    private static void AssertProgress(SpellAuraHolder holder, int duration, int ticks)
    {
        Assert.False(holder.IsRemoved);
        Assert.Equal(duration, holder.Duration);
        Assert.Equal(ticks, Assert.Single(holder.Auras.OfType<SpellAura>()).TickCount);
    }

    private static void AssertTickResult(AuraType type, Player target, Unit expectedCaster, RecordingDamageSink sink)
    {
        Assert.Equal(type switch
        {
            AuraType.PeriodicDamage or AuraType.PeriodicTriggerSpell => 25u,
            AuraType.PeriodicHeal => 35u,
            _ => 30u,
        }, target.Health);
        Assert.Equal(type == AuraType.PeriodicEnergize ? 5u : 0u, SpellSystem.GetPower(target, PowerType.Rage));
        if (type == AuraType.PeriodicEnergize)
        {
            Assert.Empty(sink.Calls);
            return;
        }
        DamageCall call = Assert.Single(sink.Calls);
        Assert.Same(expectedCaster, call.Caster);
        Assert.Same(target, call.Target);
        Assert.Equal(5u, call.Amount);
        Assert.Equal(type == AuraType.PeriodicHeal, call.IsHeal);
        Assert.Equal(type != AuraType.PeriodicTriggerSpell, call.Periodic);
        Assert.Equal(type == AuraType.PeriodicTriggerSpell ? TriggerId : AuraId, call.SpellId);
    }

    private static void AssertTickLog(FakeSession session, AuraType type, ObjectGuid target, ObjectGuid originalCaster)
    {
        if (type == AuraType.PeriodicTriggerSpell)
        {
            byte[] triggered = Assert.Single(Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog));
            int cursor = 0;
            Assert.Equal(target.Value, ReadPackedGuid(triggered, ref cursor));
            Assert.Equal(target.Value, ReadPackedGuid(triggered, ref cursor)); // actual fallback trigger actor
            Assert.Equal(TriggerId, ReadUInt32(triggered, ref cursor));
            Assert.Equal(5u, ReadUInt32(triggered, ref cursor));
            return;
        }
        byte[] body = Assert.Single(Packets(session, WorldOpcode.SmsgPeriodicauralog));
        int offset = 0;
        Assert.Equal(target.Value, ReadPackedGuid(body, ref offset));
        Assert.Equal(originalCaster.Value, ReadPackedGuid(body, ref offset));
        Assert.Equal(AuraId, ReadUInt32(body, ref offset));
        Assert.Equal(1u, ReadUInt32(body, ref offset));
        Assert.Equal((uint)type, ReadUInt32(body, ref offset));
        if (type == AuraType.PeriodicEnergize) Assert.Equal((uint)PowerType.Rage, ReadUInt32(body, ref offset));
        Assert.Equal(5u, ReadUInt32(body, ref offset));
        if (type == AuraType.PeriodicDamage)
        {
            Assert.Equal(0u, ReadUInt32(body, ref offset)); // physical school, no absorption or resistance
            Assert.Equal(0u, ReadUInt32(body, ref offset));
            Assert.Equal(0u, ReadUInt32(body, ref offset));
        }
        Assert.Equal(body.Length, offset);
    }

    private static ulong ReadPackedGuid(byte[] body, ref int offset)
    {
        byte mask = body[offset++];
        ulong guid = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            if ((mask & (1 << bit)) != 0) guid |= (ulong)body[offset++] << (bit * 8);
        }
        return guid;
    }

    private static uint ReadUInt32(byte[] body, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset));
        offset += 4;
        return value;
    }

    private static void LoadMaps(WorldRuntime world)
        => WorldMaps.Of(world).Load(new MapContent(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Synthetic origin", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Synthetic destination", ""),
        ], [], [], [], []));

    private sealed record DamageCall(Unit Caster, Unit Target, uint SpellId, uint Amount, bool IsHeal, bool Periodic);

    private sealed class RecordingDamageSink : IDamageSink
    {
        private readonly HealthOnlyDamageSink _inner = new();
        internal List<DamageCall> Calls { get; } = [];
        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            uint dealt = _inner.DealSpellDamage(caster, victim, spell, damage, periodic);
            Calls.Add(new DamageCall(caster, victim, spell.Id, dealt, IsHeal: false, Periodic: periodic));
            return dealt;
        }
        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
        {
            uint healed = _inner.Heal(caster, target, spell, amount);
            Calls.Add(new DamageCall(caster, target, spell.Id, healed, IsHeal: true, Periodic: true));
            return healed;
        }
    }
}

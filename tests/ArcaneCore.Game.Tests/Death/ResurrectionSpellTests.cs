using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// The resurrect effects (18 and 113) through casts on the world's request service (ResurrectionService): offers, corpse targets, the request
/// packet and acceptance (CMSG_RESURRECT_RESPONSE through <see cref="ResurrectionService.Respond"/>).
/// </summary>
public sealed class ResurrectionSpellTests
{
    private const uint Percentage = 990180;
    private const uint Flat = 990113;

    /// <summary>The request service of <paramref name="kit"/>'s world (the world daemon's death feature sets it), with no teleport service.</summary>
    private static ResurrectionService Requests(SpellTestKit kit)
        => kit.System.Resurrection ??= new ResurrectionService(kit.World, () => null);

    internal static SpellInfo Resurrection(uint id, SpellEffectName effect, int value, int mana = 0) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(effect, value, SpellImplicitTarget.UnitFriend, misc: mana)) with
        {
            AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    [Theory]
    [InlineData(SpellEffectName.Resurrect)]
    [InlineData(SpellEffectName.ResurrectNew)]
    public void DeadPlayerReceivesOneOffer_WithoutBeingRevived(SpellEffectName effect)
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, effect, 20, 77));
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        target.Map!.Combat.KillPlayer(target);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), triggered: true));

        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        Assert.False(target.IsAlive);
    }

    [Theory]
    [InlineData(SpellEffectName.Resurrect, 20, 999, 200u, 160u)]
    [InlineData(SpellEffectName.ResurrectNew, 300, 77, 300u, 77u)]
    public void OfferCapturesCorrectHealthAndMana_AcceptanceClampsToCurrentMaxima(
        SpellEffectName effect, int value, int misc, uint health, uint mana)
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, effect, value, misc));
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1, 8);
        (Player target, _) = kit.AddPlayer(2);
        target.MaxHealth = 1000;
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 800);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy, 100);
        target.Map!.Combat.KillPlayer(target);
        Assert.True(target.Map!.Combat.RepopPlayer(target));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), true));
        ResurrectionRequest request = Assert.IsType<ResurrectionRequest>(ResurrectionRequests.Get(target));
        Assert.Equal((health, mana), (request.Health, request.Mana));
        Assert.Equal(caster.Guid, request.Resurrector);
        Assert.True(request.Resurrector.IsPlayer); // a player's offer takes the target to the caster
        Assert.Equal((caster.X, caster.Y, caster.Z, caster.Orientation), (request.X, request.Y, request.Z, request.Orientation));
        Assert.False(ResurrectionRequests.IsRequestedBy(target, new ObjectGuid(99)));
        Requests(kit).Respond(target, new ObjectGuid(99), accept: true); // only the resurrector's acceptance counts
        Assert.False(target.IsAlive);
        target.MaxHealth = 150;
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 50);
        target.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage, 80);

        Requests(kit).Respond(target, caster.Guid, accept: true);

        Assert.True(target.IsAlive);
        Assert.Equal(150u, target.Health);
        Assert.Equal(50u, target.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(0u, target.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        Assert.Equal(100u, target.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy));
        Assert.False(target.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.False(target.IsRooted);
        Assert.Null(target.Combat.Corpse);
        Assert.Empty(target.Map!.Combat.Corpses);
        // vmangos keeps the request data until the next death or a refusal (Player.cpp:1522, MiscHandler.cpp:612); a repeat answer from
        // the living player is ignored (HandleResurrectResponseOpcode returns for IsAlive).
        Assert.Same(request, ResurrectionRequests.Get(target));
        Requests(kit).Respond(target, caster.Guid, accept: true);
        Assert.Equal(150u, target.Health);
    }

    [Fact]
    public void JustDiedOffer_SurvivesTheFollowingCorpseStateUpdate()
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, SpellEffectName.Resurrect, 20));
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Map!.Combat.Kill(caster, target);
        Assert.Equal(DeathState.JustDied, target.Combat.DeathState);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), true));
        ResurrectionRequest request = Assert.IsType<ResurrectionRequest>(ResurrectionRequests.Get(target));

        kit.World.RunTick(1);

        Assert.Equal(DeathState.Corpse, target.Combat.DeathState);
        Assert.Same(request, ResurrectionRequests.Get(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorpseTarget_ResolvesTheReleasedOwnerAcrossMaps_DespiteUnitFlag(bool includeUnit)
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, SpellEffectName.Resurrect, 20));
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 10);
        var map = target.Map!;
        map.Combat.KillPlayer(target);
        Assert.True(map.Combat.RepopPlayer(target));
        var corpse = target.Combat.Corpse!;
        map.RemovePlayer(target);
        target.MapId = 1;
        target.Relocate(1000, 1000, target.Z, 0, 0);
        kit.World.GetMap(1).AddPlayer(target);
        session.Clear();
        var targets = new SpellCastTargets
        {
            Mask = SpellCastTargetFlags.CorpseAlly | (includeUnit ? SpellCastTargetFlags.Unit : SpellCastTargetFlags.Self),
            Unit = includeUnit ? target.Guid : ObjectGuid.Empty,
            Corpse = corpse.Guid,
        };

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, targets, true));

        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        Assert.IsType<ResurrectionRequest>(ResurrectionRequests.Get(target));
    }

    [Fact]
    public void CorpseTarget_RejectsUnknownBodyMismatchedOwnerAndBlockedSight()
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, SpellEffectName.Resurrect, 20));
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 10);
        target.Map!.Combat.KillPlayer(target);
        Assert.True(target.Map!.Combat.RepopPlayer(target));
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = new ObjectGuid(99) };
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, Percentage, targets, true));
        targets.Corpse = target.Combat.Corpse!.Guid;
        targets.Mask |= SpellCastTargetFlags.Unit;
        targets.Unit = caster.Guid;
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, Percentage, targets, true));
        targets.Unit = target.Guid;
        WorldCollision.Of(kit.World).Install(new FakeLineOfSight { WallX = 5 });
        Assert.Equal(SpellCastResult.LineOfSight, kit.System.CastSpell(caster, Percentage, targets, true));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
    }

    [Theory]
    [InlineData(SpellImplicitTarget.UnitFriend)]
    [InlineData(SpellImplicitTarget.LocationCasterDest)]
    public void NormalCorpseCastBarToCrossMapGhost_ResolvesOwnerAtCompletion(SpellImplicitTarget selector)
    {
        SpellInfo spell = Resurrection(Percentage, SpellEffectName.Resurrect, 20) with
        {
            CastTime = new SpellCastTime(100, 0, 100),
            Effects = [SpellTestKit.Effect(SpellEffectName.Resurrect, 20, selector)],
        };
        using var kit = new SpellTestKit(spell);
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 10);
        var map = target.Map!;
        map.Combat.KillPlayer(target);
        Assert.True(map.Combat.RepopPlayer(target));
        ObjectGuid body = target.Combat.Corpse!.Guid;
        map.RemovePlayer(target);
        target.MapId = 1;
        target.Relocate(1000, 1000, target.Z, 0, 0);
        kit.World.GetMap(1).AddPlayer(target);
        session.Clear();
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseAlly, Unit = target.Guid, Corpse = body };
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, targets, triggered: false));
        Assert.Null(ResurrectionRequests.Get(target));

        kit.Advance(100);

        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        Assert.IsType<ResurrectionRequest>(ResurrectionRequests.Get(target));
    }

    [Fact]
    public void LocationCasterDestResurrection_UsesTheExplicitDeadPlayer()
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, SpellEffectName.Resurrect, 20) with
        {
            Effects = [SpellTestKit.Effect(SpellEffectName.Resurrect, 20, SpellImplicitTarget.LocationCasterDest)],
        });
        Requests(kit);
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, FakeSession targetSession) = kit.AddPlayer(2, 2);
        target.Map!.Combat.KillPlayer(target);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), true));

        Assert.Single(targetSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        Assert.DoesNotContain(casterSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
    }

    [Fact]
    public void GhostLogoutDuringCast_RemovesBodyAndPreventsOfferAtCompletion()
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, SpellEffectName.Resurrect, 20) with { CastTime = new SpellCastTime(100, 0, 100) });
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 10);
        target.Map!.Combat.KillPlayer(target);
        Assert.True(target.Map!.Combat.RepopPlayer(target));
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = target.Combat.Corpse!.Guid };
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, targets, triggered: false));
        kit.World.RemovePlayer(target);
        session.Clear();

        kit.Advance(100);

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        Assert.Null(ResurrectionRequests.Get(target));
        Assert.False(target.IsAlive);
    }

    [Fact]
    public void CreatureCaster_OffersItsUtf8Name_AndKeepsResurrectionAtTheCurrentPosition()
    {
        using var kit = new SpellTestKit(Resurrection(Flat, SpellEffectName.ResurrectNew, 300, 77) with { AttributesEx3 = ResurrectEffects.Ex3NoResTimer });
        Requests(kit);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        var template = new CreatureTemplate { Entry = 991200, Name = "Résurrecteur", MinLevel = 1, MaxLevel = 1, Faction = 35, MinLevelHealth = 100, MaxLevelHealth = 100 };
        var caster = new Creature(1, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        caster.Relocate(0, target.Y, target.Z, 0, 0);
        target.Map!.AddObject(caster, true);
        target.Map!.Combat.KillPlayer(target);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Flat, SpellCastTargets.ForUnit(target.Guid), true));
        ResurrectionRequest request = Assert.IsType<ResurrectionRequest>(ResurrectionRequests.Get(target));
        Assert.False(request.Resurrector.IsPlayer); // a creature's offer resurrects in place
        byte[] offer = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest).Payload;
        var reader = new PacketReader(offer);
        Assert.Equal(caster.Guid.Value, reader.ReadUInt64());
        Assert.Equal(14u, reader.ReadUInt32());
        Assert.Equal(template.Name, reader.ReadCString());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void AlivePlayerIsNotOfferedResurrection_DeadTargetStillRequiresSourceAttribute()
    {
        SpellInfo spell = Resurrection(Percentage, SpellEffectName.Resurrect, 20);
        using var kit = new SpellTestKit(spell, spell with { Id = Flat, AttributesEx2 = SpellAttributesEx2.None });
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), true));
        Assert.Null(ResurrectionRequests.Get(target));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        target.Map!.Combat.KillPlayer(target);
        Assert.Equal(SpellCastResult.TargetsDead, kit.System.CastSpell(caster, Flat, SpellCastTargets.ForUnit(target.Guid), true));
    }

    [Fact]
    public void RevivalAndNewDeathInvalidatePreviousOffer_AndSettlementBlocksAcceptanceAndCompletion()
    {
        using var kit = new SpellTestKit(Resurrection(Percentage, SpellEffectName.Resurrect, 20));
        Requests(kit);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Map!.Combat.KillPlayer(target);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Percentage, SpellCastTargets.ForUnit(target.Guid), true));
        ResurrectionRequest request = Assert.IsType<ResurrectionRequest>(ResurrectionRequests.Get(target));
        Guid hold = Guid.NewGuid();
        Assert.True(target.BeginQuestSettlement(hold));
        Requests(kit).Respond(target, caster.Guid, accept: true); // a held quest settlement keeps the player dead
        Assert.False(target.IsAlive);
        Assert.Same(request, ResurrectionRequests.Get(target));
        Assert.True(target.EndQuestSettlement(hold));
        target.Map!.Combat.ResurrectPlayer(target, 0.5f, false);
        Assert.True(target.IsAlive);
        target.Map!.Combat.Kill(caster, target); // SetDeathState(JUST_DIED) clears the request (Player.cpp:1522)
        Assert.Null(ResurrectionRequests.Get(target));
        Requests(kit).Respond(target, caster.Guid, accept: true);
        Assert.False(target.IsAlive);
    }

    [Theory]
    [InlineData("", false, true, 1u)]
    [InlineData("Résurrecteur", true, false, 14u)]
    public void RequestWireUsesUtf8SizedName_AndBothSourceBooleanBytes(string name, bool sickness, bool delayed, uint size)
    {
        byte[] payload = ResurrectionPackets.BuildRequest(new ObjectGuid(0x1020304050607080), name, sickness, delayed);
        var reader = new PacketReader(payload);
        Assert.Equal(0x1020304050607080ul, reader.ReadUInt64());
        Assert.Equal(size, reader.ReadUInt32());
        Assert.Equal(name, reader.ReadCString());
        Assert.Equal(sickness ? (byte)1 : (byte)0, reader.ReadByte());
        Assert.Equal(delayed ? (byte)1 : (byte)0, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// SPELL_EFFECT_DUEL for spell 7266 (vmangos Spell::EffectDuel, SpellEffects.cpp:4650-4761; Spell::CheckCast, Spell.cpp:6187-6203). The spell row is the
/// classic-db one (Effect 83, TargetA 25, EffectMiscValue 21680) with a synthetic range and duration.
/// </summary>
public sealed class DuelChallengeTests
{
    private static SpellCastResult CastDuel(DuelRig rig, Player caster, Player target)
        => rig.Kit.System.HandleCastRequest(caster, DuelSpell, SpellCastTargets.ForUnit(target.Guid));

    private static List<GameObject> Flags(DuelRig rig) => [.. rig.Objects.GameObjects.Where(go => go.Entry == FlagEntry)];

    private static AreaTemplate Area(uint flags) => new(1, 0, 0, 1, flags, 1, "Test Area", 0, 0);

    private static List<byte> FailureReasons(FakeSession session)
        => [.. Packets(session, WorldOpcode.SmsgCastResult).Where(p => p.Length >= 6 && p[4] == (byte)SpellCastResultStatus.Failure).Select(p => p[5])];

    [Fact]
    public void CastingDuelAtAPlayer_CreatesBothHalves_TheFlagAndTheRequest()
    {
        using var rig = new DuelRig();
        rig.A.Orientation = 1.5f;
        rig.ClearPackets();

        Assert.Equal(SpellCastResult.CastOk, CastDuel(rig, rig.A, rig.B));

        GameObject flag = Assert.Single(Flags(rig));
        Assert.Equal(11f, flag.X);
        Assert.Equal(10f, flag.Y);
        Assert.Equal(rig.A.Z, flag.Z);
        Assert.Equal(1.5f, flag.Orientation);
        Assert.Equal(GameObjectType.DuelArbiter, flag.Type);
        Assert.Equal(rig.A.FactionTemplate, flag.GetUInt32(UpdateFields.GameobjectFaction));
        Assert.Equal((uint)rig.A.Level + 1, flag.GetUInt32(UpdateFields.GameobjectLevel));
        Assert.Equal(rig.A.Guid.Value, flag.GetUInt64(UpdateFields.ObjectFieldCreatedBy));
        Assert.Equal(flag.Guid.Value, rig.A.DuelArbiter);
        Assert.Equal(flag.Guid.Value, rig.B.DuelArbiter);
        Assert.Same(rig.B, rig.A.Duel!.Opponent);
        Assert.Same(rig.A, rig.B.Duel!.Opponent);
        Assert.Same(rig.A, rig.B.Duel.Initiator);
        byte[] requested = DuelPackets.Requested(flag.Guid.Value, rig.A.Guid.Value);
        Assert.Equal(requested, Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelRequested)));
        Assert.Equal(requested, Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelRequested)));
    }

    [Fact]
    public void TheCastCheck_RefusesNonPlayerTargets_AndSelf()
    {
        using var rig = new DuelRig();
        var creature = new OwnedUnit();
        creature.Spawn(rig.Map, 13, 10);

        Assert.Equal(SpellCastResult.BadTargets, rig.Service.CheckChallenge(rig.A, creature));
        Assert.Equal(SpellCastResult.BadTargets, rig.Service.CheckChallenge(rig.A, null));
        Assert.Equal(SpellCastResult.BadTargets, rig.Service.CheckChallenge(creature, rig.A));
        Assert.Equal(SpellCastResult.BadTargets, rig.Service.CheckChallenge(rig.A, rig.A));
        Assert.Equal(SpellCastResult.BadTargets, CastDuel(rig, rig.A, rig.A));
        Assert.Empty(Flags(rig));
        Assert.Null(rig.A.Duel);
    }

    [Fact]
    public void ATargetInADuel_AnswersTargetDueling_AndMakesNoSecondFlag()
    {
        using var rig = new DuelRig();
        Player third = rig.Kit.AddPlayer(3, 14, 10).Player;
        rig.Kit.Spellbook.Teach(third, DuelSpell);
        rig.Challenge();
        rig.ClearPackets();

        Assert.Equal(SpellCastResult.TargetDueling, CastDuel(rig, third, rig.B));

        Assert.Single(Flags(rig));
        Assert.Null(third.Duel);
    }

    [Fact]
    public void ReChallengingSomeoneElseWhilePending_InterruptsTheOldRequest_AndStartsANewOne()
    {
        using var rig = new DuelRig();
        (Player third, FakeSession thirdSession) = rig.Kit.AddPlayer(3, 14, 10);
        GameObject oldFlag = rig.Challenge();
        rig.ClearPackets();
        thirdSession.Clear();

        Assert.Equal(SpellCastResult.CastOk, CastDuel(rig, rig.A, third));

        Assert.Equal([0], Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelComplete)));
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelWinner));
        Assert.Null(rig.Map.FindObject(oldFlag.Guid));
        GameObject flag = Assert.Single(Flags(rig));
        Assert.NotEqual(oldFlag.Guid, flag.Guid);
        Assert.Same(third, rig.A.Duel!.Opponent);
        Assert.Equal(flag.Guid.Value, rig.A.DuelArbiter);
        Assert.Equal(flag.Guid.Value, third.DuelArbiter);
        Assert.Single(Packets(thirdSession, WorldOpcode.SmsgDuelRequested));
        Assert.True(rig.B.Duel!.Finished); // dropped by B's own next update
        Assert.Equal(0ul, rig.B.DuelArbiter);
    }

    [Fact]
    public void ReChallengingSomeoneElseMidDuel_ForfeitsTheOldOne_TheChallengerLoses()
    {
        using var rig = new DuelRig();
        Player third = rig.Kit.AddPlayer(3, 14, 10).Player;
        rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        Assert.Equal(SpellCastResult.CastOk, CastDuel(rig, rig.A, third));

        foreach (FakeSession session in new[] { rig.SessionA, rig.SessionB })
        {
            Assert.Equal([1], Assert.Single(Packets(session, WorldOpcode.SmsgDuelComplete)));
            Assert.Equal([0, .. "P2"u8, 0, .. "P1"u8, 0], Assert.Single(Packets(session, WorldOpcode.SmsgDuelWinner)));
        }

        Assert.Same(third, rig.A.Duel!.Opponent);
        Assert.Single(Flags(rig));
    }

    [Fact]
    public void ChallengingTheOpponentOfARunningDuel_AnswersTargetEnemy()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();

        SpellCastResult? result = rig.Service.Challenge(rig.A, rig.B, FlagEntry, 0);

        Assert.Equal(SpellCastResult.TargetEnemy, result);
        Assert.Single(Flags(rig));
    }

    [Fact]
    public void ATargetThatIgnoresTheCaster_GetsNoFlagAndNoRequest()
    {
        using var rig = new DuelRig();
        rig.Service.IsIgnoring = (player, guid) => ReferenceEquals(player, rig.B) && guid == rig.A.Guid;
        rig.ClearPackets();

        Assert.Equal(SpellCastResult.CastOk, CastDuel(rig, rig.A, rig.B));

        Assert.Empty(Flags(rig));
        Assert.Null(rig.A.Duel);
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelRequested));
        Assert.Empty(FailureReasons(rig.SessionA)); // dropped without a message, as in vmangos
    }

    [Fact]
    public void ATargetOnAnotherMap_IsDroppedSilently()
    {
        using var rig = new DuelRig();
        var session = new FakeSession(9);
        Player far = TestWorld.CreatePlayer(9, 12, 10, session, mapId: 1);
        rig.World.AddPlayer(far);
        rig.World.RunTick(0);

        Assert.Null(rig.Service.Challenge(rig.A, far, FlagEntry, 0));

        Assert.Empty(Flags(rig));
        Assert.Null(rig.A.Duel);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(0x40u, false)]
    [InlineData(0x41u, false)]
    public void TheAreaMustCarryTheDuelFlag_OnBothPlayersAreas(uint flags, bool refused)
    {
        foreach (bool casterSide in new[] { true, false })
        {
            using var rig = new DuelRig();
            rig.Service.AreaOf = player => ReferenceEquals(player, casterSide ? rig.A : rig.B) ? Area(flags) : Area(0x40);
            rig.ClearPackets();

            Assert.Equal(SpellCastResult.CastOk, CastDuel(rig, rig.A, rig.B));

            Assert.Equal(refused, FailureReasons(rig.SessionA).Contains((byte)SpellCastResult.NoDueling));
            Assert.Equal(refused, Flags(rig).Count == 0);
            Assert.Equal(refused, rig.A.Duel is null);
        }
    }

    [Fact]
    public void AnUnknownArea_IsAllowed_UnlessKnownAreasAreRequired()
    {
        using var open = new DuelRig();
        Assert.Equal(SpellCastResult.CastOk, CastDuel(open, open.A, open.B));
        Assert.NotNull(open.A.Duel);

        using var strict = new DuelRig(new DuelOptions { RequireKnownArea = true });
        strict.ClearPackets();
        strict.Service.AreaOf = _ => null;
        Assert.Equal(SpellCastResult.CastOk, CastDuel(strict, strict.A, strict.B));
        Assert.Contains((byte)SpellCastResult.NoDueling, FailureReasons(strict.SessionA));
        Assert.Null(strict.A.Duel);
    }

    [Fact]
    public void WhenDuelsAreDisabled_TheSpellIsRefusedWithNoDueling()
    {
        using var rig = new DuelRig(new DuelOptions { Enabled = false });

        Assert.Equal(SpellCastResult.NoDueling, CastDuel(rig, rig.A, rig.B));

        Assert.Empty(Flags(rig));
        Assert.Null(rig.A.Duel);
    }

    [Fact]
    public void WithoutTheFlagTemplate_NoDuelIsLeftBehind()
    {
        using var rig = new DuelRig(withFlagTemplate: false);
        rig.ClearPackets();

        Assert.Equal(SpellCastResult.CastOk, CastDuel(rig, rig.A, rig.B));

        Assert.Empty(Flags(rig));
        Assert.Null(rig.A.Duel);
        Assert.Null(rig.B.Duel);
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelRequested));
    }

    [Fact]
    public void TheFlagDespawnsAfterTheSpellDuration_AndTheUnacceptedRequestEndsAsFled()
    {
        using var rig = new DuelRig();
        CastDuel(rig, rig.A, rig.B);
        GameObject flag = Assert.Single(Flags(rig));
        rig.ClearPackets();

        rig.World.RunTick(6000); // the synthetic spell lasts 5 s
        rig.Tick();

        Assert.Null(rig.Map.FindObject(flag.Guid));
        Assert.Equal([1], Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete)));
    }

    [Fact]
    public void AWorldWithoutADuelInstalledOnItsSpellSystem_RefusesTheSpellInsteadOfReportingItUnsupported()
    {
        using var kit = new Spells.SpellTestKit(
            Spells.SpellTestKit.Spell(DuelSpell, Spells.SpellTestKit.Effect(SpellEffectName.Duel, 0, SpellImplicitTarget.Unit, misc: 21680)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            });
        (Player a, FakeSession sa) = kit.AddPlayer(1, 10, 10);
        (Player b, _) = kit.AddPlayer(2, 12, 10);
        kit.Spellbook.Teach(a, DuelSpell);

        kit.System.HandleCastRequest(a, DuelSpell, SpellCastTargets.ForUnit(b.Guid));

        Assert.Contains((byte)SpellCastResult.NoDueling, FailureReasons(sa));
        Assert.Null(a.Duel);
    }
}

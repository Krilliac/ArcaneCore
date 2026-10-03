using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// vmangos Handlers/DuelHandler.cpp:30-71 (accept, cancel/forfeit), Spells/SpellEffects.cpp:4732-4760 (what a challenge leaves behind) and
/// Player::UpdateDuelFlag (Player.cpp:17248-17263), on a stepping clock.
/// </summary>
public sealed class DuelLifecycleTests
{
    [Fact]
    public void Begin_SendsTheRequestToBoth_AndCrossesTheTwoHalves()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();

        byte[] expected = DuelPackets.Requested(flag.Guid.Value, rig.A.Guid.Value);
        Assert.Equal(expected, Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelRequested)));
        Assert.Equal(expected, Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelRequested)));
        Assert.Same(rig.B, rig.A.Duel!.Opponent);
        Assert.Same(rig.A, rig.B.Duel!.Opponent);
        Assert.Same(rig.A, rig.A.Duel.Initiator);
        Assert.Same(rig.A, rig.B.Duel.Initiator); // vmangos duel2: initiator = caster, opponent = caster
        Assert.Equal(flag.Guid.Value, rig.A.DuelArbiter);
        Assert.Equal(flag.Guid.Value, rig.B.DuelArbiter);
        Assert.Equal(0, rig.A.Duel.StartTimeSeconds);
    }

    [Fact]
    public void Accept_ByTheInitiator_OrWithNoDuel_OrWithoutTheOpponentsHalf_IsIgnored()
    {
        using var rig = new DuelRig();
        rig.Service.Accept(rig.B); // no duel at all
        rig.Challenge();
        rig.ClearPackets();

        rig.Service.Accept(rig.A); // the initiator cannot accept its own challenge

        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelCountdown));
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelCountdown));
        Assert.Equal(0, rig.A.Duel!.StartTimerSeconds);

        rig.B.Duel = null; // the opponent has no half
        rig.Service.Accept(rig.A);
        rig.Service.Accept(rig.B);
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelCountdown));
    }

    [Fact]
    public void Accept_StartsTheCountdown_AndTheFlagTurnsOnThreeSecondsLater()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.ClearPackets();

        rig.Service.Accept(rig.B);

        byte[] countdown = [0xB8, 0x0B, 0x00, 0x00];
        Assert.Equal(countdown, Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelCountdown)));
        Assert.Equal(countdown, Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelCountdown)));
        Assert.Equal(rig.Now, rig.A.Duel!.StartTimerSeconds);
        Assert.Equal(rig.Now, rig.B.Duel!.StartTimerSeconds);
        Assert.Equal(0, rig.A.Duel.StartTimeSeconds);

        rig.Now += 2;
        rig.Tick();
        Assert.Equal(0, rig.A.Duel.StartTimeSeconds);
        Assert.False(rig.Map.Combat.Hooks.CanAttack(rig.A, rig.B));
        Assert.Equal(0u, rig.A.DuelTeam);

        rig.Now += 1;
        rig.Tick();

        Assert.Equal(rig.Now, rig.A.Duel.StartTimeSeconds);
        Assert.Equal(rig.Now, rig.B.Duel!.StartTimeSeconds);
        Assert.Equal(0, rig.A.Duel.StartTimerSeconds);
        Assert.NotEqual(0u, rig.A.DuelTeam);
        Assert.NotEqual(0u, rig.B.DuelTeam);
        Assert.NotEqual(rig.A.DuelTeam, rig.B.DuelTeam);
        Assert.Equal([1u, 2u], new[] { rig.A.DuelTeam, rig.B.DuelTeam }.Order());
        Assert.True(rig.Map.Combat.Hooks.CanAttack(rig.A, rig.B));
        Assert.True(rig.Map.Combat.Hooks.CanAttack(rig.B, rig.A));
    }

    [Fact]
    public void Accept_AfterTheDuelStarted_IsIgnored()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        rig.Service.Accept(rig.B);

        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelCountdown));
    }

    [Fact]
    public void Cancel_BeforeTheStart_CompletesInterrupted_WithoutAWinner_AndRemovesTheFlag()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.ClearPackets();

        rig.Service.Cancel(rig.B);

        Assert.Equal([0], Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete)));
        Assert.Equal([0], Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelComplete)));
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelWinner));
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelWinner));
        Assert.Equal(0ul, rig.A.DuelArbiter);
        Assert.Equal(0ul, rig.B.DuelArbiter);
        Assert.Equal(0u, rig.A.DuelTeam);
        Assert.Null(rig.Map.FindObject(flag.Guid));
        Assert.True(rig.A.Duel!.Finished);
        Assert.True(rig.B.Duel!.Finished);
    }

    [Fact]
    public void Forfeit_AfterTheStart_StopsCombat_Groves_AndTheOpponentWins()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();
        rig.Map.Combat.Attack(rig.A, rig.B, melee: true);
        Assert.NotNull(rig.A.Combat.Victim);
        rig.ClearPackets();

        rig.Service.Cancel(rig.A); // A types /forfeit

        Assert.Null(rig.A.Combat.Victim);
        Assert.Contains(rig.Kit.System.GetAuras(rig.A), h => h.Spell.Id == DuelService.GrovelSpellId);
        foreach (FakeSession session in new[] { rig.SessionA, rig.SessionB })
        {
            Assert.Equal([1], Assert.Single(Packets(session, WorldOpcode.SmsgDuelComplete)));
            Assert.Equal([0, .. "P2"u8, 0, .. "P1"u8, 0], Assert.Single(Packets(session, WorldOpcode.SmsgDuelWinner)));
        }

        Assert.Equal(0u, rig.A.DuelTeam);
        Assert.Equal(0u, rig.B.DuelTeam);
    }

    [Fact]
    public void Cancel_WithoutADuel_IsIgnored()
    {
        using var rig = new DuelRig();

        rig.Service.Cancel(rig.A);

        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
    }

    [Fact]
    public void ACompletedDuel_IsFinishedAtOnce_AndDroppedOnTheNextMapUpdate()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();

        rig.Service.Cancel(rig.A);

        Assert.True(rig.A.Duel!.Finished);
        Assert.True(DuelRules.IsInDuelWith(rig.A, rig.B)); // still true for the rest of this tick
        Assert.False(rig.Map.Combat.Hooks.CanAttack(rig.A, rig.B)); // same team again at once
        rig.Tick();
        Assert.Null(rig.A.Duel);
        Assert.Null(rig.B.Duel);
    }

    [Fact]
    public void ASecondComplete_IsANoOp_NoDuplicatePackets()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();
        rig.Service.Complete(rig.A, DuelCompleteType.Won);
        rig.ClearPackets();

        rig.Service.Complete(rig.A, DuelCompleteType.Won);
        rig.Service.Complete(rig.B, DuelCompleteType.Fled);

        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelWinner));
    }
}

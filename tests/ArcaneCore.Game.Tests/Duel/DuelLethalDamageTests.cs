using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// Duels end at 1 hp: vmangos Unit::DealDamage (Unit.cpp:762-779 clamp, :825-843 lethal damage from anyone else interrupts, :954-969 the
/// clamped hit wins the duel and casts Grovel 7267 on the loser).
/// </summary>
public sealed class DuelLethalDamageTests
{
    private static DuelRig StartedDuel(out List<(Unit? Killer, Unit Victim)> kills)
    {
        var rig = new DuelRig();
        var list = new List<(Unit?, Unit)>();
        rig.Map.Combat.UnitKilled += (k, v) => list.Add((k, v));
        kills = list;
        rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();
        return rig;
    }

    [Fact]
    public void LethalMeleeDamage_FromTheOpponent_LeavesOneHealth_AndTheOpponentWins()
    {
        using DuelRig rig = StartedDuel(out var kills);
        rig.Map.Combat.Attack(rig.A, rig.B, melee: true);

        uint dealt = rig.Map.Combat.DealDamage(rig.A, rig.B, 1500);

        Assert.Equal(999u, dealt);
        Assert.Equal(1u, rig.B.Health);
        Assert.True(rig.B.IsAlive);
        Assert.Empty(kills);
        Assert.Contains(rig.Kit.System.GetAuras(rig.B), h => h.Spell.Id == DuelService.GrovelSpellId);
        Assert.Null(rig.A.Combat.Victim);
        Assert.False(rig.B.Combat.IsInCombat);
        foreach (FakeSession session in new[] { rig.SessionA, rig.SessionB })
        {
            Assert.Equal([1], Assert.Single(Packets(session, WorldOpcode.SmsgDuelComplete)));
            Assert.Equal([0, .. "P1"u8, 0, .. "P2"u8, 0], Assert.Single(Packets(session, WorldOpcode.SmsgDuelWinner)));
        }

        Assert.Equal(0ul, rig.B.DuelArbiter);
        Assert.Equal(0u, rig.B.DuelTeam);
    }

    [Fact]
    public void LethalDamageThatIsNotMelee_NorDirect_IsClampedToo()
    {
        using DuelRig rig = StartedDuel(out var kills);

        rig.Map.Combat.DealDamage(rig.A, rig.B, 5000, direct: false, meleeDamage: false); // a periodic tick

        Assert.Equal(1u, rig.B.Health);
        Assert.Empty(kills);
        Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelWinner));
    }

    [Fact]
    public void TheBoundary_IsDamagePlusOneReachingHealth()
    {
        using DuelRig rig = StartedDuel(out var kills);

        rig.Map.Combat.DealDamage(rig.A, rig.B, 998);
        Assert.Equal(2u, rig.B.Health);
        Assert.False(rig.A.Duel!.Finished);
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));

        rig.Map.Combat.DealDamage(rig.A, rig.B, 1); // damage + 1 == health
        Assert.Equal(1u, rig.B.Health);
        Assert.True(rig.A.Duel.Finished);
        Assert.Empty(kills);
    }

    [Fact]
    public void ASecondHitInTheFinishingWindow_IsStillClamped_AndResendsNothing()
    {
        using DuelRig rig = StartedDuel(out var kills);
        rig.Map.Combat.DealDamage(rig.A, rig.B, 5000);
        rig.ClearPackets();

        rig.Map.Combat.DealDamage(rig.A, rig.B, 5000);

        Assert.Equal(1u, rig.B.Health);
        Assert.True(rig.B.IsAlive);
        Assert.Empty(kills);
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelWinner));
    }

    [Fact]
    public void LethalDamageFromAThirdParty_KillsNormally_AndInterruptsTheDuel()
    {
        using DuelRig rig = StartedDuel(out var kills);
        Player third = rig.Kit.AddPlayer(3, 14, 10).Player;
        third.Health = third.MaxHealth = 1000;
        third.UnitFlags |= UnitFlags.Pvp;
        rig.B.UnitFlags |= UnitFlags.Pvp;
        rig.ClearPackets();

        rig.Map.Combat.DealDamage(third, rig.B, 5000);

        Assert.False(rig.B.IsAlive);
        Assert.Single(kills);
        Assert.Equal([0], Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete)));
        Assert.Equal([0], Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelComplete)));
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelWinner));
        Assert.True(rig.A.Duel!.Finished);
        Assert.Equal(0ul, rig.A.DuelArbiter);
    }

    [Fact]
    public void LethalDamageFromACreature_KillsNormally_AndInterruptsTheDuel()
    {
        using DuelRig rig = StartedDuel(out var kills);
        var wolf = new OwnedUnit(); // no owner: a plain creature
        wolf.Spawn(rig.Map, 13, 10);

        rig.Map.Combat.DealDamage(wolf, rig.B, 5000);

        Assert.False(rig.B.IsAlive);
        Assert.Single(kills);
        Assert.Equal([0], Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelComplete)));
    }

    [Fact]
    public void SelfInflictedLethalDamage_Kills_AndInterrupts()
    {
        using DuelRig rig = StartedDuel(out var kills);

        rig.Map.Combat.DealDamage(rig.B, rig.B, 5000);

        Assert.False(rig.B.IsAlive);
        Assert.Single(kills);
        Assert.Equal([0], Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete)));
    }

    [Fact]
    public void ThePetOfTheOpponent_IsClampedAsTheOpponent()
    {
        using DuelRig rig = StartedDuel(out var kills);
        var pet = new OwnedUnit { Owner = rig.A };
        pet.Spawn(rig.Map, 13, 10);

        rig.Map.Combat.DealDamage(pet, rig.B, 5000);

        Assert.Equal(1u, rig.B.Health);
        Assert.Empty(kills);
        Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelWinner));
    }

    [Fact]
    public void ARequestedDuelThatHasNotStarted_StillClampsTheOpponent_AsVmangosTestsTheDuelObject()
    {
        using var rig = new DuelRig();
        rig.Challenge(); // pending
        rig.ClearPackets();

        rig.Map.Combat.DealDamage(rig.A, rig.B, 5000);

        Assert.Equal(1u, rig.B.Health);
        Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelWinner));
    }

    [Fact]
    public void ZeroDamage_DoesNotEndTheDuel()
    {
        using DuelRig rig = StartedDuel(out _);
        rig.B.Health = 1;

        rig.Map.Combat.DealDamage(rig.A, rig.B, 0);

        Assert.False(rig.A.Duel!.Finished);
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
    }

    [Fact]
    public void PlayersOutsideADuel_StillDie()
    {
        using var rig = new DuelRig();
        var kills = new List<Unit>();
        rig.Map.Combat.UnitKilled += (_, v) => kills.Add(v);

        rig.Map.Combat.DealDamage(rig.A, rig.B, 5000);

        Assert.False(rig.B.IsAlive);
        Assert.Single(kills);
    }

    [Fact]
    public void WithoutADuelService_TheClampDoesNotExist()
    {
        (var world, var map, _, Player a, Player b, _, _) = DuelTestKit.TwoPlayers();
        using var w = world;
        DuelTestKit.Link(a, b, startTime: 100); // a duel object but no service registered

        map.Combat.DealDamage(a, b, 5000);

        Assert.False(b.IsAlive);
    }
}

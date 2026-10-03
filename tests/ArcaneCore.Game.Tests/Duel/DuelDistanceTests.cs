using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// Retail 1.12 (mangos-classic Player.cpp:6902-6942): 50 yd to leave, 40 yd to return, 10 s grace (vmangos Nostalrius widens it to 75/70, Player.cpp:6688-6716, option); the distance is 3D with both
/// bounding radii (Object.cpp:1738-1752); a flag object that is gone ends the duel as fled.
/// </summary>
public sealed class DuelDistanceTests
{
    private static void PlaceBAtDistanceFromFlag(DuelRig rig, GameObject flag, float limit, float delta)
    {
        float edge = limit + rig.B.BoundingRadius + flag.BoundingRadius;
        rig.B.SetPosition(flag.X + edge + delta, flag.Y, flag.Z, 0);
    }

    [Fact]
    public void LeavingTheArea_WarnsOnce_AtFiftyYardsPlusRadii()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        PlaceBAtDistanceFromFlag(rig, flag, 50f, -0.1f);
        rig.Tick();
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelOutofbounds));

        PlaceBAtDistanceFromFlag(rig, flag, 50f, +0.1f);
        rig.Tick();
        rig.Tick();

        Assert.Empty(Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelOutofbounds)));
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelOutofbounds));
        Assert.Equal(rig.Now, rig.B.Duel!.OutOfBoundSeconds);
    }

    [Fact]
    public void ComingBack_NeedsFortyYards_AndSendsInBoundsOnce()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.AcceptAndStart();
        PlaceBAtDistanceFromFlag(rig, flag, 50f, +0.1f);
        rig.Tick();
        rig.ClearPackets();

        PlaceBAtDistanceFromFlag(rig, flag, 40f, +0.1f); // between 40 and 50: still out
        rig.Tick();
        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelInbounds));
        Assert.NotEqual(0, rig.B.Duel!.OutOfBoundSeconds);

        PlaceBAtDistanceFromFlag(rig, flag, 40f, -0.1f);
        rig.Tick();
        rig.Tick();

        Assert.Empty(Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelInbounds)));
        Assert.Equal(0, rig.B.Duel.OutOfBoundSeconds);
    }

    [Fact]
    public void TenSecondsOutOfBounds_LosesTheDuelAsFled()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.AcceptAndStart();
        PlaceBAtDistanceFromFlag(rig, flag, 50f, +5f);
        rig.Tick();
        long outAt = rig.Now;
        rig.ClearPackets();

        rig.Now = outAt + 9;
        rig.Tick();
        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));

        rig.Now = outAt + 10;
        rig.Tick();

        foreach (FakeSession session in new[] { rig.SessionA, rig.SessionB })
        {
            Assert.Equal([1], Assert.Single(Packets(session, WorldOpcode.SmsgDuelComplete)));
            Assert.Equal([1, .. "P1"u8, 0, .. "P2"u8, 0], Assert.Single(Packets(session, WorldOpcode.SmsgDuelWinner)));
        }

        Assert.Equal(0ul, rig.A.DuelArbiter);
        Assert.Null(rig.Map.FindObject(flag.Guid));
    }

    [Fact]
    public void OptionsChangeTheDistances_AndTheGrace()
    {
        using var rig = new DuelRig(new DuelOptions { OutOfBoundsYards = 75f, ReturnInBoundsYards = 70f, OutOfBoundsGraceSeconds = 3 });
        GameObject flag = rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        PlaceBAtDistanceFromFlag(rig, flag, 75f, +0.1f);
        rig.Tick();
        Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelOutofbounds));

        rig.Now += 3;
        rig.Tick();
        Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelWinner));
    }

    [Fact]
    public void AFlagThatIsGone_EndsTheDuelAsFledAtOnce_EvenForAnUnacceptedRequest()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.ClearPackets();

        rig.Objects.Remove(flag); // what the despawn timer does
        rig.Tick();

        Assert.Equal([1], Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete)));
        Assert.Equal([1, .. "P2"u8, 0, .. "P1"u8, 0], Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelWinner)));
    }

    [Fact]
    public void ExpiredRequestIsSilent_CompletesAnUnacceptedRequestAsInterrupted_ButNotARunningDuel()
    {
        using var silent = new DuelRig(new DuelOptions { ExpiredRequestIsSilent = true });
        GameObject flag = silent.Challenge();
        silent.ClearPackets();
        silent.Objects.Remove(flag);
        silent.Tick();
        Assert.Equal([0], Assert.Single(Packets(silent.SessionA, WorldOpcode.SmsgDuelComplete)));
        Assert.Empty(Packets(silent.SessionA, WorldOpcode.SmsgDuelWinner));

        using var running = new DuelRig(new DuelOptions { ExpiredRequestIsSilent = true });
        GameObject flag2 = running.Challenge();
        running.AcceptAndStart();
        running.ClearPackets();
        running.Objects.Remove(flag2);
        running.Tick();
        Assert.Equal([1], Assert.Single(Packets(running.SessionA, WorldOpcode.SmsgDuelComplete)));
    }

    [Fact]
    public void TheFlagsDespawnTimer_EndsTheDuel()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge(despawnAfterSeconds: 5);
        rig.ClearPackets();

        rig.World.RunTick(6000); // the flag despawns inside this update
        rig.Tick();

        Assert.Null(rig.Map.FindObject(flag.Guid));
        Assert.Single(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
    }

    [Fact]
    public void ALeavingPlayer_CompletesTheDuelAsFled_ForBothClients()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        rig.Map.RemovePlayer(rig.A); // a far teleport to another map removes the player from this one

        foreach (FakeSession session in new[] { rig.SessionA, rig.SessionB })
        {
            Assert.Equal([1], Assert.Single(Packets(session, WorldOpcode.SmsgDuelComplete)));
            Assert.Equal([1, .. "P2"u8, 0, .. "P1"u8, 0], Assert.Single(Packets(session, WorldOpcode.SmsgDuelWinner)));
        }

        Assert.Null(rig.Map.FindObject(flag.Guid));
        Assert.Equal(0u, rig.B.DuelTeam);
    }

    [Fact]
    public void ARunningDuelInsideTheArea_RunsOnWithoutEnding()
    {
        using var rig = new DuelRig();
        GameObject flag = rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        rig.Now += 100;
        rig.Tick();

        Assert.Empty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
        Assert.NotNull(rig.Map.FindObject(flag.Guid));
    }
}

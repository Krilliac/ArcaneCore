using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Random event sequences against Warsong Gulch (fixed seeds, injected time only). Whatever happens: no side passes three captures,
/// the match has at most one winner and that winner owns the third capture, and a flag is carried exactly when it has a carrier.
/// </summary>
public sealed class WarsongGulchPropertyTests
{
    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheMatchKeepsItsInvariantsUnderRandomPlay(int seed)
    {
        var rng = new Random(seed);
        var (bg, _, _) = NewWsg();
        StartMatch(bg, perTeam: 4);

        for (int step = 0; step < 400; step++)
        {
            ObjectGuid[] players = [.. bg.Players];
            ObjectGuid pick() => players.Length == 0 ? Alliance[0] : players[rng.Next(players.Length)];
            switch (rng.Next(10))
            {
                case 0:
                case 1:
                    Tick(bg, (uint)rng.Next(1, 40_000), 1000);
                    break;
                case 2:
                    bg.OnFlagClicked(pick(), new WsgFlagObject(WarsongGulch.HordeFlagBaseEntry, WarsongGulch.EventFlagHorde, false));
                    break;
                case 3:
                    bg.OnFlagClicked(pick(), new WsgFlagObject(WarsongGulch.AllianceFlagBaseEntry, WarsongGulch.EventFlagAlliance, false));
                    break;
                case 4:
                    bg.OnFlagClicked(pick(), rng.Next(2) == 0
                        ? new WsgFlagObject(WarsongGulch.HordeFlagGroundEntry, BattlegroundConstants.EventNone, rng.Next(4) != 0)
                        : new WsgFlagObject(WarsongGulch.AllianceFlagGroundEntry, BattlegroundConstants.EventNone, rng.Next(4) != 0));
                    break;
                case 5:
                    bg.OnPlayerDroppedFlag(pick());
                    break;
                case 6:
                    bg.HandleAreaTrigger(pick(), rng.Next(2) == 0 ? WarsongGulch.AreaTriggerAllianceFlagSpawn : WarsongGulch.AreaTriggerHordeFlagSpawn);
                    break;
                case 7:
                    bg.HandleKillPlayer(pick(), rng.Next(3) == 0 ? null : pick());
                    break;
                case 8:
                    if (players.Length > 0)
                    {
                        ObjectGuid leaver = pick();
                        bg.RemovePlayerAtLeave(leaver, teleportToEntryPoint: true, sendStatus: true, online: rng.Next(3) != 0);
                    }

                    break;
                default:
                    ObjectGuid joiner = rng.Next(2) == 0 ? Alliance[rng.Next(Alliance.Length)] : Horde[rng.Next(Horde.Length)];
                    if (bg.PlayerTeam(joiner) is null)
                    {
                        bg.IncreaseInvitedCount(Alliance.Contains(joiner) ? Team.Alliance : Team.Horde);
                        Assert.True(bg.AddPlayer(joiner, Alliance.Contains(joiner) ? Team.Alliance : Team.Horde));
                    }

                    break;
            }

            AssertInvariants(bg, [.. bg.Players], seed, step);
        }
    }

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int i = 1; i <= 60; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static void AssertInvariants(WarsongGulch bg, HashSet<ObjectGuid> present, int seed, int step)
    {
        string where = $"seed {seed} step {step}";
        int alliance = bg.TeamScore(Team.Alliance);
        int horde = bg.TeamScore(Team.Horde);
        Assert.True(alliance <= WarsongGulch.MaxTeamScore && horde <= WarsongGulch.MaxTeamScore, where);
        Assert.False(alliance == WarsongGulch.MaxTeamScore && horde == WarsongGulch.MaxTeamScore, where + ": two winners");

        if (alliance == WarsongGulch.MaxTeamScore)
        {
            Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
            Assert.Equal(BattlegroundWinner.Alliance, bg.Winner);
        }
        else if (horde == WarsongGulch.MaxTeamScore)
        {
            Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
            Assert.Equal(BattlegroundWinner.Horde, bg.Winner);
        }

        if (bg.Winner != BattlegroundWinner.None)
        {
            Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        }

        // After the match ended vmangos only takes the aura off a carrier who leaves and keeps the flag state (BattleGroundWS.cpp:271-296),
        // so the carrier rule is a rule of the running match.
        foreach (Team flag in bg.Status == BattlegroundStatus.WaitLeave ? Array.Empty<Team>() : new[] { Team.Alliance, Team.Horde })
        {
            bool carried = bg.FlagState(flag) == WsgFlagState.OnPlayer;
            ObjectGuid picker = bg.FlagPicker(flag);
            Assert.True(carried == !picker.IsEmpty, $"{where}: {flag} flag state {bg.FlagState(flag)} picker {picker}");
            if (carried)
            {
                Assert.Contains(picker, present);
                // The Horde flag is carried by an Alliance player and the other way round.
                Assert.Equal(flag == Team.Horde ? Team.Alliance : Team.Horde, bg.PlayerTeam(picker));
            }
        }

        Assert.Equal((uint)present.Count(g => Alliance.Contains(g)), bg.PlayersCountByTeam(Team.Alliance));
        Assert.Equal((uint)present.Count(g => Horde.Contains(g)), bg.PlayersCountByTeam(Team.Horde));
        uint captures = (uint)present.Sum(g => ((WsgScore)bg.ScoreOf(g)!).FlagCaptures);
        Assert.True(captures <= alliance + horde, where + ": more captures on scoreboards than scored");
    }
}

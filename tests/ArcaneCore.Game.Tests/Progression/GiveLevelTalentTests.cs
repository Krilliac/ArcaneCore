using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Tests.Talents;
using ArcaneCore.Kernel.Accounts;
using Xunit;
using static ArcaneCore.Game.Tests.Talents.TalentServiceLearnTests;

namespace ArcaneCore.Game.Tests.Progression;

/// <summary>
/// Every real level change reaches the talent points (vmangos Player::GiveLevel, Player.cpp:3061, calls InitTalentForLevel at
/// :3197): <see cref="PlayerProgression.LevelChanged"/> is raised once per <see cref="PlayerProgression.GiveLevel"/> that changes
/// the level and once per <see cref="PlayerProgression.GiveXp(Player, uint, ObjectGuid)"/> that levels up, however many levels it
/// gains. The talent feature subscribes <see cref="Game.Talents.TalentService.InitTalentForLevel"/> to that event; the rig does the same.
/// </summary>
public sealed class GiveLevelTalentTests : IDisposable
{
    private readonly TalentRig _rig;
    private readonly PlayerProgression _progression;
    private int _raised;

    public GiveLevelTalentTests() : this(AccountSecurity.Player)
    {
    }

    private GiveLevelTalentTests(AccountSecurity security, byte level = 9)
    {
        _rig = new TalentRig(security, level);
        _progression = new PlayerProgression(new ProgressionOptions { MaxPlayerLevel = 60 });
        _progression.InitializeLoadedPlayer(_rig.Player);
        _progression.LevelChanged += player =>
        {
            _raised++;
            _rig.Service.InitTalentForLevel(player);
        };
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void GiveLevel_Up_GrantsThePointsOfTheNewLevel()
    {
        Assert.Equal(0u, _rig.Free);

        _progression.GiveLevel(_rig.Player, 12);

        Assert.Equal(12, _rig.Player.Level);
        Assert.Equal(3u, _rig.Free);
        Assert.Equal(1, _raised);
    }

    [Fact]
    public void GiveLevel_Down_ResetsAnOverspend_OfANonAdministrator()
    {
        _progression.GiveLevel(_rig.Player, 20);
        _rig.Learn(1, 2);   // three points in talent 1
        _rig.Learn(3, 0);   // a fourth in talent 3
        Assert.Equal(7u, _rig.Free);

        _progression.GiveLevel(_rig.Player, 11);   // two points at level 11, four spent

        Assert.False(_rig.Has(T1R3));
        Assert.False(_rig.Has(T3R1));
        Assert.Equal(2u, _rig.Free);
    }

    [Fact]
    public void GiveLevel_Down_BelowTen_ClearsEveryPoint()
    {
        _progression.GiveLevel(_rig.Player, 10);
        _rig.Learn(1, 0);

        _progression.GiveLevel(_rig.Player, 9);

        Assert.False(_rig.Has(T1R1));
        Assert.Equal(0u, _rig.Free);
    }

    [Fact]
    public void GiveLevel_Down_KeepsTheTalentsOfAnAdministrator_WithNoFreePoints()
    {
        using var admin = new GiveLevelTalentTests(AccountSecurity.Administrator);
        admin._progression.GiveLevel(admin._rig.Player, 20);
        admin._rig.Learn(1, 2);

        admin._progression.GiveLevel(admin._rig.Player, 11);   // two points allowed, three spent (vmangos Player.cpp:3236-3243)

        Assert.True(admin._rig.Has(T1R3));
        Assert.Equal(0u, admin._rig.Free);
    }

    [Fact]
    public void GiveLevel_ToTheSameLevel_RaisesNothing()
    {
        _progression.GiveLevel(_rig.Player, 9);

        Assert.Equal(0, _raised);
    }

    [Fact]
    public void GiveXp_ThroughOneLevel_RaisesLevelChangedOnce()
    {
        _progression.GiveXp(_rig.Player, _rig.Player.GetUInt32(UpdateFields.PlayerNextLevelXp), ObjectGuid.Empty);

        Assert.Equal(10, _rig.Player.Level);
        Assert.Equal(1, _raised);
        Assert.Equal(1u, _rig.Free);
    }

    [Fact]
    public void GiveXp_ThroughSeveralLevels_RaisesLevelChangedOnce_ForTheFinalLevel()
    {
        uint xp = PlayerXpTable.XpForLevel(9, 60) + PlayerXpTable.XpForLevel(10, 60) + PlayerXpTable.XpForLevel(11, 60);

        _progression.GiveXp(_rig.Player, xp, ObjectGuid.Empty);

        Assert.Equal(12, _rig.Player.Level);
        Assert.Equal(1, _raised);
        Assert.Equal(3u, _rig.Free);
    }
}

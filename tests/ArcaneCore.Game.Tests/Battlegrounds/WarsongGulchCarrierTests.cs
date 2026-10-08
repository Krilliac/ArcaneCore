using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The flag carrier shown on the battleground map (vmangos HandleBattleGroundPlayerPositionsOpcode, BattleGroundHandler.cpp:296-315) and
/// the generic entry points every flag drop trigger and every flag click reaches (vmangos <c>BattleGround::EventPlayerDroppedFlag</c> and
/// <c>EventPlayerClickedOnFlag</c>, virtual on the base class and overridden by Warsong Gulch).
/// </summary>
public sealed class WarsongGulchCarrierTests
{
    private static readonly BattlegroundObjectUse HordeStand = new(WarsongGulch.HordeFlagBaseEntry, WarsongGulch.EventFlagHorde, 0, WithinTenYards: false);
    private static readonly BattlegroundObjectUse AllianceStand = new(WarsongGulch.AllianceFlagBaseEntry, WarsongGulch.EventFlagAlliance, 0, WithinTenYards: false);
    private static readonly BattlegroundObjectUse HordeFlagOnGround = new(WarsongGulch.HordeFlagGroundEntry, BattlegroundConstants.EventNone, BattlegroundConstants.EventNone, WithinTenYards: true);

    [Fact]
    public void NoCarrier_IsShownToEitherSide()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);
        Assert.True(bg.FlagCarrierShownTo(Team.Alliance).IsEmpty);
        Assert.True(bg.FlagCarrierShownTo(Team.Horde).IsEmpty);
    }

    [Fact]
    public void TheViewersOwnTeamsCarrier_IsShown_AsVmangosPicksTheFlagKeeperOfTheOtherTeamsFlag()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);

        // An Alliance player takes the Horde flag: Alliance viewers get GetHordeFlagPickerGuid (BattleGroundHandler.cpp:301-302).
        bg.EventPlayerClickedOnFlag(Alliance[0], HordeStand);
        Assert.Equal(Alliance[0], bg.FlagPicker(Team.Horde));
        Assert.Equal(Alliance[0], bg.FlagCarrierShownTo(Team.Alliance));
        Assert.True(bg.FlagCarrierShownTo(Team.Horde).IsEmpty);

        // A Horde player takes the Alliance flag: Horde viewers get GetAllianceFlagPickerGuid (:303-304).
        bg.EventPlayerClickedOnFlag(Horde[0], AllianceStand);
        Assert.Equal(Horde[0], bg.FlagCarrierShownTo(Team.Horde));
    }

    [Fact]
    public void OtherBattlegroundTypes_ShowNoCarrier()
    {
        // vmangos sends uint8(0) for every type but Warsong Gulch (BattleGroundHandler.cpp:316-321).
        var host = new RecordingHost();
        var ports = new RecordingPorts();
        var ab = new ArathiBasin(ArathiBasinTests.AbTemplate(), bracket: 5, instanceId: 102, clientInstanceId: 1, new BattlegroundOptions(), ports.ToPorts(host));
        Assert.True(ab.FlagCarrierShownTo(Team.Alliance).IsEmpty);
    }

    [Fact]
    public void TheGenericDropEntry_DropsTheCarriedFlag_LikeTheAuraRemovalPath()
    {
        // SpellAuras.cpp:4069-4080: removing an aura with AURA_INTERRUPT_INVULNERABILITY_BUFF_CANCELS (the flag aura itself, a Divine Shield
        // stripping it, a right click) calls BattleGround::EventPlayerDroppedFlag.
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        bg.EventPlayerClickedOnFlag(Alliance[0], HordeStand);

        Battleground generic = bg;
        generic.EventPlayerDroppedFlag(Alliance[0]);

        Assert.Equal(WsgFlagState.OnGround, bg.FlagState(Team.Horde));
        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Contains((Alliance[0], WarsongGulch.SpellWarsongFlagDropped), ports.Casts);
        Assert.Contains((WarsongGulch.WorldStateFlagTakenHorde, uint.MaxValue), host.WorldStates);

        // A second trigger (the aura removal the drop itself causes) changes nothing.
        int casts = ports.Casts.Count;
        generic.EventPlayerDroppedFlag(Alliance[0]);
        Assert.Equal(casts, ports.Casts.Count);
    }

    [Fact]
    public void TheGenericClickEntry_PicksUpADroppedFlag()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);
        bg.EventPlayerClickedOnFlag(Alliance[0], HordeStand);
        bg.EventPlayerDroppedFlag(Alliance[0]);

        bg.EventPlayerClickedOnFlag(Alliance[1], HordeFlagOnGround);

        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
        Assert.Equal(Alliance[1], bg.FlagCarrierShownTo(Team.Alliance));
    }

    [Fact]
    public void ADropByAPlayerWhoCarriesNothing_IsIgnored()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        int states = host.WorldStates.Count;
        bg.EventPlayerDroppedFlag(Horde[1]);
        Assert.Equal(states, host.WorldStates.Count);
        Assert.DoesNotContain(ports.Casts, c => c.Spell is WarsongGulch.SpellWarsongFlagDropped or WarsongGulch.SpellSilverwingFlagDropped);
    }
}

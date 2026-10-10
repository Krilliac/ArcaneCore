using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Spells.Holidays;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class HolidayScriptsThreeTests
{
    [Theory]
    [InlineData(BellAi.GoHordeBell, 1497u, 0u, BellAi.BellTollHorde)]
    [InlineData(BellAi.GoHordeBell, 1637u, 0u, BellAi.BellTollTribal)]
    [InlineData(BellAi.GoAllianceBell, 1537u, 0u, BellAi.BellTollDwarfGnome)]
    [InlineData(BellAi.GoAllianceBell, 1657u, 0u, BellAi.BellTollNightElf)]
    [InlineData(BellAi.GoAllianceBell, 1519u, 0u, BellAi.BellTollAlliance)]
    [InlineData(BellAi.GoAllianceBell, 40u, 115u, BellAi.LighthouseFoghorn)]
    public void Bells_pick_the_vmangos_sound(uint entry, uint zone, uint area, uint sound)
        => Assert.Equal(sound, BellAi.SoundFor(entry, zone, area, 0, 0, 0));

    [Fact]
    public void Theramore_only_the_lighthouse_bell_is_a_foghorn()
    {
        Assert.Equal(BellAi.LighthouseFoghorn, BellAi.SoundFor(BellAi.GoAllianceBell, 15, 513, -3667f, -4754f, 1.8f));
        Assert.Equal(BellAi.BellTollAlliance, BellAi.SoundFor(BellAi.GoAllianceBell, 15, 513, -3600f, -4400f, 10f));
    }

    [Theory]
    [InlineData(0, BellAi.BellTollAlliance, 12)]
    [InlineData(12, BellAi.BellTollAlliance, 12)]
    [InlineData(15, BellAi.BellTollHorde, 3)]
    [InlineData(15, BellAi.BellTollDwarfGnome, 1)]
    [InlineData(9, BellAi.LighthouseFoghorn, 1)]
    public void Bells_toll_the_twelve_hour_clock(int hour, uint sound, int rings)
        => Assert.Equal(rings, BellAi.RingsFor(hour, sound));

    [Fact]
    public void Reindeer_speed_follows_the_mount()
    {
        Assert.Equal(ReindeerTransformationScript.ReindeerFast, ReindeerTransformationScript.ReindeerFor(Entities.Unit.BaseRunSpeed * 2f));
        Assert.Equal(ReindeerTransformationScript.ReindeerSlow, ReindeerTransformationScript.ReindeerFor(Entities.Unit.BaseRunSpeed * 1.6f));
    }

    [Fact]
    public void Pole_buff_needs_two_ribbons()
    {
        Assert.False(RibbonPoleDanceModule.Dancing([29705, 1u]));
        Assert.True(RibbonPoleDanceModule.Dancing([29705, 29727]));
    }

    [Fact]
    public void Invasion_stage_rises_on_fifty_kills_or_an_hour_and_stops_at_the_boss()
    {
        bool active = true;
        int saves = 0;
        var invasion = new ElementalInvasionController(e => active && e == 13) { Changed = _ => saves++ };
        Assert.Equal(3, ElementalInvasionController.SpawnCount(invasion.Stage(0)));
        for (int i = 0; i < 49; i++) invasion.OnCreatureDied(14460);
        Assert.False(invasion.TryAdvance(0, hourElapsed: false));
        invasion.OnCreatureDied(14460);
        Assert.True(invasion.TryAdvance(0, hourElapsed: false));
        Assert.Equal((2, 0), (invasion.Stage(0), invasion.Kills(0)));
        while (invasion.TryAdvance(0, hourElapsed: true)) { }
        Assert.Equal(ElementalInvasionController.StageBoss, invasion.Stage(0));
        Assert.Equal(6, ElementalInvasionController.SpawnCount(invasion.Stage(0)));
        invasion.OnCreatureDied(14455); // Air is not running
        Assert.Equal(0, invasion.Kills(1));
        invasion.Update();
        active = false;
        invasion.Update();
        Assert.Equal((1, 0), (invasion.Stage(0), invasion.Kills(0)));
        Assert.True(saves > 50);
    }
}

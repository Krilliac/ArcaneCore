using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests.Rogue;

/// <summary>Energy pool at character creation (vmangos Player::Create: energy 100 of 100, full at start).</summary>
public sealed class RogueCreationTests
{
    [Fact]
    public void RogueAppearance_StartsWithAFull100EnergyPool_WhileWarriorsStartWithEmptyRage()
    {
        PlayerAppearance rogue = CharacterPackets.BuildAppearance(new RaceInfo(49, 1), new ClassInfo(45, 0, (byte)PowerType.Energy));
        Assert.Equal(PowerType.Energy, rogue.PowerType);
        Assert.Equal(100u, rogue.MaxPower);
        Assert.Equal(100u, rogue.StartPower);

        PlayerAppearance warrior = CharacterPackets.BuildAppearance(new RaceInfo(49, 1), new ClassInfo(60, 0, (byte)PowerType.Rage));
        Assert.Equal(1000u, warrior.MaxPower);
        Assert.Equal(0u, warrior.StartPower);
    }
}

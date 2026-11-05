using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// <see cref="SummonService.Unsummon(Creature, PetUnsummonReason)"/> writes one log entry whose level comes from the reason: a routine departure at
/// Debug, a pet that ran away at Information.
/// </summary>
public sealed class PetUnsummonLoggingTests
{
    private static string[] UnsummonLog(PetUnsummonReason reason)
    {
        using var kit = new PetTestKit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(player, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);

        var logger = new LevelPrefixedCapturingLogger();
        var service = new SummonService(systems: map => ReferenceEquals(map, kit.Map) ? kit.Creatures : null, logger: logger, random: new Random(3));
        service.Unsummon(pet, reason);
        return [.. logger.Entries];
    }

    [Fact]
    public void Dismissed_IsLoggedOnceAtDebug()
    {
        string entry = Assert.Single(UnsummonLog(PetUnsummonReason.Dismissed));
        Assert.StartsWith("Debug: unsummon", entry);
    }

    [Fact]
    public void RanAway_IsLoggedOnceAtInformation()
    {
        string entry = Assert.Single(UnsummonLog(PetUnsummonReason.RanAway));
        Assert.StartsWith("Information: unsummon", entry);
    }
}

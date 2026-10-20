using ArcaneCore.Game.Entities;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>.playerbot follow</c> (<see cref="ManagedPlayerbotFeature.Leash"/>) on a bot the group coordinator has just matched, before the bot's
/// first group-driven think: the coordinator must let it go there and then, or it re-takes the leashed bot. The window is one world tick
/// wide and the manual clock cannot be stopped inside it, so this guards the outcome (it did not fail without the fix in the runs made).
/// </summary>
public sealed class PlayerbotLeashGroupMemberTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    [Fact]
    public async Task ALeash_OnABotJustMatchedIntoAGroup_ReleasesItFromTheCoordinator()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Leashtank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Leashheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Leashmage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];
        Assert.True(await world.RunUntilAsync(60_000, () => ids.All(id => world.HasQuest(id, EliteQuest))), world.Trace());

        // Stop the clock the moment the coordinator holds a member whose own think has not run yet (the leash lands in that window).
        Assert.True(await world.RunUntilAsync(30_000, () => ids.Any(world.Coordinator.Drives)), world.Trace());
        Guid held = ids.First(world.Coordinator.Drives);
        PlayerbotOperationResult result = await world.OnWorldAsync(() =>
        {
            Player master = world.Player(ids.First(id => id != held));
            return world.Bots.Leash(held, master, stay: false);
        });
        Assert.True(result.Success, result.Code);

        Assert.False(await world.OnWorldAsync(() => world.Coordinator.Drives(held)), "the coordinator still holds the leashed bot");
        await world.RunAsync(5_000);
        Assert.False(await world.OnWorldAsync(() => world.Coordinator.Drives(held)), "the coordinator took the leashed bot back");
    }
}

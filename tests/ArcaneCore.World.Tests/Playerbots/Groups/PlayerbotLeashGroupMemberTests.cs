using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>.playerbot follow</c> (<see cref="ManagedPlayerbotFeature.Leash"/>) on a bot the group coordinator has just matched, before the bot's
/// first group-driven think (so its <c>GroupDriven</c> is still false): the coordinator must let it go there and then, or its next think
/// takes the leashed bot back into the group.
/// </summary>
public sealed class PlayerbotLeashGroupMemberTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    [Fact]
    public async Task ALeash_OnABotJustMatchedIntoAGroup_ReleasesItFromTheCoordinator_AndTheBotStaysLeashed()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Leashtank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Leashheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Leashmage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];
        Assert.True(await world.RunUntilAsync(60_000, () => ids.All(id => world.HasQuest(id, EliteQuest))), world.Trace());
        // A leash needs a real player as master (a managed bot is no master).
        await using WorldTestClient real = await world.World.EnterWorldAsync("LEASHM", "Leashmaster", AccountSecurity.Player);
        ObjectGuid realGuid = await world.OnWorldAsync(() => world.World.Host.World.OnlinePlayers.Single(p => p.Name == "Leashmaster").Guid);

        // One world-thread call: the coordinator is stepped by hand until it matches the bots (no bot think runs in between), then the
        // leash lands in the window before the matched bot's first group-driven think.
        Guid held = await world.OnWorldAsync(() =>
        {
            var bots = ids.Select(id => new PlayerbotGroupCoordinator.PlayerbotGroupBot(id, world.Session(id), world.Bots.FindBrain(id)!, Free: true)).ToList();
            for (int step = 0; step < 20 && !ids.Any(world.Coordinator.Drives); step++) world.Coordinator.Update(bots, 60_000);
            Guid member = ids.FirstOrDefault(world.Coordinator.Drives);
            Assert.NotEqual(Guid.Empty, member);
            Player master = world.World.Host.World.FindOnlinePlayer(realGuid)!;
            PlayerbotOperationResult result = world.Bots.Leash(member, master, stay: false);
            Assert.True(result.Success, result.Code);
            return member;
        });

        Assert.False(await world.OnWorldAsync(() => world.Coordinator.Drives(held)), "the coordinator still holds the leashed bot");
        await world.RunAsync(1_000);
        Assert.False(await world.OnWorldAsync(() => world.Coordinator.Drives(held)), "the coordinator took the leashed bot back");
        Assert.True(world.Bots.IsPartyDriven(held), "the leashed bot was pulled out of its leash");
    }
}

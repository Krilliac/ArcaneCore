using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary><c>.playerbot population [status|enable|disable|target N]</c> through the chat command table, as an administrator types it.</summary>
public sealed class PlayerbotPopulationCommandTests
{
    private static async Task<string> SayAsync(WorldTestClient admin, string command, Func<string, bool> reply)
    {
        await admin.SendChatAsync(ChatType.Say, Language.Common, command);
        while (true)
        {
            string line = (await admin.ReadChatAsync()).Text;
            if (reply(line)) return line;
        }
    }

    [Fact]
    public async Task TheCommand_ShowsTheStatus_TogglesTheLiveOption_AndSetsTheTargetWithinItsRange()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.Enabled = false);
        await using WorldTestClient admin = await world.Host.EnterWorldAsync("POPADMIN", "Popadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string line = await SayAsync(admin, ".playerbot population status", l => l.StartsWith("Population:", StringComparison.Ordinal));
        Assert.Equal("Population: off, state disabled: the population manager is off.", line);

        await SayAsync(admin, ".playerbot population enable", l => l.StartsWith("Playerbot population enabled.", StringComparison.Ordinal));
        Assert.True(world.Population.Enabled);

        await SayAsync(admin, ".playerbot population target 7", l => l.StartsWith("Playerbot population target set to 7.", StringComparison.Ordinal));
        Assert.Equal(7, world.Population.TargetOnline);

        // Out of range (above MaxBotsCeiling, below 0): refused with the range, the value unchanged.
        int aboveCeiling = PlayerbotOptions.MaxBotsCeiling + 1;
        line = await SayAsync(admin, $".playerbot population target {aboveCeiling}", l => l.Contains("refused", StringComparison.Ordinal));
        Assert.Equal($"Population target {aboveCeiling} refused: it must be 0..{PlayerbotOptions.MaxBotsCeiling}.", line);
        Assert.Equal(7, world.Population.TargetOnline);
        line = await SayAsync(admin, ".playerbot population target -1", l => l.Contains("refused", StringComparison.Ordinal));
        Assert.Equal($"Population target -1 refused: it must be 0..{PlayerbotOptions.MaxBotsCeiling}.", line);
        Assert.Equal(7, world.Population.TargetOnline);

        // The bounds themselves are accepted.
        await SayAsync(admin, ".playerbot population target 0", l => l.StartsWith("Playerbot population target set to 0.", StringComparison.Ordinal));
        Assert.Equal(0, world.Population.TargetOnline);
        await SayAsync(admin, $".playerbot population target {PlayerbotOptions.MaxBotsCeiling}", l => l.StartsWith("Playerbot population target set to ", StringComparison.Ordinal));
        Assert.Equal(PlayerbotOptions.MaxBotsCeiling, world.Population.TargetOnline);

        // A malformed argument changes nothing (the command answers with its syntax).
        world.Population.TargetOnline = 7;
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population target seven");
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population target 3 4");
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population target");

        // The status after a pass reports the live target and the band's want against have.
        world.Ticks.Set(5, 10);
        await world.PassAsync();
        await SayAsync(admin, ".playerbot population status", l => l.StartsWith("Population: on, state settling", StringComparison.Ordinal));
        line = await SayAsync(admin, ".playerbot population status", l => l.StartsWith("Target ", StringComparison.Ordinal));
        Assert.StartsWith("Target 7 (effective 4), running 0 (0 owned), parked 0", line);
        Assert.Equal(7, world.Population.TargetOnline);

        await SayAsync(admin, ".playerbot population disable", l => l.StartsWith("Playerbot population disabled;", StringComparison.Ordinal));
        Assert.False(world.Population.Enabled);
    }

    [Fact]
    public async Task ThePopulationCommand_IsForAdministratorsOnly()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        await using WorldTestClient player = await world.Host.EnterWorldAsync("POPPLAYER", "Popplayer");
        await player.CollectAsync();
        int target = world.Population.TargetOnline;

        await player.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population target 99");
        await player.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population disable");
        // Each refused command answers; both have been handled once both answers are in.
        await player.ReadSystemChatAsync();
        await player.ReadSystemChatAsync();

        Assert.Equal(target, world.Population.TargetOnline);
        Assert.True(world.Population.Enabled);
    }
}

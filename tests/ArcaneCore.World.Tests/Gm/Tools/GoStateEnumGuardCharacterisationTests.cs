using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.GameObjects;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Tools;

/// <summary>
/// Characterises the <c>.alter gostate</c> argument guard ahead of the shared enum-name helper: 0..2 and the names
/// (including "alternative") are accepted, any other number or unknown word is a syntax error and leaves the state alone.
/// </summary>
public sealed class GoStateEnumGuardCharacterisationTests
{
    private const uint DoorEntry = 20102;
    private const uint DoorSpawn = 88202;

    [Fact]
    public async Task GoStateAcceptsInRangeNumbersAndNamesButRejectsOtherNumbersAndUnknownWords()
    {
        var door = new GameObjectTemplate { Entry = DoorEntry, Type = (uint)GameObjectType.Door, DisplayId = 5, Name = "State Door", Data = new uint[GameObjectTemplate.DataCount] };
        var objects = new GameObjectTestContext(
            new GameObjectContent([door], [new GameObjectSpawn { Guid = DoorSpawn, Entry = DoorEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f }], [], [], []),
            new LootContent([], []));
        GameObjectTestStore.Current.Value = objects;
        WorldTestHost started;
        try
        {
            started = WorldTestHost.Start();
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }

        await using WorldTestHost host = started;
        await using WorldTestClient gm = await host.EnterWorldAsync("GOSTATEG", "Gostateg", AccountSecurity.Administrator);
        await gm.CollectAsync();
        GameObject go = await host.OnWorldAsync(() => objects.Feature!.FindSystem(0u)!.GameObjects.Single(g => g.Spawn is not null));

        Assert.Contains(await RunAsync(gm, ".alter gostate 2"), l => l.EndsWith("state Ready -> ActiveAlternative.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter gostate ALTERNATIVE"), l => l.EndsWith("state ActiveAlternative -> ActiveAlternative.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter gostate Ready"), l => l.EndsWith("state ActiveAlternative -> Ready.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter gostate ACTIVEALTERNATIVE"), l => l.EndsWith("state Ready -> ActiveAlternative.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter gostate 0"), l => l.EndsWith("state ActiveAlternative -> Active.", StringComparison.Ordinal));

        foreach (string rejected in new[] { "3", "255", "4294967295", "4294967296", "-1", "+1", "sideways" })
        {
            Assert.Contains(await RunAsync(gm, $".alter gostate {rejected}"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
            Assert.Equal(GameObjectState.Active, await host.OnWorldAsync(() => go.State));
        }
    }

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}

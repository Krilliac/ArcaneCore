using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A real player inspecting an autonomous playerbot. The 1.12.1 client reads another player's gear (GetInventoryItemTexture, CanInspect) only
/// while it is within INSPECT_DISTANCE (10 yards, 3D; vmangos ObjectDefines.h:26), and the server answers CMSG_INSPECT and
/// MSG_INSPECT_HONOR_STATS only inside the same range (<see cref="InspectHandlers.InspectableTarget"/>). A bot that keeps walking its goal
/// leaves that range while the inspection window is open, so the gear re-reads empty on a tab switch and the honor request goes unanswered.
/// The bot must hold its position while a real player inspects it. The bot here is driven by a scenario stay centred 50 yards away, which
/// makes it walk off the moment it is free to think; the world runs on the manual clock, including 1.7 s ticks (the live host's worst
/// tick), so a delayed tick or a late packet cannot let the bot slip out of range.
/// </summary>
public sealed class PlayerbotInspectHoldWorldTests
{
    private const byte Human = 1, Warrior = 1;
    private const string PlayerName = "Inspector";
    private const uint NormalStepMs = 50, LongStepMs = 1700;

    private static byte[] GuidPayload(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    /// <summary>The player and a same-faction autonomous bot 4 yards east of it, the bot told to stay 54 yards further east (it walks there when free).</summary>
    private static async Task<(GroupTestWorld World, WorldTestClient Client, ulong BotGuid, Guid BotId, Vector3 StayCentre)> StartAsync()
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        WorldTestClient client = await world.World.EnterWorldAsync("INSPECTHOLD", PlayerName, AccountSecurity.Player);
        var bot = await world.AddBotAsync("Inspbot", Human, Warrior, 1, []);
        var centre = new Vector3(StartX + 54f, StartY, StartZ);
        ulong botGuid = await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
            Player botPlayer = world.Player(bot.Id);
            uint now = world.World.Host.World.NowMs;
            player.Relocate(StartX, StartY, StartZ, 0f, now);
            botPlayer.Relocate(StartX + 4f, StartY, StartZ, 0f, now);
            world.Bots.FindBrain(bot.Id)!.Stay = new PlayerbotStay(0, centre, 10f, 1, unchecked(now + 600_000));
            return botPlayer.Guid.Value;
        });
        return (world, client, botGuid, bot.Id, centre);
    }

    private static Task<float> DistanceAsync(GroupTestWorld world, Guid botId) => world.OnWorldAsync(() =>
    {
        Player player = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
        Player bot = world.Player(botId);
        float dx = player.X - bot.X, dy = player.Y - bot.Y, dz = player.Z - bot.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    });

    private static Task<string> PositionsAsync(GroupTestWorld world, Guid botId) => world.OnWorldAsync(() =>
    {
        Player player = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
        Player bot = world.Player(botId);
        return FormattableString.Invariant($"player=({player.X:F1}, {player.Y:F1}, {player.Z:F1}) bot=({bot.X:F1}, {bot.Y:F1}, {bot.Z:F1}) now={world.World.Host.World.NowMs}");
    });

    /// <summary>
    /// Change the manual clock's tick length (WorldRuntime._manualStepMs, a private field the test seam has no setter for) on the world thread,
    /// so one stretch of the run is a single long tick and the rest are normal 50 ms ticks.
    /// </summary>
    private static async Task SetTickMsAsync(GroupTestWorld world, uint ms)
    {
        FieldInfo field = world.World.Host.World.GetType().GetField("_manualStepMs", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorldRuntime._manualStepMs not found; the manual clock changed");
        await world.OnWorldAsync(() =>
        {
            field.SetValue(world.World.Host.World, ms);
            return true;
        });
    }

    /// <summary>About ten seconds of world time: normal ticks, one 1.7 s tick, normal ticks, a second 1.7 s tick, normal ticks.</summary>
    private static async Task RunTenSecondsWithLongTicksAsync(GroupTestWorld world)
    {
        await world.RunAsync(2_000);
        await SetTickMsAsync(world, LongStepMs);
        await world.RunAsync(LongStepMs);
        await SetTickMsAsync(world, NormalStepMs);
        await world.RunAsync(3_000);
        await SetTickMsAsync(world, LongStepMs);
        await world.RunAsync(LongStepMs);
        await SetTickMsAsync(world, NormalStepMs);
        await world.RunAsync(1_600);
    }

    [Fact]
    public async Task AnInspectedBot_HoldsItsPosition_SoALateHonorRequestIsStillAnswered_AfterLongWorldTicks()
    {
        (GroupTestWorld world, WorldTestClient client, ulong botGuid, Guid botId, _) = await StartAsync();
        await using GroupTestWorld owner = world;
        await using WorldTestClient player = client;

        // Sanity: in range, the inspect is answered with the bot's guid.
        float before = await DistanceAsync(world, botId);
        Assert.True(before <= InspectHandlers.InspectDistance, $"setup: the bot starts {before:F1} yd away, outside the {InspectHandlers.InspectDistance} yd inspect range");
        await player.SendAsync(WorldOpcode.CmsgInspect, GuidPayload(botGuid));
        byte[] inspected = await player.ReadUntilAsync(WorldOpcode.SmsgInspect);
        Assert.Equal(botGuid, BinaryPrimitives.ReadUInt64LittleEndian(inspected));

        // About ten seconds pass, with long ticks in them; the player keeps the inspection window open and then clicks the Honor tab.
        await RunTenSecondsWithLongTicksAsync(world);
        float after = await DistanceAsync(world, botId);
        string where = await PositionsAsync(world, botId);

        await player.SendAsync(WorldOpcode.MsgInspectHonorStats, GuidPayload(botGuid));
        byte[]? honor = null;
        try
        {
            honor = await player.ReadUntilAsync(WorldOpcode.MsgInspectHonorStats, TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Unanswered: reported with the distance below.
        }

        Assert.True(after <= InspectHandlers.InspectDistance,
            $"the inspected bot walked off: {after:F1} yd from the inspector after ~10 s (limit {InspectHandlers.InspectDistance} yd, started {before:F1} yd); {where}");
        Assert.True(honor is not null, $"MSG_INSPECT_HONOR_STATS got no answer; the bot is {after:F1} yd away; {where}");
        Assert.Equal(50, honor!.Length);
        Assert.Equal(botGuid, BinaryPrimitives.ReadUInt64LittleEndian(honor));
    }

    [Fact]
    public async Task AnInspectedBot_ResumesWalkingToItsStay_OnceTheInspectorNoLongerHasItSelected()
    {
        (GroupTestWorld world, WorldTestClient client, ulong botGuid, Guid botId, Vector3 centre) = await StartAsync();
        await using GroupTestWorld owner = world;
        await using WorldTestClient player = client;

        await player.SendAsync(WorldOpcode.CmsgInspect, GuidPayload(botGuid));
        await player.ReadUntilAsync(WorldOpcode.SmsgInspect);
        await world.RunAsync(3_000);
        float held = await DistanceAsync(world, botId);
        string heldAt = await PositionsAsync(world, botId);
        Assert.True(held <= InspectHandlers.InspectDistance, $"the bot did not hold while inspected: {held:F1} yd away after 3 s; {heldAt}");

        // The player deselects the bot (the real CMSG_SET_SELECTION with an empty guid): the hold ends and the bot goes back to its stay.
        await player.SendAsync(WorldOpcode.CmsgSetSelection, GuidPayload(0));
        Assert.True(await world.RunUntilPacedAsync(() => world.World.Host.World.FindOnlinePlayer(PlayerName)!.Selection.IsEmpty, 5_000),
            "the deselect packet was never handled");
        Assert.True(await world.RunUntilAsync(30_000, () => Vector2.Distance(new Vector2(world.Player(botId).X, world.Player(botId).Y), new Vector2(centre.X, centre.Y)) <= 10f),
            "the bot never resumed walking to its stay after the inspector deselected it; " + await PositionsAsync(world, botId));
        float end = await DistanceAsync(world, botId);
        Assert.True(end > InspectHandlers.InspectDistance, $"the bot reached its stay but is still {end:F1} yd from the inspector");
    }
}

using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.World.Ops.MapFeed;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The <c>links</c> op (design 0.6, r3): the entities linked to a real player by an owner, summoner, charmer, creator or group relation, computed on
/// the world thread only for a web connection that holds a subscription. A real summon through <see cref="SummonService"/> appears; a bot in a real
/// player's group appears; nothing is computed while no web visitor subscribes.
/// </summary>
public sealed class MapFeedPublicLinksTests
{
    private static FeedWorldSetup Setup(string directory, bool bots = false) => new()
    {
        Terrain = !bots,
        Bots = bots,
        Content = !bots,
        WebLogin = true,
        Time = new FeedTestClock(),
        Actions = o =>
        {
            o.Enabled = true;
            o.AuditDirectory = directory;
        },
    };

    private static async Task<JsonElement> LinksAsync(FeedClient client, long id, uint map = 0)
    {
        await client.SendAsync(string.Create(CultureInfo.InvariantCulture, $$"""{"op":"links","id":{{id}},"map":{{map}}}"""));
        return await ActionWire.ReadResultAsync(client, id);
    }

    private static HashSet<string> Linked(JsonElement result)
        => [.. result.GetProperty("linked").EnumerateArray().Select(e => e.GetString()!)];

    private static string Hex(ulong guid) => guid.ToString("X", CultureInfo.InvariantCulture);

    [WindowsFact]
    public async Task OnALocalConnection_LinksIsRoleMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(directory));
            FeedClient local = await world.ConnectAsync("local");
            await local.SendAsync(FeedClient.Subscribe());

            (await LinksAsync(local, 1)).AssertRefused("role-mismatch");
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task WithoutASubscription_NothingIsComputed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(directory));
            MapFeedPublicLinks links = world.Host.WorldServices.GetRequiredService<MapFeedPublicLinks>();
            FeedClient web = await world.ConnectAsync("web");

            (await LinksAsync(web, 1)).AssertRefused("not-subscribed");
            (await LinksAsync(web, 2)).AssertRefused("not-subscribed");

            Assert.Equal(0, links.Computations);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ARealSummon_IsLinkedToItsOwner_AndAnUnrelatedCreatureIsNot_AndTheScanIsThrottled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(directory));
            MapFeedPublicLinks links = world.Host.WorldServices.GetRequiredService<MapFeedPublicLinks>();
            await using WorldTestClient zed = await world.AddPlayerAsync("Zed");
            SummonService summons = world.Host.WorldServices.GetRequiredService<PetsFeature>().Service
                ?? throw new InvalidOperationException("the host has no pets service");
            (ulong pet, ulong grandChild, ulong owner) = await world.Host.OnWorldAsync(() =>
            {
                Player player = world.Host.World.FindOnlinePlayer("Zed")!;
                Unit pet = summons.Summon(player, FeedWorld.WolfEntry, player.X + 1, player.Y, player.Z, 0, 600_000)
                    ?? throw new InvalidOperationException("the summon produced no pet");
                Unit child = summons.Summon(pet, FeedWorld.WolfEntry, pet.X + 1, pet.Y, pet.Z, 0, 600_000)
                    ?? throw new InvalidOperationException("the pet's own summon produced nothing");
                return (pet.Guid.Value, child.Guid.Value, player.Guid.Value);
            });
            FeedClient web = await world.ConnectAsync("web");
            await web.SendAsync(FeedClient.Subscribe());

            JsonElement first = await LinksAsync(web, 1);
            JsonElement second = await LinksAsync(web, 2);

            Assert.True(first.Ok(), first.ToString());
            HashSet<string> linked = Linked(first);
            Assert.Contains(Hex(pet), linked);
            Assert.Contains(Hex(grandChild), linked); // owned by the pet: linked through it (transitive)
            Assert.DoesNotContain(Hex(FeedWorld.WolfGuid), linked); // the spawned wolf has no owner
            Assert.DoesNotContain(Hex(FeedWorld.ChestGuid), linked);
            Assert.DoesNotContain(Hex(owner), linked); // players are not "linked": they are hidden by kind
            Assert.Contains((uint)FeedWorld.WolfEntry, first.GetProperty("summonEntries").EnumerateArray().Select(e => e.GetUInt32()));
            Assert.Equal(Linked(first), Linked(second));
            Assert.Equal(1, links.Computations); // the second ask came inside the minimum interval
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AGameObjectOwnedByARealPlayer_IsLinked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(directory));
            await using WorldTestClient zed = await world.AddPlayerAsync("Zed");
            ulong owner = await world.Host.OnWorldAsync(() =>
            {
                Player player = world.Host.World.FindOnlinePlayer("Zed")!;
                GameObjectMapSystem objects = player.Map!.FindUpdater<GameObjectMapSystem>()!;
                objects.GameObjects.Single(g => g.Guid.Value == FeedWorld.ChestGuid).SetOwner(player.Guid);
                return player.Guid.Value;
            });
            FeedClient web = await world.ConnectAsync("web");
            await web.SendAsync(FeedClient.Subscribe());

            JsonElement result = await LinksAsync(web, 1);

            Assert.Contains(Hex(FeedWorld.ChestGuid), Linked(result));
            Assert.DoesNotContain(Hex(FeedWorld.WolfGuid), Linked(result));
            Assert.Contains((uint)FeedWorld.ChestEntry, result.GetProperty("ownedObjectEntries").EnumerateArray().Select(e => e.GetUInt32()));
            _ = owner;
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ABotInARealPlayersGroup_IsLinked_AndAnUngroupedBotIsNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(directory, bots: true));
            await using WorldTestClient zed = await world.AddPlayerAsync("Zed");
            Guid groupedBot = await world.AddBotAsync("Grouped");
            Guid looseBot = await world.AddBotAsync("Loose");
            GroupManager groups = world.Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
            (ulong grouped, ulong loose) = await world.Host.OnWorldAsync(() =>
            {
                Player leader = world.Host.World.FindOnlinePlayer("Zed")!;
                Player member = world.Host.World.FindOnlinePlayer("Grouped")!;
                groups.Invite(leader, member.Name);
                groups.Accept(member);
                return (member.Guid.Value, world.Host.World.FindOnlinePlayer("Loose")!.Guid.Value);
            });
            Assert.NotEqual(groupedBot, looseBot);
            FeedClient web = await world.ConnectAsync("web");
            await web.SendAsync(FeedClient.Subscribe());

            JsonElement result = await LinksAsync(web, 1);

            Assert.Contains(Hex(grouped), Linked(result));
            Assert.DoesNotContain(Hex(loose), Linked(result));
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }
}

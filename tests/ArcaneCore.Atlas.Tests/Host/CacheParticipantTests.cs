using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// The cache participant list (design section 3): later lanes register their caches (texture, model package, animation,
/// character composite) through <see cref="CacheParticipants"/> so nobody edits <c>StatusCommands.cs</c> again. <c>status</c> reports
/// each cache's count and bytes plus one <c>cacheBytesTotal</c>, and <c>cache.clear</c> empties every participant.
/// </summary>
public sealed class CacheParticipantTests
{
    [Fact]
    public async Task Status_ReportsEveryParticipant_AndTheSumOfAllCaches()
    {
        var textures = new FakeCache("textures", bytes: 1000, count: 3);
        var models = new FakeCache("model-packages", bytes: 250, count: 1);
        await using HostRig rig = HostRig.Create(register: context =>
        {
            CacheParticipants participants = context.Services.Require<CacheParticipants>();
            participants.Add(textures);
            participants.Add(models);
        });

        JsonNode status = await rig.DataAsync("status");

        JsonArray caches = status["caches"]!.AsArray();
        JsonNode texturesEntry = caches.Single(c => c!["name"]!.GetValue<string>() == "textures")!;
        Assert.Equal(3, texturesEntry["count"]!.GetValue<int>());
        Assert.Equal(1000, texturesEntry["bytes"]!.GetValue<long>());
        Assert.Equal(250, caches.Single(c => c!["name"]!.GetValue<string>() == "model-packages")!["bytes"]!.GetValue<long>());
        long tiles = status["cacheBytes"]!.GetValue<long>();
        Assert.Equal(tiles + 1250, status["cacheBytesTotal"]!.GetValue<long>());
    }

    [Fact]
    public async Task Status_WithNoParticipants_StillReportsATotalEqualToTheTileCache()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode status = await rig.DataAsync("status");

        Assert.Equal(status["cacheBytes"]!.GetValue<long>(), status["cacheBytesTotal"]!.GetValue<long>());
        Assert.NotNull(status["caches"]);
    }

    [Fact]
    public async Task TheHttpStatusRoute_CarriesTheSameFields()
    {
        var textures = new FakeCache("textures", bytes: 42, count: 1);
        await using HostRig rig = await HostRig.StartAsync(register: c => c.Services.Require<CacheParticipants>().Add(textures));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/status");
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(42, body["caches"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "textures")!["bytes"]!.GetValue<long>());
        Assert.True(body["cacheBytesTotal"]!.GetValue<long>() >= 42);
    }

    [Fact]
    public async Task CacheClear_WithoutALayer_ClearsEveryParticipant()
    {
        var textures = new FakeCache("textures", 1000, 3);
        var models = new FakeCache("model-packages", 250, 1);
        await using HostRig rig = HostRig.Create(register: context =>
        {
            CacheParticipants participants = context.Services.Require<CacheParticipants>();
            participants.Add(textures);
            participants.Add(models);
        });

        await rig.DataAsync("cache.clear");

        Assert.Equal(1, textures.Clears);
        Assert.Equal(1, models.Clears);
        JsonNode status = await rig.DataAsync("status");
        Assert.Equal(status["cacheBytes"]!.GetValue<long>(), status["cacheBytesTotal"]!.GetValue<long>());
    }

    [Fact]
    public async Task CacheClear_ForOneLayer_LeavesTheParticipantsAlone()
    {
        var textures = new FakeCache("textures", 1000, 3);
        await using HostRig rig = HostRig.Create(register: c => c.Services.Require<CacheParticipants>().Add(textures));

        await rig.DataAsync("cache.clear", new JsonObject { ["layer"] = LayerIds.Terrain });

        Assert.Equal(0, textures.Clears);
        Assert.Equal(1000, textures.Bytes);
    }

    [Fact]
    public async Task TheList_RefusesDuplicateNames_AndIsFrozenAfterCompose()
    {
        await using HostRig rig = HostRig.Create();
        CacheParticipants participants = rig.Context.Services.Require<CacheParticipants>();

        Assert.Throws<InvalidOperationException>(() => participants.Add(new FakeCache("late")));

        var fresh = new CacheParticipants();
        fresh.Add(new FakeCache("a"));
        Assert.Throws<ArgumentException>(() => fresh.Add(new FakeCache("a")));
        Assert.Throws<ArgumentException>(() => fresh.Add(new FakeCache(string.Empty)));
        Assert.Equal(["a"], fresh.All.Select(c => c.Name).ToArray());
        fresh.Freeze();
        Assert.Throws<InvalidOperationException>(() => fresh.Add(new FakeCache("b")));
    }
}

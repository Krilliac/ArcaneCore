using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>A Wanted-poster-style quest giver object end to end: CMSG_GAMEOBJ_USE, status, accept.</summary>
public sealed class GameObjectQuestGiverWorldTests
{
    private const uint PosterEntry = 68;
    private const uint PosterSpawn = 156561;
    private const uint PosterQuest = 176;

    [Fact]
    public void TheFeatureIsDiscovered_AndFillsTheQuestGiverSeam()
    {
        Assert.Contains(typeof(GameObjectQuestGiverFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
    }

    [Fact]
    public async Task UsingThePoster_OpensTheQuest_AcceptingStartsIt_AndTheStatusIsAvailable()
    {
        await using WorldTestHost host = Start(out ObjectGuid poster);
        await using WorldTestClient client = await host.EnterWorldAsync("POSTERQUEST", "Posterquest");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Posterquest")!.VisibleObjects.Contains(poster), "the poster becomes visible");
        await client.CollectAsync();

        // The seam is filled on every map's game object system.
        Assert.True(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Posterquest")!.Map is { } map
            && ((WorldSession)host.World.FindOnlinePlayer("Posterquest")!.Session).Services
                .GetRequiredService<GameObjectLootFeature>().FindSystem(map)?.QuestGiver is not null));

        await client.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, BitConverter.GetBytes(poster.Value));
        byte[] status = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverStatus);
        Assert.Equal(poster.Value, BinaryPrimitives.ReadUInt64LittleEndian(status));
        Assert.Equal((uint)DialogStatus.Available, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)));

        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(poster.Value));
        byte[] details = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails);
        Assert.Equal(poster.Value, BinaryPrimitives.ReadUInt64LittleEndian(details));
        Assert.Equal(PosterQuest, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(8)));

        var accept = new PacketWriter(12);
        accept.WriteUInt64(poster.Value);
        accept.WriteUInt32(PosterQuest);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        Assert.Equal(PosterQuest, await host.PlayerStateAsync("Posterquest", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
    }

    private static WorldTestHost Start(out ObjectGuid poster)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        var template = new GameObjectTemplate { Entry = PosterEntry, Type = (uint)GameObjectType.QuestGiver, DisplayId = 5, Name = "Wanted Poster", Data = data };

        // Human start is (-8949.95, -132.49, 83.53): the poster is 2 yd away.
        var spawn = new GameObjectSpawn { Guid = PosterSpawn, Entry = PosterEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        poster = ObjectGuid.WithEntry(HighGuid.GameObject, PosterEntry, PosterSpawn);
        var objects = new GameObjectContent([template], [spawn], [], [(PosterEntry, PosterQuest)], []);
        GameObjectTestStore.Current.Value = new GameObjectTestContext(objects, LootContent.Empty);
        GameObjectQuestGiverTestServices.Current.Value = new GameObjectQuestGiverFixture();
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
            GameObjectQuestGiverTestServices.Current.Value = null;
        }
    }
}

internal sealed class GameObjectQuestGiverFixture : IQuestContentStore
{
    public MemoryQuestStore Characters { get; } = new();

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
        [new QuestTemplate { Entry = 176, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Wanted: Hogger", Details = "Hogger must die.",
            Objectives = "Slay Hogger.", RequestItemsText = "Well?", ReqCreatureOrGOId1 = 448, ReqCreatureOrGOCount1 = 1 }], [], [])
    {
        GameObjectStarters = [new CreatureQuestRelation { Id = 68, Quest = 176 }],
    });
}

internal sealed class GameObjectQuestGiverTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<GameObjectQuestGiverFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture)
        {
            return;
        }

        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
    }
}

using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

public sealed class QuestInteractionWorldTests
{
    [Fact]
    public async Task SocketQuestFlow_DetailsAcceptPersistAbandonAndRelog()
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        byte[] key = await host.AddAccountAsync("INTERACTION");
        int characterId;
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync("INTERACTION", key);
            await client.CreateCharacterAsync("Interaction");
            var account = (await host.Accounts.FindByUsernameAsync("INTERACTION"))!;
            characterId = (await host.Characters.GetByAccountAsync(account.Id)).Single().Id;
            await client.LoginAsync((ulong)characterId);
            await VisibleAsync(host, "Interaction");
            await client.SendAsync(WorldOpcode.CmsgQuestgiverStatusQuery, GuidBody());
            byte[] status = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverStatus);
            Assert.Equal(12, status.Length);
            Assert.Equal(QuestInteractionFixture.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(status));
            Assert.Equal((uint)DialogStatus.Available, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8)));
            await client.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody());
            byte[] details = await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails);
            Assert.Equal(QuestInteractionFixture.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(details));
            Assert.Equal(QuestInteractionFixture.QuestId, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(8)));

            await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
            await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
            QuestNpcFeature feature = await FeatureAsync(host, "Interaction");
            await feature.Persistence.FlushCharacterAsync(characterId);
            CharacterQuestStatus accepted = fixture.Characters.Stored(characterId, QuestInteractionFixture.QuestId);
            Assert.Equal((byte)QuestStatus.Incomplete, accepted.Status);
            Assert.True(accepted.Timer > fixture.Clock.GetUtcNow().ToUnixTimeSeconds());
            Assert.Equal(QuestInteractionFixture.QuestId,
                await host.PlayerStateAsync("Interaction", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
            int saves = fixture.Characters.SaveCalls;
            await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
            await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
            await feature.Persistence.FlushCharacterAsync(characterId);
            Assert.Equal(saves, fixture.Characters.SaveCalls);

            await client.SendAsync(WorldOpcode.CmsgQuestlogRemoveQuest, [0]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Interaction")!.GetUInt32(UpdateFields.PlayerQuestLog11) == 0,
                "abandoned quest leaves the log");
            await feature.Persistence.FlushCharacterAsync(characterId);
            CharacterQuestStatus abandoned = fixture.Characters.Stored(characterId, QuestInteractionFixture.QuestId);
            Assert.Equal((byte)QuestStatus.None, abandoned.Status);
            Assert.Equal(0, abandoned.Timer);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "quest character disconnects");
        await using WorldTestClient relog = await host.ConnectAsync();
        await relog.AuthenticateAsync("INTERACTION", key);
        await relog.LoginAsync((ulong)characterId);
        Assert.Equal(0u, await host.PlayerStateAsync("Interaction", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        await VisibleAsync(host, "Interaction");
        await relog.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
        await relog.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        await (await FeatureAsync(host, "Interaction")).Persistence.FlushCharacterAsync(characterId);
        Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(characterId, QuestInteractionFixture.QuestId).Status);
    }

    [Theory]
    [InlineData("visibility")]
    [InlineData("hostile")]
    [InlineData("unknown")]
    [InlineData("distance")]
    [InlineData("relation")]
    [InlineData("dead")]
    [InlineData("flag")]
    public async Task SocketInvalidInteractions_CannotAcceptOrReadDetails(string guard)
    {
        var fixture = new QuestInteractionFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("GUARDED", "Guarded");
        await VisibleAsync(host, "Guarded");
        uint requested = guard == "relation" ? QuestInteractionFixture.UnrelatedId : QuestInteractionFixture.QuestId;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Guarded")!;
            var creature = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            switch (guard)
            {
                case "visibility": player.VisibleObjects.Remove(creature.Guid); break;
                case "hostile": creature.FactionTemplate = 900012; break;
                case "unknown": creature.FactionTemplate = 999999; break;
                case "distance": creature.Relocate(player.X + 20, player.Y, player.Z, 0, host.World.NowMs); break;
                case "dead": creature.Health = 0; break;
                case "flag": creature.NpcFlags = 0; break;
            }
        });
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody(requested));
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(requested));
        // The map processes both requests in socket order. The accept response proves
        // the preceding details request ran; inspect every intervening packet for a leak.
        while (true)
        {
            var (opcode, _) = await client.ReadAsync();
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverQuestDetails, opcode);
            if (opcode == WorldOpcode.SmsgGossipComplete) break;
        }
        Assert.Equal(0u, await host.PlayerStateAsync("Guarded", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0, fixture.Characters.SaveCalls);
    }

    [Theory]
    [InlineData(WorldOpcode.CmsgQuestgiverStatusQuery, 7)]
    [InlineData(WorldOpcode.CmsgQuestgiverStatusQuery, 9)]
    [InlineData(WorldOpcode.CmsgQuestgiverQueryQuest, 11)]
    [InlineData(WorldOpcode.CmsgQuestgiverAcceptQuest, 16)]
    [InlineData(WorldOpcode.CmsgQuestlogRemoveQuest, 0)]
    [InlineData(WorldOpcode.CmsgQuestlogRemoveQuest, 2)]
    public async Task MalformedInteractionPayload_Disconnects(WorldOpcode opcode, int length)
    {
        await using WorldTestHost host = Start(new QuestInteractionFixture());
        await using WorldTestClient client = await host.EnterWorldAsync("BADINTERACTION", "Badinteract");
        await client.CollectAsync();
        await client.SendAsync(opcode, new byte[length]);
        Assert.True(await client.IsClosedByServerAsync());
    }

    private static WorldTestHost Start(QuestInteractionFixture fixture)
    {
        QuestInteractionTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { QuestInteractionTestServices.Current.Value = null; }
    }

    private static Task VisibleAsync(WorldTestHost host, string name) => host.WaitForWorldAsync(
        () => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver becomes visible");

    private static Task<QuestNpcFeature> FeatureAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name,
        p => ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>());

    private static byte[] GuidBody()
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        return writer.ToArray();
    }

    private static byte[] QuestBody(uint id = QuestInteractionFixture.QuestId)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        writer.WriteUInt32(id);
        return writer.ToArray();
    }
}

internal sealed class QuestInteractionTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<QuestInteractionFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddSingleton(new FactionTemplateCatalog([
            new(1, 1, 0, 1, 0, 0), new(900011, 0, 0, 8, 0, 0), new(900012, 0, 0, 8, 0, 1)]));
    }
}

internal sealed class QuestInteractionFixture : IQuestContentStore, ICreatureDataStore
{
    public const uint QuestId = 900001;
    public const uint UnrelatedId = 900002;
    public const uint Entry = 900010;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 900020);
    public MemoryQuestStore Characters { get; } = new();
    public ManualQuestClock Clock { get; } = new();

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
        [new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, LimitTime = 30,
            Title = "Synthetic quest", Details = "Accept a bounded journal task.", ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1 },
        new QuestTemplate { Entry = UnrelatedId, Method = 2, Title = "Unrelated quest" }],
        [new CreatureQuestRelation { Id = Entry, Quest = QuestId }], []));

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [new CreatureTemplate { Entry = Entry, Name = "Synthetic questgiver", Faction = 900011, NpcFlags = 2, DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 900020, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
}

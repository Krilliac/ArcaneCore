using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class SpellVariantTaxiOwnershipTests
{
    [Fact]
    public async Task LearnAllMyTaxis_PassesThePlayerOwnershipTokenToPersistence()
    {
        var store = new RecordingQuestStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICharacterQuestStore>(store);
        });
        await using WorldTestClient gm = await host.EnterWorldAsync("TAXIFENCE", "Taxifence", AccountSecurity.Administrator);
        Player player = await host.PlayerAsync("Taxifence");
        CharacterOwnershipToken token = new("world", 4, Guid.NewGuid());
        await host.OnWorldAsync(() =>
        {
            player.CharacterOwnership = token;
            QuestNpcPersistence persistence = host.WorldServices.GetRequiredService<QuestNpcFeature>().Persistence;
            int characterId = (int)player.Guid.Low;
            persistence.LoadCharacter(characterId, new CharacterQuestData([], new uint[8]),
                persistence.CaptureLoadRevision(characterId), token);
            host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store = new SpellStore([], [], []);
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.ReplaceNpcs(new NpcStore(NpcContent.Empty with
            {
                TaxiNodes =
                [
                    new TaxiNode { Id = 200, MapId = 0, X = -8947, Y = -132, Z = 83, MountAlliance = 1, MountHorde = 1 },
                    new TaxiNode { Id = 201, MapId = 0, X = -9000, Y = -132, Z = 83, MountAlliance = 1, MountHorde = 1 },
                ],
                TaxiPaths = [new TaxiPath { Id = 1, FromNode = 200, ToNode = 201 }],
            }));
            host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                [new CreatureTemplate { Entry = 8002, Name = "Flightmaster", NpcFlags = (uint)NpcFlags.FlightMaster }],
                [new CreatureSpawn { Guid = 8002, Entry = 8002, MapId = 0, X = -8948, Y = -132, Z = 83 }], [], [], []));
        });

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".learn all_mytaxis");
        Assert.Contains("taxi nodes", (await gm.ReadChatAsync()).Text);
        await store.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(token, store.LastOwnership);
    }

    private sealed class RecordingQuestStore : ICharacterQuestStore
    {
        public readonly TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CharacterOwnershipToken? LastOwnership { get; private set; }

        public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterQuestData([], new uint[8]));

        public Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            => Task.CompletedTask;

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            LastOwnership = ownership;
            Saved.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
